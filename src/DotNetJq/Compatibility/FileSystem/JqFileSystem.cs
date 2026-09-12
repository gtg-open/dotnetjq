using System.Collections.ObjectModel;

namespace DotNetJq.Compatibility.FileSystem;

// DOTNETJQ PROXY
// UPSTREAM COMPONENT: src/jv_file.c and src/linker.c POSIX file/stat operations.
// REPLACEMENT: constrained System.IO access behind IJqFileSystem.
// WHY: managed hosts must be able to disable or scope jq module/data reads.
// BEHAVIORAL CONTRACT: preserve byte contents, jq lookup order, and useful open errors.
// KNOWN DIFFERENCES: platform I/O error wording is normalized; a size cap is opt-in; and the
// System.IO canonicalize/check/open sequence is not an atomic operating-system sandbox boundary.
// TESTS COVERING THE SUBSTITUTION: ModuleFileCompatibilityTests.

/// <summary>
/// Describes the outcome of a controlled jq file read.
/// </summary>
public enum JqFileReadStatus
{
    /// <summary>The file was read successfully.</summary>
    Success,

    /// <summary>The requested file does not exist.</summary>
    NotFound,

    /// <summary>The filesystem policy or host denied the read.</summary>
    AccessDenied,

    /// <summary>The requested path names a directory.</summary>
    IsDirectory,

    /// <summary>The file exceeds the configured byte limit.</summary>
    TooLarge,

    /// <summary>The supplied path is not valid.</summary>
    InvalidPath,

    /// <summary>The host reported another input/output failure.</summary>
    Error,
}

/// <summary>
/// A byte-for-byte file result returned by an <see cref="IJqFileSystem"/>.
/// </summary>
public readonly record struct JqFileReadResult(
    JqFileReadStatus Status,
    string Path,
    ReadOnlyMemory<byte> Contents,
    string? ErrorMessage)
{
    /// <summary>Gets whether the read succeeded.</summary>
    public bool IsSuccess => Status == JqFileReadStatus.Success;

    /// <summary>Creates a successful read result.</summary>
    public static JqFileReadResult Success(string path, ReadOnlyMemory<byte> contents) =>
        new(JqFileReadStatus.Success, path, contents, null);

    /// <summary>Creates a failed read result.</summary>
    public static JqFileReadResult Failure(
        JqFileReadStatus status,
        string path,
        string errorMessage) =>
        new(status, path, ReadOnlyMemory<byte>.Empty, errorMessage);
}

/// <summary>
/// The only filesystem capability consumed by jq's file and module compatibility code.
/// </summary>
public interface IJqFileSystem
{
    /// <summary>Reads a file through the capability's access policy.</summary>
    JqFileReadResult ReadFile(string path);
}

/// <summary>
/// A physical filesystem capability constrained to explicitly allowed roots.
/// </summary>
/// <remarks>
/// This is a cooperative host policy, not an operating-system sandbox. Existing links are
/// canonicalized before the allowed-root check, but that check and the later path-based open are
/// separate operations. Another process can replace a checked path component in that TOCTOU
/// window. Use descriptor-relative/no-follow access, a custom <see cref="IJqFileSystem"/>, or
/// process/container isolation when the filesystem is controlled by an adversary.
/// </remarks>
public sealed class JqFileSystem : IJqFileSystem
{
    /// <summary>A recommended opt-in 64 MiB maximum for one jq module or data file.</summary>
    /// <remarks>The two-argument constructor does not apply this value automatically.</remarks>
    public const int RecommendedMaximumFileSizeBytes = 64 * 1024 * 1024;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string baseDirectory;
    private readonly string[] allowedRoots;
    private readonly int? maximumFileSizeBytes;

    /// <summary>
    /// Creates a physical filesystem restricted to <paramref name="allowedRoots"/> without an
    /// additional project-specific per-file size limit.
    /// </summary>
    public JqFileSystem(
        string baseDirectory,
        IEnumerable<string> allowedRoots)
        : this(baseDirectory, allowedRoots, maximumFileSizeBytes: null)
    {
    }

    /// <summary>
    /// Creates a physical filesystem restricted to <paramref name="allowedRoots"/> with an
    /// explicit maximum size for each jq module or data file.
    /// </summary>
    public JqFileSystem(
        string baseDirectory,
        IEnumerable<string> allowedRoots,
        int maximumFileSizeBytes)
        : this(baseDirectory, allowedRoots, (int?)maximumFileSizeBytes)
    {
    }

    private JqFileSystem(
        string baseDirectory,
        IEnumerable<string> allowedRoots,
        int? maximumFileSizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(allowedRoots);
        if (maximumFileSizeBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFileSizeBytes),
                "Maximum file size must be positive.");
        }

        this.baseDirectory = ResolveExistingLinks(Path.GetFullPath(baseDirectory));
        this.allowedRoots = allowedRoots
            .Select(root =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(root);
                var rooted = Path.IsPathFullyQualified(root)
                    ? root
                    : Path.Combine(this.baseDirectory, root);
                return ResolveExistingLinks(Path.GetFullPath(rooted));
            })
            .Distinct(PathComparer.Instance)
            .ToArray();

        if (this.allowedRoots.Length == 0)
        {
            throw new ArgumentException("At least one allowed filesystem root is required.", nameof(allowedRoots));
        }

        this.maximumFileSizeBytes = maximumFileSizeBytes;
    }

    /// <inheritdoc />
    public JqFileReadResult ReadFile(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return JqFileReadResult.Failure(
                JqFileReadStatus.InvalidPath,
                path ?? string.Empty,
                "The file path is empty or contains a NUL byte.");
        }

        string resolvedPath;
        try
        {
            var rooted = Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(baseDirectory, path);
            resolvedPath = ResolveExistingLinks(Path.GetFullPath(rooted));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.InvalidPath, path, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.Error, path, exception.Message);
        }

        if (!allowedRoots.Any(root => IsWithinRoot(resolvedPath, root)))
        {
            return JqFileReadResult.Failure(
                JqFileReadStatus.AccessDenied,
                resolvedPath,
                "Access to the path is outside the allowed jq filesystem roots.");
        }

        try
        {
            if (Directory.Exists(resolvedPath))
            {
                return JqFileReadResult.Failure(
                    JqFileReadStatus.IsDirectory,
                    resolvedPath,
                    "It's a directory");
            }

            using var stream = new FileStream(
                resolvedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (maximumFileSizeBytes is { } maximumFileSize && stream.Length > maximumFileSize)
            {
                return JqFileReadResult.Failure(
                    JqFileReadStatus.TooLarge,
                    resolvedPath,
                    $"The file exceeds the configured limit of {maximumFileSize} bytes.");
            }

            using var destination = new MemoryStream(
                stream.Length <= int.MaxValue ? (int)stream.Length : 0);
            var buffer = new byte[81920];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (maximumFileSizeBytes is { } configuredMaximum &&
                    (read > configuredMaximum || destination.Length > configuredMaximum - read))
                {
                    return JqFileReadResult.Failure(
                        JqFileReadStatus.TooLarge,
                        resolvedPath,
                        $"The file exceeds the configured limit of {configuredMaximum} bytes.");
                }

                destination.Write(buffer, 0, read);
            }

            return JqFileReadResult.Success(resolvedPath, destination.ToArray());
        }
        catch (FileNotFoundException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.NotFound, resolvedPath, exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.NotFound, resolvedPath, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.AccessDenied, resolvedPath, exception.Message);
        }
        catch (IOException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.Error, resolvedPath, exception.Message);
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, PathComparison);
    }

    private static string ResolveExistingLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return fullPath;
        }

        var current = pathRoot;
        var remainder = fullPath[pathRoot.Length..];
        foreach (var component in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo item = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!item.Exists || item.LinkTarget is null)
            {
                continue;
            }

            var target = item.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return Path.GetFullPath(current);
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        internal static PathComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) => string.Equals(x, y, PathComparison);

        public int GetHashCode(string obj) =>
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj)
                : StringComparer.Ordinal.GetHashCode(obj);
    }
}

/// <summary>
/// A deterministic, read-only filesystem useful for sandboxed hosts and tests.
/// </summary>
public sealed class JqInMemoryFileSystem : IJqFileSystem
{
    private readonly ReadOnlyDictionary<string, byte[]> files;

    /// <summary>Creates a read-only filesystem from byte-valued files.</summary>
    public JqInMemoryFileSystem(IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        this.files = new ReadOnlyDictionary<string, byte[]>(
            files.ToDictionary(
                pair => NormalizePath(pair.Key),
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal));
    }

    /// <summary>Creates a read-only filesystem from UTF-8 text files.</summary>
    public JqInMemoryFileSystem(IEnumerable<KeyValuePair<string, string>> files)
        : this(EncodeFiles(files))
    {
    }

    /// <inheritdoc />
    public JqFileReadResult ReadFile(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return JqFileReadResult.Failure(
                JqFileReadStatus.InvalidPath,
                path ?? string.Empty,
                "The file path is empty or contains a NUL byte.");
        }

        var normalized = NormalizePath(path);
        return files.TryGetValue(normalized, out var contents)
            // Never expose the private snapshot's mutable byte[] backing.
            ? JqFileReadResult.Success(normalized, contents.ToArray())
            : JqFileReadResult.Failure(
                JqFileReadStatus.NotFound,
                normalized,
                $"Could not find file '{normalized}'.");
    }

    private static IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> EncodeFiles(
        IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Select(pair =>
            KeyValuePair.Create<string, ReadOnlyMemory<byte>>(
                pair.Key,
                System.Text.Encoding.UTF8.GetBytes(pair.Value)));
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var replaced = path.Replace('\\', '/');
        var rooted = replaced.StartsWith('/') ? replaced : "/" + replaced;
        var components = new List<string>();
        foreach (var component in rooted.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (components.Count > 0)
                {
                    components.RemoveAt(components.Count - 1);
                }

                continue;
            }

            components.Add(component);
        }

        return "/" + string.Join('/', components);
    }
}

/// <summary>
/// A filesystem capability that deterministically denies every read.
/// </summary>
public sealed class JqDenyAllFileSystem : IJqFileSystem
{
    /// <summary>Gets the shared deny-all capability.</summary>
    public static JqDenyAllFileSystem Instance { get; } = new();

    private JqDenyAllFileSystem()
    {
    }

    /// <inheritdoc />
    public JqFileReadResult ReadFile(string path) =>
        JqFileReadResult.Failure(
            JqFileReadStatus.AccessDenied,
            path ?? string.Empty,
            "Filesystem access is disabled.");
}
