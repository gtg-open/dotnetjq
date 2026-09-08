using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class CompileValidationRound3Tests
{
    private static readonly (string Source, string Message)[] InvalidPrograms =
    [
        (
            "\"u\\vw\"",
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\v\"') at <top-level>, line 1, column 3:\n" +
            "    \"u\\vw\"\n" +
            "      ^^"),
        (
            "{(0):1}",
            "jq: error: Cannot use number (0) as object key at <top-level>, line 1, column 3:\n" +
            "    {(0):1}\n" +
            "      ^"),
        (
            "{1+2:3}",
            "jq: error: syntax error, unexpected LITERAL at <top-level>, line 1, column 2:\n" +
            "    {1+2:3}\n" +
            "     ^\n" +
            "jq: error: May need parentheses around object key expression at <top-level>, line 1, column 2:\n" +
            "    {1+2:3}\n" +
            "     ^^^"),
        (
            "{non_const:., (0):1}",
            "jq: error: Cannot use number (0) as object key at <top-level>, line 1, column 16:\n" +
            "    {non_const:., (0):1}\n" +
            "                   ^"),
        (
            ". as $foo | break $foo",
            "jq: error: $*label-foo is not defined at <top-level>, line 1, column 13:\n" +
            "    . as $foo | break $foo\n" +
            "                ^^^^^^^^^^"),
        (
            ". as [] | null",
            "jq: error: syntax error, unexpected ']', expecting BINDING or '[' or '{' at <top-level>, line 1, column 7:\n" +
            "    . as [] | null\n" +
            "          ^"),
        (
            ". as {} | null",
            "jq: error: syntax error, unexpected '}' at <top-level>, line 1, column 7:\n" +
            "    . as {} | null\n" +
            "          ^"),
        (
            ". as $foo | [$foo, $bar]",
            "jq: error: $bar is not defined at <top-level>, line 1, column 20:\n" +
            "    . as $foo | [$foo, $bar]\n" +
            "                       ^^^^"),
        (
            ". as {(true):$foo} | $foo",
            "jq: error: Cannot use boolean (true) as object key at <top-level>, line 1, column 8:\n" +
            "    . as {(true):$foo} | $foo\n" +
            "           ^^^^"),
        (
            "%::wat",
            "jq: error: syntax error, unexpected '%', expecting end of file at <top-level>, line 1, column 1:\n" +
            "    %::wat\n" +
            "    ^"),
        (
            "{",
            "jq: error: syntax error, unexpected end of file at <top-level>, line 1, column 1:\n" +
            "    {\n" +
            "    ^"),
        (
            "}",
            "jq: error: syntax error, unexpected INVALID_CHARACTER, expecting end of file at <top-level>, line 1, column 1:\n" +
            "    }\n" +
            "    ^"),
    ];

    [Fact]
    public void PinnedCompileFailuresMatchJq182Diagnostics()
    {
        foreach (var (source, message) in InvalidPrograms)
        {
            var exception = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
            Assert.Equal(message, exception.Message);
        }
    }

    [Fact]
    public void DeclarationOnlyTopLevelProgramIsRejectedLikeJq182()
    {
        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile("def a: .;"));

        Assert.Equal(
            "jq: error: Top-level program not given (try \".\")",
            exception.Message);
    }

    [Fact]
    public void LexicalAndCompileArgumentVariablesRemainValid()
    {
        _ = JqProgram.Compile(
            ". as $outer | reduce .[] as $item (0; . + $item + $outer)");

        var state = libjq.jq_init();
        try
        {
            var arguments = libjq.jv_object_set(
                libjq.jv_object(),
                "provided",
                libjq.jv_number(1));
            Assert.Equal(1, libjq.jq_compile_args(state, "$provided", arguments));
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }
}
