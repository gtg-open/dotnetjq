using System.Diagnostics;
using System.Text;

namespace DotNetJq.Cli.Tests;

internal sealed record CliCommand(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public static CliCommand Executable(string path) => new(path, []);

    public static CliCommand ManagedAssembly(string path) =>
        new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", [path]);
}

internal sealed record CliResult(int ExitCode, byte[] StandardOutput, byte[] StandardError)
{
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);

    public string StandardErrorText => Encoding.UTF8.GetString(StandardError);
}

internal static class CliProcess
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static Task<CliResult> RunAsync(
        CliCommand command,
        IEnumerable<string> arguments,
        string? standardInput = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            command,
            arguments,
            standardInput is null ? [] : Encoding.UTF8.GetBytes(standardInput),
            workingDirectory,
            environment,
            timeout,
            cancellationToken);

    public static Task<CliResult> RunAsync(
        CliCommand command,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync(command, arguments, ReadOnlyMemory<byte>.Empty, cancellationToken: cancellationToken);

    public static async Task<CliResult> RunAsync(
        CliCommand command,
        IEnumerable<string> arguments,
        ReadOnlyMemory<byte> standardInput,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = workingDirectory ?? CliTestEnvironment.RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in command.PrefixArguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // Keep oracle and managed invocations deterministic and immune to the
        // developer's interactive jq configuration.
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["TZ"] = "UTC";
        startInfo.Environment.Remove("JQ_COLORS");
        startInfo.Environment.Remove("NO_COLOR");
        startInfo.Environment.Remove("LD_PRELOAD");
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null)
                    startInfo.Environment.Remove(name);
                else
                    startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start CLI process '{command.FileName}'.");

        using var timeoutCancellation = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        await using var stdout = new MemoryStream();
        await using var stderr = new MemoryStream();
        try
        {
            var stdoutCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellation.Token);
            var stderrCopy = process.StandardError.BaseStream.CopyToAsync(stderr, cancellation.Token);

            if (!standardInput.IsEmpty)
                await process.StandardInput.BaseStream.WriteAsync(standardInput, cancellation.Token);
            await process.StandardInput.BaseStream.FlushAsync(cancellation.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellation.Token);
            await Task.WhenAll(stdoutCopy, stderrCopy);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"CLI process '{command.FileName}' did not finish within {(timeout ?? DefaultTimeout).TotalSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new CliResult(process.ExitCode, stdout.ToArray(), stderr.ToArray());
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the timeout and the kill request.
        }
    }
}
