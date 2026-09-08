using System.Text;

namespace DotNetJq.Cli.Tests;

internal static class CliAssertions
{
    public static void Equal(CliResult expected, CliResult actual)
    {
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        BytesEqual("stdout", expected.StandardOutput, actual.StandardOutput);
        BytesEqual("stderr", expected.StandardError, actual.StandardError);
    }

    public static void Equal(
        CliResult actual,
        int exitCode,
        string standardOutput = "",
        string standardError = "") =>
        Equal(
            new CliResult(
                exitCode,
                Encoding.UTF8.GetBytes(standardOutput),
                Encoding.UTF8.GetBytes(standardError)),
            actual);

    public static void Equal(
        CliResult actual,
        int exitCode,
        ReadOnlySpan<byte> standardOutput,
        ReadOnlySpan<byte> standardError = default) =>
        Equal(new CliResult(exitCode, standardOutput.ToArray(), standardError.ToArray()), actual);

    private static void BytesEqual(string streamName, byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual))
            return;

        Assert.Fail(
            $"{streamName} bytes differ.{Environment.NewLine}" +
            $"Expected UTF-8: {Render(expected)}{Environment.NewLine}" +
            $"Actual UTF-8:   {Render(actual)}{Environment.NewLine}" +
            $"Expected hex: {Convert.ToHexString(expected)}{Environment.NewLine}" +
            $"Actual hex:   {Convert.ToHexString(actual)}");
    }

    private static string Render(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
}
