// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jq_test.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jq_test.c
// Strategy: PORT
// Target file: tests/DotNetJq.Tests/Harness/UpstreamTestFile.cs
// Substitutions: xUnit orchestration and managed fixture records replace the C test driver.
// Known differences: CLI-only flag and process orchestration are outside the library harness.

using System.Diagnostics.CodeAnalysis;

namespace DotNetJq.Tests.Harness;

internal sealed record UpstreamExpectedFailure(
    bool CheckMessage,
    IReadOnlyList<string> MessageLines)
{
    internal string Message => string.Join('\n', MessageLines);
}

internal sealed record UpstreamTestCase(
    int Number,
    int ProgramLine,
    string Program,
    string? Input,
    IReadOnlyList<string> ExpectedOutputs,
    UpstreamExpectedFailure? ExpectedFailure)
{
    internal bool MustFail => ExpectedFailure is not null;
}

/// <summary>
/// Reads the group format consumed by upstream's <c>run_jq_tests</c> function.
/// </summary>
internal static class UpstreamTestFile
{
    internal const string UpstreamEnvironmentVariable = "DOTNETJQ_UPSTREAM";
    internal static string DefaultUpstreamRoot => ResolveRepositoryPath("upstream/jq");

    internal static string ResolveRepositoryPath(string relativePath, string? startDirectory = null)
    {
        // Test runners may start inside bin/ rather than at the repository root.
        foreach (var start in startDirectory is null
                     ? new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
                     : new[] { startDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                {
                    return Path.GetFullPath(Path.Combine(directory.FullName, relativePath));
                }
            }
        }

        return Path.GetFullPath(Path.Combine(startDirectory ?? Environment.CurrentDirectory, relativePath));
    }

    internal static IReadOnlyList<UpstreamTestCase> ReadFixture(string fixtureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureName);

        var upstreamRoot = ResolveUpstreamRoot();
        var testsRoot = Path.GetFullPath(Path.Combine(upstreamRoot, "tests"));
        var relativeName = fixtureName.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (relativeName.StartsWith($"tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            relativeName = relativeName[("tests".Length + 1)..];
        }

        var fixturePath = Path.GetFullPath(Path.Combine(testsRoot, relativeName));
        var relativePath = Path.GetRelativePath(testsRoot, fixturePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The fixture must be beneath the upstream tests directory.", nameof(fixtureName));
        }

        using var reader = File.OpenText(fixturePath);
        return Parse(reader, fixturePath);
    }

    internal static IReadOnlyList<UpstreamTestCase> Parse(
        TextReader reader,
        string sourceName = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var lines = ReadLines(reader);
        var cases = new List<UpstreamTestCase>();
        var index = 0;
        var mustFail = false;
        var checkFailureMessage = false;

        while (index < lines.Count)
        {
            var (lineNumber, line) = lines[index++];
            if (IsSeparator(line))
            {
                continue;
            }

            if (TryReadFailureMarker(line, out var markerChecksMessage))
            {
                mustFail = true;
                checkFailureMessage = markerChecksMessage;
                continue;
            }

            var program = line;
            if (mustFail)
            {
                var messages = ReadUntilSeparator(lines, ref index);
                cases.Add(new UpstreamTestCase(
                    cases.Count + 1,
                    lineNumber,
                    program,
                    Input: null,
                    ExpectedOutputs: Array.Empty<string>(),
                    new UpstreamExpectedFailure(checkFailureMessage, messages)));
                mustFail = false;
                checkFailureMessage = false;
                continue;
            }

            if (index == lines.Count)
            {
                throw Malformed(sourceName, lineNumber, "test program has no input line");
            }

            // jq_test.c consumes the input line unconditionally. In particular, a blank or
            // comment-looking input is not treated as a group separator at this position.
            var input = lines[index++].Text;
            var outputs = ReadUntilSeparator(lines, ref index);
            cases.Add(new UpstreamTestCase(
                cases.Count + 1,
                lineNumber,
                program,
                input,
                outputs,
                ExpectedFailure: null));
        }

        if (mustFail)
        {
            throw Malformed(sourceName, lines.Count, "failure marker has no following program");
        }

        return cases;
    }

    internal static string ResolveUpstreamRoot()
    {
        if (TryResolveUpstreamRoot(out var upstreamRoot))
        {
            return upstreamRoot;
        }

        var configured = Environment.GetEnvironmentVariable(UpstreamEnvironmentVariable);
        var candidate = string.IsNullOrWhiteSpace(configured) ? DefaultUpstreamRoot : configured;
        throw new DirectoryNotFoundException(
            $"The pinned jq checkout was not found at '{candidate}'. " +
            $"Set {UpstreamEnvironmentVariable} to the jq-1.8.2 checkout root.");
    }

    internal static bool TryResolveUpstreamRoot([NotNullWhen(true)] out string? upstreamRoot)
    {
        var configured = Environment.GetEnvironmentVariable(UpstreamEnvironmentVariable);
        var candidate = string.IsNullOrWhiteSpace(configured) ? DefaultUpstreamRoot : configured;
        var fullPath = Path.GetFullPath(candidate);

        if (Directory.Exists(Path.Combine(fullPath, "tests")))
        {
            upstreamRoot = fullPath;
            return true;
        }

        upstreamRoot = null;
        return false;
    }

    private static List<(int LineNumber, string Text)> ReadLines(TextReader reader)
    {
        var lines = new List<(int LineNumber, string Text)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add((lines.Count + 1, line));
        }

        return lines;
    }

    private static List<string> ReadUntilSeparator(
        List<(int LineNumber, string Text)> lines,
        ref int index)
    {
        var values = new List<string>();
        while (index < lines.Count)
        {
            var line = lines[index++].Text;
            if (IsSeparator(line))
            {
                break;
            }

            values.Add(line);
        }

        return values;
    }

    private static bool IsSeparator(string line)
    {
        var index = 0;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }

        return index == line.Length || line[index] == '#' || line[index] == '\0';
    }

    private static bool TryReadFailureMarker(string line, out bool checkMessage)
    {
        checkMessage = line.Equals("%%FAIL", StringComparison.Ordinal);
        return checkMessage || line.Equals("%%FAIL IGNORE MSG", StringComparison.Ordinal);
    }

    private static InvalidDataException Malformed(string sourceName, int lineNumber, string message) =>
        new($"Malformed upstream test file '{sourceName}' at line {lineNumber}: {message}.");
}
