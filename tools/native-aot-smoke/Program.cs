using DotNetJq;
using DotNetJq.Compatibility.FileSystem;

var cases = new[]
{
    new SmokeCase(
        "nested fields and interpolation",
        ".users[] | \"\\(.profile.first) \\(.profile.last)#\\(.id)\"",
        """
        {
          "users": [
            { "id": 7, "profile": { "first": "Ada", "last": "Lovelace" } },
            { "id": 8, "profile": { "first": "Grace", "last": "Hopper" } }
          ]
        }
        """,
        ["\"Ada Lovelace#7\"", "\"Grace Hopper#8\""]),
    new SmokeCase(
        "field selection and object construction",
        """
        {
          owner: .account.name,
          activeIds: [.items[] | select(.active) | .id],
          label: "\(.account.name):\(.items | length)"
        }
        """,
        """
        {
          "account": { "name": "compiler" },
          "items": [
            { "id": 3, "active": true },
            { "id": 5, "active": false },
            { "id": 9, "active": true }
          ]
        }
        """,
        ["{\"owner\":\"compiler\",\"activeIds\":[3,9],\"label\":\"compiler:3\"}"]),
    new SmokeCase(
        "trim-safe generated lexer and compile",
        """
        # Exercise GPLEX comment, UTF-8 string, interpolation, number, keyword,
        # bracket, brace, and parenthesis states in the published native image.
        def add_suffix($x): "\($x)-é";
        {
          word: add_suffix("café"),
          number: 1.25e2,
          flags: [true, false, null],
          alt: (empty // "fallback")
        }
        """,
        "null",
        ["{\"word\":\"café-é\",\"number\":125,\"flags\":[true,false,null],\"alt\":\"fallback\"}"]),
    // Expected compact JSON is frozen from jq 1.8.2 commit
    // 34f7186b86743a083a589741b6cea95293524108.
    new SmokeCase(
        "global POSIX-class regex scan",
        "[scan(\"[[:alpha:]]+\")]",
        "\"A,b z\"",
        ["[\"A\",\"b\",\"z\"]"]),
    new SmokeCase(
        "gamma-family compatibility boundary",
        "[(-0.5|gamma),(-0.5|lgamma),(-0.5|lgamma_r),(-0.5|tgamma)]",
        "null",
        ["[1.2655121234846454,1.2655121234846454,[1.2655121234846454,-1],-3.5449077018110318]"]),
    new SmokeCase(
        "libc time proxy and POSIX TZ rules",
        ". as $epoch | [[$epoch,1719849600]|" +
        "map([localtime,strflocaltime(\"%F %T %z %Z\")])," +
        "($epoch|todate)," +
        "($epoch|todate|strptime(\"%Y-%m-%dT%H:%M:%SZ\")|mktime)]",
        "1709247907",
        ["[[[[2024,1,29,18,5,7,4,59],\"2024-02-29 18:05:07 -0500 EST\"]," +
         "[[2024,6,1,12,0,0,1,182],\"2024-07-01 12:00:00 -0400 EDT\"]]," +
         "\"2024-02-29T23:05:07Z\",1709247907]"],
        "EST5EDT"),
};

var completedCaseCount = 0;
foreach (var smokeCase in cases)
{
    var options = smokeCase.TimeZone is null
        ? JqExecutionOptions.Default
        : new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = "C",
                ["TZ"] = smokeCase.TimeZone,
            },
        };
    var actual = JqProgram
        .Compile(smokeCase.Filter)
        .Execute(smokeCase.Input, options)
        .Select(static value => value.GetRawText())
        .ToArray();

    if (!actual.SequenceEqual(smokeCase.Expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"NativeAOT smoke case '{smokeCase.Name}' returned " +
            $"[{string.Join(", ", actual)}], expected " +
            $"[{string.Join(", ", smokeCase.Expected)}].");
    }

    completedCaseCount++;
}

VerifyStatefulExecutionAndOutcome();
completedCaseCount++;
VerifyExecutionCapabilities();
completedCaseCount++;
VerifyInMemoryModuleCapability();
completedCaseCount++;

Console.WriteLine($"NATIVE_AOT_SMOKE_OK cases={completedCaseCount}");

static void VerifyStatefulExecutionAndOutcome()
{
    using var program = JqProgram.Compile(".items[]");
    using var execution = program.StartExecution(
        new JqExecutionInput(
            ParseJson("{\"items\":[4,9]}"),
            new JqInputPosition("native-aot-primary.json", 7)));

    if (execution.Outcome is not null)
    {
        throw new InvalidOperationException(
            "NativeAOT stateful execution reported an outcome before stream exhaustion.");
    }

    var outputs = new List<string>();
    while (execution.TryRead(out var value))
    {
        outputs.Add(value.GetRawText());
    }

    if (!outputs.SequenceEqual(["4", "9"], StringComparer.Ordinal) ||
        execution.Outcome?.Kind != JqExecutionOutcomeKind.Completed ||
        execution.Outcome.RequestedExitCode is not null ||
        execution.Outcome.HaltMessage is not null ||
        execution.Outcome.RuntimeError is not null)
    {
        throw new InvalidOperationException(
            "NativeAOT stateful StartExecution/TryRead/Outcome contract failed.");
    }
}

static void VerifyExecutionCapabilities()
{
    var input = new QueueInputSource(ParseJson("41"));
    var debug = new RecordingSink();
    var standardError = new RecordingSink();
    var capabilities = new JqExecutionCapabilities
    {
        Input = input,
        Debug = debug,
        StandardError = standardError,
    };

    using var program = JqProgram.Compile("[input, (42|debug), (43|stderr)]");
    using var execution = program.StartExecution("null", capabilities: capabilities);
    var outputs = new List<string>();
    while (execution.TryRead(out var value))
    {
        outputs.Add(value.GetRawText());
    }

    if (!outputs.SequenceEqual(["[41,42,43]"], StringComparer.Ordinal) ||
        input.ReadCount != 1 ||
        !debug.Values.SequenceEqual(["42"], StringComparer.Ordinal) ||
        !standardError.Values.SequenceEqual(["43"], StringComparer.Ordinal) ||
        execution.Outcome?.Kind != JqExecutionOutcomeKind.Completed)
    {
        throw new InvalidOperationException(
            "NativeAOT explicit input/debug/stderr capability contract failed.");
    }
}

static void VerifyInMemoryModuleCapability()
{
    var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
    {
        ["/lib/math.jq"] = "def scale($factor): . * $factor;",
    });
    var resolver = new JqModuleResolver(fileSystem, ["/lib"], "/app");
    using var program = JqProgram.Compile(
        "include \"math\"; scale(6)",
        new JqCompilationOptions
        {
            ModuleResolver = resolver,
            ProgramOrigin = "/app/main.jq",
        });
    var outputs = program.Execute("7")
        .Select(static value => value.GetRawText())
        .ToArray();
    if (!outputs.SequenceEqual(["42"], StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            "NativeAOT in-memory filesystem/module-resolver capability contract failed.");
    }
}

static System.Text.Json.JsonElement ParseJson(string json)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

internal sealed record SmokeCase(
    string Name,
    string Filter,
    string Input,
    string[] Expected,
    string? TimeZone = null);

internal sealed class QueueInputSource(params System.Text.Json.JsonElement[] values) : IJqInputSource
{
    private readonly Queue<System.Text.Json.JsonElement> values = new(values);

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

internal sealed class RecordingSink : IJqValueSink
{
    internal List<string> Values { get; } = [];

    public void Write(System.Text.Json.JsonElement value) =>
        Values.Add(value.GetRawText());
}
