// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jq_parser.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jq_parser.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jq_parser.h.cs
// Substitutions: declarations are emitted with the generated parser interface in
// Generated/Parser/parser.h.cs and fill the same native-shaped block result.
// Known differences: C pointers are represented by locfile references and out block values.

namespace DotNetJq.Port;

// jq_parse(locfile, out block) and jq_parse_library(locfile, out block) are kept
// beside the generated parser token interface so their implementation and the
// GPPG entry point cannot drift apart.
internal static partial class libjq
{
}
