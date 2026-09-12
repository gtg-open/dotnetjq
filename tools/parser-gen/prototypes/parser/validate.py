#!/usr/bin/env python3
"""Generate, type-check, and behaviorally exercise the maintained managed parser."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
GRAMMAR = ROOT / "src/DotNetJq/Grammar/parser.y"
LEXER = ROOT / "src/DotNetJq/Grammar/lexer.l"
GENERATED_PARSER = (
    ROOT / "src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs"
)
PARSER_GEN_PROJECT = (
    ROOT / "tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj"
)
DOTNETJQ_PROJECT = ROOT / "src/DotNetJq/DotNetJq.csproj"
TARGETS = HERE / "compile-prototype.targets"
PINNED_TOOL_VERSION = "1.2.5.0"
PINNED_JQ_REVISION = "34f7186b86743a083a589741b6cea95293524108"


def command(
    arguments: list[str],
    *,
    cwd: Path = ROOT,
    capture: bool = False,
    environment: dict[str, str] | None = None,
) -> str:
    print("+ " + " ".join(arguments))
    result = subprocess.run(
        arguments,
        cwd=cwd,
        check=False,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.PIPE if capture else None,
        env=environment,
    )
    if result.returncode != 0:
        if capture:
            sys.stderr.write(result.stdout)
            sys.stderr.write(result.stderr)
        raise RuntimeError(
            f"command failed with exit code {result.returncode}: {arguments[0]}"
        )
    if capture:
        return result.stdout + result.stderr
    return ""


def locate_gppg() -> Path:
    default_directory = (
        Path.home()
        / ".nuget/packages/springcomp.gppg/1.2.5/tools/net8.0/any"
    )
    tool = Path(
        os.environ.get(
            "SPRINGCOMP_GPPG_DLL", default_directory / "dotnet-gppg.dll"
        )
    ).resolve()
    if not tool.is_file():
        raise RuntimeError(
            "Springcomp.GPPG 1.2.5 is not restored; run `dotnet tool restore` "
            "or set SPRINGCOMP_GPPG_DLL"
        )

    version = command(["dotnet", str(tool), "/version"], capture=True)
    if PINNED_TOOL_VERSION not in version:
        raise RuntimeError(
            f"expected GPPG {PINNED_TOOL_VERSION}, received: {version.strip()}"
        )
    return tool


def structural_guardrails(tool: Path, audit_upstream: bool) -> None:
    base = [
        "dotnet",
        "run",
        "--project",
        str(PARSER_GEN_PROJECT),
        "-c",
        "Release",
        "--no-restore",
        "--",
    ]
    command(
        base
        + [
            "validate",
            "--parser",
            str(GRAMMAR),
            "--gppg",
            str(tool),
        ]
    )
    command(
        base
        + [
            "check-generated-parser",
            "--parser",
            str(GRAMMAR),
            "--output",
            str(GENERATED_PARSER),
        ]
    )
    if not audit_upstream:
        return

    upstream = ROOT / "upstream/jq/src/parser.y"
    upstream_lexer = ROOT / "upstream/jq/src/lexer.l"
    if not upstream.is_file() or not upstream_lexer.is_file():
        raise RuntimeError(
            "--audit-upstream requested, but the pinned parser.y/lexer.l inputs are absent"
        )
    command(
        base
        + [
            "compare",
            "--upstream-parser",
            str(upstream),
            "--managed-parser",
            str(GRAMMAR),
            "--upstream-lexer",
            str(upstream_lexer),
            "--managed-lexer",
            str(LEXER),
        ]
    )


def generate(tool: Path, directory: Path) -> Path:
    local_grammar = directory / "parser.y"
    output = directory / "JqGeneratedParser.g.cs"
    shutil.copyfile(GRAMMAR, local_grammar)
    result = subprocess.run(
        [
            "dotnet",
            str(tool),
            "/no-info",
            "/no-lines",
            "/noThrowOnError",
            "/out:" + output.name,
            local_grammar.name,
        ],
        cwd=directory,
        check=False,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if (
        result.returncode != 0
        or result.stderr
        or not output.is_file()
        or output.stat().st_size == 0
    ):
        raise RuntimeError(
            "GPPG generation failed or emitted diagnostics\n"
            + result.stdout
            + result.stderr
        )

    generated = output.read_text(encoding="utf-8-sig")
    expectations = {
        r"Rule\[169\]": "169 generated rules including GPPG's synthetic rule",
        r"State\[312\]": "312 generated states",
    }
    for pattern, description in expectations.items():
        if re.search(pattern, generated) is None:
            raise RuntimeError(f"generated parser does not contain {description}")
    if len(re.findall(r"(?m)^\s*case \d+:", generated)) != 167:
        raise RuntimeError("generated parser does not contain 167 action cases")
    return output


def build_native_oracle(directory: Path) -> Path:
    upstream = (ROOT / "upstream/jq").resolve()
    revision = command(
        ["git", "-C", str(upstream), "rev-parse", "HEAD"], capture=True
    ).strip()
    if revision != PINNED_JQ_REVISION:
        raise RuntimeError(
            f"native oracle checkout is {revision}; expected {PINNED_JQ_REVISION}"
        )

    generated_include_directory = directory / "native-generated/src"
    generated_include_directory.mkdir(parents=True)
    (generated_include_directory / "version.h").write_text(
        '#define JQ_VERSION "jq-1.8.2"\n', encoding="utf-8"
    )
    (generated_include_directory / "config_opts.inc").write_text(
        '#define JQ_CONFIG "(parser prototype native oracle)"\n',
        encoding="utf-8",
    )
    builtin = (upstream / "src/builtin.jq").read_bytes()
    octets = [f"0{value:o}," for value in builtin]
    (generated_include_directory / "builtin.inc").write_text(
        "\n".join(
            " ".join(octets[index : index + 16])
            for index in range(0, len(octets), 16)
        )
        + "\n",
        encoding="ascii",
    )

    sources = [
        "src/main.c",
        "src/builtin.c",
        "src/bytecode.c",
        "src/compile.c",
        "src/execute.c",
        "src/jq_test.c",
        "src/jv.c",
        "src/jv_alloc.c",
        "src/jv_aux.c",
        "src/jv_dtoa.c",
        "src/jv_file.c",
        "src/jv_parse.c",
        "src/jv_print.c",
        "src/jv_unicode.c",
        "src/linker.c",
        "src/locfile.c",
        "src/util.c",
        "src/jv_dtoa_tsd.c",
        "src/parser.c",
        "src/lexer.c",
        "vendor/decNumber/decContext.c",
        "vendor/decNumber/decNumber.c",
    ]
    executable = directory / "jq-1.8.2-parser-oracle"
    command(
        [
            "gcc",
            "-std=gnu11",
            "-O0",
            "-D_GNU_SOURCE",
            "-DIEEE_8087",
            "-I" + str(directory / "native-generated"),
            "-I" + str(upstream),
            "-I" + str(upstream / "src"),
            "-I" + str(upstream / "vendor"),
            "-o",
            str(executable),
            *(str(upstream / source) for source in sources),
            "-lm",
        ]
    )
    if not executable.is_file():
        raise RuntimeError("native jq oracle build did not produce an executable")
    return executable


def build(*, smoke: bool, native_oracle: Path | None = None) -> None:
    configuration = "ParserManagedSmoke" if smoke else "ParserManagedTypeCheck"
    arguments = [
        "dotnet",
        "build",
        str(DOTNETJQ_PROJECT),
        "--no-restore",
        "-c",
        configuration,
        "-p:CustomAfterMicrosoftCommonTargets=" + str(TARGETS),
    ]
    if smoke:
        arguments.extend(
            [
                "-p:OutputType=Exe",
                "-p:StartupObject=DotNetJq.Port.GeneratedParser.JqGeneratedParserSmoke",
                "-p:RunJqGeneratedParserSmoke=true",
            ]
        )
    output = command(arguments, capture=True)
    print(output, end="")
    managed_parser_warning_lines = [
        line
        for line in output.splitlines()
        if " warning " in line.lower()
        and (
            str(HERE) in line
            or str(GENERATED_PARSER.parent) in line
            or "Compatibility/ParserGenerator/GppgRuntime" in line
        )
    ]
    if managed_parser_warning_lines:
        raise RuntimeError(
            "managed generated parser produced a compiler/analyzer warning:\n"
            + "\n".join(managed_parser_warning_lines)
        )

    if not smoke:
        return
    assembly = (
        ROOT
        / "src/DotNetJq/bin"
        / configuration
        / "net10.0/DotNetJq.dll"
    )
    copied_runtime = assembly.parent / "Springcomp.GPPG.Runtime.dll"
    if not assembly.is_file():
        raise RuntimeError("managed parser smoke executable is absent")
    if copied_runtime.exists():
        raise RuntimeError("source-integrated parser unexpectedly copied a GPPG runtime DLL")
    environment = os.environ.copy()
    if native_oracle is not None:
        environment["JQ_PARSER_NATIVE_ORACLE"] = str(native_oracle)
    command(["dotnet", str(assembly)], environment=environment)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--audit-upstream",
        action="store_true",
        help="also compare with an available pinned upstream checkout",
    )
    arguments = parser.parse_args()

    try:
        tool = locate_gppg()
        structural_guardrails(tool, arguments.audit_upstream)
        with tempfile.TemporaryDirectory(prefix="jq-gppg-parser-") as temporary:
            temporary_directory = Path(temporary)
            # This independent generation proves GPPG's current table shape.
            # The production-output pipeline performs the separate normalized
            # byte comparison before this harness type-checks the committed file.
            generate(tool, temporary_directory)
            native_oracle = (
                build_native_oracle(temporary_directory)
                if arguments.audit_upstream
                else None
            )
            build(smoke=False)
            build(
                smoke=True,
                native_oracle=native_oracle,
            )
    except (OSError, RuntimeError) as exception:
        print("managed parser validation failed: " + str(exception), file=sys.stderr)
        return 1

    print(
        "managed parser validation passed: exact 167-alternative structure; "
        "169 rules; 312 states; nullable/analyzers enabled; generated actions "
        "type-checked; runnable and invalid-input differential smoke matched" +
        (" pinned native jq 1.8.2" if arguments.audit_upstream else "")
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
