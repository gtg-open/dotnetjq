using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.CompatibilityRunner;

/// <summary>
/// Runs fixture cases outside the reporting process. A worker announces and flushes each
/// case before evaluating it, so the parent can attribute an uncatchable CLR failure (for
/// example, StackOverflowException or a process-killing OOM) and resume with the next case.
/// </summary>
internal static class WorkerProcess
{
    private const string WorkerSwitch = "--internal-worker-v1";
    private const string ProtocolPrefix = "DOTNETJQ-WORKER-V1";
    private const string SelfTestCrashIndexVariable =
        "DOTNETJQ_COMPATIBILITY_RUNNER_SELFTEST_CRASH_INDEX";

    internal static bool IsInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(WorkerSwitch, StringComparison.Ordinal);

    internal static int Run(IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count != 4 ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
                start < 0 ||
                end < start)
            {
                Console.Error.WriteLine("invalid internal worker invocation");
                return 2;
            }

            var fixturePath = Path.GetFullPath(args[1]);
            using var reader = File.OpenText(fixturePath);
            var cases = FixtureFile.Parse(reader, fixturePath);
            if (end > cases.Count)
            {
                Console.Error.WriteLine("internal worker range is past the end of the fixture");
                return 2;
            }

            var resolver = CreateFixtureModuleResolver(fixturePath);
            var executionOptions = CreateFixtureExecutionOptions(fixturePath);

            for (var index = start; index < end; index++)
            {
                WriteLine("START", index);
                SimulateCrashForSelfTest(index);
                var failure = Program.Execute(cases[index], resolver, executionOptions);
                if (failure is null)
                {
                    WriteLine("PASS", index);
                    continue;
                }

                WriteLine(
                    "FAIL",
                    index,
                    [
                        Encode(failure.Phase),
                        failure.Details.Count.ToString(CultureInfo.InvariantCulture),
                        .. failure.Details.Select(Encode),
                    ]);
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    internal static void RunIsolated(
        string fixturePath,
        IReadOnlyList<FixtureCase> cases,
        int start,
        int count,
        Action<FixtureCase, CaseFailure?> completed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(completed);

        var end = checked(start + count);
        var next = start;
        while (next < end)
        {
            using var process = Start(fixturePath, next, end);
            var standardError = process.StandardError.ReadToEndAsync();
            int? started = null;

            while (process.StandardOutput.ReadLine() is { } line)
            {
                var message = ParseMessage(line);
                if (message.Index != next)
                {
                    throw ProtocolError(
                        $"worker reported fixture index {message.Index.ToString(CultureInfo.InvariantCulture)} " +
                        $"while index {next.ToString(CultureInfo.InvariantCulture)} was expected");
                }

                switch (message.Kind)
                {
                    case "START" when started is null:
                        started = next;
                        break;
                    case "PASS" when started == next:
                        completed(cases[next], null);
                        next++;
                        started = null;
                        break;
                    case "FAIL" when started == next:
                        completed(cases[next], message.Failure);
                        next++;
                        started = null;
                        break;
                    default:
                        throw ProtocolError($"worker sent invalid {message.Kind} sequence");
                }
            }

            process.WaitForExit();
            var errorText = standardError.GetAwaiter().GetResult();
            if (next == end)
            {
                return;
            }

            if (started == next && process.ExitCode != 0)
            {
                completed(cases[next], ProcessCrash(process.ExitCode, errorText));
                next++;
                continue;
            }

            var exitDescription = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            var detail = string.IsNullOrWhiteSpace(errorText)
                ? string.Empty
                : $" Worker stderr: {Abbreviate(OneLine(errorText))}";
            throw ProtocolError(
                $"worker exited with code {exitDescription} without completing fixture index " +
                $"{next.ToString(CultureInfo.InvariantCulture)}.{detail}");
        }
    }

    private static Process Start(string fixturePath, int start, int end)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidDataException("Cannot locate the compatibility runner executable.");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidDataException("Cannot locate the compatibility runner assembly.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add(WorkerSwitch);
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add(start.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(end.ToString(CultureInfo.InvariantCulture));

        return Process.Start(startInfo) ??
            throw new InvalidDataException("Failed to start the compatibility worker process.");
    }

    private static JqModuleResolver CreateFixtureModuleResolver(string fixturePath)
    {
        var fixtureDirectory = Path.GetDirectoryName(fixturePath) ??
            throw new InvalidDataException("The fixture path has no parent directory.");
        var moduleDirectory = Path.GetFullPath(Path.Combine(fixtureDirectory, "modules"));
        var fileSystem = new JqFileSystem(moduleDirectory, [moduleDirectory]);
        return new JqModuleResolver(
            fileSystem,
            [moduleDirectory],
            jqOrigin: moduleDirectory);
    }

    private static JqExecutionOptions CreateFixtureExecutionOptions(string fixturePath) =>
        Path.GetFileName(fixturePath).Equals("man.test", StringComparison.Ordinal)
            ? new JqExecutionOptions
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PAGER"] = "less",
                },
            }
            : JqExecutionOptions.Default;

    private static WorkerMessage ParseMessage(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length < 3 ||
            !fields[0].Equals(ProtocolPrefix, StringComparison.Ordinal) ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw ProtocolError($"worker sent an invalid protocol line: {Abbreviate(line)}");
        }

        return fields[1] switch
        {
            "START" when fields.Length == 3 => new WorkerMessage("START", index, null),
            "PASS" when fields.Length == 3 => new WorkerMessage("PASS", index, null),
            "FAIL" when TryParseFailure(fields, out var failure) =>
                new WorkerMessage("FAIL", index, failure),
            _ => throw ProtocolError($"worker sent an invalid protocol line: {Abbreviate(line)}"),
        };
    }

    private static CaseFailure ProcessCrash(int exitCode, string standardError)
    {
        var details = new List<string>
        {
            $"isolated worker terminated before returning a result (exit code " +
            $"{exitCode.ToString(CultureInfo.InvariantCulture)})",
        };
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            details.Add($"worker stderr: {Abbreviate(OneLine(standardError))}");
        }

        return new CaseFailure("process-crash", details);
    }

    private static bool TryParseFailure(string[] fields, out CaseFailure failure)
    {
        failure = null!;
        if (fields.Length < 5 ||
            !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var detailCount) ||
            detailCount < 0 ||
            fields.Length != 5 + detailCount)
        {
            return false;
        }

        var details = new string[detailCount];
        for (var index = 0; index < detailCount; index++)
        {
            details[index] = Decode(fields[5 + index]);
        }

        failure = new CaseFailure(Decode(fields[3]), details);
        return true;
    }

    private static void SimulateCrashForSelfTest(int index)
    {
        var configured = Environment.GetEnvironmentVariable(SelfTestCrashIndexVariable);
        if (configured is not null &&
            int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var crashIndex) &&
            crashIndex == index)
        {
            Environment.Exit(137);
        }
    }

    private static void WriteLine(string kind, int index, params string[] payload)
    {
        var fields = new string[3 + payload.Length];
        fields[0] = ProtocolPrefix;
        fields[1] = kind;
        fields[2] = index.ToString(CultureInfo.InvariantCulture);
        payload.CopyTo(fields, 3);
        Console.Out.WriteLine(string.Join('\t', fields));
        Console.Out.Flush();
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException exception)
        {
            throw ProtocolError("worker sent invalid base64 data", exception);
        }
    }

    private static InvalidDataException ProtocolError(string message, Exception? inner = null) =>
        new($"Compatibility worker protocol error: {message}", inner);

    private static string OneLine(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\n', ' ').Trim();

    private static string Abbreviate(string value)
    {
        const int limit = 1_000;
        return value.Length <= limit
            ? value
            : value[..limit] + $"...[truncated {value.Length - limit} chars]";
    }

    private sealed record WorkerMessage(string Kind, int Index, CaseFailure? Failure);
}
