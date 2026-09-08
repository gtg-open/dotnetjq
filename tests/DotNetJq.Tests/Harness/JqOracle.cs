using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DotNetJq.Tests.Harness;

internal sealed record JqOracleResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    internal bool Succeeded => ExitCode == 0;

    internal IReadOnlyList<string> OutputLines => SplitLines(StandardOutput);

    private static string[] SplitLines(string value)
    {
        if (value.Length == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}

/// <summary>Runs the pinned native jq executable strictly as a test oracle.</summary>
internal static class JqOracle
{
    internal const string OracleEnvironmentVariable = "DOTNETJQ_ORACLE";
    internal static string DefaultOraclePath => UpstreamTestFile.ResolveRepositoryPath(
        "artifacts/test-assets/jq-1.8.2/oracle/" + (OperatingSystem.IsWindows() ? "jq.exe" : "jq"));
    internal const string ExpectedVersion = "jq-1.8.2";

    internal static Task<JqOracleResult> ExecuteAsync(
        string program,
        string input,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(input);

        var commandArguments = new List<string> { "--compact-output", "--monochrome-output" };
        if (arguments is not null)
        {
            commandArguments.AddRange(arguments);
        }

        // ArgumentList and the explicit option terminator keep the program out of a shell
        // and allow filters beginning with '-' to be passed without reinterpretation.
        commandArguments.Add("--");
        commandArguments.Add(program);
        return RunAsync(commandArguments, input, workingDirectory, environment, cancellationToken);
    }

    internal static Task<JqOracleResult> ExecuteFixtureCaseAsync(
        UpstreamTestCase testCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        var arguments = new List<string>();
        string? workingDirectory = null;
        if (UpstreamTestFile.TryResolveUpstreamRoot(out var upstreamRoot))
        {
            workingDirectory = upstreamRoot;
            arguments.Add("--library-path");
            arguments.Add(Path.Combine(upstreamRoot, "tests", "modules"));
        }

        return ExecuteAsync(
            testCase.Program,
            testCase.Input ?? "null",
            arguments,
            workingDirectory,
            cancellationToken: cancellationToken);
    }

    internal static Task<JqOracleResult> ReadVersionAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(
            ["--version"],
            standardInput: null,
            workingDirectory: null,
            environment: null,
            cancellationToken);

    internal static string ResolveExecutable()
    {
        if (TryResolveExecutable(out var executable))
        {
            return executable;
        }

        var configured = Environment.GetEnvironmentVariable(OracleEnvironmentVariable);
        var candidate = string.IsNullOrWhiteSpace(configured) ? DefaultOraclePath : configured;
        throw new FileNotFoundException(
            $"The jq 1.8.2 test oracle was not found at '{candidate}'. " +
            $"Set {OracleEnvironmentVariable} to the pinned jq executable.",
            candidate);
    }

    internal static bool TryResolveExecutable([NotNullWhen(true)] out string? executable)
    {
        var configured = Environment.GetEnvironmentVariable(OracleEnvironmentVariable);
        var candidate = string.IsNullOrWhiteSpace(configured) ? DefaultOraclePath : configured;
        var fullPath = Path.GetFullPath(candidate);

        if (File.Exists(fullPath))
        {
            executable = fullPath;
            return true;
        }

        executable = null;
        return false;
    }

    private static async Task<JqOracleResult> RunAsync(
        IEnumerable<string> arguments,
        string? standardInput,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(),
            WorkingDirectory = ResolveWorkingDirectory(workingDirectory),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["NO_COLOR"] = "1";

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start jq oracle '{startInfo.FileName}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // jq can reject a filter and close stdin before this tiny test input is
                // written. Its exit code and stderr remain the authoritative outcome, so a
                // broken stdin pipe must not replace the compile diagnostic with a harness
                // failure.
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new JqOracleResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(workingDirectory);
        }

        return UpstreamTestFile.TryResolveUpstreamRoot(out var upstreamRoot)
            ? upstreamRoot
            : Directory.GetCurrentDirectory();
    }
}
