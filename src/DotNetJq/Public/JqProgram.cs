using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq;

/// <summary>A jq filter compiled for repeated, sequential managed execution.</summary>
/// <remarks>
/// Like a native <c>jq_state</c>, an instance is not thread-safe. Use a separately
/// compiled program for each concurrent execution.
/// </remarks>
public sealed class JqProgram : IDisposable
{
    private enum Lifecycle
    {
        Idle,
        Executing,
        Disposed,
        DisposedWhileExecuting,
    }

    private jq_state? state;
    private readonly string[] libraryPaths;
    private readonly string? jqOrigin;
    private readonly string? programOrigin;
    private Lifecycle lifecycle;

    private JqProgram(
        jq_state state,
        JqModuleResolver? resolver,
        string? programOrigin)
    {
        this.state = state;
        libraryPaths = resolver?.LibraryPaths.ToArray() ?? [];
        jqOrigin = resolver?.JqOrigin;
        this.programOrigin = programOrigin;
    }

    /// <summary>Releases the compiled jq state when disposal was omitted.</summary>
    ~JqProgram()
    {
        try
        {
            DisposeCore();
        }
        catch
        {
            // Finalizers must not terminate the process. Explicit Dispose still
            // reports an unexpected teardown failure to its caller.
        }
    }

    /// <summary>
    /// Prevents new executions and releases the compiled jq state when no execution is active.
    /// </summary>
    /// <remarks>
    /// If an execution cursor is active, teardown is deferred until that cursor completes or is
    /// disposed; the active cursor remains usable. Disposal is idempotent.
    /// </remarks>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Compiles a jq filter.</summary>
    /// <param name="source">The jq source text.</param>
    /// <returns>A reusable compiled program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="JqCompileException">The source is not a valid jq filter.</exception>
    public static JqProgram Compile(string source)
        => CompileCore(source, options: null);

    /// <summary>Compiles a jq filter with an explicit module filesystem and search policy.</summary>
    /// <param name="source">The jq source text.</param>
    /// <param name="resolver">The only filesystem capability available to module and data imports.</param>
    /// <returns>A reusable compiled program.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="JqCompileException">The source or one of its dependencies is not valid jq.</exception>
    public static JqProgram Compile(string source, JqModuleResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return CompileCore(source, new JqCompilationOptions { ModuleResolver = resolver });
    }

    /// <summary>Compiles a jq filter with explicit module and program-origin capabilities.</summary>
    /// <param name="source">The jq source text.</param>
    /// <param name="options">Explicit compilation capabilities.</param>
    /// <returns>A reusable compiled program.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="JqCompileException">The source or one of its dependencies is not valid jq.</exception>
    public static JqProgram Compile(string source, JqCompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CompileCore(source, options);
    }

    private static JqProgram CompileCore(string source, JqCompilationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolver = options?.ModuleResolver;
        var programOrigin = options?.ProgramOrigin;

        jq_state? state = libjq.jq_init();
        try
        {
            // The public library returns compile diagnostics as JqCompileException
            // and must not write process stderr. Detail collection observes the
            // canonical jq_report_error callback before this consumer frees it.
            libjq.jq_set_error_cb(state, libjq.jv_free);
            var compiled = resolver is null
                ? libjq.jq_compile(state, source, programOrigin)
                : libjq.jq_compile(state, source, resolver, programOrigin);
            if (compiled == 0)
            {
                throw state.CompileError ?? new JqCompileException("jq program could not be compiled");
            }

            var result = new JqProgram(state, resolver, programOrigin);
            state = null;
            return result;
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    /// <summary>Executes the compiled filter against one JSON input value.</summary>
    /// <param name="json">A JSON value.</param>
    /// <param name="options">Optional execution resource limits.</param>
    /// <returns>The jq output stream, materialized as JSON values in emission order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured resource limit is invalid.</exception>
    /// <exception cref="OperationCanceledException">Execution was canceled.</exception>
    /// <exception cref="JqParseException"><paramref name="json"/> is not valid JSON.</exception>
    /// <exception cref="JqRuntimeException">Execution fails or a configured resource limit is exceeded.</exception>
    /// <exception cref="JqHaltException">The filter invokes <c>halt</c> or <c>halt_error</c>.</exception>
    /// <exception cref="InvalidOperationException">Another execution is active on this program.</exception>
    /// <exception cref="ObjectDisposedException">This program has been disposed.</exception>
    public IReadOnlyList<JsonElement> Execute(
        string json,
        JqExecutionOptions? options = null)
    {
        var result = ExecuteDetailed(json, options);
        if (result.Outcome.Kind == JqExecutionOutcomeKind.RuntimeError)
        {
            throw result.Outcome.RuntimeError ?? new JqRuntimeException("jq execution failed");
        }

        if (result.Outcome.RequestedExitCode is { } requestedExitCode)
        {
            throw new JqHaltException(
                requestedExitCode,
                result.Outcome.HaltMessage,
                result.Outputs);
        }

        return result.Outputs;
    }

    /// <summary>Executes and materializes both values and the exact terminal outcome.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An input position or configured resource limit is invalid.</exception>
    /// <exception cref="OperationCanceledException">Execution was canceled.</exception>
    /// <exception cref="JqParseException"><paramref name="json"/> is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">Another execution is active on this program.</exception>
    /// <exception cref="ObjectDisposedException">This program has been disposed.</exception>
    public JqExecutionResult ExecuteDetailed(
        string json,
        JqExecutionOptions? options = null,
        JqExecutionCapabilities? capabilities = null,
        JqInputPosition? position = null)
    {
        using var execution = StartExecution(json, options, capabilities, position);
        var outputs = new List<JsonElement>();
        while (execution.TryRead(out var value))
        {
            outputs.Add(value);
        }

        return new JqExecutionResult(
            outputs.ToArray(),
            execution.Outcome ?? throw new InvalidOperationException("jq execution has no terminal outcome"));
    }

    /// <summary>Starts a pull-based execution for a JSON primary input.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An input position or configured resource limit is invalid.</exception>
    /// <exception cref="OperationCanceledException">Execution was canceled before the cursor was created.</exception>
    /// <exception cref="JqParseException"><paramref name="json"/> is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">Another execution is active on this program.</exception>
    /// <exception cref="ObjectDisposedException">This program has been disposed.</exception>
    public JqExecution StartExecution(
        string json,
        JqExecutionOptions? options = null,
        JqExecutionCapabilities? capabilities = null,
        JqInputPosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var input = Encoding.UTF8.GetBytes(json);
        return StartExecutionCore(
            input,
            position,
            options,
            capabilities);
    }

    /// <summary>Starts a pull-based execution for a parsed primary input and explicit position.</summary>
    /// <exception cref="ArgumentException">The input value is undefined.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An input position or configured resource limit is invalid.</exception>
    /// <exception cref="OperationCanceledException">Execution was canceled before the cursor was created.</exception>
    /// <exception cref="InvalidOperationException">Another execution is active on this program.</exception>
    /// <exception cref="ObjectDisposedException">This program has been disposed.</exception>
    public JqExecution StartExecution(
        JqExecutionInput input,
        JqExecutionOptions? options = null,
        JqExecutionCapabilities? capabilities = null)
    {
        if (input.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A jq primary input cannot be undefined.", nameof(input));
        }

        var json = input.Value.GetRawText();
        var bytes = Encoding.UTF8.GetBytes(json);
        return StartExecutionCore(
            bytes,
            input.Position,
            options,
            capabilities);
    }

    /// <summary>Starts the pull-based execution shape used by upstream src/jq_test.c.</summary>
    internal JqExecution StartUpstreamTestExecution(
        string json,
        JqExecutionOptions? options = null) =>
        StartExecution(json, options);

    private JqExecution StartExecutionCore(
        byte[] input,
        JqInputPosition? position,
        JqExecutionOptions? options,
        JqExecutionCapabilities? capabilities)
    {
        var inputBytes = input.Length;
        var executionOptions = options ?? JqExecutionOptions.Default;
        Validate(executionOptions);
        executionOptions.CancellationToken.ThrowIfCancellationRequested();
        if (position is { LineNumber: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Input line number cannot be negative.");
        }

        if (executionOptions.MaxInputBytes is { } maxInputBytes && inputBytes > maxInputBytes)
        {
            throw new JqRuntimeException("jq input-byte limit exceeded");
        }

        AcquireExecution();
        var executionTransferred = false;
        try
        {
            var executionState = state ??
                throw new ObjectDisposedException(nameof(JqProgram));
            libjq.jq_configure_execution(executionState, executionOptions, capabilities);
            libjq.jq_set_attr(
                executionState,
                libjq.jv_string("JQ_LIBRARY_PATH"),
                libjq.jv_array(libraryPaths.Select(libjq.jv_string)));
            if (jqOrigin is not null)
            {
                libjq.jq_set_attr(
                    executionState,
                    libjq.jv_string("JQ_ORIGIN"),
                    libjq.jv_string(jqOrigin));
            }

            if (programOrigin is not null)
            {
                libjq.jq_set_attr(
                    executionState,
                    libjq.jv_string("PROGRAM_ORIGIN"),
                    libjq.jv_string(programOrigin));
            }

            libjq.jq_start(
                executionState,
                ParsePrimaryInput(input),
                0,
                position,
                inputBytes);
            var execution = new JqExecution(
                executionState,
                executionOptions,
                ReleaseExecution);
            executionTransferred = true;
            return execution;
        }
        finally
        {
            if (!executionTransferred)
            {
                ReleaseExecution();
            }
        }
    }

    private static jv ParsePrimaryInput(ReadOnlySpan<byte> json)
    {
        var value = libjq.jv_parse_sized(json);
        if (value.IsValid)
        {
            return value;
        }

        if (!libjq.jv_invalid_has_msg(libjq.jv_copy(value)))
        {
            libjq.jv_free(value);
            throw new JqParseException("Expected JSON value");
        }

        var message = libjq.jv_invalid_get_msg(value);
        try
        {
            throw new JqParseException(message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
        }
    }

    private void AcquireExecution()
    {
        switch (lifecycle)
        {
            case Lifecycle.Idle:
                lifecycle = Lifecycle.Executing;
                return;
            case Lifecycle.Executing:
                throw new InvalidOperationException(
                    "This JqProgram already has an active execution. " +
                    "Complete or dispose it before starting another.");
            default:
                throw new ObjectDisposedException(nameof(JqProgram));
        }
    }

    private void ReleaseExecution()
    {
        var executionState = state;
        if (executionState is not null)
        {
            libjq.jq_reset(executionState);
        }

        switch (lifecycle)
        {
            case Lifecycle.Executing:
                lifecycle = Lifecycle.Idle;
                return;
            case Lifecycle.DisposedWhileExecuting:
                lifecycle = Lifecycle.Disposed;
                TeardownState();
                return;
            default:
                return;
        }
    }

    private void DisposeCore()
    {
        switch (lifecycle)
        {
            case Lifecycle.Idle:
                lifecycle = Lifecycle.Disposed;
                TeardownState();
                return;
            case Lifecycle.Executing:
                lifecycle = Lifecycle.DisposedWhileExecuting;
                return;
            default:
                return;
        }
    }

    private void TeardownState()
    {
        var ownedState = state;
        state = null;
        libjq.jq_teardown(ref ownedState);
    }

    private static void Validate(JqExecutionOptions options)
    {
        if (options.Timeout is { } timeout && timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout cannot be negative.");
        }

        if (options.MaxInputBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum input bytes cannot be negative.");
        }

        if (options.MaxOutputBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum output bytes cannot be negative.");
        }

        if (options.MaxOutputValues is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum output values cannot be negative.");
        }

        if (options.MaxRecursionDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum recursion depth cannot be negative.");
        }

        if (options.MaxExecutionTransitions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum execution transitions cannot be negative.");
        }

        if (options.RegexTimeout != System.Threading.Timeout.InfiniteTimeSpan &&
            options.RegexTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Regular-expression timeout must be positive or infinite.");
        }
    }
}

/// <summary>A pull-based jq output stream with an explicit terminal outcome.</summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class JqExecution : IDisposable
{
    private readonly JqExecutionOptions options;
    private Action? releaseExecution;
    private jq_state? state;
    private int outputCount;
    private long outputBytes;

    internal JqExecution(
        jq_state state,
        JqExecutionOptions options,
        Action releaseExecution)
    {
        this.state = state;
        this.options = options;
        this.releaseExecution = releaseExecution;
    }

    /// <summary>Releases the owning program lease when disposal was omitted.</summary>
    ~JqExecution()
    {
        try
        {
            DisposeCore();
        }
        catch
        {
            // Explicit disposal remains the diagnostic path. A finalizer must
            // never surface an exception on the runtime finalizer thread.
        }
    }

    /// <summary>
    /// Gets the terminal outcome after <see cref="TryRead"/> naturally reaches a terminal state,
    /// or <see langword="null"/> when the cursor was disposed before reaching one.
    /// </summary>
    public JqExecutionOutcome? Outcome { get; private set; }

    /// <summary>
    /// Pulls the next jq value, or returns false after natural termination or early disposal.
    /// Natural termination sets <see cref="Outcome"/>; early disposal leaves it null.
    /// </summary>
    public bool TryRead(out JsonElement value)
    {
        value = default;
        if (state is null)
        {
            return false;
        }

        try
        {
            var next = libjq.jq_next(state);
            if (!libjq.jv_is_valid(next))
            {
                // jq-1.8.2 src/main.c:process() distinguishes an exhausted
                // jq_next() result from an invalid value carrying an uncaught
                // runtime error. jv_invalid_has_msg() consumes its argument,
                // so inspect a copy and move the original message owner into
                // the managed runtime exception.
                if (libjq.jv_invalid_has_msg(libjq.jv_copy(next)))
                {
                    throw new JqRuntimeException(libjq.jv_invalid_get_msg(next));
                }

                libjq.jv_free(next);
                Complete(CreateTerminalOutcome(state));
                return false;
            }

            try
            {
                if (options.MaxOutputValues is { } maxOutputValues &&
                    outputCount >= maxOutputValues)
                {
                    throw new JqRuntimeException("jq output-value limit exceeded");
                }

                // The output owner remains live through JsonElement materialization
                // and is released by the finally block below.
                var outputUtf8 = libjq.jv_dump_bytes_borrowed(next);
                var valueBytes = outputUtf8.LongLength;
                if (options.MaxOutputBytes is { } maxOutputBytes &&
                    (valueBytes > maxOutputBytes || outputBytes > maxOutputBytes - valueBytes))
                {
                    throw new JqRuntimeException("jq output-byte limit exceeded");
                }

                outputCount++;
                outputBytes += valueBytes;
                value = libjq.jv_to_json_element(outputUtf8);
                return true;
            }
            finally
            {
                // jq_next() transfers one owner to its caller. Materializing
                // JsonElement ends that owner's lifetime even though the
                // managed result iterator retains its own handle.
                libjq.jv_free(next);
            }
        }
        catch (JqRuntimeException exception)
        {
            // Public outcomes expose the already-materialized exception message,
            // not jq's internal error slot. Release that owner before retaining
            // the exception in a potentially long-lived result object.
            exception.ReleaseErrorValue();
            Complete(new JqExecutionOutcome(
                JqExecutionOutcomeKind.RuntimeError,
                runtimeError: exception));
            return false;
        }
        catch (JqHostCapabilityException exception)
        {
            var original = exception.OriginalException;
            Dispose();
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Reads one step for the upstream jq.test-compatible harness.</summary>
    internal JqExecutionStep ReadNext()
    {
        if (TryRead(out var value))
        {
            return new JqExecutionStep(value, null);
        }

        return new JqExecutionStep(null, Outcome?.RuntimeError);
    }

    /// <summary>
    /// Abandons any remaining outputs and releases the owning program lease. If execution has not
    /// naturally terminated, <see cref="Outcome"/> remains null.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (state is null)
        {
            return;
        }

        state = null;
        var release = releaseExecution;
        releaseExecution = null;
        release?.Invoke();
    }

    private static JqExecutionOutcome CreateTerminalOutcome(jq_state state)
    {
        if (libjq.jq_halted(state) == 0)
        {
            return new JqExecutionOutcome(JqExecutionOutcomeKind.Completed);
        }

        var exitCode = libjq.jq_get_exit_code(state);
        var message = libjq.jq_get_error_message(state);
        try
        {
            return new JqExecutionOutcome(
                JqExecutionOutcomeKind.Halted,
                exitCode.IsValid ? libjq.jv_to_json_element(exitCode) : null,
                message.IsValid ? libjq.jv_to_json_element(message) : null);
        }
        finally
        {
            libjq.jv_free(exitCode);
            libjq.jv_free(message);
        }
    }

    private void Complete(JqExecutionOutcome outcome)
    {
        Outcome = outcome;
        Dispose();
    }
}

/// <summary>A single value or terminal jq runtime error used by the upstream fixture runner.</summary>
internal readonly record struct JqExecutionStep(
    JsonElement? Value,
    JqRuntimeException? TerminalError);
