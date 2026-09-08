using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotNetJq.DifferentialProbe;

internal static class Program
{
    private const string ExpectedOracleSha256 =
        "b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f";
    private const int ExpectedRegexCorpusCount = 4_980;
    private const int ExpectedRegexCategoryCount = 33;
    private static readonly Encoding ReportEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<int> Main(string[] args)
    {
        if (!Options.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine("error: " + error);
            Options.WriteUsage(Console.Error);
            return 2;
        }

        if (options.Help)
        {
            Options.WriteUsage(Console.Out);
            return 0;
        }

        var oraclePath = Path.GetFullPath(options.OraclePath);
        if (!File.Exists(oraclePath))
        {
            Console.Error.WriteLine("error: pinned jq oracle not found at " + oraclePath);
            return 2;
        }

        string oracleSha256;
        await using (var oracleStream = File.OpenRead(oraclePath))
        {
            oracleSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(oracleStream, CancellationToken.None).ConfigureAwait(false))
                .ToLowerInvariant();
        }
        if (!oracleSha256.Equals(ExpectedOracleSha256, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"error: expected oracle SHA-256 {ExpectedOracleSha256}, got {oracleSha256}");
            return 2;
        }

        string oracleVersion;
        try
        {
            oracleVersion = await ProbeRunner.ReadOracleVersionAsync(
                oraclePath,
                TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
        {
            Console.Error.WriteLine("error: cannot verify jq oracle: " + exception.Message);
            return 2;
        }

        if (!oracleVersion.Equals(ProbeRunner.ExpectedOracleVersion, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"error: expected oracle {ProbeRunner.ExpectedOracleVersion}, got {oracleVersion}");
            return 2;
        }

        if (options.CorpusName == "general")
        {
            try
            {
                var oracleBuiltins = await ProbeRunner.ReadOracleBuiltinsAsync(
                    oraclePath,
                    TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
                    CancellationToken.None).ConfigureAwait(false);
                var expectedBuiltins = BuiltinProbeCatalog.ExpectedSignatures.ToHashSet(StringComparer.Ordinal);
                if (oracleBuiltins.Count != Corpus.ExpectedBuiltinSignatureCount ||
                    !oracleBuiltins.SetEquals(expectedBuiltins))
                {
                    Console.Error.WriteLine(
                        "error: checked-in builtin probe inventory does not exactly match pinned jq 1.8.2 builtins/0");
                    return 2;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
            {
                Console.Error.WriteLine("error: cannot verify jq builtin inventory: " + exception.Message);
                return 2;
            }
        }

        var corpus = options.CorpusName switch
        {
            "general" => Corpus.Generate(options.Seed, options.Count),
            "regex" => GenerateRegexCorpus(options.Seed, options.Count),
            _ => throw new InvalidOperationException("unreachable corpus selection"),
        };
        if (options.CaseId is { } caseId)
        {
            corpus = corpus.Where(testCase => testCase.Id == caseId).ToArray();
            if (corpus.Count == 0)
            {
                Console.Error.WriteLine($"error: case {caseId.ToString(CultureInfo.InvariantCulture)} is outside the corpus");
                return 2;
            }
        }

        var mismatches = new List<ProbeMismatch>();
        var completed = 0;
        foreach (var testCase in corpus)
        {
            var mismatch = await ProbeRunner.CompareAsync(
                testCase,
                oraclePath,
                TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
                CancellationToken.None).ConfigureAwait(false);
            completed++;
            if (mismatch is null)
            {
                continue;
            }

            mismatches.Add(mismatch);
            WriteMismatch(Console.Out, mismatch);
        }

        Console.WriteLine(
            $"TOTAL seed={options.Seed.ToString(CultureInfo.InvariantCulture)} " +
            $"cases={completed.ToString(CultureInfo.InvariantCulture)} " +
            $"matched={(completed - mismatches.Count).ToString(CultureInfo.InvariantCulture)} " +
            $"mismatched={mismatches.Count.ToString(CultureInfo.InvariantCulture)}");
        if (options.ReportPath is { } configuredReportPath)
        {
            var reportPath = Path.GetFullPath(configuredReportPath);
            var report = BuildReport(options, oraclePath, oracleVersion, oracleSha256, corpus, mismatches);
            if (options.CheckReport)
            {
                if (!await ReportMatchesAsync(reportPath, report).ConfigureAwait(false))
                {
                    Console.Error.WriteLine("error: differential report is stale: " + reportPath);
                    return 1;
                }

                Console.WriteLine("report=" + reportPath + " (verified)");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, report, ReportEncoding).ConfigureAwait(false);
                Console.WriteLine("report=" + reportPath);
            }
        }
        else
        {
            Console.WriteLine("report=disabled; pass --report PATH to write one");
        }

        return mismatches.Count == 0 ? 0 : 1;
    }

    private static IReadOnlyList<ProbeCase> GenerateRegexCorpus(int seed, int count)
    {
        var declaredCount = RegexCorpus.DefaultCount;
        if (declaredCount != ExpectedRegexCorpusCount)
        {
            throw new InvalidOperationException(
                $"Regex differential corpus declares {declaredCount} unique cases; " +
                $"expected {ExpectedRegexCorpusCount}.");
        }

        var corpus = RegexCorpus.Generate(seed, count);
        var inventory = count == declaredCount
            ? corpus
            : RegexCorpus.Generate(RegexCorpus.DefaultSeed, declaredCount);
        var categoryCount = inventory
            .Select(static testCase => testCase.Category)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (categoryCount != ExpectedRegexCategoryCount)
        {
            throw new InvalidOperationException(
                $"Regex differential corpus declares {categoryCount} categories; " +
                $"expected {ExpectedRegexCategoryCount}.");
        }

        return corpus;
    }

    private static async Task<bool> ReportMatchesAsync(string reportPath, string expected)
    {
        if (!File.Exists(reportPath))
        {
            return false;
        }

        var actual = await File.ReadAllTextAsync(reportPath, ReportEncoding).ConfigureAwait(false);
        return NormalizeOraclePath(actual).Equals(
            NormalizeOraclePath(expected),
            StringComparison.Ordinal);
    }

    private static string NormalizeOraclePath(string report)
    {
        const string Prefix = "- Oracle: `";
        const string Replacement = "- Oracle: `<verified-pinned-oracle>`";
        var prefixStart = report.IndexOf(Prefix, StringComparison.Ordinal);
        if (prefixStart < 0)
        {
            return report;
        }

        var valueStart = prefixStart + Prefix.Length;
        var valueEnd = report.IndexOf('`', valueStart);
        return valueEnd < 0
            ? report
            : report[..prefixStart] + Replacement + report[(valueEnd + 1)..];
    }

    private static string BuildReport(
        Options options,
        string oraclePath,
        string oracleVersion,
        string oracleSha256,
        IReadOnlyList<ProbeCase> corpus,
        List<ProbeMismatch> mismatches)
    {
        var report = new StringBuilder();
        report.AppendLine(options.CorpusName == "regex"
            ? "# Regex Differential Probe Report"
            : "# Differential Probe Report");
        report.AppendLine();
        report.AppendLine("Managed DotNetJq was compared with the pinned official jq 1.8.2 executable.");
        report.AppendLine("No mismatch was excluded, allowlisted, or converted into a pass.");
        report.AppendLine(
            "Compile/runtime diagnostic payloads were compared after removing only the native " +
            "process executable/location envelope and terminal compile-count wrapper.");
        report.AppendLine();
        report.AppendLine("## Configuration");
        report.AppendLine();
        AppendInvariantLine(report, $"- Seed: `{options.Seed}`");
        AppendInvariantLine(report, $"- Cases: `{corpus.Count}`");
        AppendInvariantLine(report, $"- Corpus: `{options.CorpusName}`");
        AppendInvariantLine(
            report,
            $"- Unique filter/input pairs: `{corpus.Select(testCase => (testCase.Filter, testCase.Input)).Distinct().Count()}`");
        if (options.CorpusName == "general")
        {
            var coveredSignatures = Corpus.CoveredBuiltinSignatures(corpus);
            AppendInvariantLine(
                report,
                $"- Explicit public builtin signatures covered: `{coveredSignatures.Count}` / `{Corpus.ExpectedBuiltinSignatureCount}`");
            report.AppendLine("- Builtin inventory source: pinned jq 1.8.2 `builtins/0`, verified at run time; every signature has a representative invocation");
            AppendInvariantLine(report, $"- Declared categories covered: `{corpus.Select(testCase => testCase.Category).Distinct(StringComparer.Ordinal).Count()}` / `{Corpus.ExpectedDeclaredCategoryCount}`");
            if (corpus.Count >= Corpus.DefaultCount)
            {
                AppendInvariantLine(report, $"- Declared category filters covered: `{Corpus.DeclaredFilterCount}` / `{Corpus.ExpectedDeclaredFilterCount}`");
                AppendInvariantLine(report, $"- Declared JSON inputs covered: `{Corpus.DeclaredInputCount}` / `{Corpus.ExpectedDeclaredInputCount}`");
            }
        }
        AppendInvariantLine(report, $"- Oracle: `{EscapeInline(oraclePath)}`");
        AppendInvariantLine(report, $"- Oracle version: `{oracleVersion}`");
        AppendInvariantLine(report, $"- Oracle SHA-256: `{oracleSha256}`");
        AppendInvariantLine(report, $"- Per-oracle-case timeout: `{options.TimeoutMilliseconds} ms`");
        report.AppendLine("- Managed limits: 1 s execution timeout, 256 outputs, 1 MiB input/output, " +
                          "256 recursion depth, 100,000 execution transitions, 500 ms regex timeout");
        report.AppendLine("- Failure diagnostics: exact payload, source excerpt, carets, punctuation, values, and ordering; " +
                          "only the native process envelope and terminal compile-count wrapper are excluded");
        report.AppendLine();
        report.AppendLine("Category distribution:");
        report.AppendLine();
        report.AppendLine("| Category | Cases |");
        report.AppendLine("|---|---:|");
        foreach (var group in corpus.GroupBy(testCase => testCase.Category).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            AppendInvariantLine(report, $"| {group.Key} | {group.Count()} |");
        }

        report.AppendLine();
        report.AppendLine("## Summary");
        report.AppendLine();
        AppendInvariantLine(report, $"- Matched: `{corpus.Count - mismatches.Count}`");
        AppendInvariantLine(report, $"- Mismatched: `{mismatches.Count}`");
        if (mismatches.Count != 0)
        {
            report.AppendLine();
            report.AppendLine("Mismatch distribution:");
            report.AppendLine();
            report.AppendLine("| Category | Mismatches |");
            report.AppendLine("|---|---:|");
            foreach (var group in mismatches
                         .GroupBy(mismatch => mismatch.Case.Category)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                AppendInvariantLine(report, $"| {group.Key} | {group.Count()} |");
            }
        }

        report.AppendLine();
        report.AppendLine("## Mismatches");
        report.AppendLine();
        if (mismatches.Count == 0)
        {
            report.AppendLine("None.");
        }
        else
        {
            foreach (var mismatch in mismatches)
            {
                AppendMismatch(report, mismatch, options);
            }
        }

        if (options.CorpusName == "regex")
        {
            AppendRegexProxyBoundary(report);
        }

        return report.ToString();
    }

    private static void AppendRegexProxyBoundary(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("## Managed proxy boundary");
        report.AppendLine();
        report.AppendLine(
            "The passing corpus exercises the managed jq-shaped regex surface, including recursive and " +
            "relative subexpression calls, capture levels and conditionals, `\\G`, `\\K`, nested absent ranges, " +
            "class-context/control escapes, fixed-width lookbehind calls/backreferences/reduction, built-in " +
            "and content callouts, text segments, pinned Unicode properties/case folds, and exact invalid-pattern " +
            "diagnostics. It does not claim " +
            "native Oniguruma API, bytecode, allocator, or engine identity because those are outside " +
            "jq's public regex surface. The complete declared corpus reported no mismatch; " +
            "that bounded result is not a proof over inputs outside the declared inventory.");
    }

    private static void AppendMismatch(StringBuilder report, ProbeMismatch mismatch, Options options)
    {
        AppendInvariantLine(report, $"### Case {mismatch.Case.Id:D4}: {mismatch.Case.Category}");
        report.AppendLine();
        report.AppendLine("Minimal replay:");
        report.AppendLine();
        report.AppendLine("```bash");
        report.AppendLine(
            "dotnet run --project tools/DotNetJq.DifferentialProbe -- " +
            $"--corpus {options.CorpusName} " +
            $"--seed {options.Seed.ToString(CultureInfo.InvariantCulture)} " +
            $"--count {options.Count.ToString(CultureInfo.InvariantCulture)} " +
            $"--case {mismatch.Case.Id.ToString(CultureInfo.InvariantCulture)}");
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("Filter:");
        report.AppendLine();
        report.AppendLine("```jq");
        report.AppendLine(mismatch.Case.Filter);
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("Input:");
        report.AppendLine();
        report.AppendLine("```json");
        report.AppendLine(mismatch.Case.Input);
        report.AppendLine("```");
        report.AppendLine();
        foreach (var reason in mismatch.Reasons)
        {
            report.AppendLine("- " + reason);
        }

        AppendInvariantLine(report, $"- Official: `{mismatch.Official.Kind}`; outputs `{EscapeInline(SerializeOutputs(mismatch.Official.Outputs))}`");
        AppendInvariantLine(report, $"- Managed: `{mismatch.Managed.Kind}`; outputs `{EscapeInline(SerializeOutputs(mismatch.Managed.Outputs))}`");
        if (mismatch.Official.Diagnostic.Length != 0)
        {
            report.AppendLine("- Official diagnostic: `" + EscapeDiagnostic(mismatch.Official.Diagnostic) + "`");
        }

        if (mismatch.Managed.Diagnostic.Length != 0)
        {
            report.AppendLine("- Managed diagnostic: `" + EscapeDiagnostic(mismatch.Managed.Diagnostic) + "`");
        }

        report.AppendLine();
    }

    private static void WriteMismatch(TextWriter writer, ProbeMismatch mismatch)
    {
        writer.WriteLine(
            $"MISMATCH case={mismatch.Case.Id:D4} category={mismatch.Case.Category} " +
            $"filter={JsonSerializer.Serialize(mismatch.Case.Filter)} " +
            $"input={JsonSerializer.Serialize(mismatch.Case.Input)}");
        foreach (var reason in mismatch.Reasons)
        {
            writer.WriteLine("  " + reason);
        }
    }

    private static string SerializeOutputs(IReadOnlyList<string> outputs) =>
        JsonSerializer.Serialize(outputs);

    private static string EscapeInline(string value) => value.Replace("`", "\\`", StringComparison.Ordinal);

    private static string EscapeDiagnostic(string value) =>
        EscapeInline(Abbreviate(value))
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AppendInvariantLine(StringBuilder builder, FormattableString value) =>
        builder.AppendLine(value.ToString(CultureInfo.InvariantCulture));

    private static string Abbreviate(string value) =>
        value.Length <= 600 ? value : value[..600] + "...[truncated]";
}

internal sealed record Options(
    string CorpusName,
    int Seed,
    int Count,
    int TimeoutMilliseconds,
    string OraclePath,
    string? ReportPath,
    bool CheckReport,
    int? CaseId,
    bool Help)
{
    internal static bool TryParse(string[] args, out Options options, out string? error)
    {
        var corpusName = "general";
        var seed = Corpus.DefaultSeed;
        var count = Corpus.DefaultCount;
        var seedConfigured = false;
        var countConfigured = false;
        var timeout = 2_000;
        var oracle = Environment.GetEnvironmentVariable("DOTNETJQ_ORACLE");
        if (string.IsNullOrWhiteSpace(oracle))
        {
            oracle = DefaultOraclePath;
        }
        string? report = null;
        var checkReport = false;
        int? caseId = null;
        var help = false;

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "--help" or "-h")
            {
                help = true;
                continue;
            }

            if (name == "--check-report")
            {
                checkReport = true;
                continue;
            }

            if (++index >= args.Length)
            {
                options = default!;
                error = name + " requires a value";
                return false;
            }

            var value = args[index];
            switch (name)
            {
                case "--corpus" when value is "general" or "regex":
                    corpusName = value;
                    break;
                case "--seed" when TryPositiveOrZero(value, out seed):
                    seedConfigured = true;
                    break;
                case "--count" when TryPositive(value, out count):
                    countConfigured = true;
                    break;
                case "--timeout-ms" when TryPositive(value, out timeout):
                    break;
                case "--oracle":
                    oracle = value;
                    break;
                case "--report":
                    report = value;
                    break;
                case "--case" when TryPositive(value, out var parsedCase):
                    caseId = parsedCase;
                    break;
                default:
                    options = default!;
                    error = "unknown option or invalid value: " + name;
                    return false;
            }
        }

        if (corpusName == "regex")
        {
            if (!seedConfigured)
            {
                seed = RegexCorpus.DefaultSeed;
            }

            if (!countConfigured)
            {
                count = RegexCorpus.DefaultCount;
            }
        }

        if (checkReport && report is null)
        {
            options = default!;
            error = "--check-report requires an explicit --report PATH";
            return false;
        }

        options = new Options(corpusName, seed, count, timeout, oracle, report, checkReport, caseId, help);
        error = null;
        return true;
    }

    internal static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: DotNetJq.DifferentialProbe [--corpus general|regex] [--seed N] [--count N] [--case N]");
        writer.WriteLine("       [--timeout-ms N] [--oracle PATH] [--report PATH] [--check-report]");
    }

    private static string DefaultOraclePath
    {
        get
        {
            var relative = "artifacts/test-assets/jq-1.8.2/oracle/" +
                (OperatingSystem.IsWindows() ? "jq.exe" : "jq");
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                    {
                        return Path.Combine(directory.FullName, relative);
                    }
                }
            }

            return Path.GetFullPath(relative);
        }
    }

    private static bool TryPositive(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

    private static bool TryPositiveOrZero(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
}
