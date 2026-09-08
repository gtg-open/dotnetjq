using System.Text.Json;
using DotNetJq.Compatibility.Regex;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// Frozen jq-1.8.2/Oniguruma boundaries for full case-fold string-node
/// concatenation, quantifier ownership, extended trivia, and lookbehind.
/// </summary>
public sealed class RegexFullFoldAtomBoundaryOracleTests
{
    private static readonly FoldCase[] Cases =
    [
        Case("sharp-s-run", "ß", "ss", "i", true),
        Case("sharp-s-plus", "ß", "ss+", "i", false),
        Case("sharp-s-star-unrelated", "ﬃ", "ss*", "i", false),
        Case("sharp-s-question", "ß", "ss?", "i", false),
        Case("sharp-s-bounded", "ß", "ss{1,2}", "i", false),
        Case("sharp-s-lazy", "ß", "ss+?", "i", false),
        Case("sharp-s-possessive", "ß", "ss++", "i", false),
        Case("sharp-s-counted-suffix", "sss", "ss{2}", "i", true),
        Case("exact-one-left", "ß", "s{1}s", "i", true),
        Case("exact-one-right", "ß", "ss{1}", "i", true),
        Case("exact-range-left", "ß", "s{1,1}s", "i", true),
        Case("exact-range-right", "ß", "ss{1,1}", "i", true),
        Case("exact-one-lazy", "ß", "s{1}?s", "i", true),
        Case("exact-one-possessive", "ß", "s{1}+s", "i", true),
        Case("group-bounded-control", "ß", "(?:ss){1,2}", "i", true),
        Case("group-lazy-control", "ß", "(?:ss)+?", "i", true),
        Case("group-possessive-control", "ß", "(?:ss)++", "i", true),

        Case("noncapture-left", "ß", "(?:s)s", "i", true),
        Case("noncapture-right", "ß", "s(?:s)", "i", true),
        Case("empty-noncapture-boundary", "ß", "s(?:)s", "i", false),
        Case("capture-left-boundary", "ß", "(s)s", "i", false),
        Case("capture-right-boundary", "ß", "s(s)", "i", false),
        Case("atomic-boundary", "ß", "s(?>)s", "i", false),
        Case("lookahead-boundary", "ß", "s(?=)s", "i", false),
        Case("class-left-boundary", "ß", "[s]s", "i", false),
        Case("class-right-boundary", "ß", "s[s]", "i", false),
        Case("scoped-option-left", "ß", "(?i:s)s", "", false),
        Case("scoped-option-right", "ß", "s(?i:s)", "", false),
        Case("adjacent-option-scopes", "ß", "(?i:s)(?i:s)", "", false),
        Case("alternative-group-control", "ß", "(?:ss|x)+", "i", true),
        Case("alternative-boundary", "ß", "s(?:s|x)", "i", false),
        Case("alternative-group-second-arm", "ß", "(?:ss|s)+", "i", true),

        Case("extended-space", "ß", "s s", "ix", true),
        Case("extended-comment", "ß", "s# comment\ns", "ix", true),
        Case("extended-scoped-space", "ß", "(?x:s s)", "i", true),
        Case("extended-disabled-space", "ß", "(?x)s(?-x) s", "i", false),
        Case("quoted-middle", "ß", "s\\Qs\\E", "i", true),
        Case("quoted-left", "ß", "\\Qs\\Es", "i", true),
        Case("quoted-run", "ß", "\\Qss\\E", "i", true),
        Case("quoted-empty", "ß", "s\\Q\\Es", "i", true),
        Case("hex-left", "ß", "\\x73s", "i", true),
        Case("hex-right", "ß", "s\\x73", "i", true),
        Case("hex-brace-run", "ß", "\\x{73 73}", "i", true),

        Case("ffi-plus", "ﬃ", "ffi+", "i", false),
        Case("ffi-question-unrelated", "ß", "ffi?", "i", false),
        Case("ffi-quoted", "ﬃ", "f\\Qfi\\E", "i", true),
        Case("ffi-split-quotes", "ﬃ", "\\Qf\\Ef\\Qi\\E", "i", true),
        Case("ffi-hex-run", "ﬃ", "\\x66\\x66\\x69", "i", true),
        Case("ffi-longest-fold-no-ff-ligature", "ﬀi", "ffi", "i", false),
        Case("ffi-longest-fold-no-fi-ligature", "fﬁ", "ffi", "i", false),

        Case("lookbehind-no-width-changing-sharp-s", "SSx", "(?<=ß)x", "i", false),
        Case("lookbehind-one-scalar-sharp-s", "ẞx", "(?<=ß)x", "i", true),
        Case("lookbehind-no-width-changing-ffi", "ﬃx", "(?<=ffi)x", "i", false),
        Case("word-class-hyphen-not-range", "@", "[\\w-a]", "i", false),
        Case("word-class-hyphen-control", "-", "[\\w-a]", "i", true),
        Case("posix-class-hyphen-not-range", "@", "[[:digit:]-a]", "i", false),
        Case("posix-class-hyphen-control", "-", "[[:digit:]-a]", "i", true),
    ];

    public static TheoryData<string, string, string, string, bool> FrozenCases
    {
        get
        {
            var data = new TheoryData<string, string, string, string, bool>();
            foreach (var @case in Cases)
            {
                data.Add(
                    @case.Label,
                    @case.Input,
                    @case.Pattern,
                    @case.Modifiers,
                    @case.Expected);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void JqRegexPreservesPinnedFullFoldAtomBoundaries(
        string label,
        string input,
        string pattern,
        string modifiers,
        bool expected)
    {
        var actual = JqRegex.Test(input, pattern, modifiers);
        Assert.True(actual == expected, $"{label}: expected {expected}, got {actual}");
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void PublicJqProgramPreservesPinnedFullFoldAtomBoundaries(
        string label,
        string input,
        string pattern,
        string modifiers,
        bool expected)
    {
        var filter = ". | test(" + JsonSerializer.Serialize(pattern) + ";" +
            JsonSerializer.Serialize(modifiers) + ")";
        using var program = JqProgram.Compile(filter);
        var output = Assert.Single(program.Execute(JsonSerializer.Serialize(input)));
        var actual = output.GetBoolean();
        Assert.True(actual == expected, $"{label}: expected {expected}, got {actual}");
    }

    [Fact]
    public async Task CompleteMatrixAgreesWithPinnedJq182Oracle()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var payload = Cases.Select(@case => new
        {
            label = @case.Label,
            input = @case.Input,
            pattern = @case.Pattern,
            modifiers = @case.Modifiers,
        });
        var filter = JsonSerializer.Serialize(payload) +
            " | map(. as $c | {label:$c.label,result:($c.input | " +
            "try test($c.pattern;$c.modifiers) catch .)})";
        var oracle = await JqOracle.ExecuteAsync(
            filter,
            string.Empty,
            arguments: ["--null-input", "--compact-output"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(string.Empty, oracle.StandardError);
        using var actual = JsonDocument.Parse(oracle.StandardOutput);
        var rows = actual.RootElement.EnumerateArray().ToArray();
        Assert.Equal(Cases.Length, rows.Length);
        for (var index = 0; index < Cases.Length; index++)
        {
            Assert.Equal(Cases[index].Label, rows[index].GetProperty("label").GetString());
            Assert.Equal(Cases[index].Expected, rows[index].GetProperty("result").GetBoolean());
        }
    }

    private static FoldCase Case(
        string label,
        string input,
        string pattern,
        string modifiers,
        bool expected) =>
        new(label, input, pattern, modifiers, expected);

    private sealed record FoldCase(
        string Label,
        string Input,
        string Pattern,
        string Modifiers,
        bool Expected);
}
