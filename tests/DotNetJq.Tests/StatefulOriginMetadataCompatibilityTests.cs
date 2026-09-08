using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class StatefulOriginMetadataCompatibilityTests
{
    [Fact]
    public void PlainCompilationUsesNonAmbientOriginDefaults()
    {
        var program = JqProgram.Compile(
            "[get_search_list, [get_prog_origin], [get_jq_origin]]");
        var executionOptions = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["JQ_LIBRARY_PATH"] = "/ambient/library",
                ["JQ_ORIGIN"] = "/ambient/jq",
                ["PROGRAM_ORIGIN"] = "/ambient/program",
            },
        };

        Assert.Equal("[[],[],[]]", ExecuteOne(program, "null", executionOptions));
    }

    [Fact]
    public void ExplicitOriginsAndSearchListAreImmutableCompilationSnapshots()
    {
        var libraryPaths = new[] { "/library", "$ORIGIN/extensions", "~/jq" };
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            libraryPaths,
            "/explicit/jq",
            "/explicit/home");
        var program = JqProgram.Compile(
            "[get_search_list,get_prog_origin,get_jq_origin]",
            new JqCompilationOptions
            {
                ModuleResolver = resolver,
                ProgramOrigin = "/explicit/program",
            });

        libraryPaths[0] = "/mutated/after/compilation";

        const string expected =
            "[[\"/library\",\"$ORIGIN/extensions\",\"~/jq\"]," +
            "\"/explicit/program\",\"/explicit/jq\"]";
        Assert.Equal(expected, ExecuteOne(program, "null"));
        Assert.Equal(expected, ExecuteOne(program, "null"));
    }

    [Fact]
    public void ProgramOriginAnchorsTopLevelRelativeModuleSearch()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/explicit/program/modules/dependency.jq"] = "def value: 42;",
        });
        var resolver = new JqModuleResolver(fileSystem, ["/unrelated/library"], "/jq");

        var program = JqProgram.Compile(
            "import \"dependency\" as dependency {search:\"modules\"}; dependency::value",
            new JqCompilationOptions
            {
                ModuleResolver = resolver,
                ProgramOrigin = "/explicit/program",
            });

        Assert.Equal("42", ExecuteOne(program, "null"));
    }

    [Fact]
    public void ModuleMetaTypeErrorIsExactAndCatchable()
    {
        var options = CreateCompilationOptions();

        Assert.Equal(
            "\"modulemeta input module name must be a string\"",
            ExecuteOne(
                JqProgram.Compile("try (1|modulemeta) catch .", options),
                "null"));
    }

    [Fact]
    public void ModuleMetaMissingModuleErrorIsExactAndCatchable()
    {
        var options = CreateCompilationOptions();

        Assert.Equal(
            "\"module not found: definitely-missing\"",
            ExecuteOne(
                JqProgram.Compile(
                    "try (\"definitely-missing\"|modulemeta) catch .",
                    options),
                "null"));
    }

    [Fact]
    public void ModuleMetaUsesOnlyLibraryPathsAndPreservesUpstreamShapeAndOrder()
    {
        var program = JqProgram.Compile(
            "\"metadata\"|modulemeta",
            CreateCompilationOptions());

        const string expected =
            "{\"tag\":\"library\",\"deps\":[" +
            "{\"as\":\"a\",\"is_data\":false,\"relpath\":\"a\"}," +
            "{\"is_data\":false,\"relpath\":\"b\"}," +
            "{\"as\":\"data\",\"is_data\":true,\"relpath\":\"data\"}]," +
            "\"defs\":[\"z/0\",\"witharg/1\"]}";
        Assert.Equal(expected, ExecuteOne(program, "null"));
    }

    [Fact]
    public void StatefulOriginAndMetadataBuiltinsAppearExactlyOnce()
    {
        const string filter =
            "[\"get_search_list/0\",\"get_prog_origin/0\",\"get_jq_origin/0\"," +
            "\"modulemeta/0\"] as $required | " +
            "all($required[]; . as $name | [builtins[] | select(. == $name)] | length == 1)";

        Assert.Equal("true", ExecuteOne(JqProgram.Compile(filter), "null"));
    }

    [Fact]
    public async Task IndependentlyCompiledOriginAndMetadataCapabilitiesSupportConcurrentExecutionsAsync()
    {
        const string filter =
            "[get_search_list,get_prog_origin,get_jq_origin," +
            "(\"metadata\"|modulemeta|.tag)]";
        const string expected =
            "[[\"/library\"],\"/explicit/program\",\"/explicit/jq\",\"library\"]";

        var executions = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                using var program = JqProgram.Compile(filter, CreateCompilationOptions());
                return ExecuteOne(program, "null");
            }))
            .ToArray();

        var results = await Task.WhenAll(executions);

        Assert.All(results, result => Assert.Equal(expected, result));
        using var sequentialProgram = JqProgram.Compile(filter, CreateCompilationOptions());
        Assert.Equal(expected, ExecuteOne(sequentialProgram, "null"));
    }

    private static JqCompilationOptions CreateCompilationOptions()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/explicit/program/metadata.jq"] = "module {tag:\"program\"}; def wrong: 0;",
            ["/library/metadata.jq"] = """
                module {tag:"library"};
                import "a" as a;
                include "b";
                import "data" as $data;
                def z: 0;
                def witharg($x): $x;
                """,
        });
        var resolver = new JqModuleResolver(
            fileSystem,
            ["/library"],
            "/explicit/jq",
            "/explicit/home");
        return new JqCompilationOptions
        {
            ModuleResolver = resolver,
            ProgramOrigin = "/explicit/program",
        };
    }

    private static string ExecuteOne(
        JqProgram program,
        string input,
        JqExecutionOptions? options = null) =>
        Assert.Single(program.Execute(input, options)).GetRawText();
}
