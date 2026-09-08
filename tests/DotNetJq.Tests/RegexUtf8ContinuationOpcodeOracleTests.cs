using System.Text.Encodings.Web;
using System.Text.Json;
using DotNetJq.Compatibility.Regex;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// jq advances a global empty match by one raw UTF-8 byte. These cases freeze
/// the Oniguruma opcodes that can subsequently observe a continuation byte.
/// </summary>
public sealed class RegexUtf8ContinuationOpcodeOracleTests
{
    private static readonly JsonSerializerOptions LiteralUnicodeJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string EmptyThenRaw = "[[0,0,\"\"],[0,1,\"�\"]]";
    private const string EmptyOnly = "[[0,0,\"\"]]";
    private const string EmptyThenTwoTailBoundaries =
        "[[0,0,\"\"],[1,0,\"\"],[1,0,\"\"]]";

    private static readonly ContinuationCase[] Cases =
    [
        Case("dot", "é", "(?:\\A|.)", EmptyThenRaw),
        Case("super-dot", "é", "(?:\\A|\\O)", EmptyThenRaw),
        Case("not-newline", "é", "(?:\\A|\\N)", EmptyThenRaw),
        Case("text-cluster", "é", "(?:\\A|\\X)", EmptyThenRaw),
        Case("not-word-on-nonword-byte", "é", "(?:\\A|\\W)", EmptyThenRaw),
        Case("word-on-nonword-byte", "é", "(?:\\A|\\w)", EmptyOnly),
        Case("not-digit", "é", "(?:\\A|\\D)", EmptyThenRaw),
        Case("not-space", "é", "(?:\\A|\\S)", EmptyThenRaw),
        Case("negated-property", "é", "(?:\\A|\\P{L})", EmptyThenRaw),
        Case("positive-property", "é", "(?:\\A|\\p{L})", EmptyOnly),
        Case("general-newline", "é", "(?:\\A|\\R)", EmptyOnly),
        Case("word-boundary", "é", "(?:\\A|\\b)", EmptyThenTwoTailBoundaries),
        Case("non-word-boundary", "é", "(?:\\A|\\B)", EmptyOnly),
        Case("text-boundary", "é", "(?:\\A|\\y)", EmptyThenTwoTailBoundaries),
        Case("text-non-boundary", "é", "(?:\\A|\\Y)", EmptyOnly),
        Case("raw-class-byte", "é", "(?:\\A|[\\xA9])", EmptyThenRaw),
        Case("raw-class-range", "é", "(?:\\A|[\\x80-\\xBF])", EmptyThenRaw),
        Case("negated-raw-class-byte", "é", "(?:\\A|[^\\xA9])", EmptyOnly),

        Case("word-on-word-byte", "½", "(?:\\A|\\w)", EmptyThenRaw),
        Case("word-property-on-word-byte", "½", "(?:\\A|\\p{Word})", EmptyThenRaw),
        Case("not-word-on-word-byte", "½", "(?:\\A|\\W)", EmptyOnly),
        Case("not-word-property-on-word-byte", "½", "(?:\\A|\\P{Word})", EmptyOnly),
        Case("word-boundary-word-byte", "½", "(?:\\A|\\b)",
            "[[0,0,\"\"],[1,0,\"\"]]"),
        Case("non-word-boundary-word-byte", "½", "(?:\\A|\\B)",
            "[[0,0,\"\"],[1,0,\"\"]]"),

        Case("boundary-after-raw-byte-compares-full-previous-scalar", "⪀",
            "(?:\\A|.\\B)",
            "[[0,0,\"\"],[0,1,\"�\"],[0,1,\"�\"]]"),
        Case("opposite-boundary-after-raw-byte", "⪀", "(?:\\A|.\\b)", EmptyOnly),
    ];

    public static TheoryData<string, string, string, string> FrozenCases
    {
        get
        {
            var data = new TheoryData<string, string, string, string>();
            foreach (var @case in Cases)
            {
                data.Add(@case.Label, @case.Input, @case.Pattern, @case.ExpectedJson);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void JqRegexPreservesContinuationByteOpcodeSemantics(
        string label,
        string input,
        string pattern,
        string expectedJson)
    {
        var actual = JqRegex.Match(input, pattern, "g")
            .Select(match => new object[] { match.Offset, match.Length, match.String });
        var actualJson = JsonSerializer.Serialize(actual, LiteralUnicodeJson);
        Assert.True(
            actualJson == expectedJson,
            $"{label}: expected {expectedJson}, got {actualJson}");
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void PublicJqProgramPreservesContinuationByteOpcodeSemantics(
        string label,
        string input,
        string pattern,
        string expectedJson)
    {
        using var program = JqProgram.Compile(
            "[match(" + JsonSerializer.Serialize(pattern) +
            ";\"g\") | [.offset,.length,.string]]");
        var output = Assert.Single(program.Execute(JsonSerializer.Serialize(input)));
        var actualJson = output.GetRawText();
        Assert.True(
            actualJson == expectedJson,
            $"{label}: expected {expectedJson}, got {actualJson}");
    }

    [Fact]
    public async Task CompleteContinuationMatrixAgreesWithPinnedJq182Oracle()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        foreach (var @case in Cases)
        {
            var oracle = await JqOracle.ExecuteAsync(
                "$s | [match($p;\"g\") | [.offset,.length,.string]]",
                string.Empty,
                arguments:
                [
                    "--null-input",
                    "--arg", "s", @case.Input,
                    "--arg", "p", @case.Pattern,
                ],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(oracle.Succeeded, $"{@case.Label}: {oracle.StandardError}");
            Assert.Equal(string.Empty, oracle.StandardError);
            var actual = NormalizeNewlines(oracle.StandardOutput);
            Assert.True(
                actual == @case.ExpectedJson + "\n",
                $"{@case.Label}: expected {@case.ExpectedJson}, got {actual.TrimEnd()}");
        }
    }

    private static ContinuationCase Case(
        string label,
        string input,
        string pattern,
        string expectedJson) => new(label, input, pattern, expectedJson);

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record ContinuationCase(
        string Label,
        string Input,
        string Pattern,
        string ExpectedJson);
}
