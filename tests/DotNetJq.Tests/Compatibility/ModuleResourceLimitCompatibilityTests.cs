using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests.Compatibility;

public sealed class ModuleResourceLimitCompatibilityTests
{
    private const string ModuleA = "import \"b\" as b; def value: b::value;";
    private const string ModuleB = "import \"c\" as c; def value: c::value;";
    private const string ModuleC = "def value: 42;";

    private static readonly IReadOnlyDictionary<string, string> ChainFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/modules/a.jq"] = ModuleA,
            ["/modules/b.jq"] = ModuleB,
            ["/modules/c.jq"] = ModuleC,
            ["/modules/leaf.jq"] = "def value: 7;",
            ["/modules/data.json"] = "{\"value\":9}",
            ["/modules/cycle-a.jq"] = "import \"cycle-b\" as b; def value: b::value;",
            ["/modules/cycle-b.jq"] = "import \"cycle-a\" as a; def value: a::value;",
        };

    [Fact]
    public void ModuleCountAcceptsTheExactBoundaryAndRejectsTheNextUniqueFile()
    {
        var atBoundary = CreateResolver(new JqModuleResourceLimits { MaxModuleCount = 3 });
        var belowBoundary = CreateResolver(new JqModuleResourceLimits { MaxModuleCount = 2 });

        Assert.Equal("42", ExecuteValue(atBoundary));
        var exception = Assert.Throws<JqCompileException>(() => CompileValue(belowBoundary));
        Assert.Equal("jq: error: module-count limit exceeded (maximum 2)", exception.Message);
    }

    [Fact]
    public void ImportedByteBudgetAcceptsTheExactUtf8TotalAndRejectsOneByteLess()
    {
        var exactBytes = Encoding.UTF8.GetByteCount(ModuleA) +
                         Encoding.UTF8.GetByteCount(ModuleB) +
                         Encoding.UTF8.GetByteCount(ModuleC);
        var atBoundary = CreateResolver(
            new JqModuleResourceLimits { MaxTotalImportedBytes = exactBytes });
        var belowBoundary = CreateResolver(
            new JqModuleResourceLimits { MaxTotalImportedBytes = exactBytes - 1 });

        Assert.Equal("42", ExecuteValue(atBoundary));
        var exception = Assert.Throws<JqCompileException>(() => CompileValue(belowBoundary));
        Assert.Equal(
            $"jq: error: total imported-byte limit exceeded (maximum {exactBytes - 1})",
            exception.Message);
    }

    [Fact]
    public void DependencyDepthCountsTheRootImportAsOne()
    {
        var atBoundary = CreateResolver(new JqModuleResourceLimits { MaxDependencyDepth = 3 });
        var belowBoundary = CreateResolver(new JqModuleResourceLimits { MaxDependencyDepth = 2 });

        Assert.Equal("42", ExecuteValue(atBoundary));
        var exception = Assert.Throws<JqCompileException>(() => CompileValue(belowBoundary));
        Assert.Equal(
            "jq: error: module dependency-depth limit exceeded (maximum 2)",
            exception.Message);
    }

    [Fact]
    public void RepeatedAliasesCountOneUniqueResolvedModule()
    {
        var resolver = CreateResolver(new JqModuleResourceLimits { MaxModuleCount = 1 });

        var output = JqProgram.Compile(
                "import \"leaf\" as a; import \"leaf\" as b; [a::value,b::value]",
                resolver)
            .Execute("null");

        Assert.Equal("[7,7]", Assert.Single(output).GetRawText());
    }

    [Fact]
    public void ModuleAndDataFilesShareTheAggregateBudgets()
    {
        var resolver = CreateResolver(new JqModuleResourceLimits { MaxModuleCount = 1 });

        var exception = Assert.Throws<JqCompileException>(() => JqProgram.Compile(
            "import \"leaf\" as leaf; import \"data\" as $data; null",
            resolver));

        Assert.Equal("jq: error: module-count limit exceeded (maximum 1)", exception.Message);
    }

    [Fact]
    public void ZeroBudgetsAllowProgramsWithoutDependencies()
    {
        var resolver = CreateResolver(new JqModuleResourceLimits
        {
            MaxModuleCount = 0,
            MaxTotalImportedBytes = 0,
            MaxDependencyDepth = 0,
        });

        Assert.Equal("null", Assert.Single(JqProgram.Compile(".", resolver).Execute("null")).GetRawText());
    }

    [Fact]
    public void ModuleMetadataReadsUseTheSameAggregateBudget()
    {
        var resolver = CreateResolver(new JqModuleResourceLimits { MaxModuleCount = 0 });
        var program = JqProgram.Compile("modulemeta", resolver);

        var exception = Assert.Throws<JqRuntimeException>(() => program.Execute("\"leaf\""));

        Assert.Equal("jq: error: module-count limit exceeded (maximum 0)", exception.Message);
    }

    [Fact]
    public void CyclesRemainCyclesWhenBudgetsWouldOtherwiseAllowTheGraph()
    {
        var resolver = CreateResolver(new JqModuleResourceLimits
        {
            MaxModuleCount = 10,
            MaxTotalImportedBytes = 10_000,
            MaxDependencyDepth = 10,
        });

        var exception = Assert.Throws<JqCompileException>(() => JqProgram.Compile(
            "import \"cycle-a\" as cycle; null",
            resolver));

        Assert.Contains("circular import", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreCanceledCompilationDoesNotProbeTheFilesystem()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fileSystem = new CountingFileSystem(new JqInMemoryFileSystem(ChainFiles));
#pragma warning disable xUnit1051 // This test intentionally supplies an already-canceled token.
        var resolver = CreateResolverWithToken(
            JqModuleResourceLimits.Unlimited,
            fileSystem,
            cancellation.Token);
#pragma warning restore xUnit1051

        Assert.Throws<OperationCanceledException>(() => CompileValue(resolver));
        Assert.Equal(0, fileSystem.ReadCount);
    }

    [Fact]
    public void CancellationTriggeredByAReadIsObservedBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new CancelingFileSystem(
            new JqInMemoryFileSystem(ChainFiles),
            cancellation);
#pragma warning disable xUnit1051 // This test intentionally cancels its resolver token during I/O.
        var resolver = CreateResolverWithToken(
            JqModuleResourceLimits.Unlimited,
            fileSystem,
            cancellation.Token);
#pragma warning restore xUnit1051

        Assert.Throws<OperationCanceledException>(() => CompileValue(resolver));
        Assert.Equal(1, fileSystem.ReadCount);
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(null, -1L, null)]
    [InlineData(null, null, -1)]
    public void NegativeModuleLimitsAreRejected(
        int? moduleCount,
        long? importedBytes,
        int? dependencyDepth)
    {
        var limits = new JqModuleResourceLimits
        {
            MaxModuleCount = moduleCount,
            MaxTotalImportedBytes = importedBytes,
            MaxDependencyDepth = dependencyDepth,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateResolver(limits));
    }

    private static JqProgram CompileValue(JqModuleResolver resolver) =>
        JqProgram.Compile("import \"a\" as a; a::value", resolver);

    private static string ExecuteValue(JqModuleResolver resolver) =>
        Assert.Single(CompileValue(resolver).Execute("null")).GetRawText();

    private static JqModuleResolver CreateResolver(
        JqModuleResourceLimits limits,
        IJqFileSystem? fileSystem = null) =>
        CreateResolverWithToken(
            limits,
            fileSystem,
            TestContext.Current.CancellationToken);

    private static JqModuleResolver CreateResolverWithToken(
        JqModuleResourceLimits limits,
        IJqFileSystem? fileSystem,
        CancellationToken cancellationToken) =>
        new(
            fileSystem ?? new JqInMemoryFileSystem(ChainFiles),
            ["/modules"],
            "/program",
            homeDirectory: null,
            limits,
            cancellationToken);

    private sealed class CountingFileSystem(IJqFileSystem inner) : IJqFileSystem
    {
        internal int ReadCount { get; private set; }

        public JqFileReadResult ReadFile(string path)
        {
            ReadCount++;
            return inner.ReadFile(path);
        }
    }

    private sealed class CancelingFileSystem(
        IJqFileSystem inner,
        CancellationTokenSource cancellation) : IJqFileSystem
    {
        internal int ReadCount { get; private set; }

        public JqFileReadResult ReadFile(string path)
        {
            ReadCount++;
            var result = inner.ReadFile(path);
            cancellation.Cancel();
            return result;
        }
    }
}
