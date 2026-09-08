#!/usr/bin/env python3
"""Deterministically expand the remaining managed jq declaration templates.

This is intentionally a template expander, not a Flex/Bison translator. The
templates own only the lexer/parser declaration surfaces; the production parser
and scanner have separate GPPG/GPLEX pipelines rooted in the committed managed
``Grammar/parser.y`` and ``Grammar/lexer.l`` files. Pinned upstream hashes retain
the explicit structural audit boundary.
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
PINNED_COMMIT = "34f7186b86743a083a589741b6cea95293524108"
GRAMMAR_HASHES = {
    "src/lexer.l": "cfb3af17a786df30d7e30dae5861b84747d4904f8ce7ae9ab9b48bde342ee7f3",
    "src/parser.y": "803aa7c0b1acba2228e52d1de392fb51e60a7bbe23e42870aea1d62c43360c60",
}
GENERATION_DESCRIPTION = "deterministic managed declaration-template expansion"
COVERAGE_OUTPUT = "porting/PARSER_GRAMMAR_COVERAGE.json"

# These routes document the managed GPPG action-ownership boundary. The
# generated coverage report expands them to every exact upstream production.
# DotNetJq.ParserGen separately proves the managed grammar has the same ordered
# alternatives and produces the expected conflict-free table dimensions.
PRODUCTION_HANDLERS = {
    name: f"Grammar/parser.y {name} C# actions -> GPPG reduction cases"
    for name in (
        "TopLevel", "Module", "Imports", "FuncDefs", "Query", "Expr",
        "Import", "ImportWhat", "ImportFrom", "FuncDef", "Params", "Param",
        "StringStart", "String", "QQString", "ElseBody", "Term", "Args",
        "Arg", "RepPatterns", "Patterns", "Pattern", "ArrayPats", "ObjPats",
        "ObjPat", "Keyword", "DictPairs", "DictPair", "DictExpr",
    )
}

TOKEN_HANDLERS = {
    "INVALID_CHARACTER": "TokenKind.InvalidCharacter",
    "IDENT": "TokenKind.Identifier",
    "FIELD": "TokenKind.Dot + adjacent TokenKind.Identifier",
    "BINDING": "TokenKind.Binding",
    "LITERAL": "TokenKind.Number",
    "FORMAT": "TokenKind.Format",
    "REC": "TokenKind.Recursive",
    "SETMOD": "TokenKind.ModuloAssign",
    "EQ": "TokenKind.Equal",
    "NEQ": "TokenKind.NotEqual",
    "DEFINEDOR": "TokenKind.DefinedOr",
    "AS": "JqGeneratedParserScanner keyword mapping -> JqParserToken.AS",
    "DEF": "JqGeneratedParserScanner keyword mapping -> JqParserToken.DEF",
    "MODULE": "JqGeneratedParserScanner keyword mapping -> JqParserToken.MODULE",
    "IMPORT": "JqGeneratedParserScanner keyword mapping -> JqParserToken.IMPORT",
    "INCLUDE": "JqGeneratedParserScanner keyword mapping -> JqParserToken.INCLUDE",
    "IF": "JqGeneratedParserScanner keyword mapping -> JqParserToken.IF",
    "THEN": "JqGeneratedParserScanner keyword mapping -> JqParserToken.THEN",
    "ELSE": "JqGeneratedParserScanner keyword mapping -> JqParserToken.ELSE",
    "ELSE_IF": "JqGeneratedParserScanner keyword mapping -> JqParserToken.ELSE_IF",
    "REDUCE": "JqGeneratedParserScanner keyword mapping -> JqParserToken.REDUCE",
    "FOREACH": "JqGeneratedParserScanner keyword mapping -> JqParserToken.FOREACH",
    "END": "JqGeneratedParserScanner keyword mapping -> JqParserToken.END",
    "AND": "JqGeneratedParserScanner keyword mapping -> JqParserToken.AND",
    "OR": "JqGeneratedParserScanner keyword mapping -> JqParserToken.OR",
    "TRY": "JqGeneratedParserScanner keyword mapping -> JqParserToken.TRY",
    "CATCH": "JqGeneratedParserScanner keyword mapping -> JqParserToken.CATCH",
    "LABEL": "JqGeneratedParserScanner keyword mapping -> JqParserToken.LABEL",
    "BREAK": "JqGeneratedParserScanner keyword mapping -> JqParserToken.BREAK",
    "LOC": "TokenKind.Location",
    "SETPIPE": "TokenKind.PipeAssign",
    "SETPLUS": "TokenKind.PlusAssign",
    "SETMINUS": "TokenKind.MinusAssign",
    "SETMULT": "TokenKind.MultiplyAssign",
    "SETDIV": "TokenKind.DivideAssign",
    "SETDEFINEDOR": "TokenKind.DefinedOrAssign",
    "LESSEQ": "TokenKind.LessEqual",
    "GREATEREQ": "TokenKind.GreaterEqual",
    "ALTERNATION": "TokenKind.DestructureAlternative",
    "QQSTRING_START": "TokenKind.StringStart",
    "QQSTRING_TEXT": "TokenKind.StringText",
    "QQSTRING_INTERP_START": "TokenKind.StringInterpolationStart",
    "QQSTRING_INTERP_END": "TokenKind.StringInterpolationEnd",
    "QQSTRING_END": "TokenKind.StringEnd",
}

LITERAL_TOKEN_HANDLERS = {
    "'.'": "TokenKind.Dot",
    "'?'": "TokenKind.Question",
    "'='": "TokenKind.Assign",
    "';'": "TokenKind.Semicolon",
    "','": "TokenKind.Comma",
    "':'": "TokenKind.Colon",
    "'|'": "TokenKind.Pipe",
    "'+'": "TokenKind.Plus",
    "'-'": "TokenKind.Minus",
    "'*'": "TokenKind.Multiply",
    "'/'": "TokenKind.Divide",
    "'%'": "TokenKind.Modulo",
    "'$'": "TokenKind.Dollar",
    "'<'": "TokenKind.Less",
    "'>'": "TokenKind.Greater",
    "'('": "TokenKind.LeftParenthesis",
    "')'": "TokenKind.RightParenthesis",
    "'['": "TokenKind.LeftBracket",
    "']'": "TokenKind.RightBracket",
    "'{'": "TokenKind.LeftBrace",
    "'}'": "TokenKind.RightBrace",
}

PRECEDENCE_HANDLERS = {
    line: "Grammar/parser.y declaration -> conflict-free GPPG parse table"
    for line in (100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 112, 113, 114)
}

LEXER_RULE_HANDLERS = {
    41: "managed lexer.l action: push IN_COMMENT",
    43: "managed lexer.l action: consume comment content",
    44: "managed lexer.l action: pop IN_COMMENT on line ending",
    46: "managed lexer.l action: pop IN_COMMENT at EOF",
    **{line: "managed lexer.l action: emit keyword or operator token" for line in range(48, 82)},
    83: "managed lexer.l enter helper: push delimiter state and emit opener",
    87: "managed lexer.l try_exit helper: pop matching delimiter or emit InvalidCharacter",
    91: "managed lexer.l action: emit Format",
    95: "managed lexer.l action: emit Number",
    99: "managed lexer.l action: push IN_QQSTRING and emit StringStart",
    105: "managed lexer.l enter helper: push IN_QQINTERP and emit StringInterpolationStart",
    108: "managed lexer.l action: pop IN_QQSTRING and emit StringEnd",
    112: "managed lexer.l action: emit raw escape-run StringText",
    119: "managed lexer.l action: emit raw StringText",
    123: "managed lexer.l action: emit InvalidCharacter",
    129: "managed lexer.l action: emit Identifier",
    130: "managed lexer.l EmitField adapter: emit Dot plus adjacent Identifier",
    131: "managed lexer.l action: emit Binding",
    133: "managed lexer.l action: skip exact jq whitespace",
    135: "managed lexer.l diagnostic/raw-mode InvalidCharacter adapter",
}

RECOVERY_ORACLE_CASES = {
    ("Term", "BREAK error"): "break $__loc__",
    ("Term", "'.' error"): ". 0",
    ("Term", "'.' IDENT error"): ". foo",
    ("Term", '"if" Query "then" error'): "if true then",
    ("Term", '"try" Expr "catch" error'): "try . catch",
    ("Term", "'(' error ')'"): "(;)",
    ("Term", "'[' error ']'"): "[;]",
    ("Term", "Term '[' error ']'"): ".[;]",
    ("Term", "'{' error '}'"): "{;}",
    ("ObjPat", "error ':' Pattern"): ". as {foo +: $x} | .",
    ("DictPair", "error ':' DictExpr"): "{$__loc__:1}",
}


@dataclass(frozen=True)
class Artifact:
    component: str
    grammar: str
    template: str
    output: str


ARTIFACTS = (
    Artifact(
        "lexer",
        "src/lexer.l",
        "tools/parser-gen/templates/lexer.h.cs.in",
        "src/DotNetJq/Generated/Lexer/lexer.h.cs",
    ),
    Artifact(
        "parser",
        "src/parser.y",
        "tools/parser-gen/templates/parser.h.cs.in",
        "src/DotNetJq/Generated/Parser/parser.h.cs",
    ),
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def resolve_upstream(configured: str | None) -> Path:
    candidates: list[Path] = []
    if configured:
        candidates.append(Path(configured))
    environment = os.environ.get("DOTNETJQ_UPSTREAM")
    if environment:
        candidates.append(Path(environment))
    candidates.append(ROOT / "upstream/jq")

    for candidate in candidates:
        path = candidate.expanduser().resolve()
        if all((path / relative).is_file() for relative in GRAMMAR_HASHES):
            return path

    rendered = ", ".join(str(path) for path in candidates)
    raise RuntimeError(
        "pinned jq checkout not found; pass --upstream or set DOTNETJQ_UPSTREAM "
        f"(checked: {rendered})"
    )


def verify_upstream(upstream: Path) -> None:
    for relative, expected in GRAMMAR_HASHES.items():
        actual = sha256(upstream / relative)
        if actual != expected:
            raise RuntimeError(
                f"{relative} SHA-256 mismatch: expected {expected}, got {actual}"
            )

    try:
        completed = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=upstream,
            check=True,
            capture_output=True,
            text=True,
        )
    except (FileNotFoundError, subprocess.CalledProcessError):
        # A source archive has no Git metadata; exact grammar hashes remain the
        # authoritative generation inputs in that environment.
        return

    actual_commit = completed.stdout.strip()
    if actual_commit != PINNED_COMMIT:
        raise RuntimeError(
            f"upstream commit mismatch: expected {PINNED_COMMIT}, got {actual_commit}"
        )


def strip_parser_actions(text: str) -> str:
    """Remove C actions/comments while retaining grammar text and line numbers."""
    result: list[str] = []
    index = 0
    action_depth = 0
    quote: str | None = None
    block_comment = False
    line_comment = False
    while index < len(text):
        current = text[index]
        following = text[index + 1] if index + 1 < len(text) else ""

        if block_comment:
            if current == "*" and following == "/":
                result.extend((" ", " "))
                index += 2
                block_comment = False
            else:
                result.append("\n" if current == "\n" else " ")
                index += 1
            continue

        if line_comment:
            result.append("\n" if current == "\n" else " ")
            index += 1
            if current == "\n":
                line_comment = False
            continue

        if quote is not None:
            result.append(current if action_depth == 0 else ("\n" if current == "\n" else " "))
            if current == "\\" and following:
                result.append(
                    following if action_depth == 0 else ("\n" if following == "\n" else " ")
                )
                index += 2
                continue
            if current == quote:
                quote = None
            index += 1
            continue

        if current == "/" and following == "*":
            result.extend((" ", " "))
            index += 2
            block_comment = True
            continue
        if action_depth > 0 and current == "/" and following == "/":
            result.extend((" ", " "))
            index += 2
            line_comment = True
            continue
        if current in ("'", '"'):
            quote = current
            result.append(current if action_depth == 0 else " ")
            index += 1
            continue
        if current == "{":
            action_depth += 1
            result.append(" ")
            index += 1
            continue
        if action_depth > 0 and current == "}":
            action_depth -= 1
            result.append(" ")
            index += 1
            continue

        result.append(current if action_depth == 0 else ("\n" if current == "\n" else " "))
        index += 1

    if action_depth != 0 or quote is not None or block_comment:
        raise RuntimeError("could not structurally scan parser.y grammar actions")
    return "".join(result)


def split_unquoted(text: str, delimiter: str) -> list[tuple[int, str]]:
    result: list[tuple[int, str]] = []
    start = 0
    quote: str | None = None
    index = 0
    while index < len(text):
        current = text[index]
        if quote is not None:
            if current == "\\":
                index += 2
                continue
            if current == quote:
                quote = None
        elif current in ("'", '"'):
            quote = current
        elif current == delimiter:
            result.append((start, text[start:index]))
            start = index + 1
        index += 1
    result.append((start, text[start:]))
    return result


def parser_inventory(parser_text: str) -> tuple[list[dict[str, object]], list[dict[str, object]], list[dict[str, object]]]:
    separators = [match.start() for match in re.finditer(r"(?m)^%%\s*$", parser_text)]
    if len(separators) != 2:
        raise RuntimeError("parser.y must contain exactly two grammar separators")

    declarations = parser_text[: separators[0]]
    grammar_start = parser_text.find("\n", separators[0]) + 1
    grammar = parser_text[grammar_start : separators[1]]
    stripped = strip_parser_actions(grammar)
    lhs_matches = list(re.finditer(r"(?m)^([A-Za-z][A-Za-z0-9_]*)\s*:", stripped))
    productions: list[dict[str, object]] = []
    seen_lhs: set[str] = set()
    grammar_start_line = parser_text[:grammar_start].count("\n") + 1
    for lhs_index, match in enumerate(lhs_matches):
        lhs = match.group(1)
        seen_lhs.add(lhs)
        body_start = match.end()
        body_end = lhs_matches[lhs_index + 1].start() if lhs_index + 1 < len(lhs_matches) else len(stripped)
        body = stripped[body_start:body_end]
        alternatives = split_unquoted(body, "|")
        for alternative_index, (relative_start, alternative) in enumerate(alternatives, start=1):
            normalized = " ".join(alternative.strip().removesuffix(";").strip().split())
            if not normalized:
                raise RuntimeError(f"empty unmapped production alternative for {lhs}")
            absolute_start = body_start + relative_start
            leading = len(alternative) - len(alternative.lstrip())
            line = grammar_start_line + stripped[: absolute_start + leading].count("\n")
            production: dict[str, object] = {
                "lhs": lhs,
                "alternative": alternative_index,
                "line": line,
                "rhs": normalized,
                "managedHandler": PRODUCTION_HANDLERS.get(lhs),
            }
            if "error" in normalized.split():
                production["observableRecovery"] = {
                    "oracleSource": RECOVERY_ORACLE_CASES.get((lhs, normalized)),
                    "test": (
                        "ParserRecoveryCompatibilityTests."
                        "EveryExplicitBisonErrorProductionHasAnExactObservableRecoveryCase"
                    ),
                }
            productions.append(production)

    if seen_lhs != set(PRODUCTION_HANDLERS):
        missing = sorted(seen_lhs - set(PRODUCTION_HANDLERS))
        stale = sorted(set(PRODUCTION_HANDLERS) - seen_lhs)
        raise RuntimeError(f"production handler coverage mismatch; missing={missing}, stale={stale}")

    observed_recovery_keys = {
        (str(production["lhs"]), str(production["rhs"]))
        for production in productions
        if "error" in str(production["rhs"]).split()
    }
    if observed_recovery_keys != set(RECOVERY_ORACLE_CASES):
        missing = sorted(observed_recovery_keys - set(RECOVERY_ORACLE_CASES))
        stale = sorted(set(RECOVERY_ORACLE_CASES) - observed_recovery_keys)
        raise RuntimeError(
            f"recovery oracle coverage mismatch; missing={missing}, stale={stale}"
        )

    declared_tokens: list[dict[str, object]] = []
    aliases: set[str] = set()
    token_pattern = re.compile(
        r"(?m)^%token(?:\s+<[^>]+>)?\s+([A-Z][A-Z0-9_]*)(?:\s+([^\s]+))?\s*$"
    )
    declared_names: set[str] = set()
    for match in token_pattern.finditer(declarations):
        name = match.group(1)
        alias = match.group(2)
        declared_names.add(name)
        if alias:
            aliases.add(alias)
        declared_tokens.append(
            {
                "name": name,
                "alias": alias,
                "line": declarations[: match.start()].count("\n") + 1,
                "managedHandler": TOKEN_HANDLERS.get(name),
            }
        )
    if declared_names != set(TOKEN_HANDLERS):
        missing = sorted(declared_names - set(TOKEN_HANDLERS))
        stale = sorted(set(TOKEN_HANDLERS) - declared_names)
        raise RuntimeError(f"token handler coverage mismatch; missing={missing}, stale={stale}")

    literal_pattern = re.compile(r"'(?:\\.|[^'])*'|\"(?:\\.|[^\"])*\"")
    implicit_literals = sorted(set(literal_pattern.findall(stripped)) - aliases)
    if set(implicit_literals) != set(LITERAL_TOKEN_HANDLERS):
        missing = sorted(set(implicit_literals) - set(LITERAL_TOKEN_HANDLERS))
        stale = sorted(set(LITERAL_TOKEN_HANDLERS) - set(implicit_literals))
        raise RuntimeError(f"literal token coverage mismatch; missing={missing}, stale={stale}")
    for literal in implicit_literals:
        declared_tokens.append(
            {
                "name": literal,
                "alias": None,
                "line": None,
                "managedHandler": LITERAL_TOKEN_HANDLERS[literal],
            }
        )
    declared_tokens.extend(
        (
            {
                "name": "$end",
                "alias": None,
                "line": None,
                "managedHandler": "TokenKind.End",
            },
            {
                "name": "error",
                "alias": None,
                "line": None,
                "managedHandler": "Grammar/parser.y error productions and GPPG recovery",
            },
            {
                "name": "$undefined",
                "alias": None,
                "line": None,
                "managedHandler": "TokenKind.InvalidCharacter/error diagnostics",
            },
            {
                "name": "FUNCDEF",
                "alias": None,
                "line": 100,
                "managedHandler": "Grammar/parser.y FUNCDEF precedence and GPPG table",
            },
            {
                "name": "NONOPT",
                "alias": None,
                "line": 110,
                "managedHandler": "Grammar/parser.y NONOPT precedence and GPPG table",
            },
        )
    )

    precedence: list[dict[str, object]] = []
    precedence_pattern = re.compile(r"(?m)^%(precedence|right|left|nonassoc)\s+(.+)$")
    seen_precedence_lines: set[int] = set()
    for match in precedence_pattern.finditer(declarations):
        line = declarations[: match.start()].count("\n") + 1
        seen_precedence_lines.add(line)
        precedence.append(
            {
                "line": line,
                "declaration": "%" + match.group(1) + " " + " ".join(match.group(2).split()),
                "managedHandler": PRECEDENCE_HANDLERS.get(line),
            }
        )
    if seen_precedence_lines != set(PRECEDENCE_HANDLERS):
        missing = sorted(seen_precedence_lines - set(PRECEDENCE_HANDLERS))
        stale = sorted(set(PRECEDENCE_HANDLERS) - seen_precedence_lines)
        raise RuntimeError(f"precedence coverage mismatch; missing={missing}, stale={stale}")
    return declared_tokens, precedence, productions


def lexer_inventory(lexer_text: str) -> list[dict[str, object]]:
    lines = lexer_text.splitlines()
    if set(LEXER_RULE_HANDLERS) != {
        41, 43, 44, 46, *range(48, 82), 83, 87, 91, 95, 99,
        105, 108, 112, 119, 123, 129, 130, 131, 133, 135,
    }:
        raise RuntimeError("internal lexer-rule coverage list is incomplete")

    result: list[dict[str, object]] = []
    for line, handler in sorted(LEXER_RULE_HANDLERS.items()):
        signature = " ".join(lines[line - 1].strip().split())
        if not signature or "{" not in signature:
            raise RuntimeError(f"lexer rule mapping line {line} no longer identifies a rule")
        result.append({"line": line, "rule": signature, "managedHandler": handler})
    return result


def render_coverage(upstream: Path) -> bytes:
    lexer_text = (upstream / "src/lexer.l").read_text(encoding="utf-8")
    parser_text = (upstream / "src/parser.y").read_text(encoding="utf-8")
    tokens, precedence, productions = parser_inventory(parser_text)
    if (len(tokens), len(precedence), len(productions)) != (70, 14, 167):
        raise RuntimeError(
            "pinned parser inventory differs from generated Bison baseline: "
            f"tokens={len(tokens)}, precedence={len(precedence)}, productions={len(productions)}"
        )
    lexer_rules = lexer_inventory(lexer_text)
    if len(lexer_rules) != 53:
        raise RuntimeError(
            f"pinned lexer inventory differs from Flex source baseline: rules={len(lexer_rules)}"
        )
    report = {
        "schemaVersion": 2,
        "upstreamRevision": PINNED_COMMIT,
        "grammarSha256": GRAMMAR_HASHES,
        "parserArchitecture": (
            "GPPG-generated shift/reduce parser from the committed managed parser.y "
            "with manually ported C# semantic actions"
        ),
        "lexerArchitecture": "GPLEX-generated scanner from the committed managed lexer.l with C# actions",
        "claims": {
            "inventoryCompleteForPinnedGrammar": True,
            "explicitErrorProductionObservableCoverage": True,
            "bisonStateMachineEquivalent": False,
            "semanticParity": True,
            "semanticParityScope": (
                "Observable jq lexer/parser grammar, tokenization, UTF-8 source locations, "
                "compile diagnostics, library parsing, and recovery behavior"
            ),
            "semanticParityExclusions": [
                "Flex/Bison generated-C architecture and byte-identical parse-table identity",
                "C# semantic actions remain a manually maintained language-specific port",
                "Parser and lexer declaration surfaces remain managed templates",
            ],
        },
        "bisonGeneratedBaseline": {
            "terminalSymbols": 70,
            "grammarProductionAlternatives": 167,
            "rulesIncludingSyntheticAccept": 168,
        },
        "managedGppgBaseline": {
            "grammarProductionAlternatives": 167,
            "rulesIncludingGeneratorRules": 169,
            "states": 312,
            "unresolvedConflicts": 0,
        },
        "recoveryBaseline": {
            "explicitErrorProductionAlternatives": 11,
            "oracleLinkedAlternatives": 11,
            "malformedBoundaryVariants": 7,
            "closestValidControls": 7,
        },
        "lexerRules": lexer_rules,
        "tokens": tokens,
        "precedence": precedence,
        "productions": productions,
    }
    return (json.dumps(report, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def render(artifact: Artifact) -> bytes:
    template_path = ROOT / artifact.template
    template = template_path.read_text(encoding="utf-8")
    replacements = {
        "{{GENERATION_DESCRIPTION}}": GENERATION_DESCRIPTION,
        "{{GRAMMAR_SHA256}}": GRAMMAR_HASHES[artifact.grammar],
    }
    for marker, value in replacements.items():
        count = template.count(marker)
        if count != 1:
            raise RuntimeError(
                f"{artifact.template} must contain {marker} exactly once; found {count}"
            )
        template = template.replace(marker, value)

    if "{{" in template or "}}" in template:
        raise RuntimeError(f"unexpanded template marker in {artifact.template}")
    return template.encode("utf-8")


def diff(output: Path, expected: bytes) -> str:
    actual_text = output.read_text(encoding="utf-8") if output.is_file() else ""
    expected_text = expected.decode("utf-8")
    lines = difflib.unified_diff(
        actual_text.splitlines(keepends=True),
        expected_text.splitlines(keepends=True),
        fromfile=str(output.relative_to(ROOT)),
        tofile=str(output.relative_to(ROOT)) + " (regenerated)",
    )
    return "".join(lines)


def write_atomic(output: Path, contents: bytes) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=output.parent, delete=False) as temporary:
        temporary.write(contents)
        temporary_path = Path(temporary.name)
    temporary_path.chmod(0o644)
    os.replace(temporary_path, output)


def run(check: bool, upstream: Path, component: str) -> int:
    verify_upstream(upstream)
    failures = 0
    for artifact in ARTIFACTS:
        if component != "all" and artifact.component != component:
            continue

        output = ROOT / artifact.output
        expected = render(artifact)
        if check:
            actual = output.read_bytes() if output.is_file() else None
            if actual != expected:
                failures += 1
                print(f"STALE {artifact.output}", file=sys.stderr)
                rendered_diff = diff(output, expected)
                if rendered_diff:
                    print(rendered_diff, file=sys.stderr, end="")
            else:
                print(f"OK {artifact.output}")
        else:
            if not output.is_file() or output.read_bytes() != expected:
                write_atomic(output, expected)
                print(f"WROTE {artifact.output}")
            else:
                print(f"UNCHANGED {artifact.output}")
            output.chmod(0o644)

    coverage_output = ROOT / COVERAGE_OUTPUT
    expected_coverage = render_coverage(upstream)
    if check:
        actual_coverage = coverage_output.read_bytes() if coverage_output.is_file() else None
        if actual_coverage != expected_coverage:
            failures += 1
            print(f"STALE {COVERAGE_OUTPUT}", file=sys.stderr)
            rendered_diff = diff(coverage_output, expected_coverage)
            if rendered_diff:
                print(rendered_diff, file=sys.stderr, end="")
        else:
            print(f"OK {COVERAGE_OUTPUT}")
    else:
        if not coverage_output.is_file() or coverage_output.read_bytes() != expected_coverage:
            write_atomic(coverage_output, expected_coverage)
            print(f"WROTE {COVERAGE_OUTPUT}")
        else:
            print(f"UNCHANGED {COVERAGE_OUTPUT}")
        coverage_output.chmod(0o644)

    if failures:
        print(
            "generated parser artifacts or grammar coverage are stale; run "
            "python3 tools/parser-gen/generate.py --upstream <jq-checkout>",
            file=sys.stderr,
        )
        return 1
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="compare checked-in artifacts without modifying them",
    )
    parser.add_argument(
        "--component",
        choices=("all", "lexer", "parser"),
        default="all",
        help="generate/check all artifacts or one non-overlapping component",
    )
    parser.add_argument(
        "--upstream",
        help="path to the pinned jq 1.8.2 checkout",
    )
    arguments = parser.parse_args()

    try:
        upstream = resolve_upstream(arguments.upstream)
        return run(arguments.check, upstream, arguments.component)
    except (OSError, RuntimeError, UnicodeError) as error:
        print(f"parser generation failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
