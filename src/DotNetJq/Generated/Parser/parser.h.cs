// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/parser.h
// Source of truth: src/parser.y
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/parser.h
// Source-of-truth URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/parser.y
// Strategy: GENERATED
// Generation: deterministic managed declaration-template expansion
// Generator: tools/parser-gen/generate.py (template-expander-v1)
// Structural coverage: porting/PARSER_GRAMMAR_COVERAGE.json
// Template: tools/parser-gen/templates/parser.h.cs.in
// Upstream grammar SHA-256: 803aa7c0b1acba2228e52d1de392fb51e60a7bbe23e42870aea1d62c43360c60
// Regenerate: python3 tools/parser-gen/generate.py --upstream upstream/jq
// AUTO-GENERATED OUTPUT: edit the template, never the generated file.
// Target file: src/DotNetJq/Generated/Parser/parser.h.cs
// Substitutions: native-shaped managed block entry points replace Bison declarations.
// Known differences: GPPG replaces Bison's generated parser/runtime; semantic actions
// return the upstream block/inst compiler graph and preserve its ownership contract.

namespace DotNetJq.Port;

internal static partial class libjq
{
    // Native-shaped parser boundary used by linker.c. The caller owns the
    // returned block on success and receives gen_noop() after parser failure.
    internal static int jq_parse(locfile locations, out block answer)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var source = System.Text.Encoding.UTF8.GetString(locations.data);
        var parser = new GeneratedParser.JqGeneratedParser(
            new GeneratedParser.JqGeneratedParserScanner(source),
            locations,
            source,
            locations.fname.StringValue);
        return parser.ParseInto(out answer);
    }

    internal static int jq_parse_library(locfile locations, out block answer)
    {
        var errors = jq_parse(locations, out answer);
        if (errors != 0)
        {
            return errors;
        }

        if (block_has_main(answer))
        {
            locfile_locate(
                locations,
                UNKNOWN_LOCATION,
                "library should only have function definitions, not a main expression");
            return 1;
        }

        if (!block_has_only_binders_and_imports(answer, OP_IS_CALL_PSEUDO))
        {
            throw new InvalidOperationException(
                "The library parser produced a block other than binders and imports.");
        }

        return 0;
    }

    internal static block jq_parse_block(string source) =>
        GeneratedParser.JqGeneratedParser.ParseSource(source);

    internal static block jq_parse_block(string source, string sourceName) =>
        GeneratedParser.JqGeneratedParser.ParseSource(source, sourceName);

    internal static block jq_parse_library_block(string source) =>
        GeneratedParser.JqGeneratedParser.ParseLibrarySource(source);

    internal static block jq_parse_library_block(string source, string sourceName) =>
        GeneratedParser.JqGeneratedParser.ParseLibrarySource(source, sourceName);
}
