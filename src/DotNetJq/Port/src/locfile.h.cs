// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/locfile.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/locfile.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/locfile.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - UTF-8 byte arrays replace borrowed const char* source buffers.
// - Action<jv> is the narrow error-reporting callback adapter used by the managed jq_state.
// - CLR storage replaces native allocation, while locfile_retain/locfile_free keep the explicit
//   upstream locfile reference count and release owned jq values deterministically.
//
// Known differences:
// - locfile stores a callback rather than a native jq_state pointer. The generated production
//   parser collects diagnostics in its managed parser context and constructs locfile without a
//   callback; the callback overload remains the jq-shaped direct locfile reporting boundary.

namespace DotNetJq.Port;

internal readonly record struct location(int start, int end);

/// <summary>
/// Stable managed form of the source identity carried by jq's parser locations.
/// Lines are one-based, matching the object exposed through <c>$__loc__</c>.
/// </summary>
internal readonly record struct jq_source_location(string FileName, int Line);

internal sealed class locfile
{
    private int refct = 1;

    internal locfile(Action<jv>? errorReporter, string fileName, byte[] source, int[] lineMap)
    {
        jq = errorReporter;
        fname = libjq.jv_string(fileName);
        data = source;
        length = source.Length;
        linemap = lineMap;
        nlines = lineMap.Length - 1;
    }

    internal jv fname { get; }

    internal byte[] data { get; }

    internal int length { get; }

    internal int[] linemap { get; }

    internal int nlines { get; }

    internal string? error { get; set; }

    internal Action<jv>? jq { get; }

    internal bool IsFreed => refct == 0;

    internal locfile Retain()
    {
        ObjectDisposedException.ThrowIf(refct == 0, this);
        refct = checked(refct + 1);
        return this;
    }

    internal void Free()
    {
        if (refct <= 0)
        {
            throw new InvalidOperationException("locfile_free called after the final reference was released.");
        }

        refct--;
        if (refct == 0)
        {
            // jq-1.8.2 src/locfile.c:locfile_free() releases the owned fname
            // string when the final locfile reference is dropped.
            libjq.jv_free(fname);
        }
    }
}

internal static partial class libjq
{
    internal static readonly location UNKNOWN_LOCATION = new(-1, -1);
}
