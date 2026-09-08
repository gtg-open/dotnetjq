// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/linker.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/linker.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/linker.h.cs
// Substitutions: JqModuleResolver supplies jq_state-owned search-path/filesystem host capabilities;
// its public Resolve adapters construct owned jv arguments and invoke linker.c's canonical
// default_search/build_lib_search_chain/find_lib path, retaining the selected read snapshot.
// Known differences: cancellation is cooperative around synchronous filesystem reads; managed resource
// limits are optional host controls. The internal linker retains jq's int return/out block boundary.

using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq;

/// <summary>
/// Selects the suffix used by jq's module lookup rules.
/// </summary>
public enum JqModuleFileKind
{
    /// <summary>A jq source module with the <c>.jq</c> suffix.</summary>
    Module,

    /// <summary>An imported data file with the <c>.json</c> suffix.</summary>
    Data,
}

/// <summary>
/// A successfully resolved jq module or data file.
/// </summary>
public readonly record struct JqModuleFile(string Path, ReadOnlyMemory<byte> Contents);

/// <summary>
/// A non-throwing jq module lookup result.
/// </summary>
public readonly record struct JqModuleResolution(JqModuleFile? File, string? ErrorMessage)
{
    /// <summary>Gets whether a module file was found.</summary>
    public bool IsSuccess => File.HasValue;

    /// <summary>Creates a successful resolution.</summary>
    public static JqModuleResolution Success(JqModuleFile file) => new(file, null);

    /// <summary>Creates a failed resolution.</summary>
    public static JqModuleResolution Failure(string errorMessage) => new(null, errorMessage);
}

/// <summary>Optional aggregate resource limits for one compiled jq module graph.</summary>
public sealed record JqModuleResourceLimits
{
    /// <summary>Gets an instance with no aggregate graph limits.</summary>
    public static JqModuleResourceLimits Unlimited { get; } = new();

    /// <summary>
    /// Maximum number of unique resolved module and data files. The root jq source is not counted.
    /// </summary>
    public int? MaxModuleCount { get; init; }

    /// <summary>Maximum aggregate UTF-8 byte length of unique resolved module and data files.</summary>
    public long? MaxTotalImportedBytes { get; init; }

    /// <summary>
    /// Maximum dependency depth. A dependency imported by the root source has depth one.
    /// </summary>
    public int? MaxDependencyDepth { get; init; }
}

/// <summary>
/// Resolves jq modules and imported data through an explicit filesystem capability.
/// </summary>
public sealed class JqModuleResolver
{
    private readonly IReadOnlyList<string> libraryPaths;

    /// <summary>Creates a resolver with explicit filesystem and path capabilities.</summary>
    public JqModuleResolver(
        IJqFileSystem fileSystem,
        IEnumerable<string> libraryPaths,
        string jqOrigin,
        string? homeDirectory = null)
        : this(
            fileSystem,
            libraryPaths,
            jqOrigin,
            homeDirectory,
            JqModuleResourceLimits.Unlimited,
            CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a resolver with explicit filesystem, path, graph-limit, and cancellation capabilities.
    /// </summary>
    public JqModuleResolver(
        IJqFileSystem fileSystem,
        IEnumerable<string> libraryPaths,
        string jqOrigin,
        string? homeDirectory,
        JqModuleResourceLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(libraryPaths);
        ArgumentNullException.ThrowIfNull(jqOrigin);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxModuleCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum module count cannot be negative.");
        }

        if (limits.MaxTotalImportedBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum imported bytes cannot be negative.");
        }

        if (limits.MaxDependencyDepth is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum dependency depth cannot be negative.");
        }

        FileSystem = fileSystem;
        // Snapshot the caller's sequence and expose it only through a read-only
        // wrapper. Returning the array as IReadOnlyList would still let a caller
        // cast it back to string[] and alter this resolver's search policy.
        this.libraryPaths = Array.AsReadOnly(libraryPaths.ToArray());
        JqOrigin = jqOrigin;
        HomeDirectory = homeDirectory;
        Limits = limits;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the filesystem used for every probe and read.</summary>
    public IJqFileSystem FileSystem { get; }

    /// <summary>Gets the origin substituted for <c>$ORIGIN</c> search entries.</summary>
    public string JqOrigin { get; }

    /// <summary>Gets the explicit home used for <c>~/</c> expansion, if enabled.</summary>
    public string? HomeDirectory { get; }

    /// <summary>Gets the aggregate limits applied independently to each compiled module graph.</summary>
    public JqModuleResourceLimits Limits { get; }

    /// <summary>
    /// Gets the token checked before and after synchronous filesystem reads and between linker steps.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the immutable snapshot of jq's configured library search paths.</summary>
    public IReadOnlyList<string> LibraryPaths => libraryPaths;

    /// <summary>
    /// Finds a module using its dependency-specific search path, or jq's default
    /// current-directory-plus-library-path chain when <paramref name="searchPaths"/> is null.
    /// The returned bytes are the same capability-read snapshot that selected the path.
    /// </summary>
    public JqModuleResolution Resolve(
        string relativePath,
        JqModuleFileKind kind = JqModuleFileKind.Module,
        IEnumerable<string>? searchPaths = null,
        string? libraryOrigin = null) =>
        libjq.find_lib(this, relativePath, searchPaths, kind, libraryOrigin, includeCurrentDirectory: true);

    /// <summary>
    /// Finds a module for modulemeta, which follows upstream by searching only jq library paths.
    /// </summary>
    public JqModuleResolution ResolveModuleMetadata(string relativePath) =>
        libjq.load_module_meta(this, relativePath);

    internal void ThrowIfCancellationRequested() =>
        CancellationToken.ThrowIfCancellationRequested();
}
