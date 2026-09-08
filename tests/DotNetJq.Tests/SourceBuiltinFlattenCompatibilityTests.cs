using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class SourceBuiltinFlattenCompatibilityTests
{
    [Fact]
    public async Task DepthArgumentsRemainOrderedGeneratorsWithUpstreamErrorTiming()
    {
        await AssertMatchesOracle(
            "[1,[2,[[3]]]] | flatten(1,2,0)",
            ["[1,2,[[3]]]", "[1,2,[3]]", "[1,[2,[[3]]]]"]);
        await AssertMatchesOracle(
            "try ([1,[2]] | flatten(0,-1,2)) catch .",
            ["[1,[2]]", "\"flatten depth must not be negative\""]);
        await AssertMatchesOracle("[[1],[2]] | flatten(length, 0)", ["[1,2]", "[[1],[2]]"]);
        await AssertMatchesOracle("[1,[2]] | flatten(empty)", []);
    }

    [Fact]
    public async Task SpecialFractionalAndLazyNonnumericDepthsMatchJq182()
    {
        const string filter =
            "[" +
            "([1,[2]] | try flatten(nan) catch .)," +
            "([1,[2]] | flatten(infinite))," +
            "([1,[2]] | try flatten(-infinite) catch .)," +
            "([1,2] | flatten(\"x\"))," +
            "([1,[]] | try flatten(\"x\") catch .)," +
            "([1,[2]] | _flatten(nan))," +
            "([1,[2]] | _flatten(-infinite))," +
            "([1,[2,[3]]] | flatten(1.5))" +
            "]";

        await AssertMatchesOracle(
            filter,
            [
                "[\"flatten depth must not be negative\",[1,2]," +
                "\"flatten depth must not be negative\",[1,2]," +
                "\"string (\\\"x\\\") and number (1) cannot be subtracted\"," +
                "[1,2],[1,2],[1,2,3]]",
            ]);
    }

    [Fact]
    public async Task CanonicalSourceDefinitionFlattensTenThousandAndTenThousandOneLevels()
    {
        const string filter =
            "reduce range(10001) as $_ (0; [.]) | " +
            "[" +
            "(_flatten(9999) | [length, (.[0]|type)])," +
            "(_flatten(10000) | [length, (.[0]|type)])," +
            "(flatten(10000) | [length, (.[0]|type)])," +
            "(flatten | [length, (.[0]|type)])" +
            "]";

        await AssertMatchesOracle(
            filter,
            ["[[1,\"array\"],[1,\"number\"],[1,\"number\"],[1,\"number\"]]"]);
    }

    [Fact]
    public void UserDefinitionsWithTheSameNamesUseOrdinaryLexicalBinding()
    {
        Assert.Equal(
            ["[[\"private\",7,[1]],[\"public\",8,[1]]]"],
            Execute(
                "def _flatten($x): [\"private\", $x, .]; " +
                "def flatten($x): [\"public\", $x, .]; " +
                "[([1] | _flatten(7)), ([1] | flatten(8))]"));
        Assert.Equal(
            ["[\"zero\",[1]]"],
            Execute("def flatten: [\"zero\", .]; [1] | flatten"));
    }

    [Fact]
    public void DirectBytecodeHonorsAnExplicitManagedRecursionLimit()
    {
        // Direct bytecode enters one frame for _flatten/1 and one for its
        // filter-valued depth argument before the source body runs. Depth 2
        // therefore admits _flatten(0), while its recursive _flatten(1) arm
        // must enter a third frame and trips the explicit managed guard.
        var options = new JqExecutionOptions { MaxRecursionDepth = 2 };

        Assert.Equal(
            ["[1,[2]]"],
            Execute("[1,[2]] | _flatten(0)", options));
        var exception = Assert.Throws<JqRuntimeException>(
            () => Execute("[1,[2]] | _flatten(1)", options));
        Assert.Equal("jq recursion depth limit exceeded", exception.Message);
    }

    private static async Task AssertMatchesOracle(string filter, string[] expected)
    {
        var actual = Execute(filter);
        Assert.Equal(expected, actual);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(oracle.OutputLines, actual);
    }

    private static string[] Execute(
        string filter,
        JqExecutionOptions? options = null) =>
        JqProgram.Compile(filter)
            .Execute("null", options)
            .Select(value => value.GetRawText())
            .ToArray();
}
