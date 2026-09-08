// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jq.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jq.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jq.h.cs
//
// Direct surface: jq_state owns the compiled bytecode graph and active bytecode VM and preserves
// jq_init/jq_compile/jq_start/jq_next/jq_reset/jq_teardown lifecycle, callbacks, halt state,
// attributes, origins, input positions, and environment policy. Recompile/teardown recursively
// release the state-owned bytecode graph; reset abandons all live VM data/fork/frame owners while
// retaining compiled code. Managed delegates replace native callback/data pointer pairs; public
// capabilities are exposed as effective jq_get_* callbacks rather than bypassing that surface.
// Known differences: managed types and delegates are not C layout- or pointer-ABI compatible.
// Evidence: JqStateRefcountLifecycleCompatibilityTests, DirectBytecodeVmCompatibilityTests,
// public/stateful compatibility suites, official jq fixtures, and differential execution.

using System.Collections;
using System.Text;
using System.Text.Json;
using DotNetJq;
using DotNetJq.Compatibility.Time;

namespace DotNetJq.Port;

[Flags]
internal enum jq_debug_flags
{
    JQ_DEBUG_NONE = 0,
    JQ_DEBUG_TRACE = 1,
    JQ_DEBUG_TRACE_DETAIL = 2,
    JQ_DEBUG_TRACE_ALL = JQ_DEBUG_TRACE | JQ_DEBUG_TRACE_DETAIL,
}

// Managed callback closures replace each native callback/data-pointer pair.
// The message callback consumes its jv argument; the input callback returns
// one owned jv exactly like jq_msg_cb/jq_input_cb in jq.h.
internal delegate void jq_msg_cb(jv value);

internal delegate jv jq_input_cb();

internal sealed partial class jq_state : IDisposable
{
    // glibc keeps both the active TZ object and mktime's offset guess in process-static
    // state. Its time entry points serialize access internally. Default execution uses this
    // one guarded context; only the explicit managed Environment capability uses the
    // per-jq_state context below.
    private static readonly object AmbientTimeZoneGate = new();
    private static readonly JqTimeZoneContext AmbientTimeZoneContext = new();

    private bool halted;
    private jv exitCode = libjq.jv_invalid();
    private jv errorMessage = libjq.jv_invalid();
    private jv attrs = libjq.jv_object();
    private JqInputPosition? currentInputPosition;
    private long consumedInputBytes;
    private int debugTraceFlags;
    private Action<string>? debugTraceCallback;
    private jq_msg_cb? errorCallback;
    private jq_input_cb? inputCallback;
    private jq_msg_cb? debugCallback;
    private jq_msg_cb? standardErrorCallback;
    private bool collectCompileDiagnostics;
    private JqModuleResolver? moduleResolver;
    private bytecode? bytecode;

    internal jq_state(
        JqExecutionOptions options,
        JqExecutionCapabilities? capabilities = null)
    {
        Options = options;
        Capabilities = capabilities ?? new JqExecutionCapabilities();
    }

    internal JqExecutionOptions Options { get; private set; }

    internal JqExecutionCapabilities Capabilities { get; private set; }

    // Bounded proxy state used only by an explicit per-execution Environment.
    // Default ambient execution instead uses AmbientTimeZoneContext above.
    internal JqTimeZoneContext TimeZoneContext { get; } = new();

    internal TResult WithTimeEnvironment<TResult>(
        Func<IReadOnlyDictionary<string, string>, JqTimeZoneContext, TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Options.Environment is { } explicitEnvironment)
        {
            return operation(explicitEnvironment, TimeZoneContext);
        }

        lock (AmbientTimeZoneGate)
        {
            // Native env/time calls observe the ambient process environment at the
            // builtin boundary, not when an options object or jq_state is created.
            return operation(libjq.jq_environment_values(Options), AmbientTimeZoneContext);
        }
    }

    // A managed JqProgram reuses one jq_state just as native jq keeps the
    // compiled bytecode on that state.  Execution-specific host policy is
    // replaced only between jq_start()/jq_reset() lifetimes.
    internal void ConfigureExecution(
        JqExecutionOptions options,
        JqExecutionCapabilities? capabilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (bytecodeVm is not null)
        {
            throw new InvalidOperationException(
                "jq execution options cannot change during an active execution.");
        }

        Options = options;
        Capabilities = capabilities ?? new JqExecutionCapabilities();
    }

    // Borrowed compile-time capability retained with this compiled jq_state.
    // JqModuleResolver owns only managed policy/filesystem references and is
    // not disposable; teardown clears the reference after execution reset.
    internal JqModuleResolver? ModuleResolver => moduleResolver;

    internal void SetModuleResolver(JqModuleResolver? resolver)
    {
        if (bytecodeVm is not null)
        {
            throw new InvalidOperationException(
                "jq module resolver cannot change during an active execution.");
        }

        moduleResolver = resolver;
    }

    internal jq_msg_cb? ErrorCallback
    {
        get => errorCallback;
        set => errorCallback = value;
    }

    internal jq_input_cb? InputCallback
    {
        get => inputCallback;
        set => inputCallback = value;
    }

    internal jq_msg_cb? DebugCallback
    {
        get => debugCallback;
        set => debugCallback = value;
    }

    internal jq_msg_cb? StandardErrorCallback
    {
        get => standardErrorCallback;
        set => standardErrorCallback = value;
    }

    // Direct counterpart of jq_state::bc. The state is the sole owner of the
    // root bytecode graph; replacing or clearing it recursively frees the old
    // graph after abandoning any VM continuation that still borrows it.
    internal bytecode? Bytecode
    {
        get => bytecode;
        set
        {
            if (ReferenceEquals(bytecode, value))
            {
                return;
            }

            ResetBytecodeExecution();
            var previous = bytecode;
            bytecode = value;
            libjq.bytecode_free(previous);
        }
    }

    internal JqCompileException? CompileError { get; set; }

    internal bool Halted => halted;

    internal bool DebugTraceEnabled => (debugTraceFlags & libjq.JQ_DEBUG_TRACE) != 0;

    internal bool DebugTraceDetailed =>
        (debugTraceFlags & libjq.JQ_DEBUG_TRACE_DETAIL) != 0;

    internal jv ExitCode => exitCode;

    internal jv ErrorMessage => errorMessage;

    internal jv Attributes
    {
        get => attrs;
        set => attrs = value;
    }

    internal void Start(
        jv input,
        int flags,
        JqInputPosition? inputPosition = null,
        int inputBytes = 0)
    {
        var executable = Bytecode ??
            throw new InvalidOperationException("jq program has not been compiled");
        StartBytecode(executable, input, flags, inputPosition, inputBytes);
    }

    internal jv Next() => NextBytecode();

    internal void ResetExecution()
    {
        halted = false;
        libjq.jv_free(exitCode);
        exitCode = libjq.jv_invalid();
        libjq.jv_free(errorMessage);
        errorMessage = libjq.jv_invalid();
        currentInputPosition = null;
        consumedInputBytes = 0;
        debugTraceFlags = 0;
    }

    internal void ResetForNextExecution()
    {
        ResetBytecodeExecution();
        ResetExecution();
    }

    internal void Halt(jv requestedExitCode, jv message)
    {
        if (halted)
        {
            throw new InvalidOperationException("jq execution is already halted");
        }

        halted = true;
        exitCode = requestedExitCode;
        errorMessage = message;
    }

    internal jv ReadInput()
    {
        if (inputCallback is not null)
        {
            try
            {
                return inputCallback();
            }
            catch (Exception exception)
            {
                throw new JqHostCapabilityException(exception);
            }
        }

        var source = Capabilities.Input;
        if (source is null)
        {
            throw new JqRuntimeException(libjq.jv_string("break"));
        }

        JqInputReadResult read;
        try
        {
            read = source.ReadNext(Options.CancellationToken);
        }
        catch (Exception exception)
        {
            throw new JqHostCapabilityException(exception);
        }

        if (read.Position is { } position)
        {
            SetCurrentInputPosition(position);
        }

        return read.Kind switch
        {
            JqInputReadKind.EndOfInput => throw new JqRuntimeException(libjq.jv_string("break")),
            JqInputReadKind.Value => ParseCallerInput(
                read.Value,
                "A value input result has no value."),
            JqInputReadKind.Error => throw new JqRuntimeException(ParseCallerInput(
                read.ErrorValue,
                "An error input result has no error value.")),
            _ => throw new InvalidOperationException("Unknown jq input result kind."),
        };
    }

    // The public managed CompileError is an observer layered over jq's error
    // callback, not a second compiler-reporting path. jq_compile_args brackets
    // detail reports with these methods; the aggregate report intentionally
    // follows EndCompileDiagnostics(), matching the native callback while
    // keeping CompileError's established detail-only exception payload.
    internal void BeginCompileDiagnostics()
    {
        CompileError = null;
        collectCompileDiagnostics = true;
    }

    internal void EndCompileDiagnostics() => collectCompileDiagnostics = false;

    private jv ParseCallerInput(JsonElement? value, string missingValueMessage)
    {
        var json = value?.GetRawText() ?? throw new InvalidOperationException(missingValueMessage);
        var valueBytes = Encoding.UTF8.GetByteCount(json);
        if (Options.MaxInputBytes is { } maximum &&
            (valueBytes > maximum || consumedInputBytes > maximum - valueBytes))
        {
            // A jq handler may catch this runtime error and request another input. Keep the
            // exhausted budget sticky so a smaller later value cannot bypass the limit.
            consumedInputBytes = maximum;
            throw new JqRuntimeException("jq input-byte limit exceeded");
        }

        consumedInputBytes += valueBytes;
        return libjq.jv_parse(json);
    }

    internal void EmitDebug(jv value) => Emit(debugCallback, Capabilities.Debug, value);

    internal void EmitStandardError(jv value) =>
        Emit(standardErrorCallback, Capabilities.StandardError, value);

    internal void ReportError(jv value)
    {
        if (collectCompileDiagnostics)
        {
            var formattedForCollector = libjq.jq_format_error(libjq.jv_copy(value));
            try
            {
                if (formattedForCollector.Kind == jv_kind.JV_KIND_STRING)
                {
                    var message = formattedForCollector.StringValue.TrimEnd('\r', '\n');
                    CompileError = CompileError is null
                        ? new JqCompileException(message)
                        : new JqCompileException(
                            CompileError.Message + Environment.NewLine + message);
                }
            }
            finally
            {
                libjq.jv_free(formattedForCollector);
            }
        }

        if (errorCallback is not null)
        {
            var callbackValue = value;
            value = libjq.jv_invalid();
            try
            {
                errorCallback(callbackValue);
            }
            catch (Exception exception)
            {
                throw new JqHostCapabilityException(exception);
            }

            return;
        }

        var formatted = libjq.jq_format_error(value);
        try
        {
            Console.Error.WriteLine(
                formatted.Kind == jv_kind.JV_KIND_STRING
                    ? formatted.StringValue
                    : "jq: error: out of memory");
        }
        finally
        {
            libjq.jv_free(formatted);
        }
    }

    internal void SetDebugTraceCallback(Action<string>? callback) =>
        debugTraceCallback = callback;

    internal void EmitDebugTrace(string text)
    {
        try
        {
            debugTraceCallback?.Invoke(text);
        }
        catch (Exception exception)
        {
            throw new JqHostCapabilityException(exception);
        }
    }

    internal jv GetCurrentInputFilename() =>
        currentInputPosition?.FileName is { } fileName
            ? libjq.jv_string(fileName)
            : libjq.jv_null();

    internal jv GetCurrentInputLineNumber() =>
        currentInputPosition is { } position
            ? libjq.jv_number(position.LineNumber)
            : throw new JqRuntimeException(libjq.jv_string("Unknown input line number"));

    internal void SetCurrentInputPosition(JqInputPosition position)
    {
        ValidatePosition(position);
        currentInputPosition = position;
    }

    private static void ValidatePosition(JqInputPosition? position)
    {
        if (position is { LineNumber: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Input line number cannot be negative.");
        }
    }

    private static void Emit(jq_msg_cb? callback, IJqValueSink? sink, jv value)
    {
        try
        {
            if (callback is not null)
            {
                var callbackValue = value;
                value = libjq.jv_invalid();
                callback(callbackValue);
                return;
            }

            if (sink is null)
            {
                return;
            }

            var publicValue = libjq.jv_to_json_element(value);
            sink.Write(publicValue);
        }
        catch (Exception exception)
        {
            throw new JqHostCapabilityException(exception);
        }
        finally
        {
            // src/main.c:debug_cb()/stderr_cb() consume and free the jv_copy()
            // passed by src/builtin.c:f_debug()/f_stderr().
            libjq.jv_free(value);
        }
    }

    internal void Teardown()
    {
        ResetBytecodeExecution();
        ResetExecution();
        Bytecode = null;
        moduleResolver = null;
        libjq.jv_free(attrs);
        attrs = libjq.jv_invalid();
    }

    public void Dispose() => Teardown();
}

internal sealed class JqHostCapabilityException(Exception originalException)
    : Exception("A jq host capability failed.", originalException)
{
    internal Exception OriginalException { get; } = originalException;
}

internal static partial class libjq
{
    internal const int JQ_DEBUG_TRACE = (int)jq_debug_flags.JQ_DEBUG_TRACE;
    internal const int JQ_DEBUG_TRACE_DETAIL = (int)jq_debug_flags.JQ_DEBUG_TRACE_DETAIL;
    internal const int JQ_DEBUG_TRACE_ALL = (int)jq_debug_flags.JQ_DEBUG_TRACE_ALL;

    internal static jq_state jq_init(
        JqExecutionOptions? options = null,
        JqExecutionCapabilities? capabilities = null) =>
        new(options ?? JqExecutionOptions.Default, capabilities);

    internal static void jq_configure_execution(
        jq_state jq,
        JqExecutionOptions options,
        JqExecutionCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.ConfigureExecution(options, capabilities);
    }

    internal static void jq_set_module_resolver(
        jq_state jq,
        JqModuleResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.SetModuleResolver(resolver);
    }

    internal static void jq_reset(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.ResetForNextExecution();
    }

    internal static void jq_set_error_cb(jq_state jq, jq_msg_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.ErrorCallback = callback;
    }

    internal static void jq_get_error_cb(jq_state jq, out jq_msg_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        callback = jq.ErrorCallback;
    }

    internal static void jq_report_error(jq_state jq, jv value)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.ReportError(value);
    }

    internal static jv jq_format_error(jv message)
    {
        while (true)
        {
            if (message.Kind == jv_kind.JV_KIND_NULL ||
                (message.Kind == jv_kind.JV_KIND_INVALID &&
                 !jv_invalid_has_msg(jv_copy(message))))
            {
                jv_free(message);
                return jv_null();
            }

            if (message.Kind == jv_kind.JV_KIND_STRING)
            {
                return message;
            }

            if (message.Kind == jv_kind.JV_KIND_INVALID)
            {
                message = jv_invalid_get_msg(message);
                continue;
            }

            var text = jv_dump_string(message);
            return jv_string("jq: error: " + text);
        }
    }

    internal static void jq_set_input_cb(jq_state jq, jq_input_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.InputCallback = callback;
    }

    internal static void jq_get_input_cb(jq_state jq, out jq_input_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        // Preserve the jq callback boundary while layering the public managed
        // IJqInputSource capability behind it. ReadInput also wraps host
        // exceptions and accounts input bytes/positions.
        callback = jq.InputCallback is not null || jq.Capabilities.Input is not null
            ? jq.ReadInput
            : null;
    }

    internal static void jq_set_debug_cb(jq_state jq, jq_msg_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.DebugCallback = callback;
    }

    internal static void jq_get_debug_cb(jq_state jq, out jq_msg_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        callback = jq.DebugCallback is not null || jq.Capabilities.Debug is not null
            ? jq.EmitDebug
            : null;
    }

    internal static void jq_set_stderr_cb(jq_state jq, jq_msg_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.StandardErrorCallback = callback;
    }

    internal static void jq_get_stderr_cb(jq_state jq, out jq_msg_cb? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        callback = jq.StandardErrorCallback is not null || jq.Capabilities.StandardError is not null
            ? jq.EmitStandardError
            : null;
    }

    internal static IReadOnlyDictionary<string, string> jq_environment_values(
        JqExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Environment is { } explicitEnvironment)
        {
            return explicitEnvironment;
        }

        var ambientEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                ambientEnvironment[name] = value;
            }
        }

        return ambientEnvironment;
    }

    internal static jv jq_environment(JqExecutionOptions options)
    {
        return jv_object(
            jq_environment_values(options).Select(
                variable => KeyValuePair.Create(variable.Key, jv_string(variable.Value))));
    }

    // getenv() is case-sensitive on POSIX and case-insensitive in the Windows CRT.
    // Environment enumeration still preserves each actual key spelling so materialized
    // $ENV/env jq objects retain their ordinary case-sensitive object lookup semantics.
    internal static bool jq_getenv(
        IReadOnlyDictionary<string, string>? environment,
        string name,
        out string value) =>
        jq_getenv(environment, name, OperatingSystem.IsWindows(), out value);

    internal static bool jq_getenv(
        IReadOnlyDictionary<string, string>? environment,
        string name,
        bool windowsSemantics,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (environment is not null)
        {
            if (environment.TryGetValue(name, out value!))
            {
                return true;
            }

            if (windowsSemantics)
            {
                foreach (var variable in environment)
                {
                    if (string.Equals(variable.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = variable.Value;
                        return true;
                    }
                }
            }
        }

        value = string.Empty;
        return false;
    }

    internal static void jq_start(
        jq_state jq,
        jv value,
        int flags,
        JqInputPosition? inputPosition = null,
        int inputBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.Start(value, flags, inputPosition, inputBytes);
    }

    internal static void jq_set_debug_trace_callback(jq_state jq, Action<string>? callback)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.SetDebugTraceCallback(callback);
    }

    internal static jv jq_next(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jq.Next();
    }

    internal static void jq_halt(jq_state jq, jv exitCode, jv errorMessage)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.Halt(exitCode, errorMessage);
    }

    internal static int jq_halted(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jq.Halted ? 1 : 0;
    }

    internal static jv jq_get_exit_code(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jv_copy(jq.ExitCode);
    }

    internal static jv jq_get_error_message(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jv_copy(jq.ErrorMessage);
    }

    internal static void jq_set_attrs(jq_state jq, jv attrs)
    {
        ArgumentNullException.ThrowIfNull(jq);
        if (attrs.Kind != jv_kind.JV_KIND_OBJECT)
        {
            throw new ArgumentException("jq attributes must be an object", nameof(attrs));
        }

        jv_free(jq.Attributes);
        jq.Attributes = attrs;
    }

    internal static jv jq_get_attrs(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jv_copy(jq.Attributes);
    }

    internal static void jq_set_attr(jq_state jq, jv attr, jv value)
    {
        ArgumentNullException.ThrowIfNull(jq);
        if (attr.Kind != jv_kind.JV_KIND_STRING)
        {
            throw new ArgumentException("jq attribute name must be a string", nameof(attr));
        }

        jq.Attributes = jv_object_set(jq.Attributes, attr, value);
    }

    internal static jv jq_get_attr(jq_state jq, jv attr)
    {
        ArgumentNullException.ThrowIfNull(jq);
        if (attr.Kind != jv_kind.JV_KIND_STRING)
        {
            return jv_invalid();
        }

        return jv_object_get(jv_copy(jq.Attributes), attr);
    }

    internal static jv jq_get_jq_origin(jq_state jq) =>
        jq_get_attr(jq, jv_string("JQ_ORIGIN"));

    internal static jv jq_get_prog_origin(jq_state jq) =>
        jq_get_attr(jq, jv_string("PROGRAM_ORIGIN"));

    internal static jv jq_get_lib_dirs(jq_state jq)
    {
        var paths = jq_get_attr(jq, jv_string("JQ_LIBRARY_PATH"));
        return paths.IsValid ? paths : jv_array();
    }

    internal static void jq_dump_disassembly(jq_state jq, int indent) =>
        jq_dump_disassembly(jq, indent, Console.Out);

    // Managed host adapter for jq's stdout-backed disassembly API. Keeping the
    // TextWriter at this boundary lets embedders route the native-shaped bytecode
    // listing without changing the two-argument jq entry point.
    internal static void jq_dump_disassembly(jq_state jq, int indent, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(jq);
        ArgumentNullException.ThrowIfNull(writer);
        if (jq.Bytecode is { } bytecode)
        {
            dump_disassembly(writer, indent, bytecode);
        }
    }

    internal static void jq_teardown(ref jq_state? jq)
    {
        jq?.Teardown();
        jq = null;
    }
}
