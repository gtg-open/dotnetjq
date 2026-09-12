using System.Text;
using DotNetJq.Compatibility.FileSystem;

namespace DotNetJq.Cli;

/// <summary>
/// The command-line application's ambient filesystem capability. Unlike library hosts, the CLI
/// intentionally follows jq by allowing every path the current process can read.
/// </summary>
internal sealed class CliFileSystem : IJqFileSystem
{
    private readonly string baseDirectory;

    internal CliFileSystem(string baseDirectory)
    {
        this.baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public JqFileReadResult ReadFile(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return JqFileReadResult.Failure(
                JqFileReadStatus.InvalidPath,
                path ?? string.Empty,
                "The file path is empty or contains a NUL byte.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, baseDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.InvalidPath, path, exception.Message);
        }

        try
        {
            if (Directory.Exists(fullPath))
            {
                return JqFileReadResult.Failure(
                    JqFileReadStatus.IsDirectory,
                    fullPath,
                    "It's a directory");
            }

            return JqFileReadResult.Success(fullPath, File.ReadAllBytes(fullPath));
        }
        catch (FileNotFoundException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.NotFound, fullPath, exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.NotFound, fullPath, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.AccessDenied, fullPath, exception.Message);
        }
        catch (IOException exception)
        {
            return JqFileReadResult.Failure(JqFileReadStatus.Error, fullPath, exception.Message);
        }
    }

    internal string ReadProgram(string path, out string fullPath)
    {
        var result = ReadFile(path);
        fullPath = result.Path;
        if (!result.IsSuccess)
        {
            throw new CliFileException("jq: " + DescribeFailure(path, result));
        }

        var source = Encoding.UTF8.GetString(result.Contents.Span);
        if (source.Contains('\0', StringComparison.Ordinal))
        {
            throw new CliFileException("jq: program file contains NUL bytes");
        }

        return source;
    }

    internal static string DescribeFailure(string path, JqFileReadResult result)
    {
        var detail = result.Status switch
        {
            JqFileReadStatus.NotFound => "No such file or directory",
            JqFileReadStatus.AccessDenied => "Permission denied",
            JqFileReadStatus.IsDirectory => "It's a directory",
            _ => result.ErrorMessage ?? "unknown error",
        };
        return $"Could not open {path}: {detail}";
    }
}

internal sealed class CliFileException(string message) : IOException(message);
