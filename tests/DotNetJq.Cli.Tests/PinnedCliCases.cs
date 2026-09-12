namespace DotNetJq.Cli.Tests;

// Expected results are transcribed from jq-1.8.2 src/main.c and tests/shtest
// at commit 34f7186b86743a083a589741b6cea95293524108. The differential test below
// executes this same corpus against a binary built from that commit.
internal sealed record CliTextCase(
    string Name,
    string[] Arguments,
    string StandardInput,
    int ExitCode,
    string StandardOutput,
    string StandardError = "");

internal static class PinnedCliCases
{
    public static IReadOnlyList<CliTextCase> ArgumentAndInputCases { get; } =
    [
        new(
            "stacked short options",
            ["-ncr", "{answer: 40 + 2}"],
            "",
            0,
            "{\"answer\":42}\n"),
        new(
            "repeated null-input is idempotent",
            ["-nn", "42"],
            "",
            0,
            "42\n"),
        new(
            "negative numeric program is not classified as an option",
            ["-n", "-1"],
            "",
            0,
            "-1\n"),
        new(
            "attached library option does not consume the program",
            ["-nL.", "42"],
            "",
            0,
            "42\n"),
        new(
            "multiple concatenated JSON values",
            ["-c", "."],
            "1 {\"a\":2}",
            0,
            "1\n{\"a\":2}\n"),
        new(
            "raw input emits one string per line",
            ["-R", "-c", "."],
            "alpha\nbeta\n",
            0,
            "\"alpha\"\n\"beta\"\n"),
        new(
            "raw slurp preserves input terminators",
            ["-R", "-s", "-c", "."],
            "alpha\nbeta\n",
            0,
            "\"alpha\\nbeta\\n\"\n"),
        new(
            "raw line mode preserves carriage returns and final partial line",
            ["-R", "-c", "."],
            "a\r\nb",
            0,
            "\"a\\r\"\n\"b\"\n"),
        new(
            "raw slurp preserves carriage return line feed bytes",
            ["-R", "-s", "-c", "."],
            "a\r\nb",
            0,
            "\"a\\r\\nb\"\n"),
        new(
            "raw input preserves embedded NUL",
            ["-Rse", ". == \"a\\u0000b\\nc\\u0000d\\ne\""],
            "a\0b\nc\0d\ne",
            0,
            "true\n"),
        new(
            "slurp collects adjacent JSON values before evaluation",
            ["-c", "-s", "add"],
            "[1,2][3,4]",
            0,
            "[1,2,3,4]\n"),
        new(
            "stream parser emits scalar leaves and container ends",
            ["--stream", "-c", "."],
            "[1,{\"a\":2}]",
            0,
            "[[0],1]\n[[1,\"a\"],2]\n[[1,\"a\"]]\n[[1]]\n"),
        new(
            "slurp collects the stream-event sequence",
            ["-c", "-s", "--stream", "."],
            "[1][2]",
            0,
            "[[[0],1],[[0]],[[0],2],[[0]]]\n"),
        new(
            "stream-errors converts malformed input to a stream value",
            ["--stream-errors", "-c", "."],
            "[",
            0,
            "[\"Unfinished JSON term at EOF at line 1, column 1\",[0]]\n"),
        new(
            "JSON sequence input and output use record separators",
            ["--seq", "-c", "."],
            "\u001e1\n\u001e2\n",
            0,
            "\u001e1\n\u001e2\n"),
        new(
            "JSON sequence raw strings do not receive record separators",
            ["--seq", "-r", "."],
            "\u001e\"abc\"\n",
            0,
            "abc\n"),
    ];

    public static IReadOnlyList<CliTextCase> FormattingCases { get; } =
    [
        new(
            "default pretty printing",
            ["."],
            "{\"z\":\"μ\",\"a\":[true,null]}",
            0,
            "{\n  \"z\": \"μ\",\n  \"a\": [\n    true,\n    null\n  ]\n}\n"),
        new(
            "compact output",
            ["-c", "."],
            "{\"z\":1,\"a\":2}",
            0,
            "{\"z\":1,\"a\":2}\n"),
        new(
            "raw output affects strings but not other values",
            ["-n", "-r", "\"a\", 2, \"b\""],
            "",
            0,
            "a\n2\nb\n"),
        new(
            "join output suppresses every record terminator",
            ["-n", "-j", "\"a\", 2, \"b\""],
            "",
            0,
            "a2b"),
        new(
            "ASCII output escapes BMP and supplementary scalars",
            ["-a", "-c", "."],
            "\"μ😎\"",
            0,
            "\"\\u03bc\\ud83d\\ude0e\"\n"),
        new(
            "ASCII plus raw output retains jq's quoted ASCII serialization",
            ["-nar", "\"μ\""],
            "",
            0,
            "\"\\u03bc\"\n"),
        new(
            "sort keys applies recursively",
            ["-S", "-c", "."],
            "{\"z\":1,\"a\":{\"d\":4,\"c\":3}}",
            0,
            "{\"a\":{\"c\":3,\"d\":4},\"z\":1}\n"),
        new(
            "four-space indentation",
            ["--indent", "4", "."],
            "{\"a\":[1,2]}",
            0,
            "{\n    \"a\": [\n        1,\n        2\n    ]\n}\n"),
        new(
            "tab indentation",
            ["--tab", "."],
            "{\"a\":[1,2]}",
            0,
            "{\n\t\"a\": [\n\t\t1,\n\t\t2\n\t]\n}\n"),
        new(
            "monochrome overrides forced color",
            ["-CMcn", "null"],
            "",
            0,
            "null\n"),
        new(
            "monochrome overrides forced color in either option order",
            ["-MCcn", "null"],
            "",
            0,
            "null\n"),
        new(
            "compact after tab clears pretty formatting",
            ["-n", "--tab", "-c", "[1,2]"],
            "",
            0,
            "[1,2]\n"),
        new(
            "tab after compact restores tabbed pretty formatting",
            ["-n", "-c", "--tab", "[1,2]"],
            "",
            0,
            "[\n\t1,\n\t2\n]\n"),
        new(
            "compact after indent clears pretty formatting",
            ["-n", "--indent", "4", "-c", "[1,2]"],
            "",
            0,
            "[1,2]\n"),
        new(
            "indent after compact restores pretty formatting",
            ["-n", "-c", "--indent", "4", "[1,2]"],
            "",
            0,
            "[\n    1,\n    2\n]\n"),
    ];

    public static IReadOnlyList<CliTextCase> CompileArgumentCases { get; } =
    [
        new(
            "direct Unicode command-line argument remains one UTF-8 jq string",
            ["-n", "-c", "--arg", "value", "Grüße 🌍", "$value"],
            "",
            0,
            "\"Grüße 🌍\"\n"),
        new(
            "named string and JSON arguments populate ARGS",
            ["-n", "-c", "--arg", "foo", "1", "--argjson", "bar", "2", "{$foo, $bar} | ., . == $ARGS.named"],
            "",
            0,
            "{\"foo\":\"1\",\"bar\":2}\ntrue\n"),
        new(
            "first duplicate named argument wins",
            ["-n", "-c", "--arg", "value", "first", "--arg", "value", "second", "$value"],
            "",
            0,
            "\"first\"\n"),
        new(
            "string and JSON positional modes may be interleaved",
            ["-n", "-c", "$ARGS.positional", "--args", "foo", "1", "--jsonargs", "2", "{}", "--args", "3", "4"],
            "",
            0,
            "[\"foo\",\"1\",2,{},\"3\",\"4\"]\n"),
        new(
            "empty positional mode switches add no arguments",
            ["-n", "-c", "$ARGS.positional", "--args", "--jsonargs"],
            "",
            0,
            "[]\n"),
        new(
            "option terminator preserves the active positional mode",
            ["-n", "-c", "$ARGS.positional", "--args", "foo", "--", "--jsonargs", "-x"],
            "",
            0,
            "[\"foo\",\"--jsonargs\",\"-x\"]\n"),
        new(
            "duplicate invalid argjson is skipped after first value wins",
            ["-n", "-c", "--argjson", "value", "1", "--argjson", "value", "invalid", "$value"],
            "",
            0,
            "1\n"),
    ];

    public static IReadOnlyList<CliTextCase> ExitAndHaltCases { get; } =
    [
        new("false succeeds without exit-status", ["-n", "false"], "", 0, "false\n"),
        new("empty succeeds without exit-status", ["-n", "empty"], "", 0, ""),
        new("exit-status true", ["-n", "-e", "true"], "", 0, "true\n"),
        new("exit-status false", ["-n", "-e", "false"], "", 1, "false\n"),
        new("exit-status null", ["-n", "-e", "null"], "", 1, "null\n"),
        new("exit-status no output", ["-n", "-e", "empty"], "", 4, ""),
        new("exit-status follows the last result", ["-n", "-e", "true, false"], "", 1, "true\nfalse\n"),
        new(
            "exit-status remembers selected false across later empty inputs",
            ["-e", "select(.i == 2) | false"],
            "{\"i\":1}\n{\"i\":2}\n{\"i\":3}\n",
            1,
            "false\n"),
        new(
            "exit-status reports four when every input is filtered out",
            ["-e", "select(.i == 4)"],
            "{\"i\":1}\n{\"i\":2}\n{\"i\":3}\n",
            4,
            ""),
        new("bare halt", ["-n", "halt"], "", 0, ""),
        new("halt-error one", ["-n", "halt_error(1)"], "", 1, ""),
        new("halt-error eleven", ["-n", "halt_error(11)"], "", 11, ""),
        new("string halt message has no added newline", ["-n", "\"xy\" | halt_error(7)"], "", 7, "", "xy"),
        new("structured halt message is compact JSON with newline", ["-n", "{a:\"xyz\"} | halt_error(7)"], "", 7, "", "{\"a\":\"xyz\"}\n"),
        new("negative halt code succeeds without exit-status", ["-n", "halt_error(-11)"], "", 0, ""),
        new("negative halt code is absolute with exit-status", ["-ne", "halt_error(-11)"], "", 11, ""),
    ];

    public static IEnumerable<CliTextCase> AllTextCases =>
        ArgumentAndInputCases
            .Concat(FormattingCases)
            .Concat(CompileArgumentCases)
            .Concat(ExitAndHaltCases);
}
