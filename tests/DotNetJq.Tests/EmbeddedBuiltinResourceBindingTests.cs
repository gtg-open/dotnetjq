using System.Security.Cryptography;
using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;

namespace DotNetJq.Tests;

public sealed class EmbeddedBuiltinResourceBindingTests
{
    private const string Jq182BuiltinSha256 =
        "B8A5FD9579BE9B51C9A04E6620F8C1655539AA57EEA33A84E202A8DEA401F2A4";

    [Fact]
    public void LoaderConsumesThePinnedByteExactManifestResourceAsOneLibrary()
    {
        var bytes = LoadBuiltinBytes();
        Assert.Equal(9_631, bytes.Length);
        Assert.Equal(Jq182BuiltinSha256, Convert.ToHexString(SHA256.HashData(bytes)));

        var source = Encoding.UTF8.GetString(bytes);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(source));
        var library = JqGeneratedParser.ParseLibrarySource(source, "<builtin>");
        try
        {
            Assert.False(libjq.block_has_main(library));
            Assert.True(libjq.block_has_only_binders_and_imports(
                library,
                libjq.OP_IS_CALL_PSEUDO));
            Assert.DoesNotContain(
                RootInstructions(library),
                static instruction => instruction.op == opcode.DEPS);
            var definitions = RootInstructions(library)
                .Where(static instruction => instruction.op == opcode.CLOSURE_CREATE)
                .Select(static instruction => (instruction.symbol, instruction.nformals))
                .ToArray();
            Assert.Equal(106, definitions.Length);
            Assert.Equal(106, definitions.Distinct().Count());
        }
        finally
        {
            libjq.block_free(library);
        }
    }

    [Fact]
    public void EveryTopLevelSourceDefinitionIsBoundAndSourceWinsOverNativeFallbacks()
    {
        var sourceBytes = LoadBuiltinBytes();
        var source = Encoding.UTF8.GetString(sourceBytes);
        var library = JqGeneratedParser.ParseLibrarySource(source, "<builtin>");
        var definitions = RootInstructions(library)
            .Where(static instruction => instruction.op == opcode.CLOSURE_CREATE)
            .ToArray();

        var calls = libjq.gen_noop();
        var callInstructions = new List<inst>(definitions.Length);
        try
        {
            foreach (var definition in definitions)
            {
                var arguments = libjq.gen_noop();
                for (var index = 0; index < definition.nformals; index++)
                {
                    arguments = libjq.BLOCK(
                        arguments,
                        libjq.gen_lambda(libjq.gen_noop()));
                }

                var call = libjq.gen_call(definition.symbol!, arguments);
                callInstructions.Add(call.first!);
                calls = libjq.BLOCK(calls, call);
            }

            var bound = libjq.block_bind_referenced(
                library,
                calls,
                libjq.OP_IS_CALL_PSEUDO);
            library = default;
            calls = default;
            try
            {
                // Bind the jq-coded library first. What remains unbound at an
                // explicit source spelling is exactly its private C-function
                // dependency set; compiler-generated operator calls have a
                // wider expression location and therefore do not masquerade
                // as source-level calls here.
                var privateNativeReferences = AllInstructions(bound)
                    .Where(instruction =>
                        instruction.op == opcode.CALL_JQ &&
                        instruction.bound_by is null &&
                        instruction.symbol is { } symbol &&
                        symbol.StartsWith('_') &&
                        SourceRangeEqualsSymbol(sourceBytes, instruction))
                    .Select(static instruction =>
                        $"{instruction.symbol}/{instruction.nactuals}")
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(
                    [
                        "_group_by_impl/1",
                        "_match_impl/3",
                        "_max_by_impl/1",
                        "_min_by_impl/1",
                        "_sort_by_impl/1",
                        "_strindices/1",
                        "_unique_by_impl/1",
                    ],
                    privateNativeReferences);

                Assert.Equal(106, callInstructions.Count);
                foreach (var call in callInstructions)
                {
                    var binding = Assert.IsType<inst>(call.bound_by);
                    Assert.Equal(opcode.CLOSURE_CREATE, binding.op);
                    Assert.Equal((call.symbol, call.nactuals),
                        (binding.symbol, binding.nformals));
                }
            }
            finally
            {
                libjq.block_free(bound);
            }
        }
        finally
        {
            libjq.block_free(calls);
            libjq.block_free(library);
        }

        Assert.Equal("[2,3]", ExecuteOne("map(. + 1)", "[1,2]"));
        Assert.Equal(
            "[[1,2],[1,2]]",
            ExecuteOne("def map(f): [f, f]; map(.)", "[1,2]"));
        Assert.Equal(
            "[{\"offset\":0,\"length\":1,\"string\":\"a\",\"captures\":[]},true]",
            ExecuteOne("[match(\"a\"), test(\"a\")]", "\"a\""));
        var inventory = Assert.Single(JqProgram.Compile("builtins").Execute("null"));
        Assert.DoesNotContain(
            inventory.EnumerateArray(),
            static item => item.GetString()!.StartsWith('_'));
    }

    [Fact]
    public void BuiltinsBindIsTheCanonicalZeroErrorBlockBinder()
    {
        var program = JqGeneratedParser.ParseSource("map(.)");
        using var state = new jq_state(JqExecutionOptions.Default);

        try
        {
            Assert.Equal(0, libjq.builtins_bind(state, ref program));
            var call = Assert.Single(
                AllInstructions(program),
                static instruction =>
                    instruction.op == opcode.CALL_JQ &&
                    instruction.symbol == "map" &&
                    instruction.source.start >= 0);
            var binding = Assert.IsType<inst>(call.bound_by);
            Assert.Equal((opcode.CLOSURE_CREATE, "map", 1),
                (binding.op, binding.symbol, binding.nformals));
            Assert.Contains(
                RootInstructions(program),
                instruction => ReferenceEquals(instruction, binding));
        }
        finally
        {
            libjq.block_free(program);
        }
    }

    [Fact]
    public void ImportedLibrariesCloseOverTheSameEmbeddedBuiltinDefinitions()
    {
        var resolver = new JqModuleResolver(
            new JqInMemoryFileSystem(new Dictionary<string, string>
            {
                ["/modules/consumer.jq"] = "def increment: map(. + 1);",
            }),
            ["/modules"],
            "/program");
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "include \"consumer\"; increment", resolver));
            var compiled = Assert.IsType<bytecode>(state.Bytecode);
            Assert.Contains("increment", BytecodeNames(compiled));
            Assert.Contains("map", BytecodeNames(compiled));
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }

        Assert.Equal(
            "[2,3]",
            ExecuteOne(JqProgram.Compile("include \"consumer\"; increment", resolver), "[1,2]"));
    }

    [Fact]
    public void PrivateDefinitionsExecuteFromTheEmbeddedSourceLibrary()
    {
        Assert.Equal("{\"a\":2}", ExecuteOne("_assign(.a; 2)", "{\"a\":1}"));
        Assert.Equal("{\"a\":2}", ExecuteOne("_modify(.a; . + 1)", "{\"a\":1}"));
        Assert.Equal("[1,2,[3]]", ExecuteOne("_flatten(1)", "[1,[2,[3]]]"));
    }

    private static string ExecuteOne(string filter, string input) =>
        Assert.Single(JqProgram.Compile(filter).Execute(input)).GetRawText();

    private static string ExecuteOne(JqProgram program, string input) =>
        Assert.Single(program.Execute(input)).GetRawText();

    private static byte[] LoadBuiltinBytes()
    {
        using var stream = typeof(JqProgram).Assembly.GetManifestResourceStream(
            libjq.JqBuiltinResourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IEnumerable<inst> RootInstructions(block value)
    {
        for (var instruction = value.first;
             instruction is not null;
             instruction = instruction.next)
        {
            yield return instruction;
        }
    }

    private static IEnumerable<inst> AllInstructions(block value)
    {
        foreach (var instruction in RootInstructions(value))
        {
            yield return instruction;
            foreach (var argument in AllInstructions(instruction.arglist))
            {
                yield return argument;
            }

            foreach (var nested in AllInstructions(instruction.subfn))
            {
                yield return nested;
            }
        }
    }

    private static bool SourceRangeEqualsSymbol(byte[] source, inst instruction)
    {
        if (instruction.symbol is not { Length: > 0 } symbol ||
            instruction.source.start < 0 ||
            instruction.source.end < instruction.source.start ||
            instruction.source.end > source.Length)
        {
            return false;
        }

        var sourceRange = source.AsSpan(
            instruction.source.start,
            instruction.source.end - instruction.source.start);
        return sourceRange.SequenceEqual(Encoding.UTF8.GetBytes(symbol));
    }

    private static IEnumerable<string> BytecodeNames(bytecode value)
    {
        var name = libjq.jv_object_get(value.debuginfo, "name");
        try
        {
            if (name.Kind == jv_kind.JV_KIND_STRING)
            {
                yield return name.StringValue;
            }
        }
        finally
        {
            libjq.jv_free(name);
        }

        foreach (var child in value.subfunctions)
        {
            foreach (var childName in BytecodeNames(child))
            {
                yield return childName;
            }
        }
    }
}
