using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotNetJq.ParserGen;

internal static class ParserOutputPipeline
{
    private const string ExpectedPackage = "springcomp.gppg";
    private const string ExpectedVersion = "1.2.5";
    private const string ExpectedCommand = "dotnet-gppg";
    private const string PipelineRevision = "parser-output-v3-provenance-normalization";

    public static string Run(
        string managedSource,
        string outputPath,
        string toolCommand,
        int expectedConflicts,
        bool check)
    {
        var identity = ResolveIdentity(toolCommand);
        var sourceSha = Digest(Encoding.UTF8.GetBytes(managedSource));
        var generationKey = Digest(Encoding.UTF8.GetBytes(
            string.Join('\n', PipelineRevision, sourceSha, identity.Package, identity.Version, identity.Command)));

        if (!check && HasGenerationKey(outputPath, generationKey))
            return $"parser output is current; reused {outputPath}";

        GppgVerifier.Verify(managedSource, expectedConflicts, toolCommand);
        var generated = Generate(managedSource, identity);
        var normalized = AddHeader(NormalizeProvenanceComments(generated), sourceSha, generationKey, identity);
        var expectedBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(normalized);

        if (check)
        {
            if (!File.Exists(outputPath) || !File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(expectedBytes))
                throw new GuardrailException($"generated parser is stale: {outputPath}");
            return $"generated parser matches: {outputPath}";
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? ".");
        var temporaryOutput = fullOutputPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryOutput, expectedBytes);
            File.Move(temporaryOutput, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryOutput))
                File.Delete(temporaryOutput);
        }
        return $"generated parser: {outputPath}";
    }

    private static ToolIdentity ResolveIdentity(string toolCommand)
    {
        if (toolCommand != ExpectedCommand)
            throw new GuardrailException(
                $"reproducible parser generation requires local manifest command '{ExpectedCommand}', found '{toolCommand}'");
        var manifest = GppgVerifier.FindToolManifest(Environment.CurrentDirectory) ??
            throw new GuardrailException("cannot locate dotnet-tools.json for parser generation");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        if (!document.RootElement.GetProperty("tools").TryGetProperty(ExpectedPackage, out var package))
            throw new GuardrailException($"tool manifest does not contain {ExpectedPackage}");
        var version = package.GetProperty("version").GetString();
        var commands = package.GetProperty("commands").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        if (version != ExpectedVersion || !commands.Contains(ExpectedCommand, StringComparer.Ordinal))
            throw new GuardrailException(
                $"parser generator must be {ExpectedPackage} {ExpectedVersion} exposing {ExpectedCommand}");
        return new ToolIdentity(manifest, ExpectedPackage, version, ExpectedCommand);
    }

    private static string Generate(string source, ToolIdentity identity)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("jq-gppg-output-");
        try
        {
            var directory = temporaryDirectory.FullName;
            File.Copy(identity.ManifestPath, Path.Combine(directory, "dotnet-tools.json"), overwrite: true);
            var grammarPath = Path.Combine(directory, "managed-parser.y");
            var generatedPath = Path.Combine(directory, "managed-parser.g.cs");
            File.WriteAllText(grammarPath, source);
            File.SetLastWriteTimeUtc(grammarPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
                     {
                         "tool", "run", identity.Command, "--", "/no-info", "/no-lines",
                         "/noThrowOnError", "/out:" + generatedPath, Path.GetFileName(grammarPath),
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ??
                throw new GuardrailException("could not start local dotnet-gppg tool");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            var errors = stderr.Result;
            if (process.ExitCode != 0 || errors.Length != 0 ||
                !File.Exists(generatedPath) || new FileInfo(generatedPath).Length == 0)
            {
                throw new GuardrailException(
                    $"parser generation failed with exit code {process.ExitCode}: {errors.Trim()} {stdout.Result.Trim()}");
            }
            return File.ReadAllText(generatedPath);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    internal static string NormalizeProvenanceComments(string generated)
    {
        generated = Regex.Replace(
            generated,
            @"(?m)^// Input file <.*>\r?\n?",
            "// Input: committed managed parser grammar\n");
        // GPPG capitalizes the opening "Verbatim" marker but lowercases the
        // closing marker whose timestamp is rendered in the host time zone.
        generated = Regex.Replace(
            generated,
            @"(?m)^(\s*// (?:End )?(?i:verbatim) content from) .*$",
            "$1 committed managed parser grammar");
        return generated;
    }

    private static string AddHeader(
        string generated,
        string sourceSha,
        string generationKey,
        ToolIdentity identity) =>
        $"// jq-port-managed-source-sha256: {sourceSha}\n" +
        $"// jq-port-generator: {identity.Package}/{identity.Version} ({identity.Command})\n" +
        $"// jq-port-generation-key-sha256: {generationKey}\n" +
        "// <auto-generated/>\n" +
        "// DOTNETJQ PORT MAP\n" +
        "// Upstream repository: https://github.com/jqlang/jq\n" +
        "// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)\n" +
        "// Upstream file: src/parser.c\n" +
        "// Source of truth: src/DotNetJq/Grammar/parser.y\n" +
        "// Strategy: GENERATED\n" +
        $"// Generator: tools/parser-gen/DotNetJq.ParserGen ({identity.Package}/{identity.Version})\n" +
        "// Target file: src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs\n" +
        "//\n" +
        generated;

    private static bool HasGenerationKey(string outputPath, string generationKey)
    {
        if (!File.Exists(outputPath))
            return false;
        using var reader = File.OpenText(outputPath);
        for (var index = 0; index < 8; index++)
        {
            var line = reader.ReadLine();
            if (line is null)
                return false;
            if (line == "// jq-port-generation-key-sha256: " + generationKey)
                return true;
        }
        return false;
    }

    private static string Digest(byte[] contents) =>
        Convert.ToHexStringLower(SHA256.HashData(contents));

    private sealed record ToolIdentity(
        string ManifestPath,
        string Package,
        string Version,
        string Command);
}
