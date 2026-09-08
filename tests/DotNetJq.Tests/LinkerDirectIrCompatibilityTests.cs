// DOTNETJQ COMPATIBILITY PORT TESTS
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic source: src/linker.c (path/search validation and lookup ownership).

using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class LinkerDirectIrCompatibilityTests
{
    [Theory]
    [InlineData("../escape", "Relative paths to modules may not traverse to parent directories (../escape)")]
    [InlineData("dir\\module", "Modules must be named by relative paths using '/', not '\\' (dir\\module)")]
    [InlineData("same/same", "module names must not have equal consecutive components: same/same")]
    [InlineData("bad\0name", "Module path contains a NUL byte")]
    public void NativeValidateRelpathReturnsAnOwnedInvalidWithThePinnedMessage(
        string path,
        string expected)
    {
        var result = libjq.validate_relpath(libjq.jv_string(path));
        Assert.False(result.IsValid);
        var message = libjq.jv_invalid_get_msg(result);
        try
        {
            Assert.Equal(expected, message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
        }
    }

    [Fact]
    public void NativeDefaultSearchPrependsDotOnlyWhenDependencyOmitsSearch()
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            ["/one", "/two"],
            "/application");

        var omitted = libjq.default_search(resolver, libjq.jv_invalid());
        var explicitString = libjq.default_search(resolver, libjq.jv_string("/only"));
        try
        {
            Assert.Equal(
                [".", "/one", "/two"],
                omitted.ArrayValue.Select(value => value.StringValue));
            Assert.Equal(
                ["/only"],
                explicitString.ArrayValue.Select(value => value.StringValue));
        }
        finally
        {
            libjq.jv_free(omitted);
            libjq.jv_free(explicitString);
        }
    }

    [Fact]
    public void NativeSearchChainExpandsJqLibraryAndHomeOriginsInSourceOrder()
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            Array.Empty<string>(),
            "/application",
            "/explicit-home");
        var search = libjq.jv_array();
        search = libjq.jv_array_append(search, libjq.jv_string("."));
        search = libjq.jv_array_append(search, libjq.jv_string("$ORIGIN/modules"));
        search = libjq.jv_array_append(search, libjq.jv_string("relative"));
        search = libjq.jv_array_append(search, libjq.jv_string("~/jq"));
        search = libjq.jv_array_append(search, libjq.jv_number(1));

        var chain = libjq.build_lib_search_chain(
            resolver,
            search,
            libjq.jv_string("/application"),
            libjq.jv_string("/package"));
        try
        {
            var expanded = chain.ArrayValue[0];
            Assert.Equal(
                [".", "/application/modules", "/package/relative", "/explicit-home/jq"],
                expanded.ArrayValue.Select(value => value.StringValue));
            Assert.Equal(jv_kind.JV_KIND_NULL, chain.ArrayValue[1].Kind);
        }
        finally
        {
            libjq.jv_free(chain);
        }
    }

    [Fact]
    public void NativeSearchChainUsesOnlyTheExplicitHomeCapability()
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            Array.Empty<string>(),
            "/application",
            homeDirectory: null);
        var search = libjq.jv_array_append(libjq.jv_array(), libjq.jv_string("~/jq"));

        var chain = libjq.build_lib_search_chain(
            resolver,
            search,
            libjq.jv_string("/application"),
            libjq.jv_null());
        try
        {
            Assert.Equal(0, chain.ArrayValue[0].ArrayValue.Count);
            var error = chain.ArrayValue[1];
            Assert.False(error.IsValid);
            var message = libjq.jv_invalid_get_msg(libjq.jv_copy(error));
            try
            {
                Assert.Equal(
                    "Could not expand ~/jq. (Could not find home directory.)",
                    message.StringValue);
            }
            finally
            {
                libjq.jv_free(message);
            }
        }
        finally
        {
            libjq.jv_free(chain);
        }
    }

    [Fact]
    public void NativeFindLibTriesLayoutsOnlyAfterNotFoundAndThenAdvancesRoots()
    {
        var fileSystem = new RecordingFileSystem(path => path switch
        {
            "/first/pkg.jq" => JqFileReadResult.Failure(
                JqFileReadStatus.AccessDenied,
                path,
                "denied"),
            "/first/pkg/jq/main.jq" => JqFileReadResult.Success(path, ReadOnlyMemory<byte>.Empty),
            "/second/pkg.jq" => JqFileReadResult.Success(path, ReadOnlyMemory<byte>.Empty),
            _ => JqFileReadResult.Failure(JqFileReadStatus.NotFound, path, "missing"),
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            Array.Empty<string>(),
            "/application");
        var search = libjq.jv_array();
        search = libjq.jv_array_append(search, libjq.jv_string("/first"));
        search = libjq.jv_array_append(search, libjq.jv_string("/second"));

        var resolved = libjq.find_lib(
            resolver,
            libjq.validate_relpath(libjq.jv_string("pkg")),
            search,
            ".jq",
            libjq.jv_string("/application"),
            libjq.jv_null());
        try
        {
            Assert.True(resolved.IsValid);
            Assert.Equal("/second/pkg.jq", resolved.StringValue);
            Assert.Equal(["/first/pkg.jq", "/second/pkg.jq"], fileSystem.Reads);
        }
        finally
        {
            libjq.jv_free(resolved);
        }
    }

    [Fact]
    public void NativeSearchExpansionSkipsANullEntryBeforeFindLibRuns()
    {
        var fileSystem = new RecordingFileSystem(path =>
            JqFileReadResult.Success(path, ReadOnlyMemory<byte>.Empty));
        var resolver = new JqModuleResolver(
            fileSystem,
            Array.Empty<string>(),
            "/application");
        var search = libjq.jv_array();
        search = libjq.jv_array_append(search, libjq.jv_null());
        search = libjq.jv_array_append(search, libjq.jv_string("/never"));

        var result = libjq.find_lib(
            resolver,
            libjq.validate_relpath(libjq.jv_string("pkg")),
            search,
            ".jq",
            libjq.jv_string("/application"),
            libjq.jv_null());
        try
        {
            Assert.True(result.IsValid);
            Assert.Equal("/never/pkg.jq", result.StringValue);
            Assert.Equal(["/never/pkg.jq"], fileSystem.Reads);
        }
        finally
        {
            libjq.jv_free(result);
        }
    }

    [Theory]
    [InlineData(JqFileReadStatus.IsDirectory)]
    [InlineData(JqFileReadStatus.TooLarge)]
    public void NativeFindLibSelectsAnExistingLayoutBeforeTheLoaderReportsItsFailure(
        JqFileReadStatus status)
    {
        var fileSystem = new RecordingFileSystem(path => path switch
        {
            "/first/pkg.jq" => JqFileReadResult.Failure(status, path, "cannot load"),
            "/second/pkg.jq" => JqFileReadResult.Success(path, ReadOnlyMemory<byte>.Empty),
            _ => JqFileReadResult.Failure(JqFileReadStatus.NotFound, path, "missing"),
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            Array.Empty<string>(),
            "/application");
        var search = libjq.jv_array();
        search = libjq.jv_array_append(search, libjq.jv_string("/first"));
        search = libjq.jv_array_append(search, libjq.jv_string("/second"));

        var resolved = libjq.find_lib(
            resolver,
            libjq.validate_relpath(libjq.jv_string("pkg")),
            search,
            ".jq",
            libjq.jv_string("/application"),
            libjq.jv_null());
        try
        {
            Assert.True(resolved.IsValid);
            Assert.Equal("/first/pkg.jq", resolved.StringValue);
            Assert.Equal(["/first/pkg.jq"], fileSystem.Reads);
        }
        finally
        {
            libjq.jv_free(resolved);
        }
    }

    [Fact]
    public void NativeProcessDependenciesBindsDataUnderNamespaceAndLegacyVariableNames()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/lib/data.json"] = "1\n2\n",
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            Array.Empty<string>(),
            "/application");
        var metadata = libjq.jv_object_set(
            libjq.jv_object(),
            "search",
            libjq.jv_string("/lib"));
        var import = libjq.gen_import_meta(
            libjq.gen_import(
                libjq.jv_string("data"),
                libjq.jv_string("$data"),
                isData: 1),
            libjq.gen_const(metadata));
        var legacyRead = libjq.gen_op_unbound(opcode.LOADV, "$data");
        var source = libjq.BLOCK(import, legacyRead);
        var loading = new lib_loading_state(resolver);
        using var state = new jq_state(JqExecutionOptions.Default);

        try
        {
            var errors = libjq.process_dependencies(
                state,
                resolver,
                libjq.jv_string("/application"),
                libjq.jv_null(),
                ref source,
                loading,
                dependencyDepth: 1);

            Assert.Equal(0, errors);
            var entry = Assert.Single(loading.entries);
            Assert.Equal("/lib/data.json", entry.name);
            Assert.Equal(opcode.STORE_GLOBAL, entry.def.first!.op);
            Assert.Equal("$data", entry.def.first.symbol);
            Assert.Equal(
                [1d, 2d],
                entry.def.first.imm.constant.ArrayValue.Select(value => value.NumberValue));
            Assert.Same(entry.def.first, source.first!.bound_by);
        }
        finally
        {
            libjq.block_free(source);
            foreach (var entry in loading.entries)
            {
                libjq.block_free(entry.def);
                entry.def = libjq.gen_noop();
            }
        }
    }

    [Fact]
    public void NativeLoadProgramParsesAndLinksImportedDefinitionsIntoBlockIr()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/lib/math.jq"] = "def answer: 42;",
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            ["/lib"],
            "/application");
        using var state = new jq_state(JqExecutionOptions.Default);
        var sourceFile = libjq.locfile_init(
            "<main>",
            "import \"math\" as math; math::answer");
        var linked = libjq.gen_noop();
        try
        {
            var errors = libjq.load_program(
                state,
                sourceFile,
                resolver,
                out linked,
                programOrigin: "/program",
                loadUserStartupLibrary: false);

            Assert.Equal(0, errors);
            Assert.Null(state.CompileError);
            Assert.Same(resolver, state.ModuleResolver);
            Assert.True(libjq.block_has_main(linked));
            Assert.DoesNotContain(Instructions(linked), instruction =>
                instruction.op is opcode.DEPS or opcode.MODULEMETA);
            var importedDefinition = Assert.Single(
                Instructions(linked),
                instruction =>
                    instruction.op == opcode.CLOSURE_CREATE &&
                    instruction.symbol == "answer");
            Assert.Equal(1, importedDefinition.referenced);
        }
        finally
        {
            libjq.block_free(linked);
            libjq.locfile_free(sourceFile);
        }
    }

    [Fact]
    public void NativeLoadProgramDetectsCircularImportsBeforeBindingThem()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/lib/a.jq"] = "import \"b\" as b; def value: b::value;",
            ["/lib/b.jq"] = "import \"a\" as a; def value: a::value;",
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            ["/lib"],
            "/application");
        using var state = new jq_state(JqExecutionOptions.Default);
        string? reported = null;
        libjq.jq_set_error_cb(
            state,
            value =>
            {
                var formatted = libjq.jq_format_error(value);
                try
                {
                    reported = formatted.StringValue;
                }
                finally
                {
                    libjq.jv_free(formatted);
                }
            });
        var sourceFile = libjq.locfile_init(
            "<main>",
            "import \"a\" as a; a::value");
        var linked = libjq.gen_noop();
        try
        {
            var errors = libjq.load_program(
                state,
                sourceFile,
                resolver,
                out linked,
                programOrigin: "/program",
                loadUserStartupLibrary: false);

            Assert.Equal(1, errors);
            Assert.True(libjq.block_is_noop(linked));
            Assert.Equal("jq: error: circular import of /lib/a.jq\n", reported);
            Assert.Null(state.CompileError);
        }
        finally
        {
            libjq.block_free(linked);
            libjq.locfile_free(sourceFile);
        }
    }

    [Fact]
    public void NativeLoadProgramMaterializesTransitiveModulesDependencyFirst()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/lib/a.jq"] = "import \"b\" as b; def from_a: b::from_b;",
            ["/lib/b.jq"] = "import \"c\" as c; def from_b: c::from_c;",
            ["/lib/c.jq"] = "def from_c: 3;",
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            ["/lib"],
            "/application");
        using var state = new jq_state(JqExecutionOptions.Default);
        var sourceFile = libjq.locfile_init(
            "<main>",
            "import \"a\" as a; a::from_a");
        var linked = libjq.gen_noop();
        try
        {
            var errors = libjq.load_program(
                state,
                sourceFile,
                resolver,
                out linked,
                programOrigin: "/program",
                loadUserStartupLibrary: false);

            Assert.Equal(0, errors);
            Assert.Equal(
                ["from_c", "from_b", "from_a"],
                Instructions(linked)
                    .Where(instruction => instruction.op == opcode.CLOSURE_CREATE)
                    .Select(instruction => instruction.symbol));
        }
        finally
        {
            libjq.block_free(linked);
            libjq.locfile_free(sourceFile);
        }
    }

    [Fact]
    public void NativeLoadModuleMetaUsesTheResolverRetainedByJqState()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/lib/meta.jq"] = "module {description: \"sample\"}; def visible: 1; def _private: 2;",
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            ["/lib"],
            "/application");
        using var state = new jq_state(JqExecutionOptions.Default);
        libjq.jq_set_module_resolver(state, resolver);

        var metadata = libjq.load_module_meta(state, libjq.jv_string("meta"));
        try
        {
            Assert.True(metadata.IsValid);
            var description = libjq.jv_object_get(libjq.jv_copy(metadata), "description");
            var definitions = libjq.jv_object_get(libjq.jv_copy(metadata), "defs");
            try
            {
                Assert.Equal("sample", description.StringValue);
                Assert.Equal(
                    ["_private/0", "visible/0"],
                    definitions.ArrayValue.Select(value => value.StringValue).Order());
            }
            finally
            {
                libjq.jv_free(description);
                libjq.jv_free(definitions);
            }
        }
        finally
        {
            libjq.jv_free(metadata);
        }
    }

    [Fact]
    public void NativeLoadModuleMetaRejectsUnavailableResolverWithoutAmbientIo()
    {
        using var state = new jq_state(JqExecutionOptions.Default);
        var result = libjq.load_module_meta(state, libjq.jv_string("meta"));
        Assert.False(result.IsValid);
        var message = libjq.jv_invalid_get_msg(result);
        try
        {
            Assert.Equal(
                "modulemeta resolver is unavailable at the direct C-function boundary",
                message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
        }
    }

    private static IEnumerable<inst> Instructions(block source)
    {
        for (var current = source.first; current is not null; current = current.next)
        {
            yield return current;
        }
    }

    private sealed class RecordingFileSystem(
        Func<string, JqFileReadResult> read) : IJqFileSystem
    {
        private readonly List<string> reads = [];

        internal IReadOnlyList<string> Reads => reads;

        public JqFileReadResult ReadFile(string path)
        {
            reads.Add(path);
            return read(path);
        }
    }
}
