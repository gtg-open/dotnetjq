// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/parser.y, src/compile.c, src/linker.c
// Primary ownership paths: block_free, block_take_imports, block_module_meta,
// process_dependencies, load_module_meta.

using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;

namespace DotNetJq.Tests;

public sealed class ParsedProgramRefcountLifecycleTests
{
    [Fact]
    public void ParserMetadataOwnsFoldedChildrenWithoutRetainingDiscardedExpressionTrees()
    {
        var parsed = JqGeneratedParser.ParseSource(
            "module {\"module\":\"value\"}; " +
            "import \"dependency\" as dependency {\"search\":\"path\"}; .");
        var moduleInstruction = Assert.IsType<inst>(parsed.first);
        Assert.Equal(opcode.MODULEMETA, moduleInstruction.op);
        var dependencyInstruction = Assert.IsType<inst>(moduleInstruction.next);
        Assert.Equal(opcode.DEPS, dependencyInstruction.op);
        var moduleMetadata = moduleInstruction.imm.constant;
        var importMetadata = dependencyInstruction.imm.constant;
        var retainedModule = libjq.jv_copy(moduleMetadata);
        var retainedImport = libjq.jv_copy(importMetadata);
        var moduleStorage = Assert.IsType<jvp_object>(moduleMetadata.Value);
        var importStorage = Assert.IsType<jvp_object>(importMetadata.Value);
        var moduleValue = libjq.jv_object_get(moduleMetadata, "module");
        var searchValue = libjq.jv_object_get(importMetadata, "search");
        var moduleValueStorage = Assert.IsType<jvp_string>(moduleValue.Value);
        var searchValueStorage = Assert.IsType<jvp_string>(searchValue.Value);

        try
        {
            // One metadata instruction slot plus the getter's returned owner.
            // The temporary LOADK pair blocks were freed by gen_module() and
            // gen_import_meta() during the parser reductions.
            Assert.Equal(2, moduleValueStorage.Refcnt.Count);
            Assert.Equal(2, searchValueStorage.Refcnt.Count);
            Assert.Equal(2, moduleStorage.Refcnt.Count);
            Assert.Equal(2, importStorage.Refcnt.Count);

            libjq.jv_free(moduleValue);
            moduleValue = libjq.jv_invalid();
            libjq.jv_free(searchValue);
            searchValue = libjq.jv_invalid();

            libjq.block_free(parsed);
            parsed = default;
            Assert.Equal(1, moduleStorage.Refcnt.Count);
            Assert.Equal(1, importStorage.Refcnt.Count);
        }
        finally
        {
            libjq.block_free(parsed);
            libjq.jv_free(moduleValue);
            libjq.jv_free(searchValue);
            libjq.jv_free(retainedModule);
            libjq.jv_free(retainedImport);
        }

        Assert.Equal(0, moduleStorage.Refcnt.Count);
        Assert.Equal(0, importStorage.Refcnt.Count);
        Assert.Equal(0, moduleValueStorage.Refcnt.Count);
        Assert.Equal(0, searchValueStorage.Refcnt.Count);
    }

    [Fact]
    public void DisposingAnUntransferredParsedProgramReleasesItsRootConstants()
    {
        var parsed = JqGeneratedParser.ParseSource("\"uncompiled\"");
        var literal = Assert.IsType<inst>(parsed.first?.next);
        Assert.Equal(opcode.LOADK, literal.op);
        var retained = libjq.jv_copy(literal.imm.constant);
        var storage = Assert.IsType<jvp_string>(literal.imm.constant.Value);

        Assert.Equal(2, storage.Refcnt.Count);
        libjq.block_free(parsed);
        Assert.Equal(1, storage.Refcnt.Count);
        libjq.jv_free(retained);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void ModulemetaCopiesMetadataBeforeFreeingTheParsedModuleBlock()
    {
        var resolver = new JqModuleResolver(
            new JqInMemoryFileSystem(new Dictionary<string, string>
            {
                ["/modules/meta.jq"] =
                    "module {\"tag\":\"owned\"}; " +
                    "import \"dep\" as dep {\"search\":\"nested\"}; " +
                    "def exported: \"body\";",
            }),
            ["/modules"],
            "/program");
        using var state = new jq_state(JqExecutionOptions.Default);
        var metadata = libjq.load_module_meta(
            state,
            resolver,
            libjq.jv_string("meta"));
        var tag = libjq.jv_object_get(metadata, "tag");
        var deps = libjq.jv_object_get(metadata, "deps");
        var dependency = libjq.jv_array_get(deps, 0);
        var search = libjq.jv_object_get(dependency, "search");
        var tagStorage = Assert.IsType<jvp_string>(tag.Value);
        var searchStorage = Assert.IsType<jvp_string>(search.Value);

        try
        {
            Assert.Equal("owned", tag.StringValue);
            Assert.Equal("nested", search.StringValue);
            // Each output tree slot plus the getter result is one owner. A
            // retained parser MODULEMETA/DEPS block would make this three.
            Assert.Equal(2, tagStorage.Refcnt.Count);
            Assert.Equal(2, searchStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(tag);
            libjq.jv_free(search);
            libjq.jv_free(dependency);
            libjq.jv_free(metadata);
        }

        Assert.Equal(0, tagStorage.Refcnt.Count);
        Assert.Equal(0, searchStorage.Refcnt.Count);
    }

    [Fact]
    public void ImportedModuleConstantsTransferToTheCompiledProgramOwner()
    {
        var resolver = new JqModuleResolver(
            new JqInMemoryFileSystem(new Dictionary<string, string>
            {
                ["/modules/owned.jq"] = "def exported: \"module constant\";",
            }),
            ["/modules"],
            "/program");
        jq_state? state = libjq.jq_init();
        var retained = libjq.jv_invalid();
        jvp_string? storage = null;

        try
        {
            Assert.Equal(
                1,
                libjq.jq_compile(
                    state,
                    "include \"owned\"; exported",
                    resolver,
                    "/program/main.jq"));
            var bytecode = Assert.IsType<bytecode>(state.Bytecode);
            var constant = Assert.Single(
                EnumerateConstants(bytecode),
                static value =>
                    value.Kind == jv_kind.JV_KIND_STRING &&
                    value.StringValue == "module constant");
            storage = Assert.IsType<jvp_string>(constant.Value);
            retained = libjq.jv_copy(constant);
            Assert.Equal(2, storage.Refcnt.Count);

            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    private static IEnumerable<jv> EnumerateConstants(bytecode value)
    {
        foreach (var constant in value.constants.ArrayValue)
        {
            yield return constant;
        }

        foreach (var child in value.subfunctions)
        {
            foreach (var constant in EnumerateConstants(child))
            {
                yield return constant;
            }
        }
    }
}
