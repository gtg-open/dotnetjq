using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class UndefinedCallCompileValidationTests
{
    [Theory]
    [InlineData(
        "leaf_paths",
        "jq: error: leaf_paths/0 is not defined at <top-level>, line 1, column 1:\n" +
        "    leaf_paths\n" +
        "    ^^^^^^^^^^")]
    [InlineData(
        "[missing_fn]",
        "jq: error: missing_fn/0 is not defined at <top-level>, line 1, column 2:\n" +
        "    [missing_fn]\n" +
        "     ^^^^^^^^^^")]
    [InlineData(
        "length(1)",
        "jq: error: length/1 is not defined at <top-level>, line 1, column 1:\n" +
        "    length(1)\n" +
        "    ^^^^^^")]
    public void UndefinedSignaturesFailDuringCompilation(string source, string expectedMessage)
    {
        var exception = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("def a: b; def b: 1; a", "b/0")]
    [InlineData("(def local: 1; .) | local", "local/0")]
    [InlineData("def f(g): g(1); f(.)", "g/1")]
    public void LexicallyUnavailableCallsRemainUndefined(string source, string signature)
    {
        var exception = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

        Assert.Contains(signature + " is not defined", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("del(.a)")]
    [InlineData("_flatten(1)")]
    [InlineData("def recursive: recursive; recursive")]
    [InlineData("def twice(f): f | f; twice(. + 1)")]
    [InlineData("def add($x): . + $x; add(2)")]
    public void BuiltinsAndLexicallyBoundCallsStillCompile(string source)
    {
        _ = JqProgram.Compile(source);
    }
}
