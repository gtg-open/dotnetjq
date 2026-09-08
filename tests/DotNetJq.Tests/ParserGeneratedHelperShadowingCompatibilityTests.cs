using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// Oracle-backed coverage for jq-1.8.2 parser.y/compile.c helper calls that retain
/// generated jq helper calls rather than handwritten replacement semantics.
/// </summary>
public sealed class ParserGeneratedHelperShadowingCompatibilityTests
{
    public static TheoryData<string, string, string> BinaryHelpers => new()
    {
        { "_plus", ". + 2", "\"_plus\"" },
        { "_minus", ". - 2", "\"_minus\"" },
        { "_multiply", ". * 2", "\"_multiply\"" },
        { "_divide", ". / 2", "\"_divide\"" },
        { "_mod", ". % 2", "\"_mod\"" },
        { "_equal", ". == 2", "\"_equal\"" },
        { "_notequal", ". != 2", "\"_notequal\"" },
        { "_less", ". < 2", "\"_less\"" },
        { "_greater", ". > 2", "\"_greater\"" },
        { "_lesseq", ". <= 2", "\"_lesseq\"" },
        { "_greatereq", ". >= 2", "\"_greatereq\"" },
    };

    public static TheoryData<string, string[]> SpecializedHelpers => new()
    {
        { "def recurse: 42; ..", ["42"] },
        { "def format(f): \"custom\"; @json", ["\"custom\""] },
        { "def format(f): \"custom\"; @json\"x\\(.)\"", ["\"xcustom\""] },
        { "def format(f): \"custom\"; \"x\\(.)\"", ["\"xcustom\""] },
        { "def _negate: 42; -.", ["42"] },
        { "def _assign(a;b): 42; .x = 2", ["42"] },
        { "def _modify(a;b): 42; .x |= 2", ["42"] },
        { "def _modify(a;b): 42; .x //= 2", ["42"] },
        { "def _modify(a;b): 42; .x += 2", ["42"] },
        { "def _plus(a;b): 42; .x += 2", ["{\"x\":42}"] },
        { "def error: \"noerr\"; label $x | break $x", ["\"noerr\""] },
        {
            "def _equal(a;b): false; try (label $x | break $x) catch .",
            ["{\"__jq\":0}"]
        },
        { "def _equal(a;b): true; label $x | error(\"oops\")", [] },
        { "def _plus(a;b): \"P\"; \"\\(.)\"", ["\"P\""] },
        { "def _plus(a;b): \"P\"; \"literal\"", ["\"literal\""] },
    };

    public static TheoryData<string> ReachableHelperDefinitions => new()
    {
        { "def recurse: missing; .." },
        { "def format(f): missing; @json" },
        { "def _assign(a;b): missing; .x = 2" },
        { "def _modify(a;b): missing; .x += 2" },
        { "def _plus(a;b): missing; . + 2" },
        { "def error: missing; label $x | ." },
        { "def _equal(a;b): missing; label $x | ." },
    };

    [Theory]
    [MemberData(nameof(BinaryHelpers))]
    public async Task EveryParserBinaryHelperRemainsLexicallyShadowable(
        string helper,
        string expression,
        string expected)
    {
        var filter = $"def {helper}(a;b): \"{helper}\"; {expression}";
        var managed = Execute(filter);

        Assert.Equal([expected], managed);
        await AssertMatchesOracle(filter, managed);
    }

    [Theory]
    [MemberData(nameof(SpecializedHelpers))]
    public async Task SpecializedParserLoweringsDispatchLexicalOverrides(
        string filter,
        string[] expected)
    {
        var managed = Execute(filter);

        Assert.Equal(expected, managed);
        await AssertMatchesOracle(filter, managed);
    }

    [Fact]
    public async Task BinaryHelperReceivesTheOriginalFilterArguments()
    {
        const string filter = "def _plus(a;b): [a,b]; (1,2) + (3,4)";
        string[] expected = ["[1,2,3,4]"];

        var managed = Execute(filter);

        Assert.Equal(expected, managed);
        await AssertMatchesOracle(filter, managed);

        const string interpolation =
            "def _plus(a;b): [a,b]; \"x\\((1,2))y\\((3,4))\"";
        string[] expectedInterpolation =
            ["[[[\"x\",\"1\",\"2\"],\"y\"],\"3\",\"4\"]"];

        var managedInterpolation = Execute(interpolation);

        Assert.Equal(expectedInterpolation, managedInterpolation);
        await AssertMatchesOracle(interpolation, managedInterpolation);
    }

    [Theory]
    [InlineData("def _plus(a;b): 42; 1 + 2", "3")]
    [InlineData("def _plus(a;b): 42; try (\"x\" + 1) catch .", "\"string (\\\"x\\\") and number (1) cannot be added\"")]
    public async Task ConstantFoldingIsNotRetargetedToALexicalHelper(
        string filter,
        string expected)
    {
        var managed = Execute(filter);

        Assert.Equal([expected], managed);
        await AssertMatchesOracle(filter, managed);
    }

    [Theory]
    [MemberData(nameof(ReachableHelperDefinitions))]
    public void ParserGeneratedHelperUseKeepsItsDefinitionBodyReachableForValidation(
        string filter)
    {
        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile(filter));

        Assert.Contains("missing/0 is not defined", exception.Message, StringComparison.Ordinal);
    }

    private static string[] Execute(string filter) =>
        JqProgram.Compile(filter)
            .Execute("null")
            .Select(static value => value.GetRawText())
            .ToArray();

    private static async Task AssertMatchesOracle(
        string filter,
        IReadOnlyList<string> managed)
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            string.Empty,
            ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(managed, oracle.OutputLines);
    }
}
