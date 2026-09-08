// DOTNETJQ COMPATIBILITY PORT TESTS
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: src/builtin.c, src/execute.c, src/jq.h, src/linker.c, src/locfile.c.

using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class CanonicalCallbackRoutingCompatibilityTests
{
    private static readonly object ConsoleErrorGate = new();

    [Fact]
    public void ParseDiagnosticsAndAggregateReachErrorCallbackInUpstreamOrder()
    {
        jq_state? state = libjq.jq_init();
        var reports = new List<string>();
        try
        {
            libjq.jq_set_error_cb(state, value => reports.Add(TakeFormatted(value)));

            Assert.Equal(0, libjq.jq_compile(state, "if"));

            Assert.True(reports.Count >= 2);
            Assert.Equal("jq: 1 compile error", reports[^1]);
            var collected = Assert.IsType<JqCompileException>(state.CompileError).Message;
            Assert.Equal(
                collected,
                string.Join(
                    Environment.NewLine,
                    reports.Take(reports.Count - 1).Select(
                        static report => report.TrimEnd('\r', '\n'))));
            Assert.DoesNotContain("compile error", collected, StringComparison.Ordinal);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void LinkerDetailAndAggregateReachErrorCallbackWithPinnedNewlinePayload()
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            Array.Empty<string>(),
            "/application");
        jq_state? state = libjq.jq_init();
        var reports = new List<string>();
        try
        {
            libjq.jq_set_error_cb(state, value => reports.Add(TakeFormatted(value)));

            Assert.Equal(
                0,
                libjq.jq_compile(
                    state,
                    "import \"missing\" as missing; missing::value",
                    resolver));

            Assert.Equal(
                ["jq: error: module not found: missing\n", "jq: 1 compile error"],
                reports);
            Assert.Equal(
                "jq: error: module not found: missing",
                Assert.IsType<JqCompileException>(state.CompileError).Message);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void BuiltinCallbacksUseTheJqShapedHaltAndGetterSurfaces()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(repositoryRoot, "src/DotNetJq/Port/src/builtin.c.cs"));

        Assert.Contains("jq_halt(jq, jv_invalid(), jv_invalid())", source, StringComparison.Ordinal);
        Assert.Contains("jq_halt(jq, exitCode, input)", source, StringComparison.Ordinal);
        Assert.Contains("jq_get_input_cb(jq, out var callback)", source, StringComparison.Ordinal);
        Assert.Contains("jq_get_debug_cb(jq, out var callback)", source, StringComparison.Ordinal);
        Assert.Contains("jq_get_stderr_cb(jq, out var callback)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("jq.Halt(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("jq.ReadInput()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("jq.EmitDebug(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("jq.EmitStandardError(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicCompileCollectsExceptionWithoutWritingAmbientStandardError()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(repositoryRoot, "src/DotNetJq/Public/JqProgram.cs"));

        var callback = source.IndexOf(
            "jq_set_error_cb(state, libjq.jv_free)",
            StringComparison.Ordinal);
        var compile = source.IndexOf("var compiled = resolver is null", StringComparison.Ordinal);
        Assert.True(callback >= 0 && callback < compile);

        lock (ConsoleErrorGate)
        {
            var previous = Console.Error;
            using var captured = new StringWriter();
            try
            {
                Console.SetError(captured);
                Assert.Throws<JqCompileException>(() => JqProgram.Compile("if"));
                Assert.Equal(string.Empty, captured.ToString());
            }
            finally
            {
                Console.SetError(previous);
            }
        }
    }

    private static string TakeFormatted(jv value)
    {
        var formatted = libjq.jq_format_error(value);
        try
        {
            return formatted.StringValue;
        }
        finally
        {
            libjq.jv_free(formatted);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the DotNetJq repository root.");
    }
}
