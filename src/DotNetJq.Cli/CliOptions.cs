using DotNetJq.Port;

namespace DotNetJq.Cli;

internal enum CliImmediateAction
{
    None,
    Help,
    Version,
    BuildConfiguration,
    RunTests,
}

internal enum CliArgumentKind
{
    String,
    Json,
    RawFile,
    SlurpFile,
}

internal readonly record struct CliNamedArgument(
    string Name,
    string Value,
    CliArgumentKind Kind);

internal readonly record struct CliPositionalArgument(string Value, CliArgumentKind Kind);

internal sealed class CliOptions
{
    internal bool Slurp { get; set; }

    internal bool RawInput { get; set; }

    internal bool NullInput { get; set; }

    internal bool RawOutput { get; set; }

    internal bool RawOutputZero { get; set; }

    internal bool JoinOutput { get; set; }

    internal bool CompactOutput { get; set; }

    internal bool AsciiOutput { get; set; }

    internal bool SortKeys { get; set; }

    internal bool ForceColor { get; set; }

    internal bool ForceMonochrome { get; set; }

    internal bool TabIndent { get; set; }

    internal int Indent { get; set; } = 2;

    internal bool Unbuffered { get; set; }

    internal bool Sequence { get; set; }

    internal bool Stream { get; set; }

    internal bool StreamErrors { get; set; }

    internal bool FromFile { get; set; }

    internal bool ExitStatus { get; set; }

    // jq-1.8.2 src/main.c makes -b observable only on Windows: it changes the
    // three CRT standard descriptors from text translation to _O_BINARY.
    // dotnetjq performs standard and file I/O through byte-oriented Stream /
    // FileStream APIs on every OS, so binary mode is already in effect and this
    // parsed compatibility flag is intentionally idempotent. Consequently the
    // policy is cross-platform, while the parity difference (default jq.exe
    // CRLF/CTRL+Z translation) is Windows-only. See
    // porting/WINDOWS_STDIO_CONTRACT.md for the exact observable boundaries.
    internal bool Binary { get; set; }

    internal bool DebugDumpDisassembly { get; set; }

    internal int JqFlags { get; set; }

    internal string? Program { get; set; }

    internal CliImmediateAction ImmediateAction { get; set; }

    internal List<string> Files { get; } = [];

    internal List<string> LibraryPaths { get; } = [];

    internal List<string> TestArguments { get; } = [];

    internal List<CliPositionalArgument> PositionalArguments { get; } = [];

    internal List<CliNamedArgument> NamedArguments { get; } = [];

    internal int ParserFlags =>
        (Sequence ? libjq.JV_PARSE_SEQ : 0) |
        (Stream ? libjq.JV_PARSE_STREAMING : 0) |
        (StreamErrors ? libjq.JV_PARSE_STREAM_ERRORS : 0);
}

internal readonly record struct CliParseResult(CliOptions? Options, string? Error)
{
    internal bool IsSuccess => Options is not null;

    internal static CliParseResult Success(CliOptions options) => new(options, null);

    internal static CliParseResult Failure(string error) => new(null, error);
}
