// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/parser.y, src/compile.c
// Primary production paths: parser block/inst IR, block_bind_referenced(),
// builtins_bind(), expand_call_arglist(), and locfile diagnostics.

using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class GeneratedParserReferenceCompatibilityTests
{
    [Fact]
    public void UnusedDefinitionBodiesDoNotContributeUnresolvedReferences()
    {
        Assert.Equal([], ParseUserReferences("def f: missing; ."));
        Assert.Equal(["missing/0"], ParseUserReferences("def f: missing; f"));

        // block_bind_referenced() retains f because main calls it, then retains
        // its unresolved g call. The later unreferenced definition of g is
        // deliberately absent, matching compile.c's backwards binder walk.
        Assert.Equal(["g/0"], ParseUserReferences("def f:g; def g:.; f"));
    }

    public static TheoryData<string, string> OrderedReferenceCases => new()
    {
        {
            "missing, $x",
            "jq: error: missing/0 is not defined at <top-level>, line 1, column 1:\n" +
            "    missing, $x\n" +
            "    ^^^^^^^\n" +
            "jq: error: $x is not defined at <top-level>, line 1, column 10:\n" +
            "    missing, $x\n" +
            "             ^^"
        },
        {
            "$x, missing",
            "jq: error: $x is not defined at <top-level>, line 1, column 1:\n" +
            "    $x, missing\n" +
            "    ^^\n" +
            "jq: error: missing/0 is not defined at <top-level>, line 1, column 5:\n" +
            "    $x, missing\n" +
            "        ^^^^^^^"
        },
        {
            "reduce missing as $x (initial; update)",
            "jq: error: initial/0 is not defined at <top-level>, line 1, column 23:\n" +
            "    reduce missing as $x (initial; update)\n" +
            "                          ^^^^^^^\n" +
            "jq: error: missing/0 is not defined at <top-level>, line 1, column 8:\n" +
            "    reduce missing as $x (initial; update)\n" +
            "           ^^^^^^^\n" +
            "jq: error: update/0 is not defined at <top-level>, line 1, column 32:\n" +
            "    reduce missing as $x (initial; update)\n" +
            "                                   ^^^^^^"
        },
    };

    [Theory]
    [MemberData(nameof(OrderedReferenceCases))]
    public async Task UnresolvedReferencesFollowCompiledBlockOrder(
        string source,
        string expected)
    {
        Assert.Equal(expected, CompileFailure(source));

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComputedBindingKeyReferenceCoversTheWholePair()
    {
        const string source = "{$missing: 1}";
        var parsed = JqGeneratedParser.ParseSource(source);
        try
        {
            var reference = Assert.Single(
                EnumerateInstructions(parsed),
                static instruction =>
                    instruction.op == opcode.LOADV &&
                    instruction.bound_by is null &&
                    instruction.source.start >= 0);
            Assert.Equal("missing", reference.symbol);
            Assert.Equal(1, reference.source.start);
            Assert.Equal(12, reference.source.end);
        }
        finally
        {
            libjq.block_free(parsed);
        }

        const string expected =
            "jq: error: $missing is not defined at <top-level>, line 1, column 2:\n" +
            "    {$missing: 1}\n" +
            "     ^^^^^^^^^^^";
        Assert.Equal(expected, CompileFailure(source));

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    public static TheoryData<string, string[]> AssignmentReferenceCases => new()
    {
        { "left = right", ["left/0", "right/0"] },
        { "left |= right", ["left/0", "right/0"] },
        { "left += right", ["right/0"] },
        { "left -= right", ["right/0"] },
        { "left *= right", ["right/0"] },
        { "left /= right", ["right/0"] },
        { "left %= right", ["right/0"] },
        { "left //= right", ["right/0"] },
        { "left += empty", ["left/0"] },
        { "left += length", ["left/0"] },
        { "$x += right", ["right/0"] },
        { "left += $x", ["$x"] },
    };

    [Theory]
    [MemberData(nameof(AssignmentReferenceCases))]
    public async Task AssignmentReferencesPreserveNativeOrderAndRhsFailureGating(
        string source,
        string[] expectedReferences)
    {
        var message = CompileFailure(source);
        Assert.Equal(expectedReferences, ErrorSubjects(message));

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(message, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target[key]", new[] { "key/0", "target/0" })]
    [InlineData("target[start:finish]", new[] { "start/0", "finish/0", "target/0" })]
    [InlineData("{(key): value}", new[] { "key/0", "value/0" })]
    [InlineData(
        "{(key1): value1, (key2): value2}",
        new[] { "key1/0", "value1/0", "key2/0", "value2/0" })]
    public void ContinuationReferencesFollowNativeCompiledBlockOrder(
        string source,
        string[] expectedReferences)
    {
        Assert.Equal(expectedReferences, ErrorSubjects(CompileFailure(source)));
    }

    private static string[] ParseUserReferences(string source)
    {
        var parsed = JqGeneratedParser.ParseSource(source);
        try
        {
            return EnumerateInstructions(parsed)
                .Where(static instruction =>
                    instruction.bound_by is null &&
                    instruction.source.start >= 0 &&
                    instruction.op is opcode.CALL_JQ or opcode.LOADV or opcode.LOADVN)
                .Select(static instruction => instruction.op is opcode.LOADV or opcode.LOADVN
                    ? "$" + instruction.symbol
                    : instruction.symbol + "/" + instruction.nactuals)
                .ToArray();
        }
        finally
        {
            libjq.block_free(parsed);
        }
    }

    private static IEnumerable<inst> EnumerateInstructions(block value)
    {
        for (var instruction = value.first;
             instruction is not null;
             instruction = instruction.next)
        {
            yield return instruction;
            foreach (var argument in EnumerateInstructions(instruction.arglist))
            {
                yield return argument;
            }

            foreach (var nested in EnumerateInstructions(instruction.subfn))
            {
                yield return nested;
            }
        }
    }

    private static string CompileFailure(string source)
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(0, libjq.jq_compile(state, source));
            return Assert.IsType<JqCompileException>(state.CompileError).Message;
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static string[] ErrorSubjects(string message) => message
        .Split('\n')
        .Where(line => line.StartsWith("jq: error: ", StringComparison.Ordinal))
        .Select(line =>
        {
            const string prefix = "jq: error: ";
            const string suffix = " is not defined";
            var suffixIndex = line.IndexOf(suffix, StringComparison.Ordinal);
            return line[prefix.Length..suffixIndex];
        })
        .ToArray();
}
