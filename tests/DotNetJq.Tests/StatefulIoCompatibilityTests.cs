using System.Text.Json;

namespace DotNetJq.Tests;

public sealed class StatefulIoCompatibilityTests
{
    [Fact]
    public void MissingCapabilitiesPreserveLibjqDefaultsWithoutAmbientIo()
    {
        var output = Execute(
            JqProgram.Compile("[(try input catch .), [inputs], (1|debug), (2|stderr)]"),
            "null");

        Assert.Equal(["[\"break\",[],1,2]"], output);

        var positionOutput = Execute(
            JqProgram.Compile("[input_filename, (try input_line_number catch .)]"),
            "null");

        Assert.Equal(["[null,\"Unknown input line number\"]"], positionOutput);
    }

    [Fact]
    public void InputAndInputsPullValuesLazilyUntilEnd()
    {
        var source = new QueueInputSource(
            JqInputReadResult.FromValue(Json("1")),
            JqInputReadResult.FromValue(Json("2")),
            JqInputReadResult.FromValue(Json("3")),
            JqInputReadResult.End);

        var output = Execute(
            JqProgram.Compile("[input, inputs]"),
            "null",
            new JqExecutionCapabilities { Input = source });

        Assert.Equal(["[1,2,3]"], output);
        Assert.Equal(4, source.ReadCount);
    }

    [Fact]
    public void InputEndRaisesCatchableBreakAndInputsSuppressesIt()
    {
        var endSource = new QueueInputSource(JqInputReadResult.End);
        Assert.Equal(
            ["\"break\""],
            Execute(
                JqProgram.Compile("try input catch ."),
                "null",
                new JqExecutionCapabilities { Input = endSource }));
        Assert.Equal(1, endSource.ReadCount);

        var breakErrorSource = new QueueInputSource(
            JqInputReadResult.FromError(Json("\"break\"")),
            JqInputReadResult.FromValue(Json("99")));
        Assert.Equal(
            ["[]"],
            Execute(
                JqProgram.Compile("[inputs]"),
                "null",
                new JqExecutionCapabilities { Input = breakErrorSource }));
        Assert.Equal(1, breakErrorSource.ReadCount);
    }

    [Fact]
    public void InputsPropagatesSourceErrorsWithoutReadingPastThem()
    {
        var source = new QueueInputSource(
            JqInputReadResult.FromValue(Json("1")),
            JqInputReadResult.FromError(Json("\"source failure\"")),
            JqInputReadResult.FromValue(Json("2")));

        var output = Execute(
            JqProgram.Compile("try inputs catch ."),
            "null",
            new JqExecutionCapabilities { Input = source });

        Assert.Equal(["1", "\"source failure\""], output);
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public void InputByteLimitBoundsEachCallerSuppliedValueBeforeReparsing()
    {
        var source = new QueueInputSource(
            JqInputReadResult.FromValue(Json("\"é\"")),
            JqInputReadResult.End);
        var result = JqProgram.Compile("input").ExecuteDetailed(
            "0",
            new JqExecutionOptions { MaxInputBytes = 3 },
            new JqExecutionCapabilities { Input = source });

        Assert.Equal(JqExecutionOutcomeKind.RuntimeError, result.Outcome.Kind);
        Assert.Equal(
            "jq input-byte limit exceeded",
            Assert.IsType<JqRuntimeException>(result.Outcome.RuntimeError).Message);
        Assert.Empty(result.Outputs);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public void InputByteLimitIsAggregateAcrossPrimaryAndLazyInputs()
    {
        var source = new QueueInputSource(
            JqInputReadResult.FromValue(Json("1")),
            JqInputReadResult.FromValue(Json("2")),
            JqInputReadResult.FromValue(Json("3")),
            JqInputReadResult.FromValue(Json("4")));
        var result = JqProgram.Compile("[inputs]").ExecuteDetailed(
            "0",
            new JqExecutionOptions { MaxInputBytes = 3 },
            new JqExecutionCapabilities { Input = source });

        Assert.Equal(JqExecutionOutcomeKind.RuntimeError, result.Outcome.Kind);
        Assert.Equal(
            "jq input-byte limit exceeded",
            Assert.IsType<JqRuntimeException>(result.Outcome.RuntimeError).Message);
        Assert.Empty(result.Outputs);
        Assert.Equal(3, source.ReadCount);
    }

    [Fact]
    public void InputByteLimitAcceptsExactUtf8AggregateAndCountsErrorValues()
    {
        var exactSource = new QueueInputSource(
            JqInputReadResult.FromValue(Json("\"é\"")),
            JqInputReadResult.End);
        var exact = JqProgram.Compile("input").ExecuteDetailed(
            "0",
            new JqExecutionOptions { MaxInputBytes = 5 },
            new JqExecutionCapabilities { Input = exactSource });

        Assert.Equal(JqExecutionOutcomeKind.Completed, exact.Outcome.Kind);
        Assert.Equal("é", Assert.Single(exact.Outputs).GetString());
        Assert.Equal(1, exactSource.ReadCount);

        var errorSource = new QueueInputSource(
            JqInputReadResult.FromError(Json("\"x\"")));
        var error = JqProgram.Compile("try input catch .").ExecuteDetailed(
            "0",
            new JqExecutionOptions { MaxInputBytes = 3 },
            new JqExecutionCapabilities { Input = errorSource });

        Assert.Equal(JqExecutionOutcomeKind.Completed, error.Outcome.Kind);
        Assert.Equal(
            "jq input-byte limit exceeded",
            Assert.Single(error.Outputs).GetString());
        Assert.Equal(1, errorSource.ReadCount);
    }

    [Fact]
    public void CaughtInputByteLimitRemainsExhaustedForLaterInputs()
    {
        var source = new QueueInputSource(
            JqInputReadResult.FromValue(Json("\"é\"")),
            JqInputReadResult.FromValue(Json("1")));
        var result = JqProgram
            .Compile("[(try input catch .), (try input catch .)]")
            .ExecuteDetailed(
                "0",
                new JqExecutionOptions { MaxInputBytes = 3 },
                new JqExecutionCapabilities { Input = source });

        Assert.Equal(JqExecutionOutcomeKind.Completed, result.Outcome.Kind);
        Assert.Equal(
            ["jq input-byte limit exceeded", "jq input-byte limit exceeded"],
            Assert.Single(result.Outputs).EnumerateArray().Select(element => element.GetString()));
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public void DebugAndStderrReceiveStableRawValuesInEvaluationOrder()
    {
        var writes = new List<string>();
        var debug = new RecordingSink("debug", writes);
        var standardError = new RecordingSink("stderr", writes);

        var output = Execute(
            JqProgram.Compile("1 | debug(\"a\",2) | stderr"),
            "null",
            new JqExecutionCapabilities
            {
                Debug = debug,
                StandardError = standardError,
            });

        Assert.Equal(["1"], output);
        Assert.Equal(["debug:\"a\"", "debug:2", "stderr:1"], writes);
        Assert.Collection(
            debug.Values,
            value => Assert.Equal("a", value.GetString()),
            value => Assert.Equal(2, value.GetInt32()));
        Assert.Equal("1", Assert.Single(standardError.Values).GetRawText());
    }

    [Fact]
    public void DebugMessageStreamsPreserveIdentityOnlyAfterCleanCompletion()
    {
        var emptyWrites = new List<string>();
        Assert.Equal(
            ["1"],
            Execute(
                JqProgram.Compile("debug(empty)"),
                "1",
                new JqExecutionCapabilities
                {
                    Debug = new RecordingSink("debug", emptyWrites),
                }));
        Assert.Empty(emptyWrites);

        var orderedWrites = new List<string>();
        Assert.Equal(
            ["\"message failure\""],
            Execute(
                JqProgram.Compile(
                    "try (1|debug(\"first\",error(\"message failure\"),\"last\")) catch ."),
                "null",
                new JqExecutionCapabilities
                {
                    Debug = new RecordingSink("debug", orderedWrites),
                }));
        Assert.Equal(["debug:\"first\""], orderedWrites);
    }

    [Fact]
    public void InputValueAndErrorPositionsReplaceTheCurrentPosition()
    {
        var valueSource = new QueueInputSource(
            JqInputReadResult.FromValue(
                Json("2"),
                new JqInputPosition("secondary.json", 3)),
            JqInputReadResult.End);
        var primary = new JqExecutionInput(
            Json("1"),
            new JqInputPosition("primary.json", 1));

        Assert.Equal(
            ["[\"primary.json\",1,2,\"secondary.json\",3]"],
            Execute(
                JqProgram.Compile(
                    "[input_filename,input_line_number,input," +
                    "input_filename,input_line_number]"),
                primary,
                new JqExecutionCapabilities { Input = valueSource }));

        var errorSource = new QueueInputSource(
            JqInputReadResult.FromError(
                Json("\"bad input\""),
                new JqInputPosition("broken.json", 7)));
        Assert.Equal(
            ["[\"bad input\",\"broken.json\",7]"],
            Execute(
                JqProgram.Compile(
                    "try input catch [.,input_filename,input_line_number]"),
                primary,
                new JqExecutionCapabilities { Input = errorSource }));
    }

    [Fact]
    public void EndOfInputLeavesTheLastKnownPositionIntact()
    {
        var source = new QueueInputSource(
            JqInputReadResult.FromValue(
                Json("2"),
                new JqInputPosition("last.json", 9)),
            JqInputReadResult.End);

        var output = Execute(
            JqProgram.Compile(
                "[input, (try input catch \"end\")," +
                "input_filename,input_line_number]"),
            new JqExecutionInput(Json("1"), new JqInputPosition("first.json", 1)),
            new JqExecutionCapabilities { Input = source });

        Assert.Equal(["[2,\"end\",\"last.json\",9]"], output);
    }

    [Fact]
    public void HostInputExceptionsBypassJqCatchAndRetainTheirIdentity()
    {
        var failure = new JqRuntimeException("host input failure");
        using var execution = JqProgram.Compile("try input catch \"caught\"")
            .StartExecution(
                "null",
                capabilities: new JqExecutionCapabilities
                {
                    Input = new ThrowingInputSource(failure),
                });

        var thrown = Assert.Throws<JqRuntimeException>(() => Drain(execution));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void HostSinkExceptionsBypassJqCatchAndRetainTheirIdentity()
    {
        var debugFailure = new JqRuntimeException("host debug failure");
        using (var execution = JqProgram.Compile("try (1|debug) catch \"caught\"")
            .StartExecution(
                "null",
                capabilities: new JqExecutionCapabilities
                {
                    Debug = new ThrowingSink(debugFailure),
                }))
        {
            var thrown = Assert.Throws<JqRuntimeException>(() => Drain(execution));
            Assert.Same(debugFailure, thrown);
        }

        var stderrFailure = new JqRuntimeException("host stderr failure");
        using var stderrExecution = JqProgram.Compile("try (1|stderr) catch \"caught\"")
            .StartExecution(
                "null",
                capabilities: new JqExecutionCapabilities
                {
                    StandardError = new ThrowingSink(stderrFailure),
                });
        var stderrThrown = Assert.Throws<JqRuntimeException>(() => Drain(stderrExecution));
        Assert.Same(stderrFailure, stderrThrown);
    }

    [Fact]
    public async Task IndependentProgramsKeepConcurrentIoCapabilitiesIsolatedAsync()
    {
        using var barrier = new Barrier(2);
        var firstWrites = new List<string>();
        var secondWrites = new List<string>();

        var first = Task.Run(() =>
        {
            using var program = JqProgram.Compile("[input, debug]");
            return Execute(
                program,
                "null",
                new JqExecutionCapabilities
                {
                    Input = new CoordinatedInputSource(Json("1"), barrier),
                    Debug = new RecordingSink("first", firstWrites),
                });
        });
        var second = Task.Run(() =>
        {
            using var program = JqProgram.Compile("[input, debug]");
            return Execute(
                program,
                "null",
                new JqExecutionCapabilities
                {
                    Input = new CoordinatedInputSource(Json("2"), barrier),
                    Debug = new RecordingSink("second", secondWrites),
                });
        });

        var results = await Task.WhenAll(first, second);

        Assert.Equal(["[1,null]"], results[0]);
        Assert.Equal(["[2,null]"], results[1]);
        Assert.Equal(["first:null"], firstWrites);
        Assert.Equal(["second:null"], secondWrites);
    }

    private static string[] Execute(
        JqProgram program,
        string input,
        JqExecutionCapabilities? capabilities = null)
    {
        using var execution = program.StartExecution(input, capabilities: capabilities);
        return Drain(execution);
    }

    private static string[] Execute(
        JqProgram program,
        JqExecutionInput input,
        JqExecutionCapabilities? capabilities = null)
    {
        using var execution = program.StartExecution(input, capabilities: capabilities);
        return Drain(execution);
    }

    private static string[] Drain(JqExecution execution)
    {
        var output = new List<string>();
        while (execution.TryRead(out var value))
        {
            output.Add(value.GetRawText());
        }

        return output.ToArray();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class QueueInputSource(params JqInputReadResult[] results) : IJqInputSource
    {
        private readonly Queue<JqInputReadResult> results = new(results);

        public int ReadCount { get; private set; }

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return results.Count == 0 ? JqInputReadResult.End : results.Dequeue();
        }
    }

    private sealed class ThrowingInputSource(Exception exception) : IJqInputSource
    {
        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }

    private sealed class RecordingSink(string name, List<string> orderedWrites) : IJqValueSink
    {
        public List<JsonElement> Values { get; } = [];

        public void Write(JsonElement value)
        {
            Values.Add(value);
            orderedWrites.Add($"{name}:{value.GetRawText()}");
        }
    }

    private sealed class ThrowingSink(Exception exception) : IJqValueSink
    {
        public void Write(JsonElement value) => throw exception;
    }

    private sealed class CoordinatedInputSource(JsonElement value, Barrier barrier) : IJqInputSource
    {
        private int readCount;

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref readCount) != 1)
            {
                return JqInputReadResult.End;
            }

            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken))
            {
                throw new TimeoutException("Concurrent jq executions did not reach input together.");
            }

            return JqInputReadResult.FromValue(value);
        }
    }
}
