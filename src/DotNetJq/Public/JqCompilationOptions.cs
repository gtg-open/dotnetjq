namespace DotNetJq;

/// <summary>Explicit capabilities and provenance used while compiling a jq program.</summary>
public sealed record JqCompilationOptions
{
    /// <summary>
    /// Gets the only filesystem and search-path capability available to imports and
    /// <c>modulemeta</c>. The default denies filesystem access.
    /// </summary>
    public JqModuleResolver? ModuleResolver { get; init; }

    /// <summary>
    /// Gets the caller-supplied logical program origin used by relative module searches and
    /// <c>get_prog_origin</c>. No current-directory default is inferred.
    /// </summary>
    public string? ProgramOrigin { get; init; }
}
