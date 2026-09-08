namespace DotNetJq.Cli.Tests;

public sealed class DeveloperModeCompatibilityTests
{
    private const string LifecycleSuffix =
        "Test jq_state: .[]\n" +
        "Test jq_state: .[] | if .%2 == 0 then halt_error else . end\n" +
        "Test jq_compile_args with array args\n" +
        "  subtest: 42\n" +
        "  subtest: $val\n" +
        "  subtest: $x + $y\n" +
        "  subtest: $a + $b + $c\n" +
        "  subtest: $x * $y\n" +
        "Test jq recompile on same state\n" +
        "Test jq exhaust and reuse\n";

    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    public void FullCompatibilityModeRequiresTheExplicitValueOne(
        string? configured,
        bool expected) =>
        Assert.Equal(expected, CliTestEnvironment.IsFullCompatibilityRequired(configured));

    [Fact]
    public void ConfiguredUpstreamWinsAndStrictModeNeverUsesTheRepositoryFallback()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var repository = Path.Combine(directory.Path, "dotnetjq");
        var checkout = Path.Combine(repository, "upstream", "jq");
        var configured = Path.Combine(directory.Path, "configured-jq");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(configured);

        Assert.Equal(
            Path.GetFullPath(configured),
            CliTestEnvironment.ResolveUpstreamCheckout(
                configured,
                strict: false,
                repository,
                out var configuredFailure));
        Assert.Null(configuredFailure);

        Assert.Null(CliTestEnvironment.ResolveUpstreamCheckout(
            configured: null,
            strict: true,
            repository,
            out var strictFailure));
        Assert.Contains("DOTNETJQ_UPSTREAM", strictFailure, StringComparison.Ordinal);

        Assert.Equal(
            Path.GetFullPath(checkout),
            CliTestEnvironment.ResolveUpstreamCheckout(
                configured: null,
                strict: false,
                repository,
                out var localFailure));
        Assert.Null(localFailure);
    }

    [Fact]
    public void PinnedFixtureValidationReportsMissingPathsAndRejectsContentDrift()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var tests = Path.Combine(directory.Path, "tests");
        Directory.CreateDirectory(Path.Combine(tests, "modules"));
        File.WriteAllText(Path.Combine(tests, "jq.test"), string.Empty);

        Assert.False(CliTestEnvironment.TryValidatePinnedFixtures(
            directory.Path,
            ["jq.test", "onig.test"],
            out var failure));
        Assert.Contains("onig.test", failure, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(tests, "onig.test"), string.Empty);
        Assert.False(CliTestEnvironment.TryValidatePinnedFixtures(
            directory.Path,
            ["jq.test", "onig.test"],
            out failure));
        Assert.Contains("SHA-256 mismatches", failure, StringComparison.Ordinal);
        Assert.Contains("jq.test", failure, StringComparison.Ordinal);
        Assert.Contains("onig.test", failure, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(tests, "modules", "not-in-jq-1.8.2.jq"), string.Empty);
        Assert.False(CliTestEnvironment.TryValidatePinnedFixtures(
            directory.Path,
            ["jq.test", "onig.test"],
            out failure));
        Assert.Contains("unexpected module files", failure, StringComparison.Ordinal);
        Assert.Contains("not-in-jq-1.8.2.jq", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredSubjectMustIdentifyANonEmptyRunnableFile()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var missing = directory.File(OperatingSystem.IsWindows() ? "missing.exe" : "missing");
        Assert.Null(CliTestEnvironment.ResolveConfiguredSubject(missing, out var missingFailure));
        Assert.Contains("does not identify an existing file", missingFailure, StringComparison.Ordinal);

        var subject = directory.File(OperatingSystem.IsWindows() ? "dotnetjq.exe" : "dotnetjq");
        File.WriteAllText(subject, string.Empty);
        Assert.Null(CliTestEnvironment.ResolveConfiguredSubject(subject, out var emptyFailure));
        Assert.Contains("empty file", emptyFailure, StringComparison.Ordinal);

        File.WriteAllText(subject, "placeholder");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(subject, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Null(CliTestEnvironment.ResolveConfiguredSubject(subject, out var modeFailure));
            Assert.Contains("not executable", modeFailure, StringComparison.Ordinal);
            File.SetUnixFileMode(
                subject,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var resolved = CliTestEnvironment.ResolveConfiguredSubject(subject, out var failure);
        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(subject), resolved.FileName);
        Assert.Empty(resolved.PrefixArguments);
        Assert.Empty(failure);
    }

    [Fact]
    public async Task RunTestsFixtureGrammarAndLifecycleOutputMatchPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        const string fixture =
            ".+1\n" +
            "1\n" +
            "2\n" +
            "\n" +
            "%%FAIL\n" +
            "if\n" +
            "jq: error: syntax error, unexpected end of file at <top-level>, line 1, column 1:\n" +
            "    if\n" +
            "    ^^\n" +
            "\n";

        var expected = await CliProcess.RunAsync(
            oracle,
            ["--run-tests"],
            fixture,
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--run-tests"],
            fixture,
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(expected, actual);
        Assert.Equal(0, actual.ExitCode);
        Assert.EndsWith(LifecycleSuffix, actual.StandardOutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTestsSelectionAndErrorStatusesMatchPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        const string fixture = ".\n1\n1\n\n.+1\n1\n2\n\n";
        var fixturePath = directory.File("selection.test");
        File.WriteAllText(fixturePath, fixture);
        string[][] cases =
        [
            ["--run-tests", "--skip", "1", "--take", "1"],
            ["--run-tests", "--skip", "99"],
            ["--run-tests", "--take", "0"],
            ["--run-tests", "--skip"],
            ["--run-tests", "--take"],
        ];

        foreach (var arguments in cases)
        {
            var invocation = arguments.Length == 2
                ? arguments
                : [.. arguments, fixturePath];
            var expected = await CliProcess.RunAsync(
                oracle,
                invocation,
                cancellationToken: TestContext.Current.CancellationToken);
            var actual = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                invocation,
                cancellationToken: TestContext.Current.CancellationToken);
            CliAssertions.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task RunTestsMalformedAndRuntimeOutcomesMatchPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        string[] fixtures =
        [
            ".\n1\n2\n\n",
            "empty\nnull\n1\n\n",
            "1,2\nnull\n1\n\n",
            ".\nnot-json\n\n",
            ".\nnull\nnot-json\n\n",
            ".",
            "error(\"x\")\nnull\n\n",
            "error(\"x\")\nnull\n1\n\n",
        ];

        foreach (var fixture in fixtures)
        {
            await AssertDifferentialAsync(oracle, ["--run-tests"], fixture);
        }
    }

    [Fact]
    public async Task RunTestsFailureMarkerVariantsMatchPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        string[] fixtures =
        [
            "%%FAIL IGNORE MSG\nif\nignored expected text\n\n",
            "%%FAIL\nif\nwrong message\nremaining text\n\n",
            "%%FAIL\nif\njq: error:\n\n",
            "%%FAIL\n.\nignored\n\n",
            "%%FAIL\n",
            "%%FAIL",
        ];

        foreach (var fixture in fixtures)
        {
            await AssertDifferentialAsync(oracle, ["--run-tests"], fixture);
        }
    }

    [Fact]
    public async Task RunTestsAtoiSelectionEdgesMatchPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        const string fixture = ".\n1\n1\n\n.+1\n1\n2\n\n";
        var fixturePath = directory.File("atoi.test");
        File.WriteAllText(fixturePath, fixture);
        string[][] arguments =
        [
            ["--run-tests", "--skip", "0"],
            ["--run-tests", "--skip", "2"],
            ["--run-tests", "--skip", "3"],
            ["--run-tests", "--skip", "-1"],
            ["--run-tests", "--skip", "not-a-number"],
            ["--run-tests", "--skip", "1tail", "--take", "+1more"],
            ["--run-tests", "--take", "-7"],
        ];

        foreach (var invocation in arguments)
        {
            await AssertDifferentialAsync(
                oracle,
                [.. invocation, fixturePath],
                standardInput: null);
        }
    }

    [Fact]
    public async Task RunTestsProcessesInterleavedFilesAndTreatsTailTokensAsFilenames()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var first = directory.File("first.test");
        var second = directory.File("second.test");
        var third = directory.File("third.test");
        const string fixture = ".\n1\n1\n\n.+1\n1\n2\n\n";
        File.WriteAllText(first, fixture);
        File.WriteAllText(second, fixture);
        File.WriteAllText(third, fixture);

        await AssertDifferentialAsync(
            oracle,
            ["--run-tests", first, "--skip", "1", second, "--take", "0", third],
            standardInput: null,
            workingDirectory: directory.Path);

        foreach (var filenameToken in new[] { "-", "--skip=1", "--", "missing.test" })
        {
            await AssertDifferentialAsync(
                oracle,
                ["--run-tests", filenameToken],
                standardInput: null,
                workingDirectory: directory.Path);
        }
    }

    [Fact]
    public async Task RunTestsUsesExplicitModulesAndHomeStartupLibrary()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var modules = directory.File("modules/placeholder");
        modules = Path.GetDirectoryName(modules)!;
        File.WriteAllText(Path.Combine(modules, "m.jq"), "def value: \"module\";\n");
        var moduleFixture = directory.File("module.test");
        File.WriteAllText(moduleFixture, "include \"m\"; value\nnull\n\"module\"\n\n");
        var startup = directory.File("home/.jq");
        File.WriteAllText(startup, "def home_value: \"home\";\n");
        var homeFixture = directory.File("home.test");
        File.WriteAllText(homeFixture, "home_value\nnull\n\"home\"\n\n");
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = Path.GetDirectoryName(startup),
            ["USERPROFILE"] = null,
        };

        await AssertDifferentialAsync(
            oracle,
            ["-L", modules, "--run-tests", moduleFixture],
            standardInput: null,
            workingDirectory: directory.Path,
            environment: environment);
        await AssertDifferentialAsync(
            oracle,
            ["--run-tests", homeFixture],
            standardInput: null,
            workingDirectory: directory.Path,
            environment: environment);

        if (OperatingSystem.IsWindows())
        {
            var homeDirectory = Path.GetDirectoryName(startup)!;
            var homeDrive = Path.GetPathRoot(homeDirectory)!
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var homePath = homeDirectory[homeDrive.Length..];
            await AssertDifferentialAsync(
                oracle,
                ["--run-tests", homeFixture],
                standardInput: null,
                workingDirectory: directory.Path,
                environment: new Dictionary<string, string?>
                {
                    ["HOME"] = null,
                    ["USERPROFILE"] = null,
                    ["HOMEDRIVE"] = homeDrive,
                    ["HOMEPATH"] = homePath,
                });
        }
    }

    [Fact]
    public async Task EveryVerboseFlagEnablesTheSameManagedTestMode()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        const string fixture = ".+1\n1\n2\n\n";
        var flags = new[] { "--debug-dump-disasm", "--debug-trace", "--debug-trace=all" };
        var oracleResults = new List<CliResult>();
        var managedResults = new List<CliResult>();
        foreach (var flag in flags)
        {
            oracleResults.Add(await CliProcess.RunAsync(
                oracle,
                [flag, "--run-tests"],
                fixture,
                cancellationToken: TestContext.Current.CancellationToken));
            managedResults.Add(await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                [flag, "--run-tests"],
                fixture,
                cancellationToken: TestContext.Current.CancellationToken));
        }

        foreach (var result in oracleResults.Concat(managedResults))
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            Assert.Contains("Disassembly:\n", result.StandardOutputText, StringComparison.Ordinal);
            Assert.Contains("<backtracking>\n", result.StandardOutputText, StringComparison.Ordinal);
        }

        CliAssertions.Equal(oracleResults[0], oracleResults[1]);
        CliAssertions.Equal(oracleResults[0], oracleResults[2]);
        CliAssertions.Equal(managedResults[0], managedResults[1]);
        CliAssertions.Equal(managedResults[0], managedResults[2]);
    }

    [Fact]
    public async Task DebugDumpDisassemblyMatchesPinnedJq182ByteForByte()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        string[] programs =
        [
            ".+1",
            "def f($x): $x + 1; f(.)",
            ". as $x | def f(g): g | $x; f(. + 1)",
        ];

        foreach (var program in programs)
        {
            var arguments = new[] { "--debug-dump-disasm", "-nc", program };
            var expected = await CliProcess.RunAsync(
                oracle,
                arguments,
                cancellationToken: TestContext.Current.CancellationToken);
            var actual = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                arguments,
                cancellationToken: TestContext.Current.CancellationToken);

            CliAssertions.Equal(expected, actual);
            Assert.Contains("0000 TOP\n", actual.StandardOutputText, StringComparison.Ordinal);
            Assert.Contains(" RET\n\n", actual.StandardOutputText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunTestsStillValidatesPrecedingNamedArguments()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var missing = directory.File("missing.json");
        foreach (var option in new[] { "--rawfile", "--slurpfile" })
        {
            await AssertDifferentialAsync(
                oracle,
                [option, "value", missing, "--run-tests"],
                string.Empty,
                directory.Path);
        }

        var expected = await CliProcess.RunAsync(
            oracle,
            ["--argjson", "value", "not-json", "--run-tests"],
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--argjson", "value", "not-json", "--run-tests"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(
            expected.StandardErrorText.Replace(
                "Use jq --help",
                "Use dotnetjq --help",
                StringComparison.Ordinal),
            actual.StandardErrorText);
    }

    [Fact]
    public async Task AllOfficialRunTestsFixturesMatchPinnedJq182EndToEnd()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        string[] fixtureNames =
        [
            "jq.test",
            "man.test",
            "onig.test",
            "manonig.test",
            "base64.test",
            "uri.test",
            "optional.test",
        ];
        var upstream = CliTestEnvironment.RequirePinnedUpstreamFixtures(fixtureNames);
        var fixtures = fixtureNames
            .Select(name => Path.Combine(upstream, "tests", name))
            .ToArray();
        var modules = Path.Combine(upstream, "tests", "modules");

        using var home = CliTestEnvironment.CreateTemporaryDirectory();
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = home.Path,
            ["PAGER"] = "less",
        };
        var total = 0;
        foreach (var fixture in fixtures)
        {
            var arguments = new[] { "-L", modules, "--run-tests", fixture };
            var expected = await CliProcess.RunAsync(
                oracle,
                arguments,
                environment: environment,
                timeout: TimeSpan.FromSeconds(60),
                cancellationToken: TestContext.Current.CancellationToken);
            var actual = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                arguments,
                environment: environment,
                timeout: TimeSpan.FromSeconds(60),
                cancellationToken: TestContext.Current.CancellationToken);

            CliAssertions.Equal(expected, actual);
            var summary = actual.StandardOutputText
                .Split('\n')
                .Single(line => line.Contains(" tests passed (", StringComparison.Ordinal));
            total += int.Parse(
                summary.AsSpan(0, summary.IndexOf(' ', StringComparison.Ordinal)),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(879, total);
    }

    [Fact]
    public async Task DebugTraceMatchesPinnedObservableIdentityShape()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        var expected = await CliProcess.RunAsync(
            oracle,
            ["--debug-trace", "-nc", "."],
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--debug-trace", "-nc", "."],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            actual,
            0,
            "0000 TOP\t\n" +
            "0001 RET\tnull\n" +
            "null\n" +
            "0001 RET\t\t<backtracking>\n");
        Assert.Equal(0, expected.ExitCode);
        Assert.Empty(expected.StandardError);
        Assert.Contains(" TOP\t", expected.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains(" RET\tnull", expected.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("null\n", expected.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("<backtracking>\n", expected.StandardOutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebugTraceAllIncludesBytecodeVmStack()
    {
        var basic = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--debug-trace", "-nc", ".+1"],
            cancellationToken: TestContext.Current.CancellationToken);
        var detailed = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--debug-trace=all", "-nc", ".+1"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, basic.ExitCode);
        Assert.Equal(0, detailed.ExitCode);
        Assert.Empty(basic.StandardError);
        Assert.Empty(detailed.StandardError);
        Assert.DoesNotContain(" || ", basic.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains(" || ", detailed.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("0007 RET\t1\n1\n", basic.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("0007 RET\t1\n1\n", detailed.StandardOutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseRunTestsUsesBytecodeDisassemblyAndExecutionTrace()
    {
        const string fixture = ".+1\n1\n2\n\n";
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--debug-trace", "--run-tests"],
            fixture,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains(
            "Disassembly:\n" +
            "  0000 TOP\n" +
            "  0001 PUSHK_UNDER 1\n" +
            "  0003 DUP\n" +
            "  0004 CALL_BUILTIN _plus\n" +
            "  0007 RET\n\n",
            result.StandardOutputText,
            StringComparison.Ordinal);
        Assert.Contains("0000 TOP\t\n", result.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("<backtracking>\n", result.StandardOutputText, StringComparison.Ordinal);
        Assert.EndsWith(LifecycleSuffix, result.StandardOutputText, StringComparison.Ordinal);
    }

    private static async Task AssertDifferentialAsync(
        CliCommand oracle,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var expected = await CliProcess.RunAsync(
            oracle,
            arguments,
            standardInput,
            workingDirectory,
            environment,
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            standardInput,
            workingDirectory,
            environment,
            cancellationToken: TestContext.Current.CancellationToken);
        CliAssertions.Equal(expected, actual);
    }
}
