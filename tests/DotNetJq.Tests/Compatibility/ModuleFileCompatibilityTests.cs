using System.Runtime.InteropServices;
using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests.Compatibility;

public sealed class ModuleFileCompatibilityTests
{
    [Fact]
    public void ResolverSnapshotsLibraryPathsBehindAReadOnlyView()
    {
        var sourcePaths = new[] { "/original" };
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/original/module.jq"] = "original",
            ["/mutated/module.jq"] = "mutated",
        });
        var resolver = new JqModuleResolver(fileSystem, sourcePaths, "/program");

        sourcePaths[0] = "/mutated";

        Assert.Equal("/original", Assert.Single(resolver.LibraryPaths));
        Assert.IsNotType<string[]>(resolver.LibraryPaths);
        var list = Assert.IsAssignableFrom<IList<string>>(resolver.LibraryPaths);
        Assert.Throws<NotSupportedException>(() => list[0] = "/mutated");
        Assert.Equal("/original/module.jq", resolver.Resolve("module").File!.Value.Path);
    }

    [Fact]
    public void InMemoryFilesystemSnapshotsInputAndReadResults()
    {
        var callerBytes = Encoding.UTF8.GetBytes("original");
        var fileSystem = new JqInMemoryFileSystem(
            new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["/module.jq"] = callerBytes,
            });

        callerBytes[0] = (byte)'X';
        var first = fileSystem.ReadFile("/module.jq");
        Assert.Equal("original", Encoding.UTF8.GetString(first.Contents.Span));

        Assert.True(MemoryMarshal.TryGetArray(first.Contents, out var exposed));
        exposed.Array![exposed.Offset] = (byte)'Y';

        var second = fileSystem.ReadFile("/module.jq");
        Assert.Equal("original", Encoding.UTF8.GetString(second.Contents.Span));
    }

    [Fact]
    public void ResolverUsesUpstreamCandidateOrderAndSuffixes()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/lib/tools.jq"] = "direct",
            ["/lib/tools/jq/main.jq"] = "main",
            ["/lib/tools/tools.jq"] = "basename",
            ["/lib/data.json"] = "[1,2]",
        });
        var resolver = new JqModuleResolver(fileSystem, ["/lib"], "/program");

        var module = resolver.Resolve("tools");
        var data = resolver.Resolve("data", JqModuleFileKind.Data);

        Assert.True(module.IsSuccess);
        Assert.Equal("/lib/tools.jq", module.File!.Value.Path);
        Assert.Equal("direct", Encoding.UTF8.GetString(module.File.Value.Contents.Span));
        Assert.True(data.IsSuccess);
        Assert.Equal("/lib/data.json", data.File!.Value.Path);
    }

    [Fact]
    public void ResolverReturnsTheExactSnapshotSelectedByCanonicalFindLib()
    {
        var fileSystem = new ChangingSnapshotFileSystem();
        var resolver = new JqModuleResolver(fileSystem, ["/lib"], "/program");

        var result = resolver.Resolve("module");

        Assert.True(result.IsSuccess);
        Assert.Equal("/lib/module.jq", result.File!.Value.Path);
        Assert.Equal("first", Encoding.UTF8.GetString(result.File.Value.Contents.Span));
        Assert.Equal(1, fileSystem.SelectedPathReadCount);
    }

    [Theory]
    [InlineData("/lib/widgets/jq/main.jq", "/lib/widgets/jq/main.jq")]
    [InlineData("/lib/widgets/widgets.jq", "/lib/widgets/widgets.jq")]
    public void ResolverSupportsBothDirectoryModuleLayouts(string availablePath, string expectedPath)
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            [availablePath] = "def value: 1;",
        });
        var resolver = new JqModuleResolver(fileSystem, ["/lib"], "/program");

        var result = resolver.Resolve("widgets");

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPath, result.File!.Value.Path);
    }

    [Fact]
    public void ResolverExpandsOriginsWithoutConsultingAmbientEnvironment()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/application/modules/origin.jq"] = "origin",
            ["/package/dependencies/relative.jq"] = "relative",
            ["/explicit-home/jq/home.jq"] = "home",
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            ["/unused"],
            "/application",
            "/explicit-home");

        var fromOrigin = resolver.Resolve("origin", searchPaths: ["$ORIGIN/modules"]);
        var fromLibrary = resolver.Resolve(
            "relative",
            searchPaths: ["dependencies"],
            libraryOrigin: "/package");
        var fromHome = resolver.Resolve("home", searchPaths: ["~/jq"]);

        Assert.Equal("/application/modules/origin.jq", fromOrigin.File!.Value.Path);
        Assert.Equal("/package/dependencies/relative.jq", fromLibrary.File!.Value.Path);
        Assert.Equal("/explicit-home/jq/home.jq", fromHome.File!.Value.Path);
    }

    [Theory]
    [InlineData("../escape", "may not traverse")]
    [InlineData("dir\\module", "using '/'")]
    [InlineData("same/same", "equal consecutive")]
    [InlineData("bad\0name", "NUL byte")]
    public void ResolverRejectsInvalidJqModuleNames(string name, string expectedError)
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            Array.Empty<string>(),
            "/program");

        var result = resolver.Resolve(name);

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedError, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleMetadataLookupUsesLibraryPathsButNotCurrentDirectory()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/metadata.jq"] = "wrong",
            ["/lib/metadata.jq"] = "right",
        });
        var resolver = new JqModuleResolver(fileSystem, ["/lib"], "/program");

        var result = resolver.ResolveModuleMetadata("metadata");

        Assert.True(result.IsSuccess);
        Assert.Equal("/lib/metadata.jq", result.File!.Value.Path);
    }

    [Theory]
    [InlineData(JqFileReadStatus.Success)]
    [InlineData(JqFileReadStatus.NotFound)]
    [InlineData(JqFileReadStatus.AccessDenied)]
    [InlineData(JqFileReadStatus.IsDirectory)]
    [InlineData(JqFileReadStatus.TooLarge)]
    [InlineData(JqFileReadStatus.InvalidPath)]
    [InlineData(JqFileReadStatus.Error)]
    public void ResolveAndCompilationShareEveryCandidateStatusTransition(
        JqFileReadStatus firstCandidateStatus)
    {
        var publicFileSystem = new StatusMatrixFileSystem(firstCandidateStatus);
        var compilationFileSystem = new StatusMatrixFileSystem(firstCandidateStatus);
        var publicResolver = CreateStatusMatrixResolver(publicFileSystem);
        var compilationResolver = CreateStatusMatrixResolver(compilationFileSystem);

        var resolution = publicResolver.Resolve("pkg");
        var selected = ExpectedSelectedModule(firstCandidateStatus);
        if (selected is null)
        {
            Assert.False(resolution.IsSuccess);
            Assert.Contains("/first/pkg.jq", resolution.ErrorMessage, StringComparison.Ordinal);

            var exception = Assert.Throws<JqCompileException>(() =>
                JqProgram.Compile("import \"pkg\" as pkg; pkg::value", compilationResolver));

            Assert.Contains(resolution.ErrorMessage!, exception.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(resolution.IsSuccess);
            Assert.Equal(ExpectedSelectedPath(firstCandidateStatus), resolution.File!.Value.Path);

            using var program = JqProgram.Compile(
                "import \"pkg\" as pkg; pkg::value",
                compilationResolver);
            Assert.Equal($"\"{selected}\"", Assert.Single(program.Execute("null")).GetRawText());
        }

        Assert.Equal(
            CollapseRepeatedLoads(publicFileSystem.Reads),
            CollapseRepeatedLoads(compilationFileSystem.Reads));
    }

    [Theory]
    [InlineData(JqFileReadStatus.Success)]
    [InlineData(JqFileReadStatus.NotFound)]
    [InlineData(JqFileReadStatus.AccessDenied)]
    [InlineData(JqFileReadStatus.IsDirectory)]
    [InlineData(JqFileReadStatus.TooLarge)]
    [InlineData(JqFileReadStatus.InvalidPath)]
    [InlineData(JqFileReadStatus.Error)]
    public void ResolveModuleMetadataAndModulemetaShareEveryCandidateStatusTransition(
        JqFileReadStatus firstCandidateStatus)
    {
        var publicFileSystem = new StatusMatrixFileSystem(firstCandidateStatus);
        var runtimeFileSystem = new StatusMatrixFileSystem(firstCandidateStatus);
        var publicResolver = CreateStatusMatrixResolver(publicFileSystem);
        var runtimeResolver = CreateStatusMatrixResolver(runtimeFileSystem);

        var resolution = publicResolver.ResolveModuleMetadata("pkg");
        using var program = JqProgram.Compile("\"pkg\" | modulemeta", runtimeResolver);
        var metadata = Assert.Single(program.Execute("null"));
        var selected = ExpectedSelectedModule(firstCandidateStatus);
        if (selected is null)
        {
            Assert.False(resolution.IsSuccess);
            Assert.Contains("/first/pkg.jq", resolution.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal("null", metadata.GetRawText());
        }
        else
        {
            Assert.True(resolution.IsSuccess);
            Assert.Equal(ExpectedSelectedPath(firstCandidateStatus), resolution.File!.Value.Path);
            Assert.Equal(selected, metadata.GetProperty("selected").GetString());
        }

        Assert.Equal(
            CollapseRepeatedLoads(publicFileSystem.Reads),
            CollapseRepeatedLoads(runtimeFileSystem.Reads));
    }

    [Fact]
    public void JvLoadFileReturnsAllJsonValuesAndRawUtf8Replacement()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["/values.json"] = Encoding.UTF8.GetBytes("1\n{\"ok\":true}\n"),
            // UTF-8 encoding of a surrogate is one invalid unit to jq's
            // jvp_utf8_next(), while a CLR UTF-8 decoder replaces each byte.
            ["/raw.txt"] = new byte[] { (byte)'a', 0xED, 0xA0, 0x80, (byte)'b' },
        });

        var parsed = libjq.jv_load_file(fileSystem, "/values.json", raw: 0);
        var raw = libjq.jv_load_file(fileSystem, "/raw.txt", raw: 1);
        try
        {
            Assert.Equal(jv_kind.JV_KIND_ARRAY, parsed.Kind);
            Assert.Equal(2, parsed.ArrayValue.Count);
            Assert.Equal(1, parsed.ArrayValue[0].NumberValue);
            Assert.True(libjq.jv_object_get(parsed.ArrayValue[1], "ok").IsTruthy);
            Assert.Equal("a\uFFFDb", raw.StringValue);
            Assert.Equal(5, libjq.jv_string_length_bytes(libjq.jv_copy(raw)));
        }
        finally
        {
            libjq.jv_free(parsed);
            libjq.jv_free(raw);
        }
    }

    [Theory]
    [InlineData(1_025)]
    [InlineData(10_000)]
    public void JvLoadFileUsesJqParserDepthBoundary(int depth)
    {
        var json = new string('[', depth) + "0" + new string(']', depth);
        var fileSystem = new JqInMemoryFileSystem(
            new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["/deep.json"] = Encoding.UTF8.GetBytes(json),
            });

        var parsed = libjq.jv_load_file(fileSystem, "/deep.json", raw: 0);
        try
        {
            Assert.True(parsed.IsValid);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, parsed.Kind);
            Assert.Single(parsed.ArrayValue);
        }
        finally
        {
            libjq.jv_free(parsed);
        }
    }

    [Fact]
    public void JvLoadFileRejectsDepthBeyondJqParserBoundary()
    {
        const int depth = 10_001;
        var json = new string('[', depth) + "0" + new string(']', depth);
        var fileSystem = new JqInMemoryFileSystem(
            new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["/too-deep.json"] = Encoding.UTF8.GetBytes(json),
            });

        var parsed = libjq.jv_load_file(fileSystem, "/too-deep.json", raw: 0);
        try
        {
            Assert.False(parsed.IsValid);
            var message = libjq.jv_invalid_get_msg(libjq.jv_copy(parsed));
            try
            {
                Assert.Contains(
                    "Exceeds depth limit for parsing",
                    message.StringValue,
                    StringComparison.Ordinal);
            }
            finally
            {
                libjq.jv_free(message);
            }
        }
        finally
        {
            libjq.jv_free(parsed);
        }
    }

    [Fact]
    public void PhysicalFilesystemConfinesReadsAndEnforcesLimits()
    {
        using var fixture = new TemporaryDirectory();
        var allowed = Directory.CreateDirectory(Path.Combine(fixture.Path, "allowed")).FullName;
        var outside = Path.Combine(fixture.Path, "outside.jq");
        File.WriteAllText(Path.Combine(allowed, "inside.jq"), "inside");
        File.WriteAllText(Path.Combine(allowed, "large.jq"), "12345");
        File.WriteAllText(outside, "outside");

        var fileSystem = new JqFileSystem(allowed, [allowed], maximumFileSizeBytes: 4);

        Assert.Equal(JqFileReadStatus.TooLarge, fileSystem.ReadFile("large.jq").Status);
        Assert.Equal(JqFileReadStatus.AccessDenied, fileSystem.ReadFile(outside).Status);
        Assert.Equal(JqFileReadStatus.IsDirectory, fileSystem.ReadFile(allowed).Status);

        var smallerFileSystem = new JqFileSystem(allowed, [allowed], maximumFileSizeBytes: 16);
        var inside = smallerFileSystem.ReadFile("inside.jq");
        Assert.True(inside.IsSuccess);
        Assert.Equal("inside", Encoding.UTF8.GetString(inside.Contents.Span));
    }

    [Fact]
    public void PhysicalFilesystemDefaultConstructorDoesNotInjectAPerFileSizePolicy()
    {
        Assert.Equal(64 * 1024 * 1024, JqFileSystem.RecommendedMaximumFileSizeBytes);

        var constructor = typeof(JqFileSystem).GetConstructor(
            [typeof(string), typeof(IEnumerable<string>)]);
        var limitedConstructor = typeof(JqFileSystem).GetConstructor(
            [typeof(string), typeof(IEnumerable<string>), typeof(int)]);

        Assert.NotNull(constructor);
        Assert.NotNull(limitedConstructor);
        Assert.False(limitedConstructor.GetParameters()[2].IsOptional);

        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "module.jq"), "def value: 42;");
        var fileSystem = new JqFileSystem(fixture.Path, [fixture.Path]);

        Assert.True(fileSystem.ReadFile("module.jq").IsSuccess);
    }

    private static JqModuleResolver CreateStatusMatrixResolver(IJqFileSystem fileSystem) =>
        new(fileSystem, ["/first", "/second"], "/program");

    private static string? ExpectedSelectedModule(JqFileReadStatus firstCandidateStatus) =>
        firstCandidateStatus switch
        {
            JqFileReadStatus.Success => "first-direct",
            JqFileReadStatus.NotFound => "first-main",
            JqFileReadStatus.AccessDenied or
            JqFileReadStatus.InvalidPath or
            JqFileReadStatus.Error => "second-direct",
            JqFileReadStatus.IsDirectory or
            JqFileReadStatus.TooLarge => null,
            _ => throw new ArgumentOutOfRangeException(nameof(firstCandidateStatus)),
        };

    private static string ExpectedSelectedPath(JqFileReadStatus firstCandidateStatus) =>
        firstCandidateStatus switch
        {
            JqFileReadStatus.Success => "/first/pkg.jq",
            JqFileReadStatus.NotFound => "/first/pkg/jq/main.jq",
            JqFileReadStatus.AccessDenied or
            JqFileReadStatus.InvalidPath or
            JqFileReadStatus.Error => "/second/pkg.jq",
            _ => throw new ArgumentOutOfRangeException(nameof(firstCandidateStatus)),
        };

    private static string[] CollapseRepeatedLoads(IReadOnlyList<string> reads)
    {
        var candidates = new List<string>();
        foreach (var path in reads)
        {
            if (candidates.Count == 0 || !candidates[^1].Equals(path, StringComparison.Ordinal))
            {
                candidates.Add(path);
            }
        }

        return candidates.ToArray();
    }

    private sealed class StatusMatrixFileSystem(JqFileReadStatus firstCandidateStatus) : IJqFileSystem
    {
        private readonly List<string> reads = [];

        internal IReadOnlyList<string> Reads => reads;

        public JqFileReadResult ReadFile(string path)
        {
            reads.Add(path);
            return path switch
            {
                "/first/pkg.jq" => FirstCandidate(path),
                "/first/pkg/jq/main.jq" => Module(path, "first-main"),
                "/second/pkg.jq" => Module(path, "second-direct"),
                _ => JqFileReadResult.Failure(JqFileReadStatus.NotFound, path, "missing"),
            };
        }

        private JqFileReadResult FirstCandidate(string path) =>
            firstCandidateStatus == JqFileReadStatus.Success
                ? Module(path, "first-direct")
                : JqFileReadResult.Failure(
                    firstCandidateStatus,
                    path,
                    $"first candidate {firstCandidateStatus}");

        private static JqFileReadResult Module(string path, string selected) =>
            JqFileReadResult.Success(
                path,
                Encoding.UTF8.GetBytes(
                    $"module {{\"selected\":\"{selected}\"}}; def value: \"{selected}\";"));
    }

    private sealed class ChangingSnapshotFileSystem : IJqFileSystem
    {
        internal int SelectedPathReadCount { get; private set; }

        public JqFileReadResult ReadFile(string path)
        {
            if (!path.Equals("/lib/module.jq", StringComparison.Ordinal))
            {
                return JqFileReadResult.Failure(JqFileReadStatus.NotFound, path, "missing");
            }

            SelectedPathReadCount++;
            return JqFileReadResult.Success(
                path,
                Encoding.UTF8.GetBytes(SelectedPathReadCount == 1 ? "first" : "second"));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dotnetjq-modules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
