using System.Text.Json;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class IndexSliceContinuationOracleTests
{
    public static TheoryData<string, string[]> GeneratedValueCases => new()
    {
        {
            "({a:1},{a:2})[(\"a\",\"a\")]",
            ["1", "2", "1", "2"]
        },
        {
            "(\"abc\",\"XYZ\")[(0,1):(2,3)]",
            ["\"ab\"", "\"XY\"", "\"abc\"", "\"XYZ\"", "\"b\"", "\"Y\"", "\"bc\"", "\"YZ\""]
        },
        {
            "({a:1},{a:2}).a",
            ["1", "2"]
        },
        {
            "(1,{a:2}).a?",
            ["2"]
        },
    };

    public static TheoryData<string, string> DiagnosticCases => new()
    {
        {
            "try (error(\"target\"))[error(\"key\")] catch .",
            "\"key\""
        },
        {
            "try (error(\"target\"))[error(\"key\")]? catch .",
            "\"key\""
        },
        {
            "try (error(\"target\"))[(error(\"start\")):0] catch .",
            "\"start\""
        },
        {
            "try (error(\"target\"))[0:(error(\"end\"))] catch .",
            "\"end\""
        },
    };

    [Theory]
    [MemberData(nameof(GeneratedValueCases))]
    public async Task GeneratedKeysAndBoundsAreTheOuterContinuations(
        string source,
        string[] expected)
    {
        var managed = Execute(source);

        Assert.Equal(expected, managed);
        await AssertMatchesOracle(source, "null", managed);
    }

    [Theory]
    [MemberData(nameof(DiagnosticCases))]
    public async Task KeyAndBoundsFailBeforeTheTargetIsOpened(
        string source,
        string expected)
    {
        var managed = Execute(source);

        Assert.Equal([expected], managed);
        await AssertMatchesOracle(source, "null", managed);
    }

    [Fact]
    public async Task IndexDebugTraceShowsEachKeyReopeningTheTarget()
    {
        const string source =
            "((([\"target\",1]|debug)|{a:1})," +
            "((([\"target\",2]|debug)|{a:2})))" +
            "[((([\"key\",1]|debug)|\"a\")," +
            "((([\"key\",2]|debug)|\"a\")))]";
        string[] expectedOutput = ["1", "2", "1", "2"];
        string[] expectedDebug =
        [
            "[\"key\",1]",
            "[\"target\",1]",
            "[\"target\",2]",
            "[\"key\",2]",
            "[\"target\",1]",
            "[\"target\",2]",
        ];
        var debug = new RecordingSink();

        var managed = Execute(source, new JqExecutionCapabilities { Debug = debug });

        Assert.Equal(expectedOutput, managed);
        Assert.Equal(expectedDebug, debug.Values);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(expectedOutput, oracle.OutputLines);
        Assert.Equal(
            expectedDebug.Select(value => $"[\"DEBUG:\",{value}]").ToArray(),
            SplitLines(oracle.StandardError));
    }

    [Fact]
    public async Task SliceDebugTraceNestsStartThenEndThenTarget()
    {
        const string source =
            "(((\"abc\"|[\"target\",.]|debug|.[1]))," +
            "((\"XYZ\"|[\"target\",.]|debug|.[1])))" +
            "[((0|[\"start\",.]|debug|.[1]),(1|[\"start\",.]|debug|.[1])):" +
            "((2|[\"end\",.]|debug|.[1]),(3|[\"end\",.]|debug|.[1]))]";
        string[] expectedOutput =
            ["\"ab\"", "\"XY\"", "\"abc\"", "\"XYZ\"", "\"b\"", "\"Y\"", "\"bc\"", "\"YZ\""];
        string[] expectedDebug =
        [
            "[\"start\",0]",
            "[\"end\",2]",
            "[\"target\",\"abc\"]",
            "[\"target\",\"XYZ\"]",
            "[\"end\",3]",
            "[\"target\",\"abc\"]",
            "[\"target\",\"XYZ\"]",
            "[\"start\",1]",
            "[\"end\",2]",
            "[\"target\",\"abc\"]",
            "[\"target\",\"XYZ\"]",
            "[\"end\",3]",
            "[\"target\",\"abc\"]",
            "[\"target\",\"XYZ\"]",
        ];
        var debug = new RecordingSink();

        var managed = Execute(source, new JqExecutionCapabilities { Debug = debug });

        Assert.Equal(expectedOutput, managed);
        Assert.Equal(expectedDebug, debug.Values);
        await AssertMatchesOracle(source, "null", managed, expectedDebug);
    }

    [Fact]
    public async Task SliceEndIsReopenedForEveryStartContinuation()
    {
        const string source = "\"abcd\"[(0,1):input]";
        var input = new QueueInputSource(Json("2"), Json("3"));

        var managed = Execute(source, new JqExecutionCapabilities { Input = input });

        Assert.Equal(["\"ab\"", "\"bc\""], managed);
        Assert.Equal(2, input.ReadCount);
        await AssertMatchesOracle(source, "2\n3\n", managed, arguments: ["--null-input"]);
    }

    [Fact]
    public async Task SliceTargetIsReopenedForEveryCompletedKey()
    {
        const string source = "inputs[(0,1):2]";
        var input = new QueueInputSource(Json("\"abcd\""), Json("\"WXYZ\""));

        var managed = Execute(source, new JqExecutionCapabilities { Input = input });

        Assert.Equal(["\"ab\"", "\"WX\""], managed);
        Assert.Equal(4, input.ReadCount);
        await AssertMatchesOracle(
            source,
            "\"abcd\"\n\"WXYZ\"\n",
            managed,
            arguments: ["--null-input"]);
    }

    private static string[] Execute(
        string source,
        JqExecutionCapabilities? capabilities = null)
    {
        using var execution = JqProgram.Compile(source).StartExecution(
            "null",
            capabilities: capabilities);
        var output = new List<string>();
        while (execution.TryRead(out var value))
        {
            output.Add(value.GetRawText());
        }

        return output.ToArray();
    }

    private static async Task AssertMatchesOracle(
        string source,
        string input,
        IReadOnlyList<string> managed,
        IReadOnlyList<string>? debug = null,
        IEnumerable<string>? arguments = null)
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            input,
            arguments,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(managed, oracle.OutputLines);
        if (debug is not null)
        {
            Assert.Equal(
                debug.Select(value => $"[\"DEBUG:\",{value}]").ToArray(),
                SplitLines(oracle.StandardError));
        }
    }

    private static string[] SplitLines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class QueueInputSource(params JsonElement[] values) : IJqInputSource
    {
        private readonly Queue<JsonElement> values = new(values);

        internal int ReadCount { get; private set; }

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return values.Count == 0
                ? JqInputReadResult.End
                : JqInputReadResult.FromValue(values.Dequeue());
        }
    }

    private sealed class RecordingSink : IJqValueSink
    {
        internal List<string> Values { get; } = [];

        public void Write(JsonElement value) => Values.Add(value.GetRawText());
    }
}
