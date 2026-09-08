using System.Collections.Immutable;

namespace DotNetJq;

/// <summary>Configures cooperative execution checks and input/output accounting.</summary>
/// <remarks>
/// These options reduce accidental or hostile work but are not a complete isolation boundary.
/// Execution timeout, cancellation, transition, and recursion checks are observed only at their
/// documented cooperative boundaries. They cannot interrupt compilation, JSON parsing or
/// serialization, module or host-callback I/O already in progress, regular-expression work
/// governed by <see cref="RegexTimeout"/>, or a long operation within one VM instruction.
/// Output limits are applied after the next value has been generated, and the byte limit is
/// applied after that value has been serialized. Use process, container, or operating-system
/// resource limits when executing code that is not trusted by the host.
/// </remarks>
public sealed record JqExecutionOptions
{
    private ImmutableDictionary<string, string>? environment;
    private int maxRecursionDepth = int.MaxValue;

    /// <summary>
    /// Gets jq-compatible options with no configured execution-transition, recursion, regex,
    /// input/output-byte, or output-count limit and no explicit environment override.
    /// </summary>
    public static JqExecutionOptions Default => new();

    /// <summary>
    /// Optional cooperative elapsed-time limit for VM execution. Timing starts after the primary
    /// JSON input has been parsed and is checked immediately before each bytecode dispatch; it
    /// does not interrupt work already in progress within a dispatch. A value of zero stops at
    /// the first dispatch.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Maximum aggregate UTF-8 size of the primary input and caller-supplied values or
    /// error values pulled through <c>input</c>/<c>inputs</c>.
    /// </summary>
    public long? MaxInputBytes { get; init; }

    /// <summary>
    /// Maximum aggregate compact-JSON UTF-8 output size. Each value is generated and serialized
    /// before this limit is checked.
    /// </summary>
    public long? MaxOutputBytes { get; init; }

    /// <summary>
    /// Maximum number of output values returned to the caller. The next value is generated before
    /// this limit is checked.
    /// </summary>
    public int? MaxOutputValues { get; init; }

    /// <summary>
    /// Maximum recursive evaluation depth. Explicitly setting this value also limits
    /// optimized tail-call transitions. The default imposes no project-specific depth cap;
    /// CLR stack and collection representation limits still apply.
    /// </summary>
    public int MaxRecursionDepth
    {
        get => maxRecursionDepth;
        init
        {
            maxRecursionDepth = value;
            HasExplicitMaxRecursionDepth = true;
        }
    }

    internal bool HasExplicitMaxRecursionDepth { get; private init; }

    // `$ENV` is compiled to LOADK by jq, while `env` reads at execution time.
    // Track an explicit managed override separately so default execution keeps
    // native compile-time `$ENV` timing and only the opt-in capability patches
    // those marked constant-pool slots.
    internal bool HasExplicitEnvironment => environment is not null;

    /// <summary>
    /// Maximum direct-VM instruction-dispatch transitions, or effectively unlimited by default.
    /// </summary>
    /// <remarks>
    /// One unit is charged immediately before each bytecode dispatch, including forward execution
    /// and backtracking arms. The counter is cumulative across all outputs pulled from one
    /// execution and is reset by each new execution. A value of zero therefore stops before the
    /// first instruction. A terminal pull after the VM stack is already exhausted consumes no
    /// unit. This is a managed CPU-work policy, not a count of jq output or intermediate values.
    /// </remarks>
    public long MaxExecutionTransitions { get; init; } = long.MaxValue;

    /// <summary>
    /// Timeout applied to each regular-expression operation. The jq-compatible default is
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public TimeSpan RegexTimeout { get; init; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Cancellation token observed before primary-input parsing, before each VM dispatch, and at
    /// documented module/input capability boundaries. It cannot interrupt parsing, serialization,
    /// regular-expression work, or a blocking host callback already in progress.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Optional explicit environment snapshot exposed to <c>$ENV</c> and <c>env</c>. <c>TZ</c>,
    /// <c>LC_ALL</c>, <c>LC_TIME</c>, and <c>LANG</c> also select the compatibility context
    /// for jq's local-time formatting builtins. With an explicit snapshot, time-zone state and
    /// libc's stateful <c>mktime</c> fold guess are isolated per compiled <see cref="JqProgram"/>.
    /// The supplied entries are copied when this option is initialized.
    /// When this property is <see langword="null"/>, <c>$ENV</c> retains jq's compile-time
    /// process snapshot while <c>env</c> and local-time builtins read the ambient process
    /// environment at their native-equivalent call boundaries.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment
    {
        get => environment;
        init
        {
            environment = value?.ToImmutableDictionary(StringComparer.Ordinal);
        }
    }
}
