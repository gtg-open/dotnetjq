// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_file.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_file.c
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_file.c.cs
// UPSTREAM COMPONENT: jq's raw/JSON file loader and its jv_load_file entry point.
// REPLACEMENT: System.IO is available only through an explicitly supplied IJqFileSystem.
// WHY: a capability object keeps host file access explicit, bounded, and testable.
// BEHAVIORAL CONTRACT: raw reads preserve UTF-8 text and JSON reads return every top-level value.
// KNOWN DIFFERENCES: host I/O error wording is normalized after the jq-compatible filename prefix.
// TESTS COVERING THE SUBSTITUTION: ModuleFileCompatibilityTests, CliFileAndModuleTests,
// and module fixture coverage.

using DotNetJq.Compatibility.FileSystem;

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal static jv jv_load_file(IJqFileSystem fileSystem, string filename, int raw)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(filename);

        var result = fileSystem.ReadFile(filename);
        return jv_load_file(filename, raw, result);
    }

    // jq-1.8.2 src/jv_file.c:jv_load_file opens the named file once and then parses
    // that opened stream. This overload lets a caller that must format its own open
    // diagnostic pass the same completed read into the jq-shaped loader rather than
    // resolving and reading the pathname a second time.
    internal static jv jv_load_file(string filename, int raw, JqFileReadResult result)
    {
        ArgumentNullException.ThrowIfNull(filename);

        if (!result.IsSuccess)
        {
            var detail = result.Status == JqFileReadStatus.IsDirectory
                ? "It's a directory"
                : result.ErrorMessage ?? "unknown error";
            return jv_invalid_with_msg(jv_string($"Could not open {filename}: {detail}"));
        }

        if (raw != 0)
        {
            // jq-1.8.2 src/jv_file.c passes the fread byte count directly to
            // jv_string_sized(). Preserve that byte boundary so its exact
            // invalid-unit repair runs before any managed UTF-16 view exists.
            return jv_string_sized(result.Contents.Span, result.Contents.Length);
        }

        // jq-1.8.2 src/jv_file.c feeds every file byte through jv_parser.
        // Use the same managed port here instead of Utf8JsonReader: besides
        // preserving jq diagnostics and multiple top-level values, this keeps
        // the native 10,000-container boundary. Utf8JsonReader's previous
        // 1,024-depth proxy silently rejected imports accepted by jq.
        var parser = jv_parser_new(0);
        var data = jv_array();
        try
        {
            jv_parser_set_buf(parser, result.Contents, isPartial: false);
            while (true)
            {
                var value = jv_parser_next(parser);
                if (jv_is_valid(value))
                {
                    data = jv_array_append(data, value);
                    continue;
                }

                if (jv_invalid_has_msg(jv_copy(value)))
                {
                    jv_free(data);
                    data = value;
                }
                else
                {
                    jv_free(value);
                }

                return data;
            }
        }
        finally
        {
            jv_parser_free(parser);
        }
    }
}
