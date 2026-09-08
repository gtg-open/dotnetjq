using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotNetJq.ParserGen;

internal static class GppgVerifier
{
    private const string Marker = "jq-port: upstream-%precedence";

    public static void Verify(string managedSource, int expectedConflicts, string toolPath)
    {
        var rightVariant = CreateRightAssociativeVariant(managedSource);
        var temporaryDirectory = Directory.CreateTempSubdirectory("jq-gppg-guard-");
        try
        {
            // Reuse the same input/output paths so GPPG's source-name comments
            // cannot create a false difference between the two table builds.
            var left = Generate(temporaryDirectory.FullName, "pass", managedSource, toolPath);
            var right = Generate(temporaryDirectory.FullName, "pass", rightVariant, toolPath);

            if (left.ConflictCount != expectedConflicts)
                throw new GuardrailException(
                    $"GPPG generated {left.ConflictCount} unresolved conflicts; expected {expectedConflicts}");
            if (right.ConflictCount != expectedConflicts)
                throw new GuardrailException(
                    $"right-associative guard variant generated {right.ConflictCount} unresolved conflicts; expected {expectedConflicts}");
            if (left.StandardError.Length != 0 || right.StandardError.Length != 0)
                throw new GuardrailException(
                    "GPPG emitted unexpected stderr diagnostics after conflict accounting: " +
                    (left.StandardError + right.StandardError).Trim());
            if (!left.Generated.AsSpan().SequenceEqual(right.Generated))
                throw new GuardrailException(
                    "a 'jq-port: upstream-%precedence' declaration affects an equal-precedence parser decision; " +
                    "stock GPPG cannot preserve Bison %precedence semantics for this grammar. " +
                    DescribeFirstDifference(left.Generated, right.Generated));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private static string CreateRightAssociativeVariant(string source)
    {
        var replacements = 0;
        var lines = source.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(Marker, StringComparison.Ordinal))
                continue;
            var replaced = new Regex(@"^(\s*)%left\b").Replace(lines[index], "$1%right", 1);
            if (replaced == lines[index])
                throw new GuardrailException($"{Marker} marker must be on the same line as its canonical %left declaration");
            lines[index] = replaced;
            replacements++;
        }
        if (replacements == 0)
            throw new GuardrailException($"managed parser has no {Marker} declarations to verify");
        return string.Join('\n', lines);
    }

    private static GenerationResult Generate(string directory, string name, string source, string toolPath)
    {
        directory = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
        var grammarPath = Path.Combine(directory, "grammar.y");
        var outputPath = Path.Combine(directory, "parser.g.cs");
        File.WriteAllText(grammarPath, source);
        File.SetLastWriteTimeUtc(grammarPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var isDll = toolPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var isToolCommand = !isDll &&
                            !toolPath.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                            !toolPath.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        var toolManifest = isToolCommand ? FindToolManifest(Environment.CurrentDirectory) : null;
        if (isToolCommand && toolManifest is null)
            throw new GuardrailException($"cannot locate dotnet-tools.json for local tool command '{toolPath}'");
        if (toolManifest is not null)
        {
            // `dotnet tool run` has no --tool-manifest option. Copy the small
            // manifest into the temp directory so both tool resolution and
            // GPPG's implicit .lst output remain isolated there.
            File.Copy(toolManifest, Path.Combine(directory, "dotnet-tools.json"), overwrite: true);
        }
        var workingDirectory = directory;
        var processStart = new ProcessStartInfo
        {
            FileName = isDll || isToolCommand ? "dotnet" : toolPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        processStart.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        if (isDll)
            processStart.ArgumentList.Add(Path.GetFullPath(toolPath));
        else if (isToolCommand)
        {
            processStart.ArgumentList.Add("tool");
            processStart.ArgumentList.Add("run");
            processStart.ArgumentList.Add(toolPath);
            processStart.ArgumentList.Add("--");
        }
        processStart.ArgumentList.Add("/no-info");
        processStart.ArgumentList.Add("/no-lines");
        // Do not use /conflicts: Springcomp.GPPG 1.2.5 returns from its code
        // generator immediately after opening that file, leaving it and /out
        // empty. Normal generation reports unresolved conflicts on stderr.
        processStart.ArgumentList.Add("/noThrowOnError");
        processStart.ArgumentList.Add("/out:" + outputPath);
        processStart.ArgumentList.Add(Path.GetRelativePath(workingDirectory, grammarPath));

        using var process = Process.Start(processStart) ??
            throw new GuardrailException($"could not start GPPG at {toolPath}");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        var combinedDiagnostics = standardError.Result + standardOutput.Result;
        var hasErrors = Regex.IsMatch(
            combinedDiagnostics,
            @"(?im)(?:\berror\s+\d+\s*:|GPPG\s*-\s*Unrecognized option|Unhandled exception)");
        var outputExists = File.Exists(outputPath);
        var outputLength = outputExists ? new FileInfo(outputPath).Length : 0;
        if (process.ExitCode != 0 || hasErrors || !outputExists || outputLength == 0)
            throw new GuardrailException(
                $"GPPG {name} pass failed with exit code {process.ExitCode} " +
                $"(diagnostic errors={hasErrors}, output exists={outputExists}, output bytes={outputLength}): " +
                combinedDiagnostics.Trim());

        var conflicts = Regex.Count(
            standardError.Result,
            @"(?m)^(?:Shift/Reduce|Reduce/Reduce) conflict, state\b");
        return new GenerationResult(File.ReadAllBytes(outputPath), conflicts, standardError.Result);
    }

    private sealed record GenerationResult(byte[] Generated, int ConflictCount, string StandardError);

    internal static string? FindToolManifest(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "dotnet-tools.json");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(directory.FullName, ".config", "dotnet-tools.json");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string DescribeFirstDifference(byte[] leftBytes, byte[] rightBytes)
    {
        var left = System.Text.Encoding.UTF8.GetString(leftBytes).Split('\n');
        var right = System.Text.Encoding.UTF8.GetString(rightBytes).Split('\n');
        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            if (left[index] != right[index])
                return $"First generated difference at line {index + 1}: left='{left[index].Trim()}', right='{right[index].Trim()}'.";
        }
        return $"Generated lengths differ: left={leftBytes.Length}, right={rightBytes.Length}.";
    }
}
