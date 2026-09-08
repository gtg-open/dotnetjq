// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/util.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/util.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/util.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - Environment implements the upstream home lookup.
// - IJqFileSystem supplies canonical paths through the host-controlled filesystem boundary.
// - Span.IndexOf implements _jq_memmem and returns an offset instead of a native pointer.
// - Stream implements the private byte writer used by the C header.
//
// Known differences:
// - The CLI-owned jq_util_input_state/FILE* loop is replaced by the explicit IJqInputSource
//   stateful API. The conditional libc strptime fallback is represented by the managed
//   strptime builtin compatibility implementation rather than a POSIX tm ABI.
// - jq_realpath requires an explicit filesystem capability instead of ambient process access.

using System.Text;
using DotNetJq;
using DotNetJq.Compatibility.FileSystem;

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal static jv expand_path(jv path)
        => expand_path(path, get_home);

    // Managed capability overload used by linker.c. A null explicit home is
    // intentionally not replaced with ambient authority.
    internal static jv expand_path(jv path, JqModuleResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return expand_path(
            path,
            () => resolver.HomeDirectory is { } home
                ? jv_string(home)
                : jv_invalid_with_msg(jv_string("Could not find home directory.")));
    }

    private static jv expand_path(jv path, Func<jv> homeProvider)
    {
        ensure_string(path, nameof(path));
        ArgumentNullException.ThrowIfNull(homeProvider);
        var pathText = path.StringValue;
        if (pathText.Length <= 1 || pathText[0] != '~' || pathText[1] != '/')
        {
            return path;
        }

        var home = homeProvider();
        if (!jv_is_valid(home))
        {
            var message = jv_invalid_get_msg(home);
            try
            {
                var result = jv_invalid_with_msg(jv_string(
                    "Could not expand " + pathText + ". (" + message.StringValue + ")"));
                jv_free(path);
                return result;
            }
            finally
            {
                jv_free(message);
            }
        }

        var expanded = jv_string(home.StringValue + "/" + pathText[2..]);
        jv_free(home);
        jv_free(path);
        return expanded;
    }

    internal static jv get_home()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (home is null && OperatingSystem.IsWindows())
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (home is null)
            {
                var homePath = Environment.GetEnvironmentVariable("HOMEPATH");
                if (homePath is not null)
                {
                    home = (Environment.GetEnvironmentVariable("HOMEDRIVE") ?? string.Empty) + homePath;
                }
            }
        }

        return home is null
            ? jv_invalid_with_msg(jv_string("Could not find home directory."))
            : jv_string(home);
    }

    internal static jv jq_realpath(jv path, IJqFileSystem fileSystem)
    {
        ensure_string(path, nameof(path));
        ArgumentNullException.ThrowIfNull(fileSystem);
        var result = fileSystem.ReadFile(path.StringValue);
        if (result.Status is not (JqFileReadStatus.Success or JqFileReadStatus.IsDirectory))
        {
            return path;
        }

        jv_free(path);
        return jv_string(result.Path);
    }

    internal static int _jq_memmem(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        // The pinned Linux jq build delegates to glibc memmem(), which returns the
        // start of the haystack for an empty needle, including a zero-length haystack.
        if (needle.IsEmpty)
        {
            return 0;
        }

        if (haystack.Length < needle.Length)
        {
            return -1;
        }

        return haystack.IndexOf(needle);
    }

    internal static int _jq_memmem(string haystack, string needle)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);
        if (needle.Length == 0)
        {
            return 0;
        }

        if (haystack.Length < needle.Length)
        {
            return -1;
        }

        return haystack.IndexOf(needle, StringComparison.Ordinal);
    }

    internal static void priv_fwrite(ReadOnlySpan<byte> bytes, Stream output, bool isTty)
    {
        ArgumentNullException.ThrowIfNull(output);
        _ = isTty;
        output.Write(bytes);
    }

    internal static void priv_fwrite(string text, Stream output, bool isTty)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        priv_fwrite(bytes, output, isTty);
    }

    private static void ensure_string(jv value, string parameterName)
    {
        if (value.Kind != jv_kind.JV_KIND_STRING)
        {
            throw new ArgumentException("Expected a jq string value.", parameterName);
        }
    }
}
