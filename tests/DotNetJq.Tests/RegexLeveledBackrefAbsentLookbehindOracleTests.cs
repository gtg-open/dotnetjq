using System.Text.Json;
using DotNetJq.Compatibility.Regex;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// Frozen jq-visible boundary cases from the pinned jq-1.8.2 Oniguruma oracle for
/// leveled backreferences, capture conditions, and absent expressions in lookbehind.
/// </summary>
public sealed class RegexLeveledBackrefAbsentLookbehindOracleTests
{
    private const string MaximumWidthCase = "absent-lb-maximum-width";
    private const string MaximumWidthInputMarker = "<65535 b characters followed by c>";

    private static readonly RegexCase[] Cases =
    [
        Error("backref-empty-name", "", "\\k<>", "Regex failure: group name is empty"),
        Error("backref-level-empty-plus", "a", "(a)\\k<1+>", "Regex failure: invalid group name <1+>>"),
        Error("backref-level-empty-minus", "a", "(a)\\k<1->", "Regex failure: invalid group name <1->>"),
        Error("backref-level-trailing-character", "a", "(a)\\k<1+x>", "Regex failure: invalid group name <1+x>>"),
        Error("backref-level-double-plus", "a", "(a)\\k<1++1>", "Regex failure: invalid group name <1++1>>"),
        Error("backref-level-plus-minus", "a", "(a)\\k<1+-1>", "Regex failure: invalid group name <1+-1>>"),
        Error("backref-level-quote-empty", "a", "(a)\\k'1+'", "Regex failure: invalid group name <1+'>"),
        Error("backref-level-plus-overflow", "aa", "(a)\\k<1+2147483648>", "Regex failure: too big number"),
        Error("backref-level-minus-overflow", "aa", "(a)\\k<1-2147483648>", "Regex failure: too big number"),
        Result("backref-level-plus-int-max", "aa", "(a)\\k<1+2147483647>", false),
        Result("backref-level-minus-int-max", "aa", "(a)\\k<1-2147483647>", false),
        Result("backref-level-plus-zero", "aa", "(a)\\k<1+0>", true),
        Result("backref-level-minus-zero", "aa", "(a)\\k<1-0>", true),
        Result("backref-level-plus-leading-zero", "aa", "(a)\\k<1+00>", true),
        Error("backref-level-missing-close", "a", "(a)\\k<1+", "Regex failure: invalid char in group name <1>"),
        Error("backref-number-out-of-range", "aa", "(a)\\k<2+0>", "Regex failure: invalid backref number/name"),
        Error("backref-name-undefined", "aa", "(a)\\k<x+0>", "Regex failure: undefined name <x> reference"),

        Error("condition-zero", "b", "\\A(?(0)a|b)\\z", "Regex failure: invalid backref number/name"),
        Error("condition-number-out-of-range", "b", "\\A(?(2)a|b)(a)?\\z", "Regex failure: invalid backref number/name"),
        Error("condition-relative-out-of-range", "c", "\\A(a)?(?(-2)b|c)\\z", "Regex failure: invalid backref number/name"),
        Error("condition-relative-forward-invalid", "b", "\\A(?(-1)a|b)(a)?\\z", "Regex failure: invalid backref number/name"),
        Error("condition-name-empty", "b", "\\A(?(<>)a|b)\\z", "Regex failure: group name is empty"),
        Error("condition-name-undefined", "b", "\\A(?(<missing>)a|b)\\z", "Regex failure: undefined name <missing> reference"),
        Result("condition-bare-overflow-pattern-fallback", "b", "\\A(?(2147483648)a|b)\\z", true),
        Result("condition-forward-number", "b", "\\A(?(1)a|b)(a)?\\z", true),
        Result("condition-forward-relative-number", "b", "\\A(?(+1)a|b)(a)?\\z", true),
        Result("condition-forward-second-number", "b", "\\A(?(2)a|b)(a)?(a)?\\z", true),
        Error("condition-forward-name-angle", "b", "\\A(?(<x>)a|b)(?<x>a)?\\z", "Regex failure: undefined name <x> reference"),
        Error("condition-forward-name-quote", "b", "\\A(?('x')a|b)(?<x>a)?\\z", "Regex failure: undefined name <x> reference"),
        Result("condition-existing-name-true-arm", "ab", "\\A(?<x>a)?(?(<x>)b|c)\\z", true),
        Result("condition-existing-name-false-arm", "c", "\\A(?<x>a)?(?(<x>)b|c)\\z", true),

        Result("absent-lb-default", "b", "(?<=(?~a))b", true),
        Result("absent-lb-default-rooted", "ab", "\\Aa(?<=(?~a))b\\z", true),
        Error("absent-lb-default-after-prefix", "xb", "(?<=x(?~a))b", "Regex failure: invalid pattern in look-behind"),
        Result("absent-lb-default-quantified", "b", "(?<=(?~a){2})b", true),
        Error("absent-lb-plain-one-atom", "bc", "(?<=(?~|a|b))c", "Regex failure: invalid pattern in look-behind"),
        Error("absent-lb-plain-two-atoms", "abc", "(?<=(?~|z|ab))c", "Regex failure: invalid pattern in look-behind"),
        Error("absent-lb-range-alternative", "abc", "(?<=(?~|z|a|bc))c", "Regex failure: invalid pattern in look-behind"),
        Result("absent-lb-star", "bc", "(?<=(?~|a|b*))c", true),
        Result("absent-lb-plus", "bc", "(?<=(?~|a|b+))c", true),
        Result("absent-lb-question", "bc", "(?<=(?~|a|b?))c", true),
        Result("absent-lb-zero", "c", "(?<=(?~|a|b{0}))c", true),
        Result("absent-lb-zero-to-two", "bc", "(?<=(?~|a|b{0,2}))c", true),
        Result("absent-lb-two", "bbc", "(?<=(?~|a|b{2}))c", true),
        Result("absent-lb-one-to-two", "bc", "(?<=(?~|a|b{1,2}))c", true),
        Result("absent-lb-class-star", "bc", "(?<=(?~|a|[bc]*))c", true),
        Result("absent-lb-dot-star", "bc", "(?<=(?~|a|.*))c", true),
        Error("absent-lb-group-star", "bcc", "(?<=(?~|a|(bc)*))c", "Regex failure: invalid pattern in look-behind"),
        Result(MaximumWidthCase, MaximumWidthInputMarker, "(?<=(?~|a|b{65535}))c", true),
        Error("absent-lb-over-maximum-width", "bc", "(?<=(?~|a|b{65536}))c", "Regex failure: invalid pattern in look-behind"),
        Result("absent-lb-range-maximum-width", "bc", "(?<=(?~|a|b{1,65535}))c", true),
        Error("absent-lb-range-over-maximum-width", "bc", "(?<=(?~|a|b{1,65536}))c", "Regex failure: invalid pattern in look-behind"),
        Result("absent-lb-open-maximum-width", "bc", "(?<=(?~|a|b{65535,}))c", true),
        Result("absent-lb-open-over-maximum-width", "bc", "(?<=(?~|a|b{65536,}))c", true),
        Result("absent-negative-lb", "xb", "(?<!(?~a))b", false),
        Error("absent-negative-lb-capture", "xb", "(?<!(?~(a)))b", "Regex failure: invalid pattern in look-behind"),
    ];

    private static readonly string[] OracleRepresentativeLabels =
    [
        "backref-empty-name",
        "backref-level-double-plus",
        "backref-level-plus-overflow",
        "backref-level-plus-int-max",
        "backref-level-plus-zero",
        "backref-number-out-of-range",
        "backref-name-undefined",
        "condition-zero",
        "condition-bare-overflow-pattern-fallback",
        "condition-forward-relative-number",
        "condition-forward-name-angle",
        "condition-existing-name-false-arm",
        "absent-lb-default",
        "absent-lb-default-after-prefix",
        "absent-lb-plain-one-atom",
        "absent-lb-range-alternative",
        "absent-lb-star",
        "absent-lb-group-star",
        MaximumWidthCase,
        "absent-lb-over-maximum-width",
        "absent-lb-open-over-maximum-width",
        "absent-negative-lb",
        "absent-negative-lb-capture",
    ];

    public static TheoryData<string, string, string, bool?, string?> FrozenCases
    {
        get
        {
            var data = new TheoryData<string, string, string, bool?, string?>();
            foreach (var @case in Cases)
            {
                data.Add(
                    @case.Label,
                    @case.Input,
                    @case.Pattern,
                    @case.ExpectedResult,
                    @case.ExpectedError);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void JqRegexMatchesPinnedJq182ResultOrDiagnostic(
        string label,
        string input,
        string pattern,
        bool? expectedResult,
        string? expectedError)
    {
        input = ResolveInput(label, input);
        if (expectedError is not null)
        {
            var error = Assert.Throws<JqRuntimeException>(() => JqRegex.Test(input, pattern));
            Assert.Equal(expectedError, error.Message);
            return;
        }

        Assert.True(expectedResult.HasValue);
        Assert.Equal(expectedResult.Value, JqRegex.Test(input, pattern));
    }

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void PublicJqProgramMatchesPinnedJq182ResultOrDiagnostic(
        string label,
        string input,
        string pattern,
        bool? expectedResult,
        string? expectedError)
    {
        input = ResolveInput(label, input);
        using var program = JqProgram.Compile(BuildManagedFilter(pattern));
        var output = Assert.Single(program.Execute(JsonSerializer.Serialize(input)));

        if (expectedError is not null)
        {
            Assert.Equal(expectedError, output.GetString());
            return;
        }

        Assert.True(expectedResult.HasValue);
        Assert.Equal(expectedResult.Value, output.GetBoolean());
    }

    [Fact]
    public async Task RepresentativeCasesAgreeWithPinnedJq182OracleExactly()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var casesByLabel = Cases.ToDictionary(@case => @case.Label, StringComparer.Ordinal);
        foreach (var label in OracleRepresentativeLabels)
        {
            var @case = casesByLabel[label];
            var input = ResolveInput(@case.Label, @case.Input);
            var expectedLine = ExpectedOracleLine(@case);
            var oracle = await JqOracle.ExecuteAsync(
                "$s | try test($p) catch .",
                string.Empty,
                arguments: ["--null-input", "--arg", "s", input, "--arg", "p", @case.Pattern],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(oracle.Succeeded, $"{@case.Label}: {oracle.StandardError}");
            Assert.Equal(string.Empty, oracle.StandardError);
            Assert.Equal(expectedLine + "\n", NormalizeNewlines(oracle.StandardOutput));

            using var managedProgram = JqProgram.Compile(BuildManagedFilter(@case.Pattern));
            var managed = Assert.Single(managedProgram.Execute(JsonSerializer.Serialize(input)));
            Assert.Equal(expectedLine, managed.GetRawText());
        }
    }

    private static RegexCase Result(string label, string input, string pattern, bool expected) =>
        new(label, input, pattern, expected, ExpectedError: null);

    private static RegexCase Error(string label, string input, string pattern, string expected) =>
        new(label, input, pattern, ExpectedResult: null, expected);

    private static string ResolveInput(string label, string input) =>
        label == MaximumWidthCase ? new string('b', 65_535) + 'c' : input;

    private static string BuildManagedFilter(string pattern) =>
        $"{JsonSerializer.Serialize(pattern)} as $p | . as $s | $s | try test($p) catch .";

    private static string ExpectedOracleLine(RegexCase @case) =>
        @case.ExpectedError is not null
            ? $"\"{@case.ExpectedError}\""
            : @case.ExpectedResult == true ? "true" : "false";

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record RegexCase(
        string Label,
        string Input,
        string Pattern,
        bool? ExpectedResult,
        string? ExpectedError);
}
