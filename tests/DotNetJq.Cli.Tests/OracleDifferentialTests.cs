using System.Text;

namespace DotNetJq.Cli.Tests;

public sealed class OracleDifferentialTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    [Trait("Category", "OracleDifferential")]
    public async Task ArgumentInputFormattingCompileHaltAndExitCorpusMatchesPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();

        foreach (var testCase in PinnedCliCases.AllTextCases)
        {
            await AssertDifferentialAsync(
                oracle,
                new DifferentialInvocation(
                    testCase.Name,
                    testCase.Arguments,
                    Encoding.UTF8.GetBytes(testCase.StandardInput)));
        }

        foreach (var invocation in NonTextAndFailureInvocations())
            await AssertDifferentialAsync(oracle, invocation);
    }

    [Fact]
    [Trait("Category", "OracleDifferential")]
    public async Task FilenameModuleAndFileErrorCorpusMatchesPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();

        var unicodeOne = await WriteAsync(directory, "one value.json", "{\"source\":1}");
        var unicodeTwo = await WriteAsync(directory, "δεύτερο-😎.json", "{\"source\":2}");
        await AssertDifferentialAsync(
            oracle,
            new("Unicode and space filenames", ["-c", ".", unicodeOne, unicodeTwo], []));

        var splitOne = await WriteAsync(directory, "split-one.json", "[1,");
        var splitTwo = await WriteAsync(directory, "split-two.json", "2]");
        await AssertDifferentialAsync(
            oracle,
            new("parser state crosses files", ["-c", ".", splitOne, splitTwo], []));

        var numberOne = await WriteAsync(directory, "number-one.json", "1");
        var numberTwo = await WriteAsync(directory, "number-two.json", "2");
        await AssertDifferentialAsync(
            oracle,
            new("number token crosses files", ["-c", ".", numberOne, numberTwo], []));

        var optionFilter = await WriteAsync(directory, "-filter.jq", "{answer: 40 + 2}");
        _ = optionFilter;
        await AssertDifferentialAsync(
            oracle,
            new("option-shaped from-file program", ["-ncf", "--", "-filter.jq"], [], directory.Path));

        var originFilter = await WriteAsync(directory, "program origin/main.jq", ". + 1");
        var input = await WriteAsync(directory, "input.json", "41");
        await AssertDifferentialAsync(
            oracle,
            new("from-file program", ["-c", "-f", originFilter, input], []));

        // jq's Windows wmain converts argv to UTF-8, but jv_load_file opens
        // module paths through narrow open(). Keep that native filesystem
        // limitation out of this differential case; the managed Unicode-path
        // contract is covered directly by CliFileAndModuleTests.
        var libraryDirectory = OperatingSystem.IsWindows()
            ? "library path"
            : "library path μ";
        var libraryModule = await WriteAsync(
            directory,
            $"{libraryDirectory}/helper.jq",
            "def helper: 42;");
        await AssertDifferentialAsync(
            oracle,
            new(
                "explicit library path",
                ["-n", "-c", "-L", Path.GetDirectoryName(libraryModule)!, "include \"helper\"; helper"],
                []));

        var data = await WriteAsync(directory, "data file.json", "{\"n\":1}\n{\"n\":2}\n");
        await AssertDifferentialAsync(
            oracle,
            new(
                "slurpfile and rawfile",
                ["-n", "-c", "--slurpfile", "foo", data, "--rawfile", "bar", data, "{$foo, $bar}"],
                []));

        var absentReplacement = directory.File("absent replacement.json");
        await AssertDifferentialAsync(
            oracle,
            new(
                "first duplicate file argument wins without opening replacement",
                ["-n", "-c", "--slurpfile", "values", data, "--slurpfile", "values", absentReplacement, "$values"],
                []));

        var absentInput = directory.File("absent input.json");
        await AssertDifferentialAsync(
            oracle,
            new("null input bypasses filenames", ["-n", "-c", ".", absentInput], []));
        await AssertDifferentialAsync(
            oracle,
            new("missing input after prior output", ["-c", ".", numberOne, absentInput], []));

        var nulFilter = directory.File("nul.jq");
        await File.WriteAllBytesAsync(nulFilter, "42\0ignored"u8.ToArray(), TestContext.Current.CancellationToken);
        await AssertDifferentialAsync(
            oracle,
            new("NUL in program file", ["-n", "-f", nulFilter], []));

        var homeJq = await WriteAsync(directory, "home/.jq", "def home_definition: \"loaded\";");
        await AssertDifferentialAsync(
            oracle,
            new(
                "default home jq program",
                ["-n", "-r", "home_definition"],
                [],
                Environment: new Dictionary<string, string?> { ["HOME"] = Path.GetDirectoryName(homeJq)! }));
    }

    [Fact]
    [Trait("Category", "OracleDifferential")]
    public async Task UsageFailurePreservesJqDiagnosticsButNamesDotnetjqInGuidance()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        var arguments = new[] { "--definitely-not-an-option" };
        var oracleResult = await CliProcess.RunAsync(
            oracle,
            arguments,
            TestContext.Current.CancellationToken);
        var subjectResult = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            oracleResult,
            2,
            "",
            "jq: Unknown option --definitely-not-an-option\n" +
            "Use jq --help for help with command-line options,\n" +
            "or see the jq manpage, or online docs at https://jqlang.org\n");
        CliAssertions.Equal(
            subjectResult,
            2,
            "",
            "jq: Unknown option --definitely-not-an-option\n" +
            "Use dotnetjq --help for help with command-line options,\n" +
            "or see the jq manpage, or online docs at https://jqlang.org\n");
    }

    private static IEnumerable<DifferentialInvocation> NonTextAndFailureInvocations()
    {
        yield return new(
            "raw-output0 framing",
            ["-n", "--raw-output0", "\"alpha\", \"β\""],
            []);
        yield return new(
            "raw-output0 partial failure",
            ["-n", "--raw-output0", "\"a\", \"c\\u0000d\", \"b\""],
            []);
        yield return new(
            "forced color",
            ["-Ccn", "[{\"a\":true,\"b\":false},\"abc\",123,null]"],
            []);
        yield return new(
            "custom JQ_COLORS",
            ["-Ccn", "."],
            [],
            Environment: new Dictionary<string, string?> { ["JQ_COLORS"] = "4;31" });
        yield return new(
            "explicit empty JQ_COLORS component",
            ["-Ccn", "."],
            [],
            Environment: new Dictionary<string, string?> { ["JQ_COLORS"] = ":" });
        yield return new(
            "eight truecolor JQ_COLORS components with trailing separator",
            ["-Cn", "[{a:true}]"],
            [],
            Environment: new Dictionary<string, string?> {
                ["JQ_COLORS"] =
                    "38;2;255;173;173:38;2;255;214;165:38;2;253;255;182:" +
                    "38;2;202;255;191:38;2;155;246;255:38;2;160;196;255:" +
                    "38;2;189;178;255:38;2;255;198;255:",
            });
        yield return new(
            "invalid JQ_COLORS",
            ["-Ccn", "."],
            [],
            Environment: new Dictionary<string, string?> { ["JQ_COLORS"] = "invalid" });
        yield return new("compile failure", ["-n", "if"], []);
        yield return new("JSON parse failure", ["."], "foobar"u8.ToArray());
        yield return new("runtime failure", [".foo"], "1"u8.ToArray());
        yield return new(
            "seq resynchronizes after parse error",
            ["--seq", "-c", "."],
            "\u001e1\nnot-json\n\u001e2\n"u8.ToArray());
    }

    private static async Task AssertDifferentialAsync(CliCommand oracle, DifferentialInvocation invocation)
    {
        var expected = await CliProcess.RunAsync(
            oracle,
            invocation.Arguments,
            invocation.StandardInput,
            invocation.WorkingDirectory,
            invocation.Environment,
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            invocation.Arguments,
            invocation.StandardInput,
            invocation.WorkingDirectory,
            invocation.Environment,
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            CliAssertions.Equal(expected, actual);
        }
        catch (Exception exception) when (exception is Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException(
                $"Differential CLI case '{invocation.Name}' diverged from pinned jq 1.8.2.\n{exception.Message}");
        }
    }

    private static async Task<string> WriteAsync(
        TemporaryDirectory directory,
        string relativePath,
        string contents)
    {
        var path = directory.File(relativePath);
        await File.WriteAllTextAsync(path, contents, Utf8NoBom, TestContext.Current.CancellationToken);
        return path;
    }

    private sealed record DifferentialInvocation(
        string Name,
        string[] Arguments,
        byte[] StandardInput,
        string? WorkingDirectory = null,
        IReadOnlyDictionary<string, string?>? Environment = null);
}
