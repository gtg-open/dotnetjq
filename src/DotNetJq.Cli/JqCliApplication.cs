using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Cli;

internal readonly record struct CliHost(
    Stream StandardInput,
    Stream StandardOutput,
    Stream StandardError,
    string CurrentDirectory,
    string ExecutablePath,
    bool IsInputTerminal,
    bool IsOutputTerminal,
    bool SupportsColor,
    IReadOnlyDictionary<string, string> Environment,
    CancellationToken CancellationToken);

internal static class JqCliApplication
{
    internal const string ProductName = "dotnetjq";
    internal static readonly string ProductVersion = ResolveProductVersion();
    internal const string CompatibleJqVersion = DotNetJqVersionInfo.CompatibleJqVersion;

    private const int JqOk = 0;
    private const int JqOkNullKind = -1;
    private const int JqErrorSystem = 2;
    private const int JqErrorCompile = 3;
    private const int JqOkNoOutput = -4;
    private const int JqErrorUnknown = 5;

    internal static int Run(IReadOnlyList<string> arguments, CliHost host)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ValidateHost(host);

        try
        {
            var parsed = CliOptionParser.Parse(arguments);
            if (!parsed.IsSuccess)
            {
                WriteError(host, parsed.Error + "\n");
                WriteDie(host);
                return JqErrorSystem;
            }

            var options = parsed.Options!;
            if (options.ImmediateAction != CliImmediateAction.None)
            {
                return RunImmediate(options, host);
            }

            if (options.Program is null && !options.FromFile &&
                (!host.IsInputTerminal || !host.IsOutputTerminal))
            {
                options.Program = ".";
            }

            if (options.Program is null)
            {
                WriteUsage(host.StandardError, keepShort: true);
                return JqErrorSystem;
            }

            return RunFilter(options, host);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (CliFileException exception)
        {
            WriteError(host, exception.Message + "\n");
            return JqErrorSystem;
        }
        catch (IOException exception)
        {
            TryWriteError(host, $"jq: error: writing output failed: {exception.Message}\n");
            return JqErrorSystem;
        }
        catch (UnauthorizedAccessException exception)
        {
            WriteError(host, $"jq: error: {exception.Message}\n");
            return JqErrorSystem;
        }
    }

    private static int RunImmediate(CliOptions options, CliHost host)
    {
        switch (options.ImmediateAction)
        {
            case CliImmediateAction.Help:
                WriteUsage(host.StandardOutput, keepShort: false);
                break;
            case CliImmediateAction.Version:
                WriteOutput(
                    host,
                    $"{ProductName}-{ProductVersion} (jq-{CompatibleJqVersion} compatible)\n");
                break;
            case CliImmediateAction.BuildConfiguration:
                var engine = RuntimeFeature.IsDynamicCodeSupported ? "CoreCLR" : "NativeAOT";
                WriteOutput(
                    host,
                    $"net10.0; {engine}; {RuntimeInformation.RuntimeIdentifier}; jq-{CompatibleJqVersion} compatible\n");
                break;
            case CliImmediateAction.RunTests:
                _ = BuildProgramArguments(options, new CliFileSystem(host.CurrentDirectory));
                return ManagedTestRunner.Run(options, host);
            default:
                throw new InvalidOperationException("Unknown immediate CLI action.");
        }

        return JqOk;
    }

    private static int RunFilter(CliOptions options, CliHost host)
    {
        var fileSystem = new CliFileSystem(host.CurrentDirectory);
        var programOrigin = Path.GetFullPath(host.CurrentDirectory);
        string program;
        if (options.FromFile)
        {
            program = fileSystem.ReadProgram(options.Program!, out var programPath);
            programOrigin = Path.GetDirectoryName(programPath) ?? host.CurrentDirectory;
        }
        else
        {
            program = options.Program!;
        }

        var jqOrigin = Path.GetDirectoryName(
            Path.GetFullPath(host.ExecutablePath, host.CurrentDirectory)) ?? host.CurrentDirectory;
        var libraryPaths = options.LibraryPaths.Count == 0
            ? new[] { "~/.jq", "$ORIGIN/../lib/jq", "$ORIGIN/../lib" }
            : options.LibraryPaths.Select(path => Path.GetFullPath(path, host.CurrentDirectory)).ToArray();
        var home = ResolveHome(host.Environment);
        var resolver = new JqModuleResolver(fileSystem, libraryPaths, jqOrigin, home);

        var arguments = BuildProgramArguments(options, fileSystem);
        using var input = new CliInputReader(
            host.StandardInput,
            host.CurrentDirectory,
            options.Files,
            options.RawInput,
            options.ParserFlags,
            message => WriteError(host, message + "\n"));

        var color = host.SupportsColor &&
            string.IsNullOrEmpty(GetEnvironment(host.Environment, "NO_COLOR"));
        if (options.ForceColor)
        {
            color = true;
        }

        if (options.ForceMonochrome)
        {
            color = false;
        }

        var output = new CliOutput(
            host.StandardOutput,
            host.StandardError,
            options,
            color,
            GetEnvironment(host.Environment, "JQ_COLORS"),
            out var validColors);
        if (!validColors)
        {
            WriteError(host, "Failed to set $JQ_COLORS\n");
        }

        var executionOptions = new JqExecutionOptions
        {
            Environment = host.Environment,
            CancellationToken = host.CancellationToken,
        };
        jq_state? state = libjq.jq_init(executionOptions);
        try
        {
            // src/main.c installs jq_msg_cb functions carrying owned jv values.
            // Keep that representation through the CLI rather than round-tripping
            // through the external JsonElement sink capability.
            libjq.jq_set_debug_cb(state, output.WriteDebug);
            libjq.jq_set_stderr_cb(state, output.WriteStandardError);
            libjq.jq_set_error_cb(state, value => WriteCompileDiagnostic(host, value));
            libjq.jq_set_debug_trace_callback(
                state,
                text => CliOutput.WriteUtf8(host.StandardOutput, text));
            libjq.jq_set_attr(
                state,
                libjq.jv_string("JQ_LIBRARY_PATH"),
                libjq.jv_array(libraryPaths.Select(libjq.jv_string)));
            libjq.jq_set_attr(state, libjq.jv_string("JQ_ORIGIN"), libjq.jv_string(jqOrigin));
            libjq.jq_set_attr(
                state,
                libjq.jv_string("PROGRAM_ORIGIN"),
                libjq.jv_string(programOrigin));

            if (libjq.jq_compile_args(
                    state,
                    program,
                    arguments,
                    resolver,
                    programOrigin,
                    loadUserStartupLibrary: true) == 0)
            {
                return JqErrorCompile;
            }

            if (options.DebugDumpDisassembly)
            {
                BytecodeDisassembler.Write(state, host.StandardOutput);
                WriteOutput(host, "\n");
            }

            // jq-1.8.2 main.c installs jq_util_input_next_input_cb over the
            // same input state consumed by the outer primary-input loop.
            // Return owned jv values directly instead of routing CLI input
            // through the public JsonElement capability adapter and reparsing.
            libjq.jq_set_input_cb(
                state,
                () => input.ReadNextInput(state, host.CancellationToken));

            return RunInputs(state, input, output, options, host);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static int RunInputs(
        jq_state state,
        CliInputReader input,
        CliOutput output,
        CliOptions options,
        CliHost host)
    {
        var result = JqOkNoOutput;
        var lastResult = -1;
        if (options.NullInput)
        {
            result = Process(
                state,
                libjq.jv_null(),
                position: null,
                inputBytes: 0,
                input,
                output,
                options);
        }
        else if (options.Slurp)
        {
            var slurped = ReadSlurped(input, options, output);
            if (slurped.Kind == CliInputKind.Error)
            {
                libjq.jv_free(slurped.Value);
                return input.FailureCount != 0 ? JqErrorSystem : JqErrorUnknown;
            }

            result = Process(
                state,
                slurped.Value,
                slurped.Position,
                slurped.ByteCount,
                input,
                output,
                options);
            if (result <= 0 && result != JqOkNoOutput)
            {
                lastResult = result != JqOkNullKind ? 1 : 0;
            }
        }
        else
        {
            while (true)
            {
                CliInput next;
                try
                {
                    next = input.ReadNext();
                }
                catch (CliFileException exception)
                {
                    WriteError(host, exception.Message + "\n");
                    result = JqErrorSystem;
                    break;
                }

                if (next.Kind == CliInputKind.End)
                {
                    break;
                }

                if (next.Kind == CliInputKind.Error)
                {
                    try
                    {
                        var message = next.Value.Kind == jv_kind.JV_KIND_STRING
                            ? next.Value.StringValue
                            : libjq.jv_dump_string_borrowed(next.Value);
                        output.WriteParseError(message, ignored: options.Sequence);
                        if (!options.Sequence)
                        {
                            result = JqErrorUnknown;
                            break;
                        }
                    }
                    finally
                    {
                        libjq.jv_free(next.Value);
                    }

                    continue;
                }

                result = Process(
                    state,
                    next.Value,
                    next.Position,
                    next.ByteCount,
                    input,
                    output,
                    options);
                if (result <= 0 && result != JqOkNoOutput)
                {
                    lastResult = result != JqOkNullKind ? 1 : 0;
                }

                if (libjq.jq_halted(state) != 0)
                {
                    break;
                }
            }
        }

        if (input.FailureCount != 0)
        {
            result = JqErrorSystem;
        }

        if (!options.ExitStatus)
        {
            return result > 0 ? result : JqOk;
        }

        if (result != JqOkNoOutput)
        {
            return Math.Abs(result);
        }

        return lastResult switch
        {
            -1 => Math.Abs(JqOkNoOutput),
            0 => Math.Abs(JqOkNullKind),
            _ => JqOk,
        };
    }

    private static CliInput ReadSlurped(
        CliInputReader input,
        CliOptions options,
        CliOutput output)
    {
        if (options.RawInput)
        {
            return input.ReadSlurped();
        }

        var values = new List<jv>();
        var totalBytes = 0;
        while (true)
        {
            var next = input.ReadNext();
            switch (next.Kind)
            {
                case CliInputKind.End:
                    return CliInput.FromValue(
                        libjq.jv_array(values),
                        // jq_util_input_next_input() returns the slurped array
                        // only after read_more reaches the final source. The
                        // callback's observable position is therefore that
                        // final input-state filename/line, not the location of
                        // the last successfully parsed value.
                        next.Position ?? new JqInputPosition("<stdin>", 0),
                        totalBytes);
                case CliInputKind.Value:
                    values.Add(next.Value);
                    totalBytes = checked(totalBytes + next.ByteCount);
                    break;
                case CliInputKind.Error:
                    var message = next.Value.Kind == jv_kind.JV_KIND_STRING
                        ? next.Value.StringValue
                        : libjq.jv_dump_string_borrowed(next.Value);
                    output.WriteParseError(message, ignored: options.Sequence);
                    if (!options.Sequence)
                    {
                        foreach (var value in values)
                        {
                            libjq.jv_free(value);
                        }

                        return next;
                    }

                    libjq.jv_free(next.Value);
                    break;
                default:
                    throw new InvalidOperationException("Unknown CLI input kind.");
            }
        }
    }

    private static int Process(
        jq_state state,
        jv inputValue,
        JqInputPosition? position,
        int inputBytes,
        CliInputReader input,
        CliOutput output,
        CliOptions options)
    {
        var resultCode = JqOkNoOutput;
        var finalResult = libjq.jv_invalid();
        try
        {
            libjq.jq_start(state, inputValue, options.JqFlags, position, inputBytes);
            try
            {
                while (true)
                {
                    finalResult = libjq.jq_next(state);
                    if (!finalResult.IsValid)
                    {
                        break;
                    }

                    try
                    {
                        output.WriteResult(finalResult);
                        resultCode = options.RawOutput && finalResult.Kind == jv_kind.JV_KIND_STRING
                            ? JqOk
                            : finalResult.Kind is jv_kind.JV_KIND_FALSE or jv_kind.JV_KIND_NULL
                                ? JqOkNullKind
                                : JqOk;
                    }
                    finally
                    {
                        // jq_next() transfers an owned result to main.c, which
                        // frees it after rendering before requesting another one.
                        libjq.jv_free(finalResult);
                        finalResult = libjq.jv_invalid();
                    }
                }
            }
            catch (JqRuntimeException exception)
            {
                try
                {
                    output.WriteRuntimeError(input.CurrentPosition ?? position, exception);
                    return JqErrorUnknown;
                }
                finally
                {
                    exception.ReleaseErrorValue();
                }
            }
            catch (JqHostCapabilityException exception)
            {
                ExceptionDispatchInfo.Capture(exception.OriginalException).Throw();
                throw;
            }

            if (libjq.jq_halted(state) != 0)
            {
                var exitCode = libjq.jq_get_exit_code(state);
                try
                {
                    resultCode = !exitCode.IsValid
                        ? JqOk
                        : exitCode.Kind == jv_kind.JV_KIND_NUMBER
                            ? unchecked((int)exitCode.NumberValue)
                            : JqErrorUnknown;
                }
                finally
                {
                    libjq.jv_free(exitCode);
                }

                var haltMessage = libjq.jq_get_error_message(state);
                try
                {
                    output.WriteHaltMessage(haltMessage);
                }
                finally
                {
                    libjq.jv_free(haltMessage);
                }

                return resultCode;
            }

            // jq-1.8.2 src/main.c:process() keeps the final invalid jq_next()
            // result and renders its attached value as an uncaught exception.
            // The direct bytecode VM transfers that owned invalid through
            // jq_next(), preserving the same jq-shaped presentation boundary.
            if (libjq.jv_invalid_has_msg(libjq.jv_copy(finalResult)))
            {
                var exception = new JqRuntimeException(
                    libjq.jv_invalid_get_msg(libjq.jv_copy(finalResult)));
                try
                {
                    output.WriteRuntimeError(input.CurrentPosition ?? position, exception);
                    return JqErrorUnknown;
                }
                finally
                {
                    exception.ReleaseErrorValue();
                }
            }

            return resultCode;
        }
        finally
        {
            libjq.jv_free(finalResult);
        }
    }

    internal static jv BuildProgramArguments(CliOptions options, IJqFileSystem fileSystem)
    {
        var named = libjq.jv_object();
        foreach (var argument in options.NamedArguments)
        {
            jv value;
            switch (argument.Kind)
            {
                case CliArgumentKind.String:
                    value = libjq.jv_string(argument.Value);
                    break;
                case CliArgumentKind.Json:
                    value = libjq.jv_parse(argument.Value);
                    break;
                case CliArgumentKind.RawFile:
                case CliArgumentKind.SlurpFile:
                    var file = fileSystem.ReadFile(argument.Value);
                    if (!file.IsSuccess)
                    {
                        var failedKind = argument.Kind == CliArgumentKind.RawFile
                            ? "rawfile"
                            : "slurpfile";
                        throw new CliFileException(
                            $"jq: Bad JSON in --{failedKind} {argument.Name} {argument.Value}: " +
                            CliFileSystem.DescribeFailure(argument.Value, file));
                    }

                    // jq-1.8.2 src/main.c:491 receives both the open result and
                    // parsed value from one jv_load_file call. Preserve that
                    // single-open snapshot after applying the CLI diagnostic above.
                    value = libjq.jv_load_file(
                        argument.Value,
                        argument.Kind == CliArgumentKind.RawFile ? 1 : 0,
                        file);
                    if (!value.IsValid)
                    {
                        var detail = libjq.jv_invalid_get_msg(value);
                        var kind = argument.Kind == CliArgumentKind.RawFile ? "rawfile" : "slurpfile";
                        try
                        {
                            throw new CliFileException(
                                $"jq: Bad JSON in --{kind} {argument.Name} {argument.Value}: " +
                                (detail.Kind == jv_kind.JV_KIND_STRING
                                    ? detail.StringValue
                                    : libjq.jv_dump_string_borrowed(detail)));
                        }
                        finally
                        {
                            libjq.jv_free(detail);
                        }
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unknown named argument kind.");
            }

            named = libjq.jv_object_set(named, argument.Name, value);
        }

        var positional = libjq.jv_array(options.PositionalArguments.Select(argument =>
            argument.Kind == CliArgumentKind.Json
                ? libjq.jv_parse(argument.Value)
                : libjq.jv_string(argument.Value)));
        var args = libjq.jv_object(
        [
            KeyValuePair.Create("positional", positional),
            // jq-1.8.2 src/main.c:624-625: $ARGS.named receives
            // jv_copy(program_arguments); program_arguments keeps its owner
            // and is then extended with ARGS below.
            KeyValuePair.Create("named", libjq.jv_copy(named)),
        ]);
        var programArguments = named;
        programArguments = libjq.jv_object_set(programArguments, "ARGS", args);
        if (!libjq.jv_object_has(programArguments, "JQ_BUILD_CONFIGURATION"))
        {
            programArguments = libjq.jv_object_set(
                programArguments,
                "JQ_BUILD_CONFIGURATION",
                libjq.jv_string(BuildConfigurationValue()));
        }

        return programArguments;
    }

    private static string BuildConfigurationValue()
    {
        var engine = RuntimeFeature.IsDynamicCodeSupported ? "CoreCLR" : "NativeAOT";
        return $"net10.0; {engine}; {RuntimeInformation.RuntimeIdentifier}; jq-{CompatibleJqVersion} compatible";
    }

    private static string ResolveProductVersion()
    {
        // The SDK generates this attribute from $(Version). Reading that concrete
        // attribute is supported by NativeAOT and preserves SemVer prerelease data.
        var informationalVersion = typeof(JqCliApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return "1.0.0";
        }

        // SourceLink commonly appends +<commit>; jq's --version reports the public
        // package version rather than build metadata.
        var buildMetadata = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return buildMetadata < 0
            ? informationalVersion
            : informationalVersion[..buildMetadata];
    }

    private static void WriteCompileDiagnostic(CliHost host, jv value)
    {
        var formatted = libjq.jq_format_error(value);
        try
        {
            WriteError(
                host,
                (formatted.Kind == jv_kind.JV_KIND_STRING
                    ? formatted.StringValue
                    : "jq: error: out of memory") + "\n");
        }
        finally
        {
            libjq.jv_free(formatted);
        }
    }

    private static string? GetEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string name) => environment.TryGetValue(name, out var value) ? value : null;

    // jq-1.8.2 src/util.c:get_home() uses HOME on every host. The WIN32
    // branch then falls back to USERPROFILE and finally HOMEDRIVE + HOMEPATH.
    // Keep this lookup over CliHost's immutable environment snapshot instead
    // of consulting mutable process state after startup.
    internal static string? ResolveHome(
        IReadOnlyDictionary<string, string> environment) =>
        ResolveHome(environment, OperatingSystem.IsWindows());

    internal static string? ResolveHome(
        IReadOnlyDictionary<string, string> environment,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var home = GetEnvironment(environment, "HOME");
        if (home is not null || !isWindows)
        {
            return home;
        }

        home = GetEnvironment(environment, "USERPROFILE");
        if (home is not null)
        {
            return home;
        }

        var homePath = GetEnvironment(environment, "HOMEPATH");
        return homePath is null
            ? null
            : (GetEnvironment(environment, "HOMEDRIVE") ?? string.Empty) + homePath;
    }

    private static void WriteUsage(Stream stream, bool keepShort)
    {
        CliOutput.WriteUtf8(
            stream,
            $"{ProductName} - commandline JSON processor " +
            $"[version {ProductVersion}; jq-{CompatibleJqVersion} compatible]\n\n" +
            $"Usage:\t{ProductName} [options] <jq filter> [file...]\n" +
            $"\t{ProductName} [options] --args <jq filter> [strings...]\n" +
            $"\t{ProductName} [options] --jsonargs <jq filter> [JSON_TEXTS...]\n\n" +
            "dotnetjq processes JSON inputs with jq-compatible filters and writes the results " +
            "as JSON to standard output.\n\n" +
            (keepShort
                ? $"For listing the command options, use {ProductName} --help.\n"
                : FullOptionHelp));
    }

    private const string FullOptionHelp =
        "Command options:\n" +
        "  -n, --null-input          use `null` as the single input value;\n" +
        "  -R, --raw-input           read each line as string instead of JSON;\n" +
        "  -s, --slurp               read all inputs into an array and use it as one value;\n" +
        "  -c, --compact-output      compact instead of pretty-printed output;\n" +
        "  -r, --raw-output          output strings without escapes and quotes;\n" +
        "      --raw-output0         implies -r and outputs NUL after each output;\n" +
        "  -j, --join-output         implies -r and omits output newlines;\n" +
        "  -a, --ascii-output        escape non-ASCII output characters;\n" +
        "  -S, --sort-keys           sort object keys on output;\n" +
        "  -C, --color-output        colorize JSON output;\n" +
        "  -M, --monochrome-output   disable colored output;\n" +
        "      --tab                 use tabs for indentation;\n" +
        "      --indent n            use n spaces for indentation (maximum 7);\n" +
        "      --unbuffered          flush after each output;\n" +
        "      --stream              parse input in streaming fashion;\n" +
        "      --stream-errors       implies --stream and emits parse errors as arrays;\n" +
        "      --seq                 use application/json-seq framing and recovery;\n" +
        "  -f, --from-file           load the filter from a file;\n" +
        "  -L, --library-path dir    search modules from the directory;\n" +
        "      --arg name value      set $name to a string value;\n" +
        "      --argjson name value  set $name to a JSON value;\n" +
        "      --slurpfile name file set $name to an array of JSON values;\n" +
        "      --rawfile name file   set $name to the file's string contents;\n" +
        "      --args                collect remaining arguments as strings;\n" +
        "      --jsonargs            collect remaining arguments as JSON values;\n" +
        "  -e, --exit-status         set exit status based on the final output;\n" +
        "  -b, --binary              request binary standard streams on Windows;\n" +
        "  -V, --version             show the version;\n" +
        "      --build-configuration show the managed build configuration;\n" +
        "  -h, --help                show this help;\n" +
        "      --                    terminate argument processing.\n\n" +
        "Named arguments are also in $ARGS.named; positional arguments are in " +
        "$ARGS.positional. jq language documentation: https://jqlang.org/\n";

    private static void WriteDie(CliHost host) => WriteError(
        host,
        $"Use {ProductName} --help for help with command-line options,\n" +
        "or see the jq manpage, or online docs at https://jqlang.org\n");

    private static void WriteOutput(CliHost host, string text) =>
        CliOutput.WriteUtf8(host.StandardOutput, text);

    private static void WriteError(CliHost host, string text) =>
        CliOutput.WriteUtf8(host.StandardError, text);

    private static void TryWriteError(CliHost host, string text)
    {
        try
        {
            WriteError(host, text);
        }
        catch (IOException)
        {
            // Both output channels are unavailable; preserve the process status.
        }
    }

    private static void ValidateHost(CliHost host)
    {
        ArgumentNullException.ThrowIfNull(host.StandardInput);
        ArgumentNullException.ThrowIfNull(host.StandardOutput);
        ArgumentNullException.ThrowIfNull(host.StandardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.CurrentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.ExecutablePath);
        ArgumentNullException.ThrowIfNull(host.Environment);
    }
}
