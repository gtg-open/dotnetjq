using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DotNetJq.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Arguments and standard streams deliberately have different encoding boundaries.
        // On Windows, .NET supplies the Unicode command line as CLR strings; jq strings
        // subsequently receive their pinned UTF-8 representation through libjq.jv_string().
        // stdin/stdout/stderr remain raw byte streams on every OS, so no text encoding
        // setting or -b conversion is applied here. Native jq makes the same argument-only
        // UTF-16 -> UTF-8 conversion in src/main.c:wmain(), while its default Windows stdio
        // differs through CRT text translation. See porting/WINDOWS_STDIO_CONTRACT.md.
        var onWindows = OperatingSystem.IsWindows();
        using var standardInput = onWindows
            ? Console.OpenStandardInput()
            : OpenUnixStandardStream(StandardInputDescriptor, FileAccess.Read);
        using var standardOutput = onWindows
            ? Console.OpenStandardOutput()
            : OpenUnixStandardStream(StandardOutputDescriptor, FileAccess.Write);
        using var standardError = onWindows
            ? Console.OpenStandardError()
            : OpenUnixStandardStream(StandardErrorDescriptor, FileAccess.Write);
        var isInputTerminal = onWindows
            ? !Console.IsInputRedirected
            : IsATty(StandardInputDescriptor) != 0;
        var isOutputTerminal = onWindows
            ? !Console.IsOutputRedirected
            : IsATty(StandardOutputDescriptor) != 0;

        // jq installs no custom SIGINT/CTRL+C handler in main.c. Retain the OS
        // default shell-visible termination status. .NET can otherwise turn an
        // interrupted stream write into jq's exit 2 before it dispatches its
        // signal action, so the POSIX callback terminates with 128 + SIGINT.
        // ConsoleCancelKeyPress is not
        // used on Unix because initializing that path mutates tty state and emits
        // terminfo keypad-mode bytes before the first jq result.
        using var interruptRegistration = onWindows
            ? null
            : PosixSignalRegistration.Create(
                PosixSignal.SIGINT,
                context =>
                {
                    context.Cancel = true;
                    Environment.Exit(130);
                });
        var host = new CliHost(
            standardInput,
            standardOutput,
            standardError,
            Environment.CurrentDirectory,
            ResolveExecutablePath(),
            isInputTerminal,
            isOutputTerminal,
            SupportsAutomaticColor(isOutputTerminal),
            CaptureEnvironment(),
            CancellationToken.None);
        return JqCliApplication.Run(args, host);
    }

    private static FileStream OpenUnixStandardStream(int descriptor, FileAccess access) =>
        new FileStream(
            new SafeFileHandle((nint)descriptor, ownsHandle: false),
            access,
            bufferSize: 1,
            isAsync: false);

    private static bool SupportsAutomaticColor(bool isOutputTerminal)
    {
        if (!isOutputTerminal)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        // Match jq-1.8.2 main.c: a Windows tty receives automatic ANSI color
        // only when it is a real console and either ANSICON is installed or VT
        // processing can be enabled. -C remains an explicit override.
        var outputHandle = GetStdHandle(StandardOutputHandle);
        if (outputHandle == nint.Zero || outputHandle == InvalidHandle ||
            GetConsoleMode(outputHandle, out var mode) == 0)
        {
            return false;
        }

        return Environment.GetEnvironmentVariable("ANSICON") is not null ||
            SetConsoleMode(outputHandle, mode | EnableVirtualTerminalProcessing) != 0;
    }

    private static Dictionary<string, string> CaptureEnvironment()
    {
        // Windows environment-variable names are case-insensitive, matching the
        // CRT getenv() boundary used by jq.exe. POSIX environment names remain
        // case-sensitive. Preserve that platform contract after taking the host
        // snapshot so jq's HOME/NO_COLOR/JQ_COLORS lookups behave the same way.
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string ResolveExecutablePath()
    {
        // BaseDirectory is the application directory for framework-dependent apphosts,
        // dotnet-tool installs, single-file publication, and NativeAOT. Assembly.Location is
        // deliberately avoided because it is empty under NativeAOT and trips IL3000.
        var executableName = OperatingSystem.IsWindows() ? "dotnetjq.exe" : "dotnetjq";
        return Path.Combine(AppContext.BaseDirectory, executableName);
    }

    private const int StandardOutputHandle = -11;
    private const int StandardInputDescriptor = 0;
    private const int StandardOutputDescriptor = 1;
    private const int StandardErrorDescriptor = 2;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private static readonly nint InvalidHandle = new(-1);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetConsoleMode(nint consoleHandle, out uint mode);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetConsoleMode(nint consoleHandle, uint mode);

    [DllImport("libc", EntryPoint = "isatty", ExactSpelling = true, SetLastError = true)]
    private static extern int IsATty(int descriptor);
}
