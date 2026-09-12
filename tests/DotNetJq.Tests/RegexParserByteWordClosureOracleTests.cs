using System.Text.Json;
using DotNetJq.Compatibility.Regex;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// Frozen jq-1.8.2 boundaries for source-shaped property/name parsing,
/// escaped supplementary scalars, and crude-byte character classes.
/// </summary>
public sealed class RegexParserByteWordClosureOracleTests
{
    private static readonly RegexCase[] Cases =
    [
        Result("escaped-supplementary-literal", "𐐀", "\\𐐀", "", true),
        Result("escaped-supplementary-literal-other", "𐐨", "\\𐐀", "", false),
        Result("escaped-supplementary-literal-fold", "𐐨", "\\𐐀", "i", true),
        Result("escaped-supplementary-literal-unrelated", "😀", "\\𐐀", "i", false),
        Result("escaped-bmp-nonascii-literal", "é", "\\é", "", true),
        Result("escaped-bmp-nonascii-literal-fold", "ω", "\\Ω", "i", true),
        Result("escaped-underscore-literal", "_", "\\_", "", true),
        Result("escaped-supplementary-class", "𐐀", "[\\𐐀]", "", true),
        Result("escaped-supplementary-class-fold", "𐐨", "[\\𐐀]", "i", true),
        Result("escaped-supplementary-class-unrelated", "😀", "[\\𐐀]", "i", false),
        Result("escaped-bmp-nonascii-class", "é", "[\\é]", "", true),
        Result("escaped-bmp-nonascii-class-fold", "ω", "[\\Ω]", "i", true),
        Result("escaped-underscore-class", "_", "[\\_]", "", true),
        Result("ineffective-anchor-escape-in-class", "A", "[\\A]", "", true),
        Result("ineffective-backreference-escape-in-class", "k", "[\\k]", "", true),
        Result("escaped-supplementary-range-low", "𐐀", "[\\𐐀-\\𐐨]", "", true),
        Result("escaped-supplementary-range-high", "𐐨", "[\\𐐀-\\𐐨]", "", true),
        Result("escaped-supplementary-range-outside", "😀", "[\\𐐀-\\𐐨]", "", false),
        Result("escaped-emoji-range-inside", "😁", "[\\😀-\\😃]", "", true),
        Result("escaped-emoji-range-outside", "😄", "[\\😀-\\😃]", "", false),
        Error("escaped-supplementary-range-reversed-by-scalar", "x", "[\\𐐀-z]", "",
            "Regex failure: empty range in char class"),

        Result("failed-brace-hex-is-x", "x", "[\\x{}]", "", true),
        Result("failed-brace-hex-retains-open-brace", "{", "[\\x{}]", "", true),
        Result("failed-brace-hex-is-not-nul", "\0", "[\\x{}]", "", false),
        Result("failed-brace-hex-with-text-retains-z", "Z", "[\\x{Z}]", "", true),
        Result("bare-hex-class-is-nul", "\0", "[\\x]", "", true),
        Result("initial-zero-one-is-octal", "\u0001", "\\01", "", true),
        Result("initial-zero-stops-before-eight", "\0" + "8", "\\08", "", true),
        Result("three-digit-octal-stops-before-eight", "\a8", "\\078", "", true),
        Result("three-digit-octal-stops-before-fourth-digit", "\n3", "\\0123", "", true),
        Result("three-zero-octal-stops-before-fourth-digit", "\0" + "1", "\\0001", "", true),
        Result("initial-zero-remains-octal-with-ten-groups", "\u0001",
            "()()()()()()()()()()\\01", "", true),
        Error("initial-zero-following-hex-lead-is-not-backref", "a", "\\xC3\\01", "",
            "Regex failure: invalid code point value"),
        Error("initial-zero-eight-following-hex-lead-is-not-backref", "a", "\\xC3\\08", "",
            "Regex failure: invalid code point value"),
        Error("initial-zero-run-following-hex-lead-is-not-backref", "a", "\\xC3\\001", "",
            "Regex failure: invalid code point value"),

        Result("negated-crude-lead-range-excludes-two-byte", "é", "[^\\x80-\\xF5]", "", false),
        Result("negated-crude-lead-range-excludes-four-byte", "😀", "[^\\x80-\\xF5]", "", false),
        Result("negated-crude-lead-range-excludes-three-byte", "€", "[^\\x80-\\xF5]", "", false),
        Result("negated-crude-lead-range-allows-ascii", "a", "[^\\x80-\\xF5]", "", true),
        Result("negated-crude-continuation-range-allows-scalar", "é", "[^\\x80-\\xBF]", "", true),
        Result("negated-crude-high-range-allows-scalar", "😀", "[^\\xF5-\\xFF]", "", true),
        Result("positive-crude-range-does-not-surface-continuation", "é", "[\\x80-\\xF5]", "", false),
        Result("mixed-negated-class-ignores-raw-lead", "€", "[^\\x80-\\xF5é]", "", true),
        Result("mixed-negated-class-still-excludes-member", "é", "[^\\x80-\\xF5é]", "", false),
        Result("invalid-multibyte-component-changes-opcode", "€",
            "[^\\x80-\\xF5\\xED\\xA0\\x80]", "", true),
        Result("ignorecase-ascii-a-keeps-bitset-only", "é", "[^\\x80-\\xF5a]", "i", false),
        Result("ignorecase-kelvin-fold-makes-class-mixed", "é", "[^\\x80-\\xF5k]", "i", true),
        Result("ignorecase-long-s-fold-makes-class-mixed", "€", "[^\\x80-\\xF5s]", "i", true),
        Result("ignorecase-full-fold-alone-does-not-make-class-mixed", "é",
            "[^\\x80-\\xF5f]", "i", false),

        Result("runtime-word-includes-latin1-number", "²", "\\w", "", true),
        Result("runtime-word-property-includes-latin1-number", "²", "\\p{Word}", "", true),
        Result("class-word-excludes-latin1-number", "²", "[\\w]", "", false),
        Result("class-word-property-excludes-latin1-number", "²", "[\\p{Word}]", "", false),
        Result("posix-word-excludes-latin1-number", "²", "[[:word:]]", "", false),
        Result("word-control-excludes-circled-number", "①", "\\w", "", false),
        Result("latin1-number-group-name", "aa", "(?<²>a)\\k<²>", "", true),
        Result("definition-retains-fetch-name-leniency", "a", "(?<a!>a)", "", true),
        Result("subcall-retains-fetch-name-leniency", "aa", "(?<a!>a)\\g<a!>", "", true),
        Error("backreference-name-remains-strict", "aa", "(?<a!>a)\\k<a!>", "",
            "Regex failure: invalid char in group name <a!>"),
        Error("capture-condition-name-remains-strict", "ab", "(?<a!>a)(?(<a!>)b|c)", "",
            "Regex failure: invalid char in group name <a!>"),
        Error("definition-name-stops-at-parenthesis", "a", "(?<a)>a)", "",
            "Regex failure: invalid group name <a>"),
        Error("subcall-name-stops-at-parenthesis", "aa", "(?<a>a)\\g<a)>", "",
            "Regex failure: invalid group name <a>"),
        Error("backreference-name-stops-at-parenthesis", "aa", "(?<a>a)\\k<a)>", "",
            "Regex failure: invalid group name <a)>>"),

        Result("posix-punct-includes-symbols", "😀", "[[:punct:]]", "", true),
        Error("posix-name-is-case-sensitive", "!", "[[:Punct:]]", "",
            "Regex failure: invalid POSIX bracket type"),
        Error("posix-long-name-is-not-an-alias", "!", "[[:punctuation:]]", "",
            "Regex failure: invalid POSIX bracket type"),

        Error("property-nonascii-long-s", " ", "\\p{ſpace}", "",
            "Regex failure: invalid character property name {ſpace}"),
        Error("property-nonascii-dotless-i", "a", "\\p{ıdStart}", "",
            "Regex failure: invalid character property name {ıdStart}"),
        Error("property-nonascii-kelvin", "K", "\\p{K}", "",
            "Regex failure: invalid character property name {K}"),
        Error("property-malformed-nested-braces", "a", "\\p{{}}", "",
            "Regex failure: end pattern with unmatched parenthesis"),
        Error("property-malformed-open-brace", "a", "\\p{{L}", "",
            "Regex failure: end pattern with unmatched parenthesis"),
        Error("class-property-malformed-nested-braces", "a", "[\\p{{}}]", "",
            "Regex failure: end pattern with unmatched parenthesis"),

        // Parser-normalization closure: every entry is a minimal representative
        // of a distinct jq-1.8.2 root cause found by the exhaustive audit.
        Error("class-control-consumes-close-before-diagnostic", "", "[\\c]", "i",
            "Regex failure: premature end of char-class"),
        Error("definition-delimiter-precedes-close-paren", "a", "(?<a)>a)", "i",
            "Regex failure: invalid group name <a>"),
        Error("subcall-delimiter-precedes-close-paren", "a", "\\g<a)>", "im",
            "Regex failure: invalid group name <a>"),
        Result("ignorecase-literal-nul", "\0", "\0", "i", true),
        Result("control-open-parenthesis", "\u0008", "\\c(", "i", true),
        Result("control-close-parenthesis", "\u0009", "\\c)", "i", true),
        Result("control-open-bracket", "\u001B", "\\c[", "", true),

        Error("targetless-complete-repeat", "", "{0}", "",
            "Regex failure: target of repeat operator is not specified"),
        Error("targetless-too-big-precedence", "", "{100001}", "",
            "Regex failure: too big number for repeat range"),
        Error("targetless-upper-smaller-precedence", "", "{2,1}", "",
            "Regex failure: upper is smaller than lower in repeat range"),
        Result("lowerless-braces-remain-literal", "{,1}", "{,1}", "", true),
        Error("comment-does-not-supply-repeat-target", "", "(?#c)*", "",
            "Regex failure: target of repeat operator is not specified"),
        Result("comment-is-transparent-between-repeat-and-target", "a", "a(?#c)*", "", true),
        Error("invalid-alternative-poisons-repeat-target", "a", "a|(?#c)*", "",
            "Regex failure: target of repeat operator is not specified"),
        Error("anchor-repeat-remains-invalid", "", "^*", "",
            "Regex failure: target of repeat operator is invalid"),
        Result("option-scope-can-make-anchor-repeat-valid", "", "(?i:^)*", "", true),
        Result("extended-trivia-starts-nested-optional-repeat", "", "a+ ?", "x", true),

        Error("exact-repeat-chain-without-target", "", "{1}{0}", "",
            "Regex failure: target of repeat operator is not specified"),
        Result("ordered-repeat-product-boundary-accepted", "", "a{21474}{100000}", "", false),
        Error("ordered-repeat-product-boundary-rejected", "", "a{100000}{21474}", "",
            "Regex failure: too big number for repeat range"),
        Result("nested-repeat-callout-count-normalization", "aaa",
            "\\A(?:(?:(*COUNT[T]{>})a)*)*(*CMP{T,==,4})\\z", "", true),

        Result("repeated-hyphen-scoped-options", "A", "(?i--m:a)", "", true),
        Result("multiple-disable-segments-scoped-options", "A", "(?i-m-s:a)", "is", true),
        Result("hyphen-only-isolated-options", "a", "(?--)a", "", true),
        Error("whole-pattern-recursion-closing-diagnostic", "a", "a(?R)?", "",
            "Regex failure: unmatched close parenthesis"),

        Result("supplementary-letter-is-not-other", "𐐀", "\\P{C}", "", true),
        Result("supplementary-letter-is-assigned-in-class", "𐐀", "[\\p{Assigned}]", "", true),
        Result("supplementary-private-use-is-other", "󰀀", "[\\p{Other}]", "", true),
        Result("supplementary-private-use-is-not-surrogate", "󰀀", "\\P{Cs}", "", true),

        Error("empty-positive-character-class", "", "[]", "",
            "Regex failure: empty char-class"),
        Error("empty-negative-character-class", "", "[^]", "",
            "Regex failure: empty char-class"),
        Result("perl-ng-class-treats-set-punctuation-literally", "[", "[&&&&[\\S]", "", true),
        Result("perl-ng-negated-class-excludes-literal-open", "[", "[^&&[é]", "", false),
        Result("perl-ng-adjacent-class-language", "[b]", "\\A[[][]&&[^b]]\\z", "", true),
        Result("perl-ng-class-range-before-literal-close", "a]", "\\A[a&&[-[b&&]]\\z", "", true),
        Result("posix-union-survives-source-class-scan", "A", "[é[:alpha:]^*]", "", true),
        Result("negated-posix-property-union", "0", "[^[:alpha:]\\p{L}^?[]", "", true),
        Result("escaped-dash-range-remains-source-shaped", "A", "[*é\\--b]", "", true),
        Result("escaped-open-bracket-remains-literal", "[", "\\[", "", true),
        Result("disabled-collating-token-closes-at-first-bracket", "[", "[[.[a\\s.]]", "i", false),
        Result("disabled-collating-token-does-not-hide-class-close", " ", "[\\w[.{.]", "g", false),

        Result("literal-close-brace-is-possessive-repeat-target", "}", "a?} *+,", "x", false),
        Error("targetless-question-precedes-later-close", "a{b", "a|?{-8)", "i",
            "Regex failure: target of repeat operator is not specified"),
        Error("targetless-question-precedes-octal-looking-tail", "{", "a|?{01)", "i",
            "Regex failure: target of repeat operator is not specified"),
        Error("targetless-interval-precedes-later-close", "9", "a|{2}-{)", "i",
            "Regex failure: target of repeat operator is not specified"),
        Result("literal-close-brace-accepts-possessive-optional", "", "a|}?+2|a", "x", false),
        Result("literal-close-brace-accepts-possessive-star", "a{b", "}*+a*", "x", true),

        Result("negative-relative-numeric-backreference", "aa", "(a)\\k<-1>", "", true),
        Result("negative-relative-numeric-backreference-two-groups", "aba", "(a)(b)\\k<-2>", "", true),
        Error("undefined-angle-numeric-subcall", "a", "(a)\\g<2>", "",
            "Regex failure: undefined group <2> reference"),
        Error("undefined-parenthesized-numeric-subcall", "a", "(a)(?2)", "",
            "Regex failure: undefined group <2> reference"),

        Result("definition-name-retains-open-parenthesis", "a", "(?<a(>a)", "", true),
        Result("definition-name-retains-open-bracket", "a", "(?<a[>a)", "", true),
        Result("subcall-name-retains-open-parenthesis", "aa", "(?<a(>a)\\g<a(>", "", true),
        Result("ampersand-subcall-name-retains-open-bracket", "aa", "(?<a[>a)(?&a[)", "", true),
        Error("strict-backreference-rejects-open-bracket", "aa", "(?<a[>a)\\k<a[>", "",
            "Regex failure: invalid char in group name <a[>"),
        Error("lookbehind-opener-is-not-a-group-name", "a", "(?<!+>a)", "i",
            "Regex failure: target of repeat operator is not specified"),

        Error("angle-definition-rejects-leading-number", "aa", "(?<0>a)", "",
            "Regex failure: invalid group name <0>"),
        Error("quote-definition-rejects-leading-number", "aa", "(?'0'a)", "",
            "Regex failure: invalid group name <0>"),
        Error("angle-definition-first-error-recovers-through-delimiter", "aa", "(?<)a>a)", "",
            "Regex failure: invalid char in group name <)a>"),
        Error("quote-definition-first-error-recovers-through-delimiter", "aa", "(?')a'a)", "",
            "Regex failure: invalid char in group name <)a>"),
        Error("angle-subcall-final-delimiter-is-in-error-parameter", "aa", "(?<!>a)\\g<!>", "",
            "Regex failure: invalid char in group name <!>>"),
        Error("perl-subcall-final-delimiter-is-in-error-parameter", "aa", "(?<!>a)(?&!)", "",
            "Regex failure: invalid char in group name <!)>"),
        Error("plain-name-undesignated-close-stops-error-parameter", "aa", "(?<a>a)\\g<a)>", "",
            "Regex failure: invalid group name <a>"),
        Error("leveled-name-bad-level-forces-pattern-end", "aa", "(?<a>a)\\k<a+>", "",
            "Regex failure: invalid group name <a+>>"),
        Error("leveled-name-terminal-sign-excludes-sign", "aa", "(?<a>a)\\k<a+", "",
            "Regex failure: invalid char in group name <a>"),
        Error("leveled-name-numeric-to-punctuation-is-invalid-group", "aa", "(?<a>a)\\k<0!>", "",
            "Regex failure: invalid group name <0!>"),
        Error("leveled-name-late-punctuation-overwrites-invalid-group", "aa", "(?<a>a)\\k<0a!>", "",
            "Regex failure: invalid char in group name <0a!>"),
        Error("enclosed-condition-bad-level-commits-full-tail", "aa", "(?<a>a)(?(<a+>)a|b)", "",
            "Regex failure: invalid group name <a+>)a|b)>"),
        Error("perl-numeric-call-that-becomes-name-has-empty-parameter", "aa", "(a)(?1a)", "",
            "Regex failure: invalid group name <>"),
        Error("plain-numeric-subcall-can-become-name", "aa", "(?<a>a)\\g<0+>", "",
            "Regex failure: undefined name <0+> reference"),
        Error("name-error-parameter-truncates-at-27-utf8-bytes", "",
            "\\g<!abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN>", "",
            "Regex failure: invalid char in group name <!abcdefghijklmnopqrstuvwxyz...>"),
        Error("name-error-parameter-preserves-byte-split-replacement", "",
            "\\g<!😀😀😀😀😀😀😀😀😀😀>", "",
            "Regex failure: invalid char in group name <!😀😀😀😀😀😀�...>"),
    ];

    private static readonly ExactProgramCase[] ExactProgramCases =
    [
        new(
            "search-start-state-across-public-apis",
            "\"a\" | {match:[match(\"\\\\G\";\"g\")|[.offset,.length,.string]]," +
            "capture:[capture(\"\\\\G\";\"g\")],scan:[scan(\"\\\\G\")]," +
            "splits:[splits(\"\\\\G\")],split:split(\"\\\\G\")," +
            "sub:sub(\"\\\\G\";\"X\";\"g\"),gsub:gsub(\"\\\\G\";\"X\")}",
            "{\"match\":[[0,0,\"\"],[1,0,\"\"]],\"capture\":[{},{}]," +
            "\"scan\":[\"\",\"\"],\"splits\":[\"\",\"a\",\"\"]," +
            "\"split\":[\"a\"],\"sub\":\"XaX\",\"gsub\":\"XaX\"}"),
        new(
            "nested-repeat-materialization",
            "[(\"éa\"|[match(\"a??*+\";\"g\")|[.offset,.length,.string]])," +
            "(\"abab\"|[match(\"(?:ab){1}{0,}?\";\"g\")|[.offset,.length,.string]])," +
            "(\"ababb\"|[match(\"\\\\x{61 62}{2}\";\"g\")|[.offset,.length,.string]])]",
            "[[[0,0,\"\"],[1,0,\"\"],[1,0,\"\"],[2,0,\"\"]]," +
            "[[0,1,\"a\"],[2,1,\"a\"]],[[2,3,\"abb\"]]]"),
        new(
            "nested-repeat-parse-depth-boundary",
            "[1022,1023] | map(. as $n | (\"a\" + (\"*\"*$n)) as $p | " +
            "try (\"\"|test($p)) catch .)",
            "[true,\"Regex failure: parse depth limit over\"]"),
    ];

    public static TheoryData<string, string, string, string, bool?, string?> FrozenCases
    {
        get
        {
            var data = new TheoryData<string, string, string, string, bool?, string?>();
            foreach (var @case in Cases)
            {
                data.Add(
                    @case.Label,
                    @case.Input,
                    @case.Pattern,
                    @case.Modifiers,
                    @case.ExpectedResult,
                    @case.ExpectedError);
            }

            return data;
        }
    }

    public static TheoryData<string, string, string> FrozenExactProgramCases
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var @case in ExactProgramCases)
            {
                data.Add(@case.Label, @case.Filter, @case.ExpectedJson);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void JqRegexMatchesFrozenParserAndByteBoundaries(
        string label,
        string input,
        string pattern,
        string modifiers,
        bool? expectedResult,
        string? expectedError)
    {
        if (expectedError is not null)
        {
            var error = Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(input, pattern, modifiers));
            Assert.Equal(expectedError, error.Message);
            return;
        }

        Assert.True(expectedResult.HasValue, label);
        Assert.Equal(expectedResult.Value, JqRegex.Test(input, pattern, modifiers));
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void PublicJqProgramMatchesFrozenParserAndByteBoundaries(
        string label,
        string input,
        string pattern,
        string modifiers,
        bool? expectedResult,
        string? expectedError)
    {
        using var program = JqProgram.Compile(BuildFilter(input, pattern, modifiers));
        var output = Assert.Single(program.Execute("null"));
        if (expectedError is not null)
        {
            Assert.Equal(expectedError, output.GetString());
            return;
        }

        Assert.True(expectedResult.HasValue, label);
        Assert.Equal(expectedResult.Value, output.GetBoolean());
    }

    [Fact]
    public void NonGlobalSearchDoesNotSynthesizeContinuationByteMatches()
    {
        var direct = Assert.Single(JqRegex.Match("é😀", "(?=.)"));
        Assert.Equal((0, 0), (direct.Offset, direct.Length));

        using var program = JqProgram.Compile("match(\"(?=.)\")");
        var output = Assert.Single(program.Execute(JsonSerializer.Serialize("é😀")));
        Assert.Equal(0, output.GetProperty("offset").GetInt32());
        Assert.Equal(0, output.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task CompleteFrozenMatrixAgreesWithPinnedJq182Oracle()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        foreach (var @case in Cases)
        {
            var oracle = await JqOracle.ExecuteAsync(
                ". as $case | $case.input | try test($case.pattern;$case.modifiers) catch .",
                JsonSerializer.Serialize(new
                {
                    input = @case.Input,
                    pattern = @case.Pattern,
                    modifiers = @case.Modifiers,
                }),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(oracle.Succeeded, $"{@case.Label}: {oracle.StandardError}");
            Assert.Equal(string.Empty, oracle.StandardError);
            var actual = NormalizeNewlines(oracle.StandardOutput);
            if (@case.ExpectedError is not null)
            {
                using var decoded = JsonDocument.Parse(actual);
                Assert.Equal(JsonValueKind.String, decoded.RootElement.ValueKind);
                Assert.Equal(@case.ExpectedError, decoded.RootElement.GetString());
                continue;
            }

            var expected = @case.ExpectedResult == true ? "true" : "false";
            Assert.True(
                actual == expected + "\n",
                $"{@case.Label}: expected {expected}, got {actual.TrimEnd()}");
        }
    }

    [Theory]
    [MemberData(nameof(FrozenExactProgramCases))]
    public async Task ExactMaterializationMatchesPublicApiAndPinnedJq182Oracle(
        string label,
        string filter,
        string expectedJson)
    {
        var output = Assert.Single(JqProgram.Compile(filter).Execute("null"));
        using var expected = JsonDocument.Parse(expectedJson);
        Assert.True(
            JsonElement.DeepEquals(expected.RootElement, output),
            $"{label}: expected {expectedJson}, got {output.GetRawText()}");

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, $"{label}: {oracle.StandardError}");
        Assert.Equal(string.Empty, oracle.StandardError);
        Assert.Equal(expectedJson + "\n", NormalizeNewlines(oracle.StandardOutput));
    }

    [Fact]
    public void ExactMaterializationAlsoUsesTheDirectRegexFacade()
    {
        Assert.Equal(
            [(0, 0), (1, 0)],
            JqRegex.Match("a", "\\G", "g").Select(match => (match.Offset, match.Length)));
        Assert.Equal(["", ""], JqRegex.Scan("a", "\\G").Select(match => match.String));
        Assert.Equal(["", "a", ""], JqRegex.Split("a", "\\G"));
        Assert.Equal("XaX", JqRegex.Gsub("a", "\\G", "X"));

        Assert.Equal(
            [(0, 0), (1, 0), (1, 0), (2, 0)],
            JqRegex.Match("éa", "a??*+", "g").Select(match => (match.Offset, match.Length)));
        Assert.Equal(
            [(0, 1, "a"), (2, 1, "a")],
            JqRegex.Match("abab", "(?:ab){1}{0,}?", "g")
                .Select(match => (match.Offset, match.Length, match.String)));
        Assert.Equal(
            (2, 3, "abb"),
            JqRegex.Match("ababb", "\\x{61 62}{2}", "g")
                .Select(match => (match.Offset, match.Length, match.String))
                .Single());

        Assert.True(JqRegex.Test(string.Empty, "a" + new string('*', 1022)));
        Assert.Equal(
            "Regex failure: parse depth limit over",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "a" + new string('*', 1023))).Message);
    }

    private static RegexCase Result(
        string label,
        string input,
        string pattern,
        string modifiers,
        bool expected) => new(label, input, pattern, modifiers, expected, null);

    private static RegexCase Error(
        string label,
        string input,
        string pattern,
        string modifiers,
        string expected) => new(label, input, pattern, modifiers, null, expected);

    private static string BuildFilter(string input, string pattern, string modifiers) =>
        $"{JsonSerializer.Serialize(input)} | try test(" +
        $"{JsonSerializer.Serialize(pattern)};{JsonSerializer.Serialize(modifiers)}) catch .";

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record RegexCase(
        string Label,
        string Input,
        string Pattern,
        string Modifiers,
        bool? ExpectedResult,
        string? ExpectedError);

    private sealed record ExactProgramCase(string Label, string Filter, string ExpectedJson);
}
