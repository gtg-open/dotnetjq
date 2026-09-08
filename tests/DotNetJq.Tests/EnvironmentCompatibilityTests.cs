using DotNetJq.Port;

namespace DotNetJq.Tests;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class EnvironmentCompatibilityTests
{
    [Fact]
    public void DefaultEnvironmentPreservesNativeCompileAndExecutionTiming()
    {
        var variableName = "DOTNETJQ_ENVIRONMENT_SNAPSHOT_" + Guid.NewGuid().ToString("N");
        const string compiledValue = "visible-while-compiling";
        const string executionValue = "visible-when-env-runs";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, compiledValue);
            using var program = JqProgram.Compile(
                $"[$ENV.{variableName}, env.{variableName}]");

            var options = JqExecutionOptions.Default;
            Environment.SetEnvironmentVariable(variableName, executionValue);

            var output = Assert.Single(program.Execute("null", options));

            Assert.Equal(
                $"[\"{compiledValue}\",\"{executionValue}\"]",
                output.GetRawText());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public void DefaultEnvReadsAmbientEnvironmentAtEveryBuiltinInvocation()
    {
        var variableName = "DOTNETJQ_ENVIRONMENT_CALL_" + Guid.NewGuid().ToString("N");
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "before-input");
            using var program = JqProgram.Compile(
                $"env.{variableName}, (input | empty), env.{variableName}");
            var source = new MutatingInputSource(
                () => Environment.SetEnvironmentVariable(variableName, "after-input"));

            var result = program.ExecuteDetailed(
                "null",
                capabilities: new JqExecutionCapabilities { Input = source });

            Assert.Equal(
                ["\"before-input\"", "\"after-input\""],
                result.Outputs.Select(value => value.GetRawText()));
            Assert.Equal(JqExecutionOutcomeKind.Completed, result.Outcome.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public void ExplicitEmptyEnvironmentPreservesTheOptInSandboxBoundary()
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        };
        var output = Assert.Single(JqProgram.Compile("[$ENV, env]").Execute("null", options));

        Assert.Equal("[{},{}]", output.GetRawText());
    }

    [Fact]
    public void EnvironmentInputIsSnapshottedAndSharedByVariableAndBuiltin()
    {
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PAGER"] = "less",
        };
        var options = new JqExecutionOptions { Environment = supplied };
        supplied["PAGER"] = "more";
        supplied["AMBIENT"] = "not-visible";

        var output = Assert.Single(
            JqProgram.Compile("[$ENV.PAGER, env.PAGER, $ENV == env, $ENV.AMBIENT]")
                .Execute("null", options));

        Assert.Equal("[\"less\",\"less\",true,null]", output.GetRawText());
    }

    [Fact]
    public void ReusedProgramKeepsEnvironmentSnapshotsIsolatedPerExecution()
    {
        var program = JqProgram.Compile("[$ENV.PAGER, env.PAGER]");
        var configured = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PAGER"] = "less",
            },
        };
        var empty = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        Assert.Equal("[null,null]", Assert.Single(program.Execute("null", empty)).GetRawText());
        Assert.Equal(
            "[\"less\",\"less\"]",
            Assert.Single(program.Execute("null", configured)).GetRawText());
        Assert.Equal("[null,null]", Assert.Single(program.Execute("null", empty)).GetRawText());
    }

    [Fact]
    public void DefaultExecutionRestoresCompiledEnvironmentAfterExplicitOverride()
    {
        var variableName = "DOTNETJQ_ENVIRONMENT_RESTORE_" + Guid.NewGuid().ToString("N");
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "compiled");
            using var program = JqProgram.Compile(
                $"[$ENV.{variableName}, env.{variableName}]");

            var explicitOptions = new JqExecutionOptions
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [variableName] = "explicit",
                },
            };
            Assert.Equal(
                "[\"explicit\",\"explicit\"]",
                Assert.Single(program.Execute("null", explicitOptions)).GetRawText());

            Environment.SetEnvironmentVariable(variableName, "runtime-default");
            var defaultOptions = JqExecutionOptions.Default;
            Assert.Equal(
                "[\"compiled\",\"runtime-default\"]",
                Assert.Single(program.Execute("null", defaultOptions)).GetRawText());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public void DefaultTimeBuiltinsShareLibcStyleProcessStaticMktimeState()
    {
        string[] variableNames = ["LC_ALL", "LC_TIME", "LANG", "TZ"];
        var previousValues = variableNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("LC_ALL", "C");
            Environment.SetEnvironmentVariable("LC_TIME", "C");
            Environment.SetEnvironmentVariable("LANG", "C");
            Environment.SetEnvironmentVariable("TZ", "Europe/Paris");

            using var seed = JqProgram.Compile(
                "[2024,6,1,12,0,0,0,0]|strflocaltime(\"%s %z %Z\")");
            using var fold = JqProgram.Compile(
                "[2024,9,27,2,30,0,0,0]|strflocaltime(\"%s %z %Z\")");

            Assert.Equal(
                "1719828000 +0200 CEST",
                Assert.Single(seed.Execute("null")).GetString());
            Assert.Equal(
                "1729989000 +0200 CEST",
                Assert.Single(fold.Execute("null")).GetString());
        }
        finally
        {
            foreach (var (name, value) in previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void GetenvUsesPlatformCaseRulesWithoutChangingEnvironmentObjectKeys()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tz"] = "Europe/Paris",
            ["lc_time"] = "C",
        };

        Assert.False(libjq.jq_getenv(
            environment,
            "TZ",
            windowsSemantics: false,
            out _));
        Assert.True(libjq.jq_getenv(
            environment,
            "TZ",
            windowsSemantics: true,
            out var windowsTimeZone));
        Assert.Equal("Europe/Paris", windowsTimeZone);
        Assert.True(libjq.jq_getenv(
            environment,
            "LC_TIME",
            windowsSemantics: true,
            out var windowsLocale));
        Assert.Equal("C", windowsLocale);

        using var program = JqProgram.Compile("[$ENV.TZ, $ENV.tz]");
        var output = Assert.Single(program.Execute(
            "null",
            new JqExecutionOptions { Environment = environment }));
        Assert.Equal("[null,\"Europe/Paris\"]", output.GetRawText());
    }

    private sealed class MutatingInputSource(Action mutation) : IJqInputSource
    {
        private bool completed;

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed)
            {
                return JqInputReadResult.End;
            }

            completed = true;
            mutation();
            return JqInputReadResult.FromValue(
                System.Text.Json.JsonSerializer.SerializeToElement<object?>(null));
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentGroup
{
    public const string Name = "Process environment compatibility";
}
