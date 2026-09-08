// jq 1.8.2 source references:
// - src/compile.c: LOADV($ENV) expansion and bytecode graph construction
// - src/bytecode.c: constant-pool graph ownership and teardown
// - src/execute.c: jq_start lifecycle

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class EnvironmentFastPathCompatibilityTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("env")]
    [InlineData("def f: .; f")]
    public void ProgramsWithoutEnvironmentConstantsHaveAFalseGraphSummary(string filter)
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));

            var root = Assert.IsType<bytecode>(state.Bytecode);
            Assert.False(root.has_environment_constant_slots);
            Assert.False(root.compiled_environment.IsValid);
            Assert.All(
                EnumerateBytecode(root),
                current =>
                {
                    Assert.False(current.has_environment_constant_slots);
                    Assert.Empty(current.environment_constant_indexes);
                });
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void RootEnvironmentConstantSetsTheGraphSummary()
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "$ENV"));

            var root = Assert.IsType<bytecode>(state.Bytecode);
            Assert.True(root.has_environment_constant_slots);
            Assert.True(root.compiled_environment.IsValid);
            Assert.Single(root.environment_constant_indexes);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void NestedEnvironmentConstantPropagatesToEveryAncestor()
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "def nested: $ENV; nested"));

            var root = Assert.IsType<bytecode>(state.Bytecode);
            var nodes = EnumerateBytecode(root).ToArray();
            var slotOwner = Assert.Single(
                nodes,
                node => node.environment_constant_indexes.Length != 0);

            Assert.True(root.has_environment_constant_slots);
            Assert.NotSame(root, slotOwner);
            for (var current = slotOwner; current is not null; current = current.parent)
            {
                Assert.True(current.has_environment_constant_slots);
            }

            Assert.All(
                nodes.Where(node => !IsAncestorOf(node, slotOwner)),
                node => Assert.False(node.has_environment_constant_slots));
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void EnvBuiltinDoesNotRequireEnvironmentConstantMaterialization()
    {
        var options = ExplicitEnvironment("DOTNETJQ_FAST_PATH", "builtin-only");
        jq_state? state = libjq.jq_init(options);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "env.DOTNETJQ_FAST_PATH"));
            Assert.False(Assert.IsType<bytecode>(state.Bytecode).has_environment_constant_slots);

            libjq.jq_start(state, libjq.jv_null(), 0);
            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal("builtin-only", output.StringValue);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void RecompileClearsEnvironmentGraphMetadataFromTheReleasedProgram()
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "$ENV"));
            var released = Assert.IsType<bytecode>(state.Bytecode);
            Assert.True(released.has_environment_constant_slots);

            Assert.Equal(1, libjq.jq_compile(state, "."));

            Assert.False(released.has_environment_constant_slots);
            Assert.Empty(released.environment_constant_indexes);
            Assert.False(released.compiled_environment.IsValid);
            var replacement = Assert.IsType<bytecode>(state.Bytecode);
            Assert.False(replacement.has_environment_constant_slots);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void SequentialExplicitEnvironmentsStillReplaceEveryEnvironmentConstantFreshly()
    {
        jq_state? state = libjq.jq_init(ExplicitEnvironment("DOTNETJQ_FAST_PATH", "first"));
        var output = libjq.jv_invalid();
        jvp_object? firstStorage = null;
        jvp_object? secondStorage = null;
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "$ENV"));
            Assert.True(Assert.IsType<bytecode>(state.Bytecode).has_environment_constant_slots);

            libjq.jq_start(state, libjq.jv_null(), 0);
            output = libjq.jq_next(state);
            firstStorage = Assert.IsType<jvp_object>(output.Value);
            AssertEnvironmentValue(output, "DOTNETJQ_FAST_PATH", "first");
            libjq.jv_free(output);
            output = libjq.jv_invalid();

            libjq.jq_reset(state);
            libjq.jq_configure_execution(
                state,
                ExplicitEnvironment("DOTNETJQ_FAST_PATH", "second"));
            libjq.jq_start(state, libjq.jv_null(), 0);
            output = libjq.jq_next(state);
            secondStorage = Assert.IsType<jvp_object>(output.Value);
            AssertEnvironmentValue(output, "DOTNETJQ_FAST_PATH", "second");

            Assert.NotSame(firstStorage, secondStorage);
            Assert.Equal(0, firstStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
        }

        Assert.NotNull(secondStorage);
        Assert.Equal(0, secondStorage.Refcnt.Count);
    }

    [Fact]
    public void IndependentProgramsNeverShareExplicitEnvironmentOwners()
    {
        jq_state? firstState = libjq.jq_init(ExplicitEnvironment("DOTNETJQ_FAST_PATH", "first"));
        jq_state? secondState = libjq.jq_init(ExplicitEnvironment("DOTNETJQ_FAST_PATH", "second"));
        var firstOutput = libjq.jv_invalid();
        var secondOutput = libjq.jv_invalid();
        jvp_object? firstStorage = null;
        jvp_object? secondStorage = null;
        try
        {
            Assert.Equal(1, libjq.jq_compile(firstState, "$ENV"));
            Assert.Equal(1, libjq.jq_compile(secondState, "$ENV"));

            libjq.jq_start(firstState, libjq.jv_null(), 0);
            firstOutput = libjq.jq_next(firstState);
            firstStorage = Assert.IsType<jvp_object>(firstOutput.Value);
            AssertEnvironmentValue(firstOutput, "DOTNETJQ_FAST_PATH", "first");

            libjq.jq_start(secondState, libjq.jv_null(), 0);
            secondOutput = libjq.jq_next(secondState);
            secondStorage = Assert.IsType<jvp_object>(secondOutput.Value);
            AssertEnvironmentValue(secondOutput, "DOTNETJQ_FAST_PATH", "second");

            Assert.NotSame(firstStorage, secondStorage);
        }
        finally
        {
            libjq.jv_free(firstOutput);
            libjq.jv_free(secondOutput);
            libjq.jq_teardown(ref firstState);
            libjq.jq_teardown(ref secondState);
        }

        Assert.NotNull(firstStorage);
        Assert.NotNull(secondStorage);
        Assert.Equal(0, firstStorage.Refcnt.Count);
        Assert.Equal(0, secondStorage.Refcnt.Count);
    }

    private static JqExecutionOptions ExplicitEnvironment(string name, string value) =>
        new()
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [name] = value,
            },
        };

    private static void AssertEnvironmentValue(jv environment, string name, string expected)
    {
        var value = libjq.jv_object_get(libjq.jv_copy(environment), libjq.jv_string(name));
        try
        {
            Assert.True(value.IsValid);
            Assert.Equal(expected, value.StringValue);
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    private static bool IsAncestorOf(bytecode candidate, bytecode descendant)
    {
        for (var current = descendant; current is not null; current = current.parent)
        {
            if (ReferenceEquals(candidate, current))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<bytecode> EnumerateBytecode(bytecode root)
    {
        var pending = new Stack<bytecode>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            yield return current;
            foreach (var subfunction in current.subfunctions)
            {
                pending.Push(subfunction);
            }
        }
    }
}
