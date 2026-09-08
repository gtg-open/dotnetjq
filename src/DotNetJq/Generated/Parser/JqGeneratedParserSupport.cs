// Support types for the managed parser generated from Grammar/parser.y.
//
// jq-port-upstream-repository: https://github.com/jqlang/jq
// jq-port-upstream-revision: 34f7186b86743a083a589741b6cea95293524108
// jq-port-upstream-path: src/parser.y
//
// GPPG has no Bison %destructor hook. ParserBlockOwner and the two literal
// owner ledgers below provide the same deterministic unwind boundary for
// discarded semantic values while the grammar actions themselves construct
// jq's block/inst compiler IR directly.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DotNetJq;
using DotNetJq.Port;
using StarodubOleg.GPPG.Runtime;

namespace DotNetJq.Port.GeneratedParser;

/// <summary>
/// Location carrier preserving jq's UTF-8 byte offsets and the UTF-16 spans
/// needed to render managed diagnostics.
/// </summary>
internal sealed class JqParserLocation : IMerge<JqParserLocation>
{
    public JqParserLocation()
    {
    }

    internal JqParserLocation(
        int startByte,
        int endByte,
        int startIndex,
        int endIndex,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        StartByte = startByte;
        EndByte = endByte;
        StartIndex = startIndex;
        EndIndex = endIndex;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    internal int StartByte { get; }

    internal int EndByte { get; }

    internal int StartIndex { get; }

    internal int EndIndex { get; }

    internal int StartLine { get; }

    internal int StartColumn { get; }

    internal int EndLine { get; }

    internal int EndColumn { get; }

    internal location NativeLocation => new(StartByte, EndByte);

    public JqParserLocation Merge(JqParserLocation last) => new(
        StartByte,
        last.EndByte,
        StartIndex,
        last.EndIndex,
        StartLine,
        StartColumn,
        last.EndLine,
        last.EndColumn);
}

internal sealed record GeneratedParserDiagnostic(
    string Message,
    JqParserLocation Location);

/// <summary>
/// One owned block semantic value. Normal reductions move the block out of
/// this holder; parse abort/recovery cleanup frees every holder not moved.
/// This is the managed equivalent of parser.y's %destructor block_free($$).
/// </summary>
internal sealed class ParserBlockOwner
{
    private block value;
    private bool owns = true;

    internal ParserBlockOwner(block value)
    {
        this.value = value;
    }

    internal block Borrow()
    {
        if (!owns)
        {
            throw new InvalidOperationException("The parser block owner has already been moved.");
        }

        return value;
    }

    internal block Take()
    {
        var result = Borrow();
        value = default;
        owns = false;
        return result;
    }

    internal void Free()
    {
        if (!owns)
        {
            return;
        }

        var released = value;
        value = default;
        owns = false;
        libjq.block_free(released);
    }
}

internal partial class JqGeneratedParser
{
    internal const int MaximumParserStackDepth =
        PushdownPrefixState<ValueType>.MaximumDepth;

    // The pinned Bison parser accepts 9,994 balanced parenthesis levels and
    // reports memory exhaustion at 9,995. Keep that jq-visible boundary even
    // though GPPG's state bookkeeping differs by one slot.
    internal const int MaximumNestedParenthesisDepth =
        MaximumParserStackDepth - 6;

    private readonly locfile generatedParserLocations;
    private readonly int generatedParserSourceLineOffset;
    private readonly string generatedParserSource;
    private readonly string generatedParserSourceName;
    private readonly List<GeneratedParserDiagnostic> generatedParserDiagnostics;
    private readonly JqGeneratedParserScanner? generatedParserScanner;
    private readonly List<ParserBlockOwner> generatedParserBlocks = [];
    private readonly Dictionary<object, jv> generatedParserLiterals =
        new(ReferenceEqualityComparer.Instance);
    private readonly Stack<jv> generatedParserStringFormats = [];

    internal JqGeneratedParser(
        AbstractScanner<ValueType, JqParserLocation> scanner,
        locfile locations,
        string source,
        string sourceName,
        int sourceLineOffset = 0)
        : base(scanner)
    {
        generatedParserLocations = locations;
        generatedParserSource = source;
        generatedParserSourceName = sourceName;
        generatedParserSourceLineOffset = sourceLineOffset;
        generatedParserScanner = scanner as JqGeneratedParserScanner;
        generatedParserDiagnostics = generatedParserScanner is not null
            ? generatedParserScanner.Diagnostics
            : [];
    }

    internal static block ParseSource(
        string source,
        string sourceName = "<top-level>",
        int sourceLineOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceName);
        var locations = libjq.locfile_init(sourceName, source);
        try
        {
            var parser = new JqGeneratedParser(
                new JqGeneratedParserScanner(source),
                locations,
                source,
                sourceName,
                sourceLineOffset);
            return parser.ParseProgram();
        }
        finally
        {
            libjq.locfile_free(locations);
        }
    }

    internal static block ParseLibrarySource(
        string source,
        string sourceName = "<top-level>")
    {
        var result = ParseSource(source, sourceName);
        if (libjq.block_has_main(result))
        {
            libjq.block_free(result);
            throw new JqCompileException(
                "jq: error: library should only have function definitions, not a main expression");
        }

        if (!libjq.block_has_only_binders_and_imports(
                result,
                libjq.OP_IS_CALL_PSEUDO))
        {
            libjq.block_free(result);
            throw new InvalidOperationException(
                "The library parser produced a block other than binders and imports.");
        }

        return result;
    }

    internal block ParseProgram()
    {
        try
        {
            bool succeeded;
            try
            {
                succeeded = Parse();
            }
            catch (ParserStackExhaustedException)
            {
                throw new JqCompileException("jq: error: memory exhausted");
            }

            if (generatedParserDiagnostics.Count != 0)
            {
                throw new JqCompileException(
                    string.Join(
                        "\n",
                        generatedParserDiagnostics.Select(RenderDiagnostic)));
            }

            if (!succeeded)
            {
                throw new JqCompileException("jq: error: generated parser rejected the input");
            }

            return TakeBlock(CurrentSemanticValue.blk);
        }
        finally
        {
            // GPPG has no %destructor. Release blocks/literals abandoned by
            // recovery, a failed reduction, or parser-stack exhaustion.
            FreeOutstandingBlocks();
            FreeOutstandingParserLiterals();
            generatedParserScanner?.FreeUnclaimedLiterals();
        }
    }

    internal int ParseInto(out block answer)
    {
        try
        {
            answer = ParseProgram();
            return 0;
        }
        catch (JqCompileException exception)
        {
            answer = libjq.gen_noop();
            if (generatedParserDiagnostics.Count == 0)
            {
                const string prefix = "jq: error: ";
                var message = exception.Message.StartsWith(prefix, StringComparison.Ordinal)
                    ? exception.Message[prefix.Length..]
                    : exception.Message;
                libjq.locfile_locate(
                    generatedParserLocations,
                    libjq.UNKNOWN_LOCATION,
                    "%s",
                    message);
            }
            else
            {
                foreach (var diagnostic in generatedParserDiagnostics)
                {
                    libjq.locfile_locate(
                        generatedParserLocations,
                        diagnostic.Location.NativeLocation,
                        "jq: error: %s",
                        diagnostic.Message);
                }
            }

            return Math.Max(1, generatedParserDiagnostics.Count);
        }
    }

    private ParserBlockOwner OwnBlock(block value)
    {
        var owner = new ParserBlockOwner(value);
        generatedParserBlocks.Add(owner);
        return owner;
    }

    private static block BorrowBlock(ParserBlockOwner? owner) =>
        (owner ?? throw new InvalidOperationException("Missing parser block semantic value.")).Borrow();

    private static block TakeBlock(ParserBlockOwner? owner) =>
        (owner ?? throw new InvalidOperationException("Missing parser block semantic value.")).Take();

    private void FreeOutstandingBlocks()
    {
        foreach (var owner in generatedParserBlocks)
        {
            owner.Free();
        }

        generatedParserBlocks.Clear();
    }

    private jv OwnParserLiteral(jv value)
    {
        if (value.Value is { } allocation)
        {
            generatedParserLiterals.Add(allocation, value);
        }

        return value;
    }

    private jv TakeLiteral(jv value)
    {
        generatedParserScanner?.RelinquishLiteral(value);
        if (value.Value is { } allocation)
        {
            _ = generatedParserLiterals.Remove(allocation);
        }

        return value;
    }

    private void FreeLiteral(jv value)
    {
        _ = TakeLiteral(value);
        libjq.jv_free(value);
    }

    private void FreeOutstandingParserLiterals()
    {
        generatedParserStringFormats.Clear();
        foreach (var value in generatedParserLiterals.Values)
        {
            libjq.jv_free(value);
        }

        generatedParserLiterals.Clear();
    }

    private void ReportDiagnostic(JqParserLocation location, string message) =>
        generatedParserDiagnostics.Add(new GeneratedParserDiagnostic(message, location));

    // GPPG locates its synthetic `error` symbol at the recovery lookahead,
    // while Bison's YYLLOC_DEFAULT begins it at the first discarded object-key
    // token. The scanner records that key extent when yyerror fires. Rebuild
    // only these two explicit error-production locations so parser.y's @$ / @1
    // diagnostics stay byte-for-byte compatible with the pinned parser.
    private JqParserLocation ObjectEntryRecoveryLocation(JqParserLocation location)
    {
        if (generatedParserScanner?.LastErrorObjectEntryStartIndex is not { } rawStart)
        {
            return location;
        }

        var start = Math.Clamp(rawStart, 0, location.StartIndex);
        var end = generatedParserScanner.LastErrorObjectEntryEndIndex is { } rawEnd
            ? Math.Clamp(rawEnd, start, generatedParserSource.Length)
            : Math.Clamp(location.EndIndex, start, generatedParserSource.Length);
        while (start < location.StartIndex && char.IsWhiteSpace(generatedParserSource[start]))
        {
            start++;
        }

        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < start; index++)
        {
            if (generatedParserSource[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        var endLine = line;
        var endLineStart = lineStart;
        for (var index = start; index < end; index++)
        {
            if (generatedParserSource[index] == '\n')
            {
                endLine++;
                endLineStart = index + 1;
            }
        }

        return new JqParserLocation(
            Encoding.UTF8.GetByteCount(generatedParserSource.AsSpan(0, start)),
            Encoding.UTF8.GetByteCount(generatedParserSource.AsSpan(0, end)),
            start,
            end,
            line,
            Encoding.UTF8.GetByteCount(
                generatedParserSource.AsSpan(lineStart, start - lineStart)) + 1,
            endLine,
            Encoding.UTF8.GetByteCount(
                generatedParserSource.AsSpan(endLineStart, end - endLineStart)) + 1);
    }

    private string RenderDiagnostic(GeneratedParserDiagnostic diagnostic)
    {
        var sourceIndex = Math.Clamp(
            diagnostic.Location.StartIndex,
            0,
            generatedParserSource.Length);
        var endIndex = Math.Clamp(
            diagnostic.Location.EndIndex,
            sourceIndex,
            generatedParserSource.Length);
        var token = new Token(
            TokenKind.InvalidCharacter,
            generatedParserSource[sourceIndex..endIndex],
            Math.Max(0, diagnostic.Location.StartByte),
            sourceIndex,
            Math.Max(1, diagnostic.Location.StartLine),
            Math.Max(1, diagnostic.Location.StartColumn));
        return libjq.CreateSourceError(
            generatedParserSource,
            generatedParserSourceName,
            diagnostic.Message,
            token,
            Math.Max(1, diagnostic.Location.EndByte - diagnostic.Location.StartByte)).Message;
    }
}
