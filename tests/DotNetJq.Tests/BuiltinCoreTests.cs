namespace DotNetJq.Tests;

public sealed class BuiltinCoreTests
{
    [Fact]
    public void CompiledCallDispatchesFilterArgumentsEndToEnd()
    {
        Assert.Equal("12", ExecuteOne("map(. * 2) | add", "[1,2,3]"));
    }

    [Fact]
    public void ScalarAndCollectionIntrospectionMatchesJqShapes()
    {
        Assert.Equal("3", ExecuteOne("length", "[1,2,3]"));
        Assert.Equal("3", ExecuteOne("length", "\"a😀b\""));
        Assert.Equal("\"object\"", ExecuteOne("type", "{\"b\":2,\"a\":1}"));
        Assert.Equal("[\"a\",\"b\"]", ExecuteOne("keys", "{\"b\":2,\"a\":1}"));
        Assert.Equal("[\"b\",\"a\"]", ExecuteOne("keys_unsorted", "{\"b\":2,\"a\":1}"));
        Assert.Equal("true", ExecuteOne("has(\"a\")", "{\"a\":1}"));
        Assert.Equal("false", ExecuteOne("has(-1)", "[1]"));
    }

    [Fact]
    public void FilterArgumentsRetainStreamSemantics()
    {
        Assert.Equal("[2,4,6]", ExecuteOne("map(. * 2)", "[1,2,3]"));
        Assert.Equal("6", ExecuteOne("add", "[1,2,3]"));
        Assert.Empty(Execute("select(.)", "false"));
        Assert.Equal("2", ExecuteOne("select(. > 1)", "2"));
        Assert.Equal("[1,2,3]", ExecuteOne("sort_by(., -.)", "[3,1,2]"));
    }

    [Fact]
    public void OrderingGroupingAndExtremaAreDeterministic()
    {
        Assert.Equal("[1,2,3]", ExecuteOne("sort", "[3,1,2]"));
        Assert.Equal("[1,2]", ExecuteOne("unique", "[2,1,2,1]"));
        Assert.Equal("[[2],[3,1]]", ExecuteOne("group_by(. % 2)", "[3,1,2]"));
        Assert.Equal("1", ExecuteOne("min", "[3,1,2]"));
        Assert.Equal("3", ExecuteOne("max", "[3,1,2]"));
        Assert.Equal("null", ExecuteOne("min", "[]"));
    }

    [Fact]
    public void StreamLimitingAndRangesFollowJqCounts()
    {
        Assert.Equal(["0", "1", "2", "3"], Execute("range(0; 3.2)", "null"));
        Assert.Equal(["0", "1"], Execute("limit(1.2; range(0; 5))", "null"));
        Assert.Equal(["1", "2", "3", "4"], Execute("skip(1.2; range(0; 5))", "null"));
        Assert.Equal("1", ExecuteOne("nth(1.2; range(0; 5))", "null"));
        Assert.Equal("30", ExecuteOne("nth(-1)", "[10,20,30]"));
    }

    [Fact]
    public void EntryStringAndUnicodeUtilitiesCompose()
    {
        Assert.Equal("[{\"key\":\"a\",\"value\":1}]", ExecuteOne("to_entries", "{\"a\":1}"));
        Assert.Equal(
            "{\"x\":1,\"y\":2}",
            ExecuteOne("from_entries", "{\"a\":{\"key\":\"x\",\"value\":1},\"b\":{\"key\":\"y\",\"value\":2}}"));
        Assert.Equal("[0,2]", ExecuteOne("indices(\"😀\")", "\"😀a😀\""));
        Assert.Equal("[\"a\",\"b\",\"c\"]", ExecuteOne("split(\"\")", "\"abc\""));
        Assert.Equal("\"a--1-true\"", ExecuteOne("join(\"-\")", "[\"a\",null,1,true]"));
        Assert.Equal("[65,128512]", ExecuteOne("explode", "\"A😀\""));
        Assert.Equal("\"A😀\"", ExecuteOne("implode", "[65.9,128512]"));
        Assert.Equal("\"abc-Ä\"", ExecuteOne("ascii_downcase", "\"ABC-Ä\""));
    }

    [Fact]
    public void PathsCanBeReadUpdatedAndDeletedTogether()
    {
        Assert.Equal("2", ExecuteOne("getpath([\"a\",1])", "{\"a\":[1,2]}"));
        Assert.Equal("{\"a\":[1,9]}", ExecuteOne("setpath([\"a\",1]; 9)", "{\"a\":[1,2]}"));
        Assert.Equal("[0,1]", ExecuteOne("delpaths([[-1],[-2]])", "[0,1,2,3]"));
        Assert.Equal(["[0]", "[1]", "[1,0]"], Execute("paths", "[1,[2]]"));
    }

    [Fact]
    public void RegexBuiltinsUseTheCompatibilityProxy()
    {
        Assert.Equal("true", ExecuteOne("test(\"^a\")", "\"abc\""));
        Assert.Equal(["\"a\"", "\"a\""], Execute("scan(\"a\")", "\"aba\""));
        Assert.Equal("\"XbX\"", ExecuteOne("gsub(\"a\"; \"X\")", "\"aba\""));
    }

    private static string ExecuteOne(string filter, string input) => Assert.Single(Execute(filter, input));

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
