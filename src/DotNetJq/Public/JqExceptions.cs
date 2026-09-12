using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq;

/// <summary>Base exception for DotNetJq compilation and execution failures.</summary>
public class JqException : Exception
{
    /// <summary>Creates a jq exception.</summary>
    public JqException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a jq exception with an inner cause.</summary>
    public JqException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports an invalid jq program.</summary>
public sealed class JqCompileException : JqException
{
    /// <summary>Creates a compile exception.</summary>
    public JqCompileException(string message)
        : base(message)
    {
    }
}

/// <summary>Reports invalid JSON input.</summary>
public sealed class JqParseException : JqException
{
    /// <summary>Creates a parse exception.</summary>
    public JqParseException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a parse exception with an inner cause.</summary>
    public JqParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports a jq runtime error.</summary>
public sealed class JqRuntimeException : JqException
{
    private jv? errorValue;

    /// <summary>Creates a runtime exception.</summary>
    public JqRuntimeException(string message)
        : base(message)
    {
    }

    internal JqRuntimeException(jv errorValue)
        : base(errorValue.Kind == jv_kind.JV_KIND_STRING
            ? errorValue.StringValue
            : libjq.jv_dump_string_borrowed(errorValue))
    {
        // This overload takes one jq owner, matching jq's error slot.  Managed
        // catch sites either move that owner back into jq evaluation or release
        // it when the error crosses a public/materialized boundary.
        this.errorValue = errorValue;
    }

    /// <summary>Returns the exception-owned value as a borrowed handle.</summary>
    internal jv? ErrorValue => errorValue;

    /// <summary>Moves the exception-owned value to a jq catch/error slot.</summary>
    internal jv? TakeErrorValue()
    {
        var owned = errorValue;
        errorValue = null;
        return owned;
    }

    /// <summary>Releases an exception-owned value that will not re-enter jq evaluation.</summary>
    internal void ReleaseErrorValue()
    {
        if (TakeErrorValue() is { } owned)
        {
            libjq.jv_free(owned);
        }
    }
}

/// <summary>
/// Reports <c>halt_error</c> through the legacy materializing execution API.
/// Use the detailed or pull API to consume halt state without an exception.
/// </summary>
public sealed class JqHaltException : JqException
{
    internal JqHaltException(
        JsonElement requestedExitCode,
        JsonElement? haltMessage,
        IReadOnlyList<JsonElement> partialOutputs)
        : base($"jq halted with exit code {requestedExitCode.GetRawText()}")
    {
        RequestedExitCode = requestedExitCode.Clone();
        HaltMessage = haltMessage?.Clone();
        PartialOutputs = partialOutputs.Select(static value => value.Clone()).ToArray();
    }

    /// <summary>Gets the jq numeric value supplied to <c>halt_error</c>.</summary>
    public JsonElement RequestedExitCode { get; }

    /// <summary>Gets the halt message. A present element may itself be JSON null.</summary>
    public JsonElement? HaltMessage { get; }

    /// <summary>Gets values emitted before <c>halt_error</c> stopped execution.</summary>
    public IReadOnlyList<JsonElement> PartialOutputs { get; }
}
