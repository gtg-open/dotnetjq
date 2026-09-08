using System.Text.Json;

namespace DotNetJq.Tests.Compatibility;

public sealed class PublicExecutionFacadeContractTests
{
    [Fact]
    public void PublicModuleFacadeTypesDoNotLeakThePortNamespace()
    {
        Assert.Equal("DotNetJq", typeof(JqModuleResolver).Namespace);
        Assert.Equal("DotNetJq", typeof(JqModuleResourceLimits).Namespace);
        Assert.Equal("DotNetJq", typeof(JqModuleResolution).Namespace);
        Assert.Equal("DotNetJq", typeof(JqModuleFile).Namespace);
        Assert.Equal("DotNetJq", typeof(JqModuleFileKind).Namespace);
        Assert.Equal(typeof(JqModuleResolver),
            typeof(JqCompilationOptions).GetProperty(nameof(JqCompilationOptions.ModuleResolver))!.PropertyType);
    }

    [Fact]
    public void CompileAndExecuteReturnsJsonValuesInJqOrder()
    {
        var program = JqProgram.Compile(".values[]");

        var results = program.Execute("""{"values":[1,"two",null]}""");

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].GetInt32());
        Assert.Equal("two", results[1].GetString());
        Assert.Equal(JsonValueKind.Null, results[2].ValueKind);
    }

    [Fact]
    public void CompiledProgramCanBeReused()
    {
        var program = JqProgram.Compile(".value + 1");

        var first = Assert.Single(program.Execute("""{"value":1}"""));
        var second = Assert.Single(program.Execute("""{"value":4}"""));

        Assert.Equal(2, first.GetInt32());
        Assert.Equal(5, second.GetInt32());
    }

    [Theory]
    [InlineData("\"a\\u0000b\"|@html", "a\\0b")]
    [InlineData("\"a\\u0000b\"|@sh", "'a\\0b'")]
    [InlineData("[\"a\\u0000b\"]|@csv", "\"a\\0b\"")]
    [InlineData("[\"a\\u0000b\"]|@tsv", "a\\0b")]
    public void EscapingFormatsRenderEmbeddedNulLikeJq(string filter, string expected)
    {
        var result = Assert.Single(JqProgram.Compile(filter).Execute("null"));

        Assert.Equal(expected, result.GetString());
    }

    [Theory]
    [InlineData("text", "\"value\"", "value")]
    [InlineData("json", "\"value\"", "\"value\"")]
    [InlineData("csv", "[\"value\"]", "\"value\"")]
    public void FormatSelectorStopsAtEmbeddedNulLikeJq(
        string selector,
        string inputJson,
        string expected)
    {
        var filter = $"format(\"{selector}\\u0000junk\")";

        var result = Assert.Single(JqProgram.Compile(filter).Execute(inputJson));

        Assert.Equal(expected, result.GetString());
    }

    [Fact]
    public void InvalidFilterRaisesCompileException()
    {
        Assert.Throws<JqCompileException>(() => JqProgram.Compile("["));
    }

    [Fact]
    public void InvalidJsonRaisesParseException()
    {
        var program = JqProgram.Compile(".");

        var error = Assert.Throws<JqParseException>(() => program.Execute("{"));

        Assert.Equal(
            "Unfinished JSON term at EOF at line 1, column 1 (while parsing '{')",
            error.Message);
    }

    [Fact]
    public void PublicManagedStringInputDoesNotApplyTheInternalCStringNulBoundary()
    {
        var program = JqProgram.Compile(".");

        var error = Assert.Throws<JqParseException>(() => program.Execute("{}\0{}"));

        Assert.Equal(
            "Invalid numeric literal at line 1, column 4 (while parsing '{}')",
            error.Message);
    }

    [Fact]
    public void InputByteLimitCountsUtf8Bytes()
    {
        var program = JqProgram.Compile(".");
        var options = new JqExecutionOptions { MaxInputBytes = 3 };

        var exception = Assert.Throws<JqRuntimeException>(() => program.Execute("\"é\"", options));

        Assert.Contains("input-byte", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputValueLimitAppliesToGeneratedStream()
    {
        var program = JqProgram.Compile(".[]");
        var options = new JqExecutionOptions { MaxOutputValues = 1 };

        var exception = Assert.Throws<JqRuntimeException>(() => program.Execute("[1,2]", options));

        Assert.Contains("output-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputByteLimitCountsCompactJsonUtf8Bytes()
    {
        var program = JqProgram.Compile(".");
        var options = new JqExecutionOptions { MaxOutputBytes = 3 };

        var exception = Assert.Throws<JqRuntimeException>(() => program.Execute("\"é\"", options));

        Assert.Contains("output-byte", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsObservedBeforeParsingInput()
    {
        var program = JqProgram.Compile(".");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new JqExecutionOptions { CancellationToken = cancellation.Token };

        Assert.Throws<OperationCanceledException>(() => program.Execute("not-json", options));
    }
}
