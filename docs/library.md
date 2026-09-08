# .NET library

Install `DotNetJq.Library`; the assembly and namespace are `DotNetJq`.

## Materialized execution

```csharp
using DotNetJq;

using var jq = JqProgram.Compile(".items | map(.price) | add");
var outputs = jq.Execute(
    """{"items":[{"price":12},{"price":8}]}""");

foreach (var output in outputs)
{
    Console.WriteLine(output.GetRawText());
}
```

`Execute` accepts one JSON value and returns jq's output stream as detached
`JsonElement` values in emission order. It throws `JqParseException`,
`JqCompileException`, `JqRuntimeException`, or `JqHaltException` at the
corresponding boundary.

## Detailed and lazy execution

Use `ExecuteDetailed` when the difference between normal completion, an
uncaught jq error, and `halt`/`halt_error` matters:

```csharp
using DotNetJq;

using var jq = JqProgram.Compile("1, (\"stop\" | halt_error(7))");
var result = jq.ExecuteDetailed("null");

Console.WriteLine(result.Outputs[0].GetInt32());
Console.WriteLine(result.Outcome.Kind);
Console.WriteLine(result.Outcome.RequestedExitCode?.GetInt32());
Console.WriteLine(result.Outcome.HaltMessage?.GetString());
```

Pull a large or unbounded jq stream one value at a time:

```csharp
using var jq = JqProgram.Compile(".items[]");
using var execution = jq.StartExecution("""{"items":[1,2,3]}""");

while (execution.TryRead(out var value))
{
    Console.WriteLine(value.GetRawText());
}

if (execution.Outcome?.Kind == JqExecutionOutcomeKind.RuntimeError)
{
    throw execution.Outcome.RuntimeError!;
}
```

`JqProgram` owns one jq-shaped state. It is reusable after an execution ends,
but only one execution may be active at a time. Neither the program nor its
cursor is thread-safe; compile independent programs for concurrent execution.

## Modules and filesystem authority

Plain `JqProgram.Compile(source)` grants no filesystem access. Module and data
imports require an explicit `JqModuleResolver` and `IJqFileSystem` capability.
An in-memory example:

```csharp
using DotNetJq;
using DotNetJq.Compatibility.FileSystem;

var files = new JqInMemoryFileSystem(
    new Dictionary<string, string>
    {
        ["/modules/math.jq"] = "def twice: . * 2;",
    });
var resolver = new JqModuleResolver(
    files,
    new[] { "/modules" },
    jqOrigin: "/app");

using var jq = JqProgram.Compile("include \"math\"; twice", resolver);
Console.WriteLine(jq.Execute("21")[0].GetInt32());
```

`JqInMemoryFileSystem` snapshots supplied and returned bytes. The physical
`JqFileSystem` root policy is cooperative and has a documented check/open TOCTOU
window; it is not an OS sandbox.

## Stateful capabilities and limits

`JqExecutionCapabilities` explicitly supplies input, debug, and standard-error
value callbacks. `JqExecutionOptions` supplies cancellation, timeout, input and
output byte limits, output count, recursion and VM transition budgets, regex
timeout, and an immutable environment view.

These controls bound common failure modes but are cooperative. Compilation has
no general source-size/time limit, output limits apply after a value exists, and
there is no general intermediate-memory quota. Do not treat untrusted filter
execution as a hard security sandbox; see the
[security/resource audit](../porting/SECURITY_RESOURCE_AUDIT.md).

## Compatibility identity

```csharp
Console.WriteLine(DotNetJqVersionInfo.CompatibleJqVersion); // 1.8.2
Console.WriteLine(DotNetJqVersionInfo.CompatibleJqCommit);
```

The assembly informational version records the DotNetJq SemVer, jq compatibility,
CI build number, and release source commit. Package versions remain ordinary
SemVer and do not include build metadata.
