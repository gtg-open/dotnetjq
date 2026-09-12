using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class CliStartupLibraryCompatibilityTests
{
    [Fact]
    public void UserStartupLibraryIsAnExplicitCompileOptIn()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/home/.jq"] = "def from_home: \"loaded\";",
        });
        var resolver = new JqModuleResolver(fileSystem, ["~/.jq"], "/app", "/home");

        AssertCompileResult(
            resolver,
            loadUserStartupLibrary: false,
            expectedStatus: 0,
            expectedValue: null);
        AssertCompileResult(
            resolver,
            loadUserStartupLibrary: true,
            expectedStatus: 1,
            expectedValue: "\"loaded\"");
    }

    [Fact]
    public void MissingUserStartupLibraryIsOptionalButInvalidFoundLibraryIsNot()
    {
        var missing = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            ["~/.jq"],
            "/app",
            "/missing-home");
        AssertCompileResult(
            missing,
            loadUserStartupLibrary: true,
            expectedStatus: 1,
            expectedValue: "null",
            source: ".");

        var invalidFileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/home/.jq"] = "this is not jq source )",
        });
        var invalid = new JqModuleResolver(invalidFileSystem, ["~/.jq"], "/app", "/home");
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(
                0,
                libjq.jq_compile_args(
                    state,
                    ".",
                    libjq.jv_object(),
                    invalid,
                    programOrigin: "/program",
                    loadUserStartupLibrary: true));
            Assert.Contains("/home/.jq", state.CompileError?.Message, StringComparison.Ordinal);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static void AssertCompileResult(
        JqModuleResolver resolver,
        bool loadUserStartupLibrary,
        int expectedStatus,
        string? expectedValue,
        string source = "from_home")
    {
        jq_state? state = libjq.jq_init();
        try
        {
            var status = libjq.jq_compile_args(
                state,
                source,
                libjq.jv_object(),
                resolver,
                programOrigin: "/program",
                loadUserStartupLibrary);
            if (expectedStatus == 0)
            {
                Assert.Equal(0, status);
                Assert.NotNull(state.CompileError);
                return;
            }

            Assert.Equal(expectedStatus, status);
            libjq.jq_start(state, libjq.jv_null(), 0);
            var actual = libjq.jq_next(state);
            Assert.True(actual.IsValid);
            Assert.Equal(expectedValue, libjq.jv_dump_string(actual));
            Assert.False(libjq.jq_next(state).IsValid);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }
}
