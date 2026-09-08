using System.Globalization;
using System.Text;

namespace DotNetJq.Tests;

/// <summary>
/// Source-facing regressions for jq-1.8.2 src/compile.c's ARG_NEWCLOSURE
/// overflow guards, also exercised by tests/shtest issue #3458.
/// </summary>
public sealed class CompilerClosureLimitCompatibilityTests
{
    private const string ExpectedDiagnostic =
        "jq: error: too many function parameters or local function definitions (max 4095)";

    [Fact]
    public void MoreThan4095FunctionParametersAreRejectedBeforeExecution()
    {
        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile(BuildFunctionWithParameters(4097)));

        Assert.Equal(ExpectedDiagnostic, exception.Message);
    }

    [Fact]
    public void A4097thLocalFunctionClosureIsRejectedBeforeExecution()
    {
        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile(BuildLocalFunctions(4097, referenceAll: true)));

        Assert.Equal(ExpectedDiagnostic, exception.Message);
    }

    [Fact]
    public void The4096EncodableLocalClosureSlotsRemainAccepted()
    {
        _ = JqProgram.Compile(BuildLocalFunctions(4096, referenceAll: false));
    }

    [Fact]
    public void UnreferencedOversizedDefinitionsAreDroppedLikeUpstream()
    {
        _ = JqProgram.Compile(BuildUnusedFunctionWithParameters(4097));
        _ = JqProgram.Compile(BuildUnusedRecursiveFunctionWithParameters(4097));
        _ = JqProgram.Compile(BuildLocalFunctions(4097, referenceAll: false));
    }

    private static string BuildFunctionWithParameters(int count)
    {
        var source = new StringBuilder("def f(");
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                source.Append(';');
            }

            source.Append('a').Append(index.ToString(CultureInfo.InvariantCulture));
        }

        source.Append("): .; f(");
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                source.Append(';');
            }

            source.Append('0');
        }

        return source.Append(')').ToString();
    }

    private static string BuildUnusedFunctionWithParameters(int count)
    {
        var source = new StringBuilder("def f(");
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                source.Append(';');
            }

            source.Append('a').Append(index.ToString(CultureInfo.InvariantCulture));
        }

        return source.Append("): .; 0").ToString();
    }

    private static string BuildUnusedRecursiveFunctionWithParameters(int count)
    {
        var source = new StringBuilder("def f(");
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                source.Append(';');
            }

            source.Append('a').Append(index.ToString(CultureInfo.InvariantCulture));
        }

        source.Append("): f(");
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                source.Append(';');
            }

            source.Append('a').Append(index.ToString(CultureInfo.InvariantCulture));
        }

        return source.Append("); 0").ToString();
    }

    private static string BuildLocalFunctions(int count, bool referenceAll)
    {
        var source = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            source.Append("def f").Append(suffix).Append(':').Append(suffix).Append(';');
        }

        if (!referenceAll)
        {
            return source.Append('0').ToString();
        }

        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                source.Append('+');
            }

            source.Append('f').Append(index.ToString(CultureInfo.InvariantCulture));
        }

        return source.ToString();
    }
}
