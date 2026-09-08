using System.Text.Json;

namespace DotNetJq;

/// <summary>Classifies one pull from an explicit jq input source.</summary>
public enum JqInputReadKind
{
    /// <summary>The source has no more values.</summary>
    EndOfInput,

    /// <summary>The source produced a jq input value.</summary>
    Value,

    /// <summary>The source produced a jq error value.</summary>
    Error,
}

/// <summary>Identifies the source location of a jq input value or input error.</summary>
/// <param name="FileName">The logical source name, or <see langword="null"/> when unnamed.</param>
/// <param name="LineNumber">The zero-or-one-based state supplied by the input adapter.</param>
public readonly record struct JqInputPosition(string? FileName, long LineNumber);

/// <summary>A value, error, or end marker returned by an explicit jq input source.</summary>
public readonly record struct JqInputReadResult
{
    private JqInputReadResult(
        JqInputReadKind kind,
        JsonElement? value,
        JsonElement? errorValue,
        JqInputPosition? position)
    {
        Kind = kind;
        Value = value;
        ErrorValue = errorValue;
        Position = position;
    }

    /// <summary>Gets a reusable end-of-input marker.</summary>
    public static JqInputReadResult End { get; } =
        new(JqInputReadKind.EndOfInput, null, null, null);

    /// <summary>Gets the result kind.</summary>
    public JqInputReadKind Kind { get; }

    /// <summary>Gets the produced value when <see cref="Kind"/> is <see cref="JqInputReadKind.Value"/>.</summary>
    public JsonElement? Value { get; }

    /// <summary>Gets the jq error value when <see cref="Kind"/> is <see cref="JqInputReadKind.Error"/>.</summary>
    public JsonElement? ErrorValue { get; }

    /// <summary>Gets the new current input position, when supplied by the source.</summary>
    public JqInputPosition? Position { get; }

    /// <summary>Creates a successful input read.</summary>
    public static JqInputReadResult FromValue(
        JsonElement value,
        JqInputPosition? position = null)
    {
        EnsureDefined(value, nameof(value));
        return new JqInputReadResult(JqInputReadKind.Value, value.Clone(), null, position);
    }

    /// <summary>Creates a catchable jq input-source error.</summary>
    public static JqInputReadResult FromError(
        JsonElement errorValue,
        JqInputPosition? position = null)
    {
        EnsureDefined(errorValue, nameof(errorValue));
        return new JqInputReadResult(JqInputReadKind.Error, null, errorValue.Clone(), position);
    }

    private static void EnsureDefined(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A jq value cannot be undefined.", parameterName);
        }
    }
}

/// <summary>Synchronously supplies values consumed by jq's <c>input</c> builtin.</summary>
public interface IJqInputSource
{
    /// <summary>Reads one value, error, or end marker.</summary>
    JqInputReadResult ReadNext(CancellationToken cancellationToken);
}

/// <summary>Synchronously receives an undecorated jq value.</summary>
public interface IJqValueSink
{
    /// <summary>Writes one stable JSON value.</summary>
    void Write(JsonElement value);
}

/// <summary>Explicit, per-execution capabilities for jq's stateful I/O builtins.</summary>
public sealed record JqExecutionCapabilities
{
    /// <summary>Gets the source used by <c>input</c> and <c>inputs</c>, or no source by default.</summary>
    public IJqInputSource? Input { get; init; }

    /// <summary>Gets the raw-value sink used by <c>debug</c>, or no sink by default.</summary>
    public IJqValueSink? Debug { get; init; }

    /// <summary>Gets the raw-value sink used by <c>stderr</c>, or no sink by default.</summary>
    public IJqValueSink? StandardError { get; init; }
}

/// <summary>A primary jq value and its optional explicit source position.</summary>
/// <param name="Value">The primary jq value.</param>
/// <param name="Position">Its source position, when known.</param>
public readonly record struct JqExecutionInput(
    JsonElement Value,
    JqInputPosition? Position = null);

/// <summary>Classifies how a jq execution stopped.</summary>
public enum JqExecutionOutcomeKind
{
    /// <summary>The jq output stream was exhausted normally.</summary>
    Completed,

    /// <summary>The jq program invoked <c>halt</c> or <c>halt_error</c>.</summary>
    Halted,

    /// <summary>The jq stream ended with an uncaught jq runtime error.</summary>
    RuntimeError,
}

/// <summary>The immutable terminal state of one jq execution.</summary>
public sealed record JqExecutionOutcome
{
    internal JqExecutionOutcome(
        JqExecutionOutcomeKind kind,
        JsonElement? requestedExitCode = null,
        JsonElement? haltMessage = null,
        JqRuntimeException? runtimeError = null)
    {
        Kind = kind;
        RequestedExitCode = requestedExitCode;
        HaltMessage = haltMessage;
        RuntimeError = runtimeError;
    }

    /// <summary>Gets how execution stopped.</summary>
    public JqExecutionOutcomeKind Kind { get; }

    /// <summary>
    /// Gets the jq number supplied to <c>halt_error</c>. Absence identifies bare <c>halt</c>.
    /// </summary>
    public JsonElement? RequestedExitCode { get; }

    /// <summary>
    /// Gets the value supplied to <c>halt_error</c>. A present element may itself be JSON null.
    /// </summary>
    public JsonElement? HaltMessage { get; }

    /// <summary>Gets the uncaught jq error when <see cref="Kind"/> is runtime error.</summary>
    public JqRuntimeException? RuntimeError { get; }
}

/// <summary>A materialized jq output stream together with its terminal state.</summary>
public sealed record JqExecutionResult
{
    internal JqExecutionResult(
        IReadOnlyList<JsonElement> outputs,
        JqExecutionOutcome outcome)
    {
        Outputs = outputs;
        Outcome = outcome;
    }

    /// <summary>Gets values emitted before execution stopped.</summary>
    public IReadOnlyList<JsonElement> Outputs { get; }

    /// <summary>Gets the terminal execution state.</summary>
    public JqExecutionOutcome Outcome { get; }
}
