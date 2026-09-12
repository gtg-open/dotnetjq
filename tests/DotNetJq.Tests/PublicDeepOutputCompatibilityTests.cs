using System.Text;

namespace DotNetJq.Tests;

public sealed class PublicDeepOutputCompatibilityTests
{
    private const int JqMaximumStructuralDepth = 10_000;

    [Fact]
    public void ExecuteMaterializesOutputAtJqMaximumStructuralDepth()
    {
        var json = NestedArrayJson(JqMaximumStructuralDepth);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        var options = new JqExecutionOptions
        {
            MaxInputBytes = byteCount,
            MaxOutputBytes = byteCount,
            MaxOutputValues = 1,
        };

        var output = Assert.Single(JqProgram.Compile(".").Execute(json, options));

        Assert.Equal(json, output.GetRawText());
    }

    [Fact]
    public void ExecuteAppliesOutputByteLimitBeforeDeepJsonMaterialization()
    {
        var json = NestedArrayJson(JqMaximumStructuralDepth);
        var options = new JqExecutionOptions
        {
            MaxOutputBytes = Encoding.UTF8.GetByteCount(json) - 1,
        };

        var error = Assert.Throws<JqRuntimeException>(
            () => JqProgram.Compile(".").Execute(json, options));

        Assert.Equal("jq output-byte limit exceeded", error.Message);
    }

    [Fact]
    public void OutputByteLimitPrecedesParsingOfTheBeyondDepthPrinterMarker()
    {
        var options = new JqExecutionOptions { MaxOutputBytes = 0 };
        using var program = JqProgram.Compile(
            "reduce range(0; 10001) as $index (0; [.])");

        var error = Assert.Throws<JqRuntimeException>(
            () => program.Execute("null", options));

        // jq's printer emits the deliberately non-JSON
        // <skipped: too deep> marker beyond depth 10,000. The configured byte
        // limit must reject those bytes before JsonDocument parses them.
        Assert.Equal("jq output-byte limit exceeded", error.Message);
    }

    private static string NestedArrayJson(int depth) =>
        string.Concat(new string('[', depth), "0", new string(']', depth));
}
