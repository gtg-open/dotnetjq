using System.Text.Json;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests.Compatibility;

public sealed class ModuleExecutionCompatibilityTests
{
    private static readonly IReadOnlyDictionary<string, string> ModuleFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/modules/a.jq"] = "module {version:1.7}; def a: \"a\";",
            ["/modules/b/b.jq"] = "def a: \"b\"; def b: \"c\";",
            ["/modules/c/c.jq"] = """
                module {whatever:null};
                import "a" as foo;
                import "d" as d {search:"./"};
                import "d" as d2 {search:"./"};
                import "e" as e {search:"./../lib/jq"};
                import "f" as f {search:"./../lib/jq"};
                import "data" as $d;
                def a: 0;
                def c:
                  if $d::d[0] != {this:"is a test",that:"is too"} then error("data import is busted")
                  elif d2::meh != d::meh then error("import twice doesn't work")
                  elif foo::a != "a" then error("foo::a didn't work as expected")
                  elif d::meh != "meh" then error("d::meh didn't work as expected")
                  elif e::bah != "bah" then error("e::bah didn't work as expected")
                  elif f::f != "f is here" then error("f::f didn't work as expected")
                  else foo::a + "c" + d::meh + e::bah end;
                """,
            ["/modules/c/d.jq"] = "def meh: \"meh\";",
            ["/modules/data.json"] = "{\"this\":\"is a test\",\"that\":\"is too\"}",
            ["/modules/lib/jq/e/e.jq"] = "def bah: \"bah\";",
            ["/modules/lib/jq/f.jq"] = "def f: \"f is here\";",
            ["/modules/shadow1.jq"] = "def e: 1; def e: 2;",
            ["/modules/shadow2.jq"] = "def e: 3;",
            ["/modules/test_bind_order.jq"] = """
                import "test_bind_order0" as t;
                import "test_bind_order1" as t;
                import "test_bind_order2" as t;
                def check: if [t::sym0,t::sym1,t::sym2] == [0,1,2] then true else false end;
                """,
            ["/modules/test_bind_order0.jq"] = "def sym0: 0; def sym1: 0;",
            ["/modules/test_bind_order1.jq"] = "def sym1: 1; def sym2: 1;",
            ["/modules/test_bind_order2.jq"] = "def sym2: 2;",
            ["/modules/cycle_a.jq"] = "import \"cycle_b\" as b; def f: null;",
            ["/modules/cycle_b.jq"] = "import \"cycle_a\" as a; def f: null;",
        };

    [Theory]
    [InlineData(
        "import \"a\" as foo; import \"b\" as bar; def fooa: foo::a; [fooa, bar::a, bar::b, foo::a]",
        "[\"a\",\"b\",\"c\",\"a\"]")]
    [InlineData("import \"c\" as foo; [foo::a, foo::c]", "[0,\"acmehbah\"]")]
    [InlineData("include \"c\"; [a, c]", "[0,\"acmehbah\"]")]
    [InlineData(
        "import \"data\" as $e; import \"data\" as $d; [$d[].this,$e[].that,$d::d[].this,$e::e[].that]|join(\";\")",
        "\"is a test;is too;is a test;is too\"")]
    [InlineData(
        "import \"data\" as $a; import \"data\" as $b; def f: {$a, $b}; f",
        "{\"a\":[{\"this\":\"is a test\",\"that\":\"is too\"}],\"b\":[{\"this\":\"is a test\",\"that\":\"is too\"}]}")]
    [InlineData("include \"shadow1\"; e", "2")]
    [InlineData("include \"shadow1\"; include \"shadow2\"; e", "3")]
    [InlineData(
        "import \"shadow1\" as f; import \"shadow2\" as f; import \"shadow1\" as e; [e::e, f::e]",
        "[2,3]")]
    [InlineData("import \"test_bind_order\" as check; check::check", "true")]
    public void ExecutesUpstreamModuleCases(string filter, string expected)
    {
        var actual = JqProgram.Compile(filter, CreateResolver()).Execute("null");

        Assert.Single(actual);
        using var expectedDocument = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actual[0]));
    }

    [Fact]
    public void ModuleMetaPreservesMetadataDependenciesAndDefinitions()
    {
        const string expected = """
            {"whatever":null,"deps":[{"as":"foo","is_data":false,"relpath":"a"},{"search":"./","as":"d","is_data":false,"relpath":"d"},{"search":"./","as":"d2","is_data":false,"relpath":"d"},{"search":"./../lib/jq","as":"e","is_data":false,"relpath":"e"},{"search":"./../lib/jq","as":"f","is_data":false,"relpath":"f"},{"as":"d","is_data":true,"relpath":"data"}],"defs":["a/0","c/0"]}
            """;

        var actual = JqProgram.Compile("modulemeta", CreateResolver()).Execute("\"c\"");

        Assert.Single(actual);
        using var expectedDocument = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actual[0]));
    }

    [Fact]
    public void ModuleMetaSupportsDependencyAndDefinitionCounts()
    {
        var resolver = CreateResolver();

        Assert.Equal("6", JqProgram.Compile("modulemeta | .deps | length", resolver).Execute("\"c\"")[0].GetRawText());
        Assert.Equal("2", JqProgram.Compile("modulemeta | .defs | length", resolver).Execute("\"c\"")[0].GetRawText());
    }

    [Fact]
    public void PlainCompileHasNoAmbientFilesystemCapability()
    {
        var exception = Assert.Throws<JqCompileException>(() =>
            JqProgram.Compile("include \"a\"; a"));

        Assert.Contains("module not found: a", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CircularImportsAreRejectedByResolvedPath()
    {
        var exception = Assert.Throws<JqCompileException>(() =>
            JqProgram.Compile("import \"cycle_a\" as a; null", CreateResolver()));

        Assert.Contains("circular import", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/modules/cycle_a.jq", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("module (.+1); 0", "Module metadata must be constant")]
    [InlineData("module []; 0", "Module metadata must be an object")]
    [InlineData("include \"a\" (.+1); 0", "Module metadata must be constant")]
    [InlineData("include \"a\" []; 0", "Module metadata must be an object")]
    [InlineData("include \"\\(a)\"; 0", "Import path must be constant")]
    public void InvalidModuleDeclarationsAreCompileErrors(string filter, string message)
    {
        var exception = Assert.Throws<JqCompileException>(() =>
            JqProgram.Compile(filter, CreateResolver()));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NulImportPathIsRejectedBeforeFilesystemAccess()
    {
        var exception = Assert.Throws<JqCompileException>(() =>
            JqProgram.Compile("import \"a\\u0000b\" as a; null", CreateResolver()));

        Assert.Contains("NUL byte", exception.Message, StringComparison.Ordinal);
    }

    private static JqModuleResolver CreateResolver() =>
        new(new JqInMemoryFileSystem(ModuleFiles), ["/modules"], "/program");
}
