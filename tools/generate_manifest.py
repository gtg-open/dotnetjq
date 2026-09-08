#!/usr/bin/env python3
"""Generate the source-traceability manifest from the pinned jq checkout."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate the source-traceability manifest from the pinned jq checkout."
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify that PORTING_MANIFEST.json is current without modifying it",
    )
    parser.add_argument(
        "--release",
        action="store_true",
        help=(
            "enforce release closure: every non-OMITTED file, dependency, auxiliary "
            "implementation, and test-category mapping must be done with scoped parity"
        ),
    )
    return parser.parse_args()


ARGS = parse_args()

ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = Path(os.environ.get("DOTNETJQ_UPSTREAM") or ROOT / "upstream" / "jq").resolve()
OUTPUT = ROOT / "porting" / "PORTING_MANIFEST.json"

COMPLETION_POLICY = {
    "statusDoneMeaning": (
        "The declared PORT, PROXY, GENERATED, REUSE, or OMITTED disposition is complete "
        "for its explicit semanticParityScope; it is not an unscoped claim of C ABI, "
        "implementation-architecture, or generator-state identity"
    ),
    "semanticParityTrueMeaning": (
        "Pinned jq 1.8.2 behavior is matched inside the explicit semanticParityScope by "
        "the named evidence; semanticParityExclusions remain outside that claim"
    ),
    "semanticParityFalseMeaning": (
        "The mapping/classification may be complete, but no parity is asserted for the "
        "intentionally omitted or still-open semantic surface"
    ),
}

PROXIES = {
    "src/jv_alloc.c": "Managed allocation and garbage collection",
    "src/jv_alloc.h": "Managed allocation and garbage collection",
    "src/jv_dtoa.c": "Managed numeric formatting plus jq compatibility logic",
    "src/jv_dtoa.h": "Managed numeric formatting plus jq compatibility logic",
    "src/jv_dtoa_tsd.c": ".NET runtime thread-local state",
    "src/jv_dtoa_tsd.h": ".NET runtime thread-local state",
    "src/jv_file.c": "System.IO behind a controlled compatibility boundary",
    "src/jv_thread.h": ".NET runtime primitives",
    "src/jv_unicode.c": ".NET Rune APIs plus jq compatibility logic",
    "src/jv_unicode.h": ".NET Rune APIs plus jq compatibility logic",
    "src/jv_utf8_tables.h": ".NET Rune and managed UTF-8 traversal logic in the shared jv_unicode proxy",
    "src/libm.h": "System.Math plus managed Sun fdlibm kernels and a replaceable gamma-only compatibility ABI",
    "vendor/decNumber/decContext.c": "Managed numeric representation plus jq compatibility logic",
    "vendor/decNumber/decContext.h": "Managed numeric representation plus jq compatibility logic",
    "vendor/decNumber/decNumber.c": "Managed numeric representation plus jq compatibility logic",
    "vendor/decNumber/decNumber.h": "Managed numeric representation plus jq compatibility logic",
    "vendor/decNumber/decNumberLocal.h": "Managed numeric representation plus jq compatibility logic",
}

GENERATED = {
    "src/lexer.c": {
        "target": "src/DotNetJq/Generated/Lexer/lexer.c.cs",
        "sourceOfTruth": "src/lexer.l",
        "managedSource": "src/DotNetJq/Grammar/lexer.l",
        "generator": "tools/parser-gen/generate-gplex-lexer.sh",
        "generatorPackage": "springcomp.gplex/1.2.5 (dotnet-gplex)",
    },
    "src/lexer.h": {
        "target": "src/DotNetJq/Generated/Lexer/lexer.h.cs",
        "sourceOfTruth": "src/lexer.l",
        "generator": "tools/parser-gen/generate.py",
        "template": "tools/parser-gen/templates/lexer.h.cs.in",
    },
    "src/parser.c": {
        "target": "src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs",
        "sourceOfTruth": "src/parser.y",
        "managedSource": "src/DotNetJq/Grammar/parser.y",
        "generator": "tools/parser-gen/DotNetJq.ParserGen",
        "generatorPackage": "springcomp.gppg/1.2.5 (dotnet-gppg)",
    },
    "src/parser.h": {
        "target": "src/DotNetJq/Generated/Parser/parser.h.cs",
        "sourceOfTruth": "src/parser.y",
        "generator": "tools/parser-gen/generate.py",
        "template": "tools/parser-gen/templates/parser.h.cs.in",
    },
    "src/lexer.l": {
        "target": "src/DotNetJq/Grammar/lexer.l",
        "sourceOfTruth": "src/lexer.l",
        "generator": "tools/parser-gen/generate-gplex-lexer.sh",
        "role": "Committed managed GPLEX grammar and C# actions structurally pinned to jq's lexer.l",
        "generatedOutputs": [
            "src/DotNetJq/Generated/Lexer/lexer.c.cs",
        ],
    },
    "src/parser.y": {
        "target": "src/DotNetJq/Grammar/parser.y",
        "sourceOfTruth": "src/parser.y",
        "generator": "tools/parser-gen/DotNetJq.ParserGen",
        "role": "Committed managed GPPG grammar with close C-to-C# semantic-action translations",
        "generatedOutputs": [
            "src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs",
            "src/DotNetJq/Generated/Parser/parser.h.cs",
        ],
    },
}

TARGET_OVERRIDES = {
    "src/main.c": "src/DotNetJq.Cli/",
    "src/jv_utf8_tables.h": "src/DotNetJq/Port/src/jv_unicode.c.cs",
}

OMITTED = {
    "src/inject_errors.c": "Upstream build/developer fault-injection utility",
}

REUSED = {
    "src/builtin.jq": {
        "target": "src/DotNetJq/Resources/builtin.jq",
        "reason": "Byte-for-byte jq-language source embedded, parsed as a library, and bound into every compiled program",
        "runtimeUsage": {
            "embeddedResource": True,
            "loadedByEngine": True,
            "note": "The resource is parsed once from the assembly and all 106 ordered definitions are bound for each compiled program; private native primitives remain managed compatibility callbacks",
        },
        "semanticParityScope": "resource bytes and runtime library binding",
    },
}

TEST_REUSE = {
    "tests/jq.test": "Unchanged official core semantic fixture used by the managed compatibility runner",
    "tests/man.test": "Unchanged official manual-derived fixture used by the managed compatibility runner",
    "tests/onig.test": "Unchanged official Oniguruma behavior fixture used to quantify regex compatibility",
    "tests/manonig.test": "Unchanged official manual-derived Oniguruma fixture used to quantify regex compatibility",
    "tests/base64.test": "Unchanged official base64 fixture used by the managed compatibility runner",
    "tests/uri.test": "Unchanged official URI fixture used by the managed compatibility runner",
    "tests/optional.test": "Unchanged official optional-feature fixture used by the managed compatibility runner",
}

TEST_DRIVER_SHA256 = {
    "mantest": "4b74f99e88105e606fb52c54c0906c646c1d41537205be9a49df935bfe17551b",
    "jqtest": "69d39819afabf813321b8f74b46806d49a80d3893ea2194767daaa51af114028",
    "shtest": "a991a539e32640df0a59c4f7273cffcd0be14f44d4d85ac683a37aafb9f0cbae",
    "utf8test": "c2ac29c59e6f3e4461b8c2b2ceac8c9438f1c32a34afba27a2d0808624fc6991",
    "base64test": "1beddd88bef60e127619fe7529faab24d8901502455921903b44406ae1e6e7d4",
    "uritest": "46bccaf65b88c31e2935b6ecd05d382943fa885c5632add13af14646eba168be",
    "optionaltest": "53d32337a91fd589aa3adb16e1ddf4a6a46338396f4e36dec40517ce15efa7a7",
    "onigtest": "f7ec99be459cc148d08f8bb1fdd52fefc1d8d80b4c3b95e404e9902a72556a7d",
    "manonigtest": "51aa04effca68fda52fb5ba4343204cd8ad3c191ade841143431a83754a02dc7",
}

MODULE_FIXTURE_GIT_TREE = "457673594ed5c117efba91acbd87931fb9bf309e"
MODULE_FIXTURE_FILES = {
    "tests/modules/a.jq": "7b7313916208fa1da942666fed434b86764e793e2f0bdc958af434bb25c2d9c7",
    "tests/modules/b/b.jq": "ab098816baea1d1c80a461f0e1531badefde8ec3c61b9df9186a4860efee2fcd",
    "tests/modules/c/c.jq": "cd046303810732db16a92a41f34d00ff1db7df62a2015fdd4651731e7cb5ba8e",
    "tests/modules/c/d.jq": "f1f1e4e99cb7e90e3ede5c213fdc10240ca382c02fc146fc0298b4b4cb91e54c",
    "tests/modules/cycle_a.jq": "163235bbfa8db8e6189207d338976e9c938f0ca463350e03685183a85386d28b",
    "tests/modules/cycle_b.jq": "d250d077621822e8816230b8a8a4e4b0c574cbb8c8619ce22fd4bcde38df3e3b",
    "tests/modules/cycle_self.jq": "b1f15cc427e16e90538ed34705e4389ca66cb57fb7f34203a17eff8f7b025577",
    "tests/modules/data.json": "2792ffbd2efa54ed2c0d1fc2d49a15c2c78d66fc327de2c62e5d67517600b512",
    "tests/modules/home1/.jq": "9af4419dd48334fece4fffd6e609fa959050dbcb4d0579e6df9d4b433d2dd435",
    "tests/modules/home2/.jq/g.jq": "290ac9ccd65fbc3207f858afcb698b4c03214b6fa53584613f731257aeaed3cd",
    "tests/modules/lib/jq/e/e.jq": "5a8139806897104ba640598676d1ceca16751c20cb2b470806f6ab58a588724a",
    "tests/modules/lib/jq/f.jq": "5b17ccce65d6949daca26d51b078106954549830ee346a7d4473ea369218f0e6",
    "tests/modules/shadow1.jq": "f81cd9dadecb4261b551897b5fe1928fb8801bd2e9edac87253bc3f96a8cfc1f",
    "tests/modules/shadow2.jq": "9ca1a1064b6f2905b8e19982ce5c7d9b8faf550421bc404567fb1ea97021e06e",
    "tests/modules/syntaxerror/syntaxerror.jq": "97a34c7388efcdc06458d4170e68f0d5466c5427be5f193f3dbf78ee8a30fc88",
    "tests/modules/test_bind_order.jq": "27e7416b8f1f50b56b40e062c3dadd143dda941ee3c5da87b2225a9c5bf6f0d1",
    "tests/modules/test_bind_order0.jq": "48bbb068b4be846c573367ff0e6eb877d14bff744888942dd0466ce193b9c495",
    "tests/modules/test_bind_order1.jq": "9080d347b1e59e17080169d424468c4e86311b3218c9fbad9a16b848797f8aa5",
    "tests/modules/test_bind_order2.jq": "6ba3c7b667796643474ac6f4ec2cb2cd4af6221c3152ea03df9497319cc4153f",
}

TORTURE_FIXTURE_GIT_TREE = "b7ee9eb5804f1cade2d0321af1566d807c165791"
TORTURE_FIXTURE_FILES = {
    "tests/torture/input0.json": "6cde61ff437ed25e744ee6dbe9074c3fc083ae695362f88f6588db51888190aa",
}

CLI_EXCLUSIONS = [
    {
        "id": "native-shell-and-memory-tooling",
        "upstreamSources": ["src/main.c", "tests/shtest", "tests/torture/input0.json"],
        "reason": "Native FILE*/LD_PRELOAD fault injection, Valgrind accounting, allocator failures, signals, and exact native VM program-counter/stack/refcount trace identity are implementation-specific mechanics; managed run-tests, disassembly, and debug traces plus the unchanged shell drivers remain in scope",
    },
]

AUXILIARY_IMPLEMENTATIONS = {
    "src/DotNetJq.Cli/ManagedTestRunner.cs": {
        "upstreamSources": ["src/jq_test.c"],
        "strategy": "PORT",
        "reason": "Managed implementation of jq's --run-tests fixture grammar and lifecycle/value regression harness behind the dotnetjq CLI",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Observable jq 1.8.2 --run-tests fixture parsing, --skip/--take selection, module/startup loading, diagnostics, counters, lifecycle checks, verbose mode, and exit statuses through the managed compiler/direct VM",
        "semanticParityExclusions": [
            "Native jv internal representation assertions, pthread implementation identity, allocator instrumentation, and C FILE* errno wording beyond the tested portable diagnostic contract",
            "Native memory addresses and refcount-address trace annotations remain implementation-specific",
        ],
        "evidence": [
            "ManagedTestRunner.cs carries the full jq 1.8.2 src/jq_test.c provenance header and streams the pinned fixture grammar through jq-shaped managed state APIs",
            "DeveloperModeCompatibilityTests passes 22/22 cases across 18 methods (17 facts and one five-row theory), covering fixture/lifecycle oracle comparison, selection/status/file/option edge cases, all seven official --run-tests fixtures (879/879), trace shape, detailed trace, and verbose integration",
            "The complete DotNetJq.Cli.Tests process suite passes 84/84 with no skipped tests",
            "IndexSliceContinuationOracleTests verifies managed trace event order for continuation-sensitive index and slice execution",
        ],
    },
    "src/DotNetJq/Port/src/libm.glibc-compat.cs": {
        "upstreamSources": ["src/libm.h"],
        "strategy": "PROXY",
        "reason": "Thin ABI boundary to the replaceable gamma-only DotNetJq.GlibcCompat assembly",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Thin managed ABI forwarding of jq's gamma, lgamma, lgamma_r, and tgamma calls to the separately replaceable compatibility assembly",
        "semanticParityExclusions": ["Native dynamic-linker, symbol-versioning, errno, and floating-point-environment ABI"],
        "evidence": ["GammaCompatExactOracleCorpusTests: 1,366/1,366 exact value/sign-bit records", "PACKAGE_ISOLATION_REPORT.md proves offline DLL-only component replacement while the main assembly and consumer remain unchanged"],
    },
    "src/DotNetJq/Compatibility/FileSystem/JqFileSystem.cs": {
        "upstreamSources": ["src/jv_file.c", "src/linker.c", "src/util.c"],
        "strategy": "PROXY",
        "reason": "Host filesystem access is isolated behind the explicit IJqFileSystem capability",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Explicit-capability file result, path, content, error-category, confinement, and size-limit contract consumed by jq file/module operations",
        "semanticParityExclusions": ["Ambient OS filesystem authority, the documented System.IO check/open TOCTOU window, and exact platform-specific error suffixes"],
        "evidence": ["ModuleFileCompatibilityTests covers immutable resolver/content snapshots plus path and explicit size policy; ModuleResourceLimitCompatibilityTests covers graph budgets", "Package isolation executes with the repository hidden and no network"],
    },
    "src/DotNetJq/Compatibility/Json/ManagedDecimal.cs": {
        "upstreamSources": [
            "src/jv.c",
            "src/jv_dtoa.c",
            "vendor/decNumber/decContext.c",
            "vendor/decNumber/decNumber.c",
        ],
        "strategy": "PROXY",
        "reason": "Managed arbitrary-precision literal-number representation shared by decNumber and jv proxies",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "jq-consumed exact decimal-literal parse, exponent, sign, comparison, native-double conversion, negation/absolute, and serialization behavior",
        "semanticParityExclusions": ["IBM decNumber C API/layout/status words and decimal arithmetic not consumed by jq", "Ordinary jq arithmetic remains binary64"],
        "evidence": ["NumericCompatibilityTests and exact jq-visible numeric corpora", "All jq.test literal-number/extreme-exponent cases pass"],
    },
    "src/DotNetJq/Compatibility/Regex/OnigurumaExtendedGraphemeData.cs": {
        "upstreamSources": ["vendor/oniguruma/src/unicode_egcb_data.c"],
        "strategy": "GENERATED",
        "reason": "Deterministically extracted Oniguruma Unicode 16.0 extended-grapheme-cluster ranges used by the managed regex segmentation adapter",
        "sourceOfTruth": "vendor/oniguruma/src/unicode_egcb_data.c",
        "sourceSha256": "21663445ace4f64775506f3fc53332a96e1b2b9f509b63eeb5462913daeb6d73",
        "rangeCount": 1376,
        "generator": "tools/DotNetJq.DifferentialProbe/verify-oniguruma-egcb-data.pl",
        "regenerationCommand": "perl tools/DotNetJq.DifferentialProbe/verify-oniguruma-egcb-data.pl --check upstream/jq/vendor/oniguruma/src/unicode_egcb_data.c src/DotNetJq/Compatibility/Regex/OnigurumaExtendedGraphemeData.cs",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Exact pinned Oniguruma Unicode 16.0 extended-grapheme range extraction, encoded managed payload, and lookup data consumed by jq regex segmentation",
        "semanticParityExclusions": ["Oniguruma native table layout and internal property API identity", "Unicode grapheme-property versions other than pinned Oniguruma Unicode 16.0"],
        "evidence": ["Pinned unicode_egcb_data.c SHA-256 is 21663445ace4f64775506f3fc53332a96e1b2b9f509b63eeb5462913daeb6d73 and contains 1,376 ranges", "verify-oniguruma-egcb-data.pl independently regenerates and byte-compares the payload: Unicode 16.0, 1,792 encoded bytes, LV 399, LVT 399", "RegexTextSegmentationExactnessTests and RegexCompatibilityRound3Tests pass inside the 1,029/1,029 final Regex/Oniguruma slice; the 100,002-character case completes under 500 ms", "Pinned property-checker partitions: 612/15/629/886/627; onig.test 47/47, manonig.test 19/19, complete declared regex inventory 4,980/4,980"],
    },
    "src/DotNetJq/Compatibility/Regex/OnigurumaSimpleCaseFoldData.cs": {
        "upstreamSources": ["vendor/oniguruma/src/unicode_fold_data.c"],
        "strategy": "GENERATED",
        "reason": "Deterministically extracted Oniguruma Unicode 16.0 simple-case-fold groups used by the managed callout atom matcher and regex adapter",
        "sourceOfTruth": "vendor/oniguruma/src/unicode_fold_data.c",
        "sourceSha256": "690b14e8f84ff1345ec38657ab41a0c630dea37b99fb39519d9789ddf2558a55",
        "recordCounts": {"groups": 1423, "members": 1453},
        "generator": "tools/DotNetJq.DifferentialProbe/generate-oniguruma-simple-case-fold-data.pl",
        "regenerationCommand": "perl tools/DotNetJq.DifferentialProbe/generate-oniguruma-simple-case-fold-data.pl --check upstream/jq/vendor/oniguruma/src/unicode_fold_data.c src/DotNetJq/Compatibility/Regex/OnigurumaSimpleCaseFoldData.cs",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Exact pinned Oniguruma Unicode 16.0 simple-case-fold group/member extraction and generated managed lookup payload",
        "semanticParityExclusions": ["Oniguruma native table layout and internal case-fold API identity", "Full multi-code-point fold execution outside this simple-fold data artifact"],
        "evidence": ["Pinned unicode_fold_data.c SHA-256 is 690b14e8f84ff1345ec38657ab41a0c630dea37b99fb39519d9789ddf2558a55", "Generator --check reproduces Unicode casefold version 160000, 1,423 groups, and 1,453 non-canonical members exactly", "OnigurumaCaseFold helper tests 25/25 and combined fold tests 27/27; build 0 warnings/errors"],
    },
    "src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyCatalog.cs": {
        "upstreamSources": ["vendor/oniguruma/src/unicode_property_data.c"],
        "strategy": "GENERATED",
        "reason": "Deterministically encoded complete Oniguruma Unicode 16.0 property range catalog",
        "sourceOfTruth": "vendor/oniguruma/src/unicode_property_data.c",
        "sourceSha256": "6f69e9852bf746acfc3847c9789225d4ba1f085f1852ca00fcab7fa912982f52",
        "recordCounts": {"encodedUnicodeTables": 612},
        "generator": "tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl",
        "regenerationCommand": "perl tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl upstream/jq/vendor/oniguruma/src/unicode_property_data.c upstream/jq/vendor/oniguruma/src/unicode_property_data_posix.c src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyCatalog.cs src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyAliases.cs src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyData.cs",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Exact encoded endpoints for all 612 physical Unicode property tables in pinned Oniguruma Unicode 16.0",
        "semanticParityExclusions": ["Oniguruma native C table layout, gperf implementation, and property API ABI", "Unicode property versions other than pinned Oniguruma Unicode 16.0"],
        "evidence": ["Pinned unicode_property_data.c SHA-256 6f69e9852bf746acfc3847c9789225d4ba1f085f1852ca00fcab7fa912982f52", "verify-regex-unicode-property-data.pl decodes and endpoint-compares all 612 managed tables to the pinned C arrays", "RegexUnicodePropertyCorpus and focused regex tests cover property lookup and jq-visible matching"],
    },
    "src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyAliases.cs": {
        "upstreamSources": ["vendor/oniguruma/src/unicode_property_data.c", "vendor/oniguruma/src/unicode_property_data_posix.c"],
        "strategy": "GENERATED",
        "reason": "Deterministically encoded Oniguruma POSIX property ranges and normalized Unicode property aliases",
        "sourceOfTruth": ["vendor/oniguruma/src/unicode_property_data.c", "vendor/oniguruma/src/unicode_property_data_posix.c"],
        "sourceSha256": {"vendor/oniguruma/src/unicode_property_data.c": "6f69e9852bf746acfc3847c9789225d4ba1f085f1852ca00fcab7fa912982f52", "vendor/oniguruma/src/unicode_property_data_posix.c": "27bf4f37819b67b43d2a223a0a251978335d1c44a0ec02b4f907fba32b002058"},
        "recordCounts": {"encodedPosixTables": 15, "propertyAliases": 886},
        "generator": "tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl",
        "regenerationCommand": "perl tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl upstream/jq/vendor/oniguruma/src/unicode_property_data.c upstream/jq/vendor/oniguruma/src/unicode_property_data_posix.c src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyCatalog.cs src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyAliases.cs src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyData.cs",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Exact pinned Oniguruma Unicode 16.0 POSIX range tables, CodeRanges routing, and normalized property-name alias mapping",
        "semanticParityExclusions": ["Oniguruma native gperf/hash-table layout and lookup ABI", "Unicode property aliases outside the pinned Unicode 16.0 data"],
        "evidence": ["Pinned source SHA-256 values 6f69e9852bf746acfc3847c9789225d4ba1f085f1852ca00fcab7fa912982f52 and 27bf4f37819b67b43d2a223a0a251978335d1c44a0ec02b4f907fba32b002058", "Independent checker proves 15 POSIX tables, 629 CodeRanges entries, 886 gperf aliases, and 627 decoded range tables exactly", "RegexCompatibilityRound3Tests and expanded regex corpus cover jq-visible POSIX/property behavior"],
    },
    "src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyData.cs": {
        "upstreamSources": ["vendor/oniguruma/src/unicode_property_data.c"],
        "strategy": "GENERATED",
        "reason": "Deterministically extracted focused emoji and extended-pictographic ranges used by case-fold and grapheme behavior",
        "sourceOfTruth": "vendor/oniguruma/src/unicode_property_data.c",
        "sourceSha256": "6f69e9852bf746acfc3847c9789225d4ba1f085f1852ca00fcab7fa912982f52",
        "recordCounts": {"focusedTables": 6, "focusedRanges": 359},
        "generator": "tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl",
        "regenerationCommand": "perl tools/DotNetJq.DifferentialProbe/verify-regex-unicode-property-data.pl upstream/jq/vendor/oniguruma/src/unicode_property_data.c upstream/jq/vendor/oniguruma/src/unicode_property_data_posix.c src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyCatalog.cs src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyAliases.cs src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyData.cs",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "Exact pinned endpoints for the six focused Emoji, Emoji_Component, Emoji_Modifier, Emoji_Modifier_Base, Emoji_Presentation, and Extended_Pictographic tables",
        "semanticParityExclusions": ["The remaining property catalog is owned by the separate generated catalog auxiliary", "Native Oniguruma C array layout and Unicode versions other than pinned 16.0"],
        "evidence": ["Independent checker compares all six arrays and 359 ranges endpoint-for-endpoint with pinned unicode_property_data.c", "Pinned source SHA-256 6f69e9852bf746acfc3847c9789225d4ba1f085f1852ca00fcab7fa912982f52", "Grapheme, case-fold, property, and expanded regex tests exercise their managed consumers"],
    },
    "src/DotNetJq/Compatibility/Regex/JqRegex.cs": {
        "upstreamSources": ["src/builtin.c", "src/builtin.jq", "vendor/oniguruma"],
        "strategy": "PROXY",
        "reason": "System.Text.RegularExpressions compatibility boundary for jq's Oniguruma-backed operations",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "jq-visible match/test/capture/scan/splits/sub/gsub contract through the managed regex adapter",
        "semanticParityExclusions": ["Native Oniguruma API, bytecode, allocator, engine architecture, and C ABI identity"],
        "evidence": ["Regex and Oniguruma focused xUnit slice: 1,029/1,029; full managed library suite: 2,678/2,678, both with zero skips", "Unchanged onig.test 47/47 and manonig.test 19/19", "Complete declared regex-generator inventory: 4,980/4,980 distinct filter/input pairs across 33 categories with no allowlist; requesting 4,981 is rejected", "Independent frozen-artifact audit: 92,589/92,589 primary rows and 62/62 isolated processes, with zero mismatch, crash, abort, unhandled-exception, or timeout outcomes", "OnigurumaGrammarClosureCompatibilityTests covers pinned valid grammar, exact diagnostics, and source-shaped retry-boundary failure-pop counts"],
    },
    "src/DotNetJq/Compatibility/Time/JqStrptimeRegex.cs": {
        "upstreamSources": ["src/builtin.c", "glibc-2.39/time/strptime_l.c"],
        "strategy": "PROXY",
        "reason": "A managed AOT-safe scanner replaces libc strptime while preserving the raw struct-tm state consumed by jq",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "jq-visible Linux/glibc-2.39 C-locale strptime directives, modifiers, ranges, field sentinels, whitespace, partial tails, embedded-NUL boundary, epoch conversion, and failure diagnostics",
        "semanticParityExclusions": ["Non-C locale era and alternate-digit tables, collation bytes, and installed-locale inventory remain platform globalization data", "Native struct tm layout, libc locale-global mutation, and C parsing ABI", "Host-libc extensions or differences outside the pinned Linux/glibc-2.39 oracle"],
        "evidence": ["TimeBuiltinOracleMatrixTests: 125/125 cases covering every C-locale directive plus modifiers, flags, ranges, raw field sentinels, tails, NUL, failed normalization, errors, TZ, DST call order, and epoch limits; platform-independent expectations are frozen and four host-TZif-sensitive gap rows compare exactly with the same-host pinned jq oracle", "StrptimeRegexProxyTests: 17/17 focused scanner and header-isolation cases", "NativeAOT smoke publishes and executes nine cases, including winter/summer strptime/strftime/mktime/localtime/todate with omitted POSIX DST rules, stateful execution/outcomes, explicit I/O sinks/sources, and in-memory module/filesystem capabilities"],
    },
    "src/DotNetJq/Compatibility/Time/JqTimeZone.cs": {
        "upstreamSources": ["src/builtin.c", "glibc-2.39/time/tzfile.c", "glibc-2.39/time/tzset.c", "glibc-2.39/time/mktime.c", "glibc-2.39/time/mktime-internal.h", "glibc-2.39/time/strftime_l.c", "glibc-2.39/time/timegm.c"],
        "strategy": "PROXY",
        "reason": "A managed TZif/POSIX-TZ evaluator exposes the offsets, DST normalization, and abbreviations jq obtains from libc without private CoreLib reflection or process-global TZ mutation",
        "status": "done",
        "semanticParity": True,
        "semanticParityScope": "jq-visible Linux/glibc-2.39 behavior for ordinary IANA TZif v1-v4 data, POSIX TZ strings and installed posixrules history, partial identifiers, UTC, source-shaped stateful mktime gap/fold/explicit-isdst/raw-second selection, %s local interpretation, abbreviations, and epochs beyond DateTime range",
        "semanticParityExclusions": ["Leap-second-aware right/* TZif timelines", "Malformed POSIX M rules whose narrowed month lies outside 1..12 make glibc compute_change read outside __mon_yday (undefined C behavior); the managed evaluator remains bounds-safe", "Absolute TZ file paths and arbitrary host file reads", "Windows-only/system zones without TZif data use public TimeZoneInfo names and its year range", "Host TZif/tzdata build or version differences remain external platform data", "The explicit managed Environment capability deliberately isolates its TZ/mktime context instead of sharing libc's process-global state; default ambient execution shares process-static state"],
        "evidence": ["TimeBuiltinOracleMatrixTests: 125/125 cases, including 27 original POSIX grammar/default-rule-history rows, 22 original fold call-order rows, negative-DST/date-line gaps, explicit isdst and raw-second cache order, partial/%hu POSIX parsing, far-year C arithmetic, relative TZ path spelling, IANA/invalid-TZ, %z/%Z/%s, missing-locale fallback, and explicit per-state isolation; four host-TZif-sensitive gap rows compare exactly with the same-host pinned jq oracle", "EnvironmentCompatibilityTests freezes default ambient call-time environment reads, cross-program process-static mktime state, explicit isolation/restoration, and POSIX/Windows getenv casing", "Expanded direct CLI differential probes against statically linked jq-1.8.2 oracle SHA-256 b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f: original 54/54 matrix plus independent 11/11 mktime control-flow recheck", "NativeAOT smoke publishes and executes winter and summer through the omitted-rule POSIX-TZ path with no reflection or native time library"],
    },
}

ONIGURUMA_IMPLEMENTATION_TARGETS = [
    {
        "target": "src/DotNetJq/Compatibility/Regex/JqRegex.cs",
        "strategy": "PROXY",
        "upstreamSources": [
            "vendor/oniguruma/src/regparse.c",
            "vendor/oniguruma/src/regcomp.c",
            "vendor/oniguruma/src/regexec.c",
            "vendor/oniguruma/src/unicode.c",
        ],
        "role": "jq-shaped regex operation adapter, syntax translation, and bounded fallback routing",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaCalloutArgumentParser.cs",
        "strategy": "PORT",
        "upstreamSources": ["vendor/oniguruma/src/regparse.c"],
        "role": "Built-in callout argument and tag lexer/parser",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaCalloutAtomMatcher.cs",
        "strategy": "PROXY",
        "upstreamSources": [
            "vendor/oniguruma/src/regparse.c",
            "vendor/oniguruma/src/regexec.c",
            "vendor/oniguruma/src/unicode.c",
            "vendor/oniguruma/src/unicode_property_data.c",
            "vendor/oniguruma/src/unicode_egcb_data.c",
        ],
        "role": "Scalar-aware ordinary atom matching for the callout event runner",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaCalloutErrorData.cs",
        "strategy": "PORT",
        "upstreamSources": [
            "vendor/oniguruma/src/regerror.c",
            "vendor/oniguruma/src/oniguruma.h",
        ],
        "role": "Pinned Oniguruma error-code text used by the built-in ERROR callout",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaCalloutEventRunner.cs",
        "strategy": "PROXY",
        "upstreamSources": [
            "vendor/oniguruma/src/regparse.c",
            "vendor/oniguruma/src/regexec.c",
        ],
        "role": "Bounded backtracking callout, absent-range, lookbehind, and keep-state runner",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaCalloutOptimizationPlan.cs",
        "strategy": "PORT",
        "upstreamSources": [
            "vendor/oniguruma/src/regcomp.c",
            "vendor/oniguruma/src/regexec.c",
        ],
        "role": "UTF-8 byte-based exact/map optimizer candidate scheduling",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaCaseFold.cs",
        "strategy": "PROXY",
        "upstreamSources": [
            "vendor/oniguruma/src/regcomp.c",
            "vendor/oniguruma/src/unicode_fold_data.c",
        ],
        "role": "Managed scalar-aware simple/full case-fold compatibility helper",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaContentCallout.cs",
        "strategy": "PORT",
        "upstreamSources": ["vendor/oniguruma/src/regparse.c"],
        "role": "Content-callout delimiter, tag, direction, and diagnostic parser",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaExtendedGraphemeData.cs",
        "strategy": "GENERATED",
        "upstreamSources": ["vendor/oniguruma/src/unicode_egcb_data.c"],
        "role": "Exact generated Unicode 16.0 extended-grapheme range payload",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaRadixEscape.cs",
        "strategy": "PORT",
        "upstreamSources": ["vendor/oniguruma/src/regparse.c"],
        "role": "Perl-NG hexadecimal/octal brace-escape parser",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaSimpleCaseFoldData.cs",
        "strategy": "GENERATED",
        "upstreamSources": ["vendor/oniguruma/src/unicode_fold_data.c"],
        "role": "Exact generated Unicode 16.0 simple-case-fold payload",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaTextSegmentation.cs",
        "strategy": "PROXY",
        "upstreamSources": [
            "vendor/oniguruma/src/unicode.c",
            "vendor/oniguruma/src/unicode_egcb_data.c",
        ],
        "role": "Managed GB3-GB13 cluster and boundary behavior",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyAliases.cs",
        "strategy": "GENERATED",
        "upstreamSources": [
            "vendor/oniguruma/src/unicode_property_data.c",
            "vendor/oniguruma/src/unicode_property_data_posix.c",
        ],
        "role": "Exact generated POSIX range and normalized property-alias payload",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyCatalog.cs",
        "strategy": "GENERATED",
        "upstreamSources": ["vendor/oniguruma/src/unicode_property_data.c"],
        "role": "Exact generated complete Unicode property range catalog",
    },
    {
        "target": "src/DotNetJq/Compatibility/Regex/OnigurumaUnicodePropertyData.cs",
        "strategy": "GENERATED",
        "upstreamSources": ["vendor/oniguruma/src/unicode_property_data.c"],
        "role": "Exact generated focused emoji and extended-pictographic ranges",
    },
]

GLIBC_GAMMA_IMPLEMENTATION_TARGETS = [
    {
        "target": "src/DotNetJq.GlibcCompat/GlibcCompatMath.cs",
        "strategy": "PORT",
        "upstreamSources": [
            "sysdeps/ieee754/dbl-64/e_gamma_r.c",
            "sysdeps/ieee754/dbl-64/lgamma_neg.c",
            "sysdeps/ieee754/dbl-64/lgamma_product.c",
            "math/mul_split.h",
            "sysdeps/ieee754/dbl-64/e_exp.c",
            "sysdeps/ieee754/dbl-64/e_exp_data.c",
            "sysdeps/ieee754/dbl-64/s_expm1.c",
            "sysdeps/ieee754/dbl-64/e_lgamma_r.c",
        ],
        "role": "jq-visible gamma, lgamma, lgamma_r, and tgamma algorithms and special-case dispatch",
    },
    {
        "target": "src/DotNetJq.GlibcCompat/GlibcCompatMath.Pow.cs",
        "strategy": "PORT",
        "upstreamSources": [
            "sysdeps/ieee754/dbl-64/e_pow.c",
            "sysdeps/ieee754/dbl-64/e_pow_log_data.c",
            "sysdeps/ieee754/dbl-64/e_exp2.c",
            "sysdeps/ieee754/dbl-64/e_exp_data.c",
        ],
        "role": "Positive-finite pow and exp2 paths reached by gamma_positive",
    },
    {
        "target": "src/DotNetJq.GlibcCompat/GlibcCompatMath.X87.cs",
        "strategy": "PORT",
        "upstreamSources": [
            "sysdeps/ieee754/ldbl-96/gamma_product.c",
        ],
        "role": "Managed binary80 intermediate evaluation for the pinned x86-64 gamma_product path",
    },
]


DEPENDENCIES = {
    "glibc-gamma-compat": {
        "repository": "https://sourceware.org/git/glibc.git",
        "tag": "glibc-2.39",
        "tagObject": "9609a435f3f9a07c1cf607ad5821b12f735abd69",
        "commit": "ef321e23c20eebc6d6fb4044425c00e6df27b05f",
        "target": "src/DotNetJq.GlibcCompat/",
        "implementationTargets": GLIBC_GAMMA_IMPLEMENTATION_TARGETS,
        "strategy": "PORT",
        "license": "LGPL-2.1-or-later",
        "status": "done",
        "semanticParity": True,
      "semanticParityScope": "jq-visible gamma, lgamma, lgamma_r, and tgamma binary64 behavior",
      "semanticParityExclusions": [
        "Native GNU libm symbol-versioning, errno, floating-point-environment flags, and ABI identity"
      ],
        "evidence": [
            "GammaCompatExactOracleCorpusTests: 1,366/1,366 deterministic records, exact value/sign bits",
            "SpecialMathExactOracleCorpusTests and LibmCompatibilityTests",
            "Raw corpus SHA-256 994167f4b84bcd588b3eab27e4a94ddf26a2455316825dec9f964281a10ca3a2",
            "Both managed assemblies declare IsAotCompatible and IsTrimmable; DotNetJq.Library ships exact XML documentation for the four-method GlibcCompat ABI",
        ],
    },
    "vendor/oniguruma": {
        "repository": "https://github.com/kkos/oniguruma.git",
        "commit": "4ef89209a239c1aea328cf13c05a2807e5c146d1",
        "target": "src/DotNetJq/Compatibility/Regex/JqRegex.cs",
        "implementationTargets": ONIGURUMA_IMPLEMENTATION_TARGETS,
        "strategy": "PROXY",
        "replacement": "System.Text.RegularExpressions behind a jq-shaped compatibility adapter",
        "reason": "The managed library does not compile or link the pinned native Oniguruma submodule",
      "status": "done",
      "semanticParity": True,
      "semanticParityScope": "jq-visible Oniguruma-backed regex filter contract through the managed System.Text.RegularExpressions adapter",
      "semanticParityExclusions": [
        "Native Oniguruma API, bytecode, allocator, engine architecture, and C ABI identity"
      ],
      "evidence": [
        "Regex and Oniguruma focused xUnit slice: 1,029/1,029; full managed library suite: 2,678/2,678, both with zero skips",
        "Unchanged onig.test 47/47 and manonig.test 19/19",
        "Complete declared regex-generator inventory: 4,980/4,980 distinct filter/input pairs across 33 categories with no allowlist; requesting 4,981 is rejected",
        "Independent frozen-artifact audit: 92,589/92,589 primary rows and 62/62 isolated processes, with zero mismatch, crash, abort, unhandled-exception, or timeout outcomes",
        "OnigurumaGrammarClosureCompatibilityTests covers pinned valid grammar, exact diagnostics, and source-shaped retry-boundary failure-pop counts"
      ],
    },
}


def completion(
    semantic_parity_scope: str,
    evidence: list[str],
    semantic_parity_exclusions: list[str],
    *,
    semantic_parity: bool = True,
    status: str = "done",
) -> dict[str, object]:
    """Create the uniform completion record used by every inventoried mapping."""

    return {
        "status": status,
        "semanticParity": semantic_parity,
        "semanticParityScope": semantic_parity_scope,
        "semanticParityExclusions": semantic_parity_exclusions,
        "evidence": evidence,
    }


BROAD_LIBRARY_EVIDENCE = [
    "Final integrated managed unit suite: 2,678/2,678 with zero skipped tests",
    "All seven unchanged official fixtures pass: 879/879 (jq 550, man 231, onig 47, manonig 19, base64 10, uri 20, optional 2)",
    "Diagnostic-aware Differential Probe Report: 5,040/5,040 unique general filter/input pairs, seed 18082, exact failure payloads after only documented native process/compile-summary wrapper removal, zero allowlist",
    "DotNetJq.Library package isolation verification passes without the upstream checkout, a usable jq executable, native libraries, or network access",
]

DIRECT_VALUE_EVIDENCE = [
    "DIRECT_PORT_SYMBOL_AUDIT.md classifies the complete value/path/location/module/utility symbol ledger and every deliberate C-only omission",
    "JvDirectPortSymbolCompatibilityTests, JvAuxCompatibilityTests, DeepStructureCompatibilityRound3Tests, UnicodeCompatibilityTests, and PathUpdateCompatibilityTests; array-set boundary coverage proves jq's exact (INT_MAX >> 2) - array-offset decision without materializing a huge sparse array",
    *BROAD_LIBRARY_EVIDENCE,
]

JV_C_EVIDENCE = [
    "DIRECT_PORT_SYMBOL_AUDIT.md classifies the complete value/path/location/module/utility symbol ledger and every deliberate C-only omission",
    "JvArrayRefcountCowCompatibilityTests ports jq_test.c array copy/free/COW assertions, structural capacity/slice/deep-release cases, and jq's exact (INT_MAX >> 2) - array-offset set boundary without materializing a huge sparse array",
    "JvObjectRefcountCowCompatibilityTests ports jq_test.c object copy/mutation assertions and verifies deferred child copies, tombstones, rehash compaction, iteration ownership, and 20,000-level iterative release",
    "JvStringInvalidRefcountCowCompatibilityTests ports jq_test.c string ownership assertions and verifies UTF-8 byte storage, explicit string/invalid refcounts, cached hash invalidation, capacity growth, copy-on-write detachment, embedded NULs, malformed-unit repair, and 20,000-level iterative invalid release",
    "JvLiteralNumberRefcountCompatibilityTests verifies allocated decimal-literal copy/free counts, final invalidation, jq canonical text, and immediate binary64 behavior against the pinned native library",
    "ParserLiteralOwnershipCompatibilityTests, PathUpdateCompatibilityTests, and JqRuntimeExceptionOwnershipTests verify source-shaped string/error owner transfer across parser recovery, LOADK, object/path keys, jq catch/labels, and public/CLI boundaries",
    "JvDirectPortSymbolCompatibilityTests, JvAuxCompatibilityTests, DeepStructureCompatibilityRound3Tests, and UnicodeCompatibilityTests",
    *BROAD_LIBRARY_EVIDENCE,
]

JV_H_EVIDENCE = [
    "DIRECT_PORT_SYMBOL_AUDIT.md classifies the complete value/path/location/module/utility symbol ledger and every deliberate C-only omission",
    "JvArrayRefcountCowCompatibilityTests, JvObjectRefcountCowCompatibilityTests, JvStringInvalidRefcountCowCompatibilityTests, and JvLiteralNumberRefcountCompatibilityTests exercise allocation refcounts, copy-on-write boundaries, child-owner transfer, literal-number lifetime, physical capacity, UTF-8 string storage, and iterative destruction",
    "JvDirectPortSymbolCompatibilityTests, JvAuxCompatibilityTests, DeepStructureCompatibilityRound3Tests, and UnicodeCompatibilityTests",
    *BROAD_LIBRARY_EVIDENCE,
]

EXECUTOR_EVIDENCE = [
    "EXECUTE_VM_IMPLEMENTATION_MAP.md maps every executable opcode and direct stack/frame/fork/path/error copy/free boundary to pinned src/execute.c",
    "DirectBytecodeVmCompatibilityTests covers all executable opcode families, ON_BACKTRACK arms, frames, closures, cfunctions, paths, errors, result transfer, and reset cleanup",
    "BytecodeProductionPipelineTests verifies compiler-produced pinned instruction streams execute only through jq_start/jq_next and the direct VM",
    "ExecutionTransitionBudgetContractTests freezes 0/1/N transition-budget timing across forward dispatch, backtracking, tail calls, builtin streams, errors, cancellation, and sequential reuse",
    "EnvironmentFastPathCompatibilityTests verifies that programs without $ENV bypass replacement eligibility while $ENV programs retain fresh sequential and independent owners",
    "EnvironmentCompatibilityTests freezes native default $ENV compile timing, env call timing, process-static time state, explicit override restoration, and platform getenv casing",
    "Compiler/value refcount lifecycle and oracle suites verify LOADK pools, builtin arguments, calls, control flow, path/index/slice operations, full exhaustion, and early teardown",
    *BROAD_LIBRARY_EVIDENCE,
]

PARSER_EVIDENCE = [
    "tools/parser-gen/generate-gplex-lexer.sh --check byte-compares the pinned GPLEX scanner output against its committed managed source/tool key",
    "DotNetJq.ParserGen validates and byte-compares the conflict-free GPPG parser generated from committed Grammar/parser.y",
    "tools/parser-gen/generate.py --check byte-compares the remaining parser/lexer declaration templates and structural coverage against pinned grammar hashes",
    "PARSER_GRAMMAR_COVERAGE.json maps all 53 lexer rules, 70 tokens, 14 precedence declarations, and 167 parser alternatives",
    "The final 2,678/2,678 Release suite includes generated-parser token/location, recovery, library, constant-folding, parser-abort release, literal-owner transfer, reference-order, helper-shadowing, and exact stack-boundary coverage",
    *BROAD_LIBRARY_EVIDENCE,
]

NUMERIC_EVIDENCE = [
    "Numeric/libm focused suites exercise 10,088 frozen exact oracle cases inside the final zero-failure managed suite",
    "FdlibmElementaryExactOracleCorpusTests: 2,596 exact jq-visible binary64 records",
    "GammaCompatExactOracleCorpusTests: 1,366 exact value/sign-bit records",
    *BROAD_LIBRARY_EVIDENCE,
]


MAPPING_COMPLETION: dict[str, dict[str, object]] = {}


def assign_completion(paths: tuple[str, ...], record: dict[str, object]) -> None:
    for path in paths:
        if path in MAPPING_COMPLETION:
            raise RuntimeError(f"duplicate completion metadata for {path}")
        MAPPING_COMPLETION[path] = record


assign_completion(
    ("src/builtin.c", "src/builtin.h"),
    completion(
        "The jq 1.8.2 library-facing builtin registration and invocation contract, including the complete public inventory, jq-coded source bindings, native/private primitives, result streams, errors, and explicit state capabilities",
        [
            "BUILTIN_COMPLETENESS_AUDIT.md: 226/226 public signatures with zero missing/extra and an execution route for every signature",
            "BUILTIN_RESOURCE_ANALYSIS.md: all 106/106 unchanged builtin.jq definitions are source-bound, with all seven unresolved private primitives supplied",
            "EmbeddedBuiltinResourceBindingTests directly proves builtins_bind installs all 106 source definitions, returns jq's zero-error result, and is the compile path",
            "Regex and Oniguruma focused slice 1,029/1,029, unchanged onig/manonig 66/66, complete declared corpus 4,980/4,980, and independent frozen-artifact audit 92,589/92,589 plus 62/62 isolated processes",
            "TimeBuiltinOracleMatrixTests passes 125/125 glibc-oracle cases across the complete C-locale directive surface, raw tm state, errors, source-shaped mktime normalization, POSIX/TZif history and relative paths, stateful DST call order, and epoch boundaries; four host-TZif-sensitive gap rows compare exactly with the same-host pinned jq oracle",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "Native C function-pointer, allocation, and calling ABIs",
            "Native Oniguruma API, bytecode, allocator, engine architecture, and C ABI identity",
            "Time locale era/alternate-digit tables and host-libc extensions outside the pinned Linux/glibc-2.39 C-locale oracle",
            "Leap-second right/* zones, absolute TZ file paths, and Windows-only zone-name/far-epoch behavior without TZif data",
            "Undefined out-of-bounds glibc behavior from malformed POSIX M months outside 1..12",
            "CLI-only presentation policy",
        ],
    ),
)

assign_completion(
    ("src/bytecode.c",),
    completion(
        "Production bytecode helper contract: opcode description and variable-length decoding, recursive disassembly, state-owned graph cleanup, compile-time environment-slot graph metadata, and explicit-environment constant restoration over compiler-produced bytecode",
        [
            "BytecodeProductionPipelineTests verifies pinned compiler streams, subfunction graphs, VM dispatch, and recursive production teardown",
            "Jq182BytecodeLayoutStructuralTests covers all metadata/lengths, recursive golden disassembly, globals ownership, and descendant cleanup",
            "EnvironmentCompatibilityTests covers compile-time $ENV constants, ambient env call timing, and explicit execution replacement/restoration",
            "EnvironmentFastPathCompatibilityTests covers root/nested graph summaries, no-$ENV bypass eligibility, metadata cleanup, fresh sequential replacement, and independent ownership",
        ],
        [
            "Managed arrays and object references are not native allocation/layout ABI",
            "Malformed bytecode and native allocation-failure injection",
        ],
    ),
)

assign_completion(
    ("src/bytecode.h",),
    completion(
        "Production declaration contract: the 43-opcode enum, opcode/cfunction/symbol-table/bytecode data shapes, flags, ABI constants, pools, lexical parents, subfunction graphs, and managed-only environment-slot graph summary consumed by the compiler and VM",
        [
            "Jq182BytecodeLayoutStructuralTests exhaustively compares enum order, flags, encoded lengths, and stack effects",
            "BytecodeProductionPipelineTests exercises compiler-produced constants, cfunctions, locals, closures, parents, and subfunctions",
            "DirectBytecodeVmCompatibilityTests executes the production structures",
            "EnvironmentFastPathCompatibilityTests verifies the environment-slot summary on root, nested, builtin-only, recompiled, and independent bytecode graphs",
        ],
        [
            "Managed types are not native C layout- or pointer-ABI compatible",
            "Native allocation addresses and function-pointer ABI",
        ],
    ),
)

assign_completion(
    ("src/exec_stack.h",),
    completion(
        "Production exec-stack contract: negative-offset block/link storage, shared-successor forest topology, non-LIFO removal, reallocation stability, alignment, reset, typed VM payload association, and cleanup",
        [
            "Jq182BytecodeLayoutStructuralTests covers directed forests, non-LIFO pops, 256-block reallocations, alignment, zero-size blocks, reset, and cleanup",
            "DirectBytecodeVmCompatibilityTests executes value, frame, local, closure, and forkpoint payloads through the stack",
            "EXECUTE_VM_IMPLEMENTATION_MAP.md maps stack_push/pop/popn/save/restore and frame lifetime to pinned execute.c",
        ],
        [
            "Managed payload references are side-tabled by logical block address rather than stored in a moving raw byte buffer",
            "Managed objects and arrays are not native C memory layout or allocator ABI",
        ],
    ),
)

assign_completion(
    ("src/opcode_list.h",),
    completion(
        "Structural opcode-table contract only: all 43 entries in exact order with jq 1.8.2 flags, encoded lengths, stack inputs, and stack outputs",
        [
            "Jq182BytecodeLayoutStructuralTests exhaustively verifies all 43 opcode descriptions",
            "Runtime table construction rejects count or enum-order drift",
        ],
        [
            "Opcode dispatch and VM execution behavior",
            "Compiler lowering and bytecode debug metadata",
        ],
    ),
)

assign_completion(
    ("src/compile.c",),
    completion(
        "Production block/inst compiler contract: source-named generation and binding, call argument expansion, dead-definition handling, lexical nesting, locals/closures/cfunctions/constants, branch and call encoding, diagnostics, bytecode limits, argument/environment expansion with deterministic bottom-up graph metadata, optimization handoff, and deterministic ownership transfer",
        [
            "BytecodeProductionPipelineTests verifies pinned jq 1.8.2 ushort streams, constants, locals, cfunctions, subfunction graphs, VM dispatch, runtime errors, and teardown",
            "EnvironmentFastPathCompatibilityTests verifies deterministic bottom-up $ENV slot summaries without changing bytecode words or constant ownership",
            "CompiledLiteralRefcountLifecycleTests and CompileArgumentRawObjectIterationCompatibilityTests verify LOADK pool, argument, recompile, suspension, exhaustion, and teardown ownership",
            "CompilerClosureLimitCompatibilityTests and compiler/module suites cover closure/function-size boundaries, binding, diagnostics, and dependency lowering",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "Managed block/inst/bytecode objects are not C layout- or pointer-ABI compatible",
            "Native allocation failure injection and address identity",
        ],
    ),
)

assign_completion(
    ("src/compile.h",),
    completion(
        "Production compiler IR declaration contract: source-shaped inst_immediate, inst, block, binding/branch reference identity, moved-owner list headers, and left-associated BLOCK_1 through BLOCK_8 composition",
        [
            "GeneratedParserReferenceCompatibilityTests and direct parser/linker IR suites exercise source-shaped generation and binding",
            "BytecodeProductionPipelineTests proves the declared IR lowers to pinned jq 1.8.2 bytecode",
            "Compiler ownership lifecycle suites cover consumed block and constant owners",
        ],
        [
            "Managed reference/value types are not native C struct layout- or pointer-ABI compatible",
            "Native allocator and address identity",
        ],
    ),
)

assign_completion(
    ("src/execute.c",),
    completion(
        "Production direct-bytecode VM contract: jq_start/jq_next/reset, frame and closure traversal, shared data/fork stacks, every executable opcode and ON_BACKTRACK arm, lazy result order, errors and try handling, paths, cfunction calls, normal/tail calls, optimization, explicit copy/move/free boundaries, debug traces, no-$ENV environment-materialization bypass, and opt-in managed execution policies",
        EXECUTOR_EVIDENCE,
        [
            "Managed objects, arrays, delegates, and exceptions are not native C layout, address, function-pointer, or allocation ABI",
            "Typed CLR stack payloads are side-tabled by native-shaped logical stack address",
            "Invalid bytecode throws a managed exception where native debug builds assert or release builds may have undefined behavior",
            "Explicit timeout, cancellation, transition, recursion, and environment policies are opt-in managed extensions; defaults preserve jq execution behavior",
        ],
    ),
)

assign_completion(
    ("src/jq.h",),
    completion(
        "Production jq_state lifecycle contract: init/compile/start/next/reset/teardown, state-owned bytecode and VM cleanup, attributes/origins/search paths, owned-jv and public input callbacks, debug/stderr callbacks, halt state, native-default ambient environment/time behavior, explicit environment policy, and terminal outcomes",
        [
            "JqStateRefcountLifecycleCompatibilityTests verifies bytecode retention, result transfer, reset/recompile, exhaustion, and teardown owner counts",
            "Stateful IO/origin/environment/halt compatibility suites exercise callbacks, metadata, environment, and terminal states",
            "CliInputCallbackCompatibilityTests exercises the jq-shaped owned-jv CLI callback while the public IJqInputSource contract remains unchanged",
            "DirectBytecodeVmCompatibilityTests and public execution suites execute the state-owned VM path",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "Managed state, delegates, and VM objects are not native C layout- or pointer-ABI compatible",
            "Native allocation-failure hooks, FILE*, and ambient library I/O",
            "Host callbacks are synchronous and require host-side bounding if they block",
        ],
    ),
)

assign_completion(
    ("src/main.c",),
    completion(
        "The public jq 1.8.2 command-line contract through the dotnetjq executable: interspersed and bundled options, stdin/file/raw/sequence/stream input through the source-shaped owned-jv callback, program and module files, compile arguments, output formatting/color, diagnostics, halt and exit statuses, home startup loading, managed --run-tests/disassembly/debug-trace compatibility, and byte-oriented standard streams on Unix and Windows",
        [
            "src/DotNetJq.Cli implements the executable over the jq-shaped managed compile, parser, module, execution, and value APIs without launching the reference jq binary",
            "CliInputCallbackCompatibilityTests verifies direct owned-jv input/inputs, end/error/position behavior, early-reset ownership, raw/slurp/stream sharing, file failures, cancellation, and sustained streaming continuation",
            "CliErrorContractTests verifies jq_util_input_read_more-compatible 4091-byte/newline buffering and exact zero-, one-, and multi-line runtime source positions, including values that complete before their terminating newline is consumed by jv_parser",
            "CliInputBufferCompatibilityTests verifies the single source-shaped read_more state machine against pinned jq for parsed EOF/trailing blanks, final empty files, raw record continuation across files, exact 4091-byte chunks, C2/E2/F0 UTF-8 tail reads, per-buffer malformed-unit repair, raw slurp, and final filename/line state",
            "CliFileAndModuleTests verifies --rawfile and --slurpfile retain jq's one-open behavior by validating and parsing the same capability-read byte snapshot",
            "DotNetJq.Cli.Tests passes 84/84 process tests comparing exit status, stdout, and stderr bytes against pinned jq 1.8.2 cases; product branding is asserted separately rather than normalized",
            "ManagedTestRunner ports the jq_test.c fixture grammar and lifecycle/value checks; DeveloperModeCompatibilityTests passes 22/22 cases across 18 methods (17 facts and one five-row theory), including all seven official fixtures (879/879), selection/status/file/option edge cases, trace shape/detail, and verbose test execution",
            "--debug-dump-disasm uses compiler-produced state bytecode and --debug-trace[=all] uses direct VM opcode/address/backtracking events",
            "tools/verify-cli-compatibility.sh hash-checks and executes the unchanged pinned tests/shtest and tests/utf8test drivers against dotnetjq",
            "tests/cli-shell/verify.sh and tests/powershell/Verify-DotNetJq.ps1 exercise native invocation, Unicode paths and pipelines, byte output, NUL framing, and jq-compatible exit status; release CI installs the exact selector/RID packages from its local-only bundle on win-x64 and win-arm64 and requires both PowerShell 7 and Windows PowerShell 5.1",
            "The release tooling defines eight 64-bit NativeAOT targets and RID-specific dotnetjq tool packages; the managed API package is separately identified as DotNetJq.Library while retaining its DotNetJq assembly and namespace",
            "NuGet packing requires explicit Authors and RepositoryUrl values; package-set verification enforces identical exact identity metadata across the library, selector, and RID packages",
            "CLI project packaging settings request deterministic native linking on Windows/macOS and omit no-debug macOS dSYM sidecars; tools/release/README.md records the pre-release cold-build evidence and the eight-platform byte-equality/unchanged process-test acceptance gate, without changing jq runtime semantics",
        ],
        [
            "The executable is branded dotnetjq; --version and --build-configuration identify the managed product and jq compatibility level",
            "Exact native jq bytecode/program-counter/VM-stack/refcount trace and disassembly-text identity, allocator/FILE* fault injection, and signals",
            "Managed console, filesystem, globalization, and process APIs replace the C, POSIX, and Windows-native implementation details",
        ],
    ),
)

assign_completion(
    ("src/jq_parser.h",),
    completion(
        "Observable jq_parse and jq_parse_library entry-point contract, including definitions-only library validation, source names, exact byte locations, and compile-facing parse diagnostics",
        PARSER_EVIDENCE,
        [
            "Managed locfile references and out block values replace C pointer ABI details",
            "No Bison parser-state or native parser ABI identity",
        ],
    ),
)

assign_completion(
    ("src/lexer.c",),
    completion(
        "Deterministic GPLEX-generated managed lexer implementation plus jq 1.8.2 observable tokenization: all mapped rules/tokens, exact whitespace and longest-match boundaries, delimiter/interpolation states, segmented strings, escapes, comments, and UTF-8 byte spans",
        [
            "Deterministic normalization internalizes GPLEX frame-only BufferException and ScanBuff types so the production public API is unchanged",
            "Deterministic normalization replaces GPLEX's reflective token-sentinel lookup with a statically rooted typed maxParseToken constant; the NativeAOT lexer/compile smoke publishes and executes it",
            "The incremental UTF-8 coordinate cursor matched the former eager three-array scanner on 7,407/7,407 cases and reduced allocation from 352,112.020 to 144,080.020 bytes per 17,328-character scan",
            *PARSER_EVIDENCE,
        ],
        [
            "The embedded C# semantic actions are a manually maintained language-specific port of jq's C actions",
            "GPLEX DFA/runtime code replaces Flex output; there is no Flex scanner-memory-layout or generated-C source identity",
        ],
    ),
)

assign_completion(
    ("src/lexer.h",),
    completion(
        "Deterministically generated managed token/location declaration surface corresponding to the pinned jq 1.8.2 lexer token set and observable UTF-8 byte spans",
        PARSER_EVIDENCE,
        [
            "Managed Token/TokenKind declarations replace Flex's C scanner header and are not C ABI-identical",
            "No Flex-generated internal scanner declarations or state-table identity",
        ],
    ),
)

assign_completion(
    ("src/lexer.l",),
    completion(
        "Committed managed grammar structural parity for all six start conditions and 53 ordered lexer rules, plus observable behavior mapping into the deterministic GPLEX scanner output",
        PARSER_EVIDENCE,
        [
            "The C# semantic actions are manually ported and still require behavioral compatibility proof",
            "GPLEX generation is not proof of Flex automaton/table identity",
        ],
    ),
)

assign_completion(
    ("src/parser.c",),
    completion(
        "Deterministically GPPG-generated managed shift/reduce parser plus jq 1.8.2 observable source-language behavior: accepted/rejected forms, precedence, direct block/inst compiler IR, library-only parsing, compile references, metadata folding, source diagnostics, all 11 explicit recovery alternatives, and deterministic release of discarded tokens, reduced blocks, and standalone metadata",
        PARSER_EVIDENCE,
        [
            "GPPG LR tables and managed runtime replace Bison tables/runtime; generated tables are structurally guarded but not byte-identical",
            "No Bison yyparse stack layout, native destructor ABI/invocation identity, or generated-C source identity; logical discarded-value release remains in scope",
        ],
    ),
)

assign_completion(
    ("src/parser.h",),
    completion(
        "Deterministically generated native-shaped managed block entry-point surface for jq program and definitions-only library parsing",
        PARSER_EVIDENCE,
        [
            "Managed locfile references and out block values replace C pointer ABI details",
            "No Bison-generated internal state declarations",
        ],
    ),
)

assign_completion(
    ("src/parser.y",),
    completion(
        "Committed managed GPPG grammar with ordered structural parity for all 70 tokens, 14 precedence declarations, and 167 production alternatives, including close C-to-C# block/inst actions, all 11 explicit error alternatives, and deterministic release of discarded tokens, reduced blocks, and standalone metadata",
        PARSER_EVIDENCE,
        [
            "C semantic actions are necessarily maintained as explicit C# translations and are behaviorally tested rather than mechanically transpiled",
            "GPPG parse tables/runtime replace Bison; no generated-C or internal table-byte identity",
        ],
    ),
)

assign_completion(
    ("src/jq_test.c",),
    completion(
        "Managed harness equivalence for the official .test grammar, success/error/compile-failure records, superfluous-output checks, state reset/reuse, compile arguments, value tests, and concurrency checks",
        [
            "FixtureHarnessContractTests and Harness/UpstreamTestFile.cs map jq_test.c record parsing and final-output validation",
            "All seven unchanged official fixtures pass 879/879 through the managed compatibility runner",
            "Stateful/public execution tests cover reset, recompile, exhaustion, reuse, compile arguments, and concurrent isolation",
        ],
        [
            "xUnit and the managed runner replace the native jq_testsuite callback/thread orchestration",
            "This mapping does not implement the omitted jq command-line application",
        ],
    ),
)

assign_completion(
    ("src/jv.c",),
    completion(
        "jq-visible managed value semantics plus explicit logical owners for invalid/literal-number/string/array/object allocations: kinds, owned invalid payloads, literal/native numbers, UTF-8 string buffers, logical refcounts, string/array/object copy-on-write, capacity growth, slices, cached string hashing, object buckets/tombstones/rehash, physical-order iteration, identity/equality/contains/merge, and exact 10,000-depth outcomes",
        JV_C_EVIDENCE,
        [
            "Native packed byte layouts and raw pointers; CLR references represent allocation identity and separate managed arrays represent native trailing object buckets",
            "Native jvp_literal_number trailing decNumberUnit[] layout is replaced by a managed JvNumber allocation; its logical refcount, final release, exact decimal value, and canonical literal rendering remain in scope",
            "C variadic formatting and FILE*/terminal printer entry points",
        ],
    ),
)

assign_completion(
    ("src/jv.h",),
    completion(
        "Managed jv handle contract including byte-backed string allocations, allocated literal-number state, array view offset/size, object capacity size, owned invalid messages, CLR allocation identity, and explicit logical refcounts for invalid/literal-number/string/array/object owners",
        JV_H_EVIDENCE,
        [
            "Native packed byte layout and raw pointers are represented by a readonly managed handle plus CLR allocation references",
            "Native jvp_literal_number trailing decNumberUnit[] layout is replaced by a managed JvNumber allocation; its logical refcount, final release, exact decimal value, and canonical literal rendering remain in scope",
            "C variadic formatting and FILE*/terminal printer entry points",
        ],
    ),
)

assign_completion(
    ("src/jv_aux.c",),
    completion(
        "jq-visible get/set/has/path/delete/keys/compare/stable-sort/group/unique behavior, numeric index/slice normalization, path correlation, and 10,000-depth outcomes",
        DIRECT_VALUE_EVIDENCE,
        [
            "Native pointer ownership and temporary sort-allocation representation",
            "Private C worker layout where the public jq-shaped function is implemented by an iterative managed engine",
        ],
    ),
)

assign_completion(
    ("src/jv_private.h",),
    completion(
        "The jq-private number comparison and NaN predicate contract through jvp_number_cmp and jvp_number_is_nan",
        [
            "DIRECT_PORT_SYMBOL_AUDIT.md maps both exported private-header symbols",
            "NumericCompatibilityTests and JvDirectPortSymbolCompatibilityTests cover literal/native comparison and NaN",
        ],
        ["Native header layout and header guard mechanics"],
    ),
)

assign_completion(
    ("src/jv_alloc.c", "src/jv_alloc.h"),
    completion(
        "jq-observable allocation/lifecycle substitution: jq-shaped allocation helpers, explicit parser/state disposal, and managed no-memory delegate shape without leaking native ownership into results",
        [
            "JvRuntimeProxyCompatibilityTests directly covers allocation sizes/zero-fill, overflow, duplication, reallocation prefixes, free, and handler registration",
            "Direct value, streaming parser, state lifecycle, early-disposal, and package-isolation tests",
            "LICENSE_AND_PROXY_AUDIT.md verifies mandatory substitution metadata on both mapped targets",
        ],
        [
            "CLR allocation replaces native malloc/realloc/free and process-global OOM termination; jq logical jv_refcnt behavior is reproduced in jv.c.cs and is not part of this allocator-ABI exclusion",
            "No native allocation address or timing identity",
        ],
    ),
)

assign_completion(
    ("src/jv_dtoa.c", "src/jv_dtoa.h"),
    completion(
        "Direct jq-shaped parity for the mode-0 jvp_dtoa/jvp_dtoa_fmt and jvp_strtod decimal-prefix contract used by upstream jq, plus managed public jq serialization/diagnostics: shortest binary64 digits, decimal-point/sign/end positions, signed zero, nonfinite values, overflow/underflow, exponent/fixed layout thresholds, and exact literal preservation",
        [
            "JvDtoaDirectProxyCompatibilityTests matches pinned mode-0 digit/decpt/sign/end tuples, exact strtod prefix indices/value bits, and jvp_dtoa_fmt boundary strings",
            *NUMERIC_EVIDENCE,
        ],
        [
            "Managed formatters replace David Gay dtoa internals, native buffers, and allocator ABI",
            "jvp_dtoa modes 1 and 4-9 are retained compatibility conveniences but do not claim every native bigint digit-request boundary; production jq calls mode 0",
            "Other non-mode-0 digit-request behavior and native rounding-environment identity are outside this jq-visible scope",
        ],
    ),
)

assign_completion(
    ("src/jv_dtoa_tsd.c", "src/jv_dtoa_tsd.h"),
    completion(
        "Thread-isolated dtoa-context lifecycle as observed through deterministic, concurrent jq numeric parsing and formatting",
        [
            "JvRuntimeProxyCompatibilityTests verifies stable same-thread and distinct explicit cross-thread dtoa contexts",
            "Numeric exact-corpus tests and concurrent public execution tests",
            "Package isolation verification executes numeric formatting without native state",
        ],
        [
            "CLR thread/context isolation replaces native pthread/TLS allocation and finalization",
            "No native dtoa context-layout identity",
        ],
    ),
)

assign_completion(
    ("src/jv_file.c",),
    completion(
        "Library file-loading behavior through explicit IJqFileSystem: raw UTF-8 replacement, multiple JSON values, BOM handling, success/error result shapes, and module-data use",
        [
            "ModuleFileCompatibilityTests, ModuleExecutionCompatibilityTests, and module fixture coverage",
            "CliFileAndModuleTests verifies --rawfile and --slurpfile parse exactly one completed IJqFileSystem read snapshot without reopening or re-resolving the pathname",
            "Package isolation verifies no ambient checkout, jq process, native dependency, or network is required",
        ],
        [
            "Host I/O is capability-injected rather than ambient System.IO access",
            "Platform-specific OS error suffix wording and CLI filename iteration",
        ],
    ),
)

assign_completion(
    ("src/jv_parse.c",),
    completion(
        "Direct jq_parser_* incremental byte-parser port for normal, sequence, streaming, and stream-error modes across arbitrary partial UTF-8 buffers, adjacent values, BOMs, errors, paths, literal numbers, and remaining-byte counts",
        [
            "jv_parse and jv_parse_sized route through the same mapped incremental parser and return jq-shaped invalid-with-message values; public and CLI adapters alone translate terminal parse errors",
            "JV_PARSER_STREAMING_AUDIT.md and JvParserStreamingCompatibilityTests cover every two-chunk split and key single-byte boundaries",
            "Unchanged jq/shell sequence and streaming vectors are frozen in focused library tests",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "Managed objects replace native C struct layout, raw pointers, and allocator identity",
            "The public JsonElement facade parses the already-produced canonical UTF-8 value payload after the direct jq parser boundary",
        ],
    ),
)

assign_completion(
    ("src/jv_print.c",),
    completion(
        "Direct jq value-printer contract for compact, pretty, sorted, ASCII-only, color, tab, indentation, invalid-value, literal-number, nonfinite, escaping, diagnostic-truncation, and 10,000-depth behavior through one iterative traversal",
        [
            "The public API and CLI share jv_dump/jv_dumpf/jv_dump_string/jv_dump_bytes traversal; public output materializes one canonical UTF-8 payload for byte accounting and JsonElement parsing",
            "JvPrintOwnershipCompatibilityTests and JvPrintSourceArchitectureGuardTests verify consuming/borrowed ownership, all four top-level adapters, flags, byte sinks, and the absence of a duplicate CLI serializer",
            "JsonProxyCompatibilityRound2Tests, NumericCompatibilityTests, DeepStructureCompatibilityRound3Tests, FormatCompatibilityTests, and CLI byte-output tests",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "Managed byte sinks and console adapters replace FILE*, native output buffers, allocation layout, and TTY implementation details",
            "Windows standard streams deliberately remain byte-oriented as documented in WINDOWS_STDIO_CONTRACT.md",
        ],
    ),
)

assign_completion(
    ("src/jv_thread.h",),
    completion(
        "Thread-safety and per-execution isolation observable through concurrent compilation, execution, callbacks, attributes, and number formatting",
        [
            "JvRuntimeProxyCompatibilityTests directly covers mutex ownership/errors, concurrent run-once, per-thread keys, destructor disposal, and dtoa-context isolation",
            "Concurrent public execution, stateful capability-isolation, parser, and exact numeric tests",
            "Package isolation verifies the replacement uses managed runtime primitives only",
        ],
        [
            ".NET synchronization primitives replace pthread/Win32 types and are not ABI-identical",
            "No native thread handle, destructor, or scheduling identity",
        ],
    ),
)

assign_completion(
    ("src/jv_unicode.c", "src/jv_unicode.h", "src/jv_utf8_tables.h"),
    completion(
        "jq UTF-8 traversal/repair/encoding, reverse traversal, scalar/byte offsets, and exact jq whitespace classification as consumed by value, parser, regex-offset, and diagnostic paths",
        [
            "UnicodeCompatibilityTests and ParserTokenLocationCompatibilityTests cover malformed advancement, backtracking, encoding, byte/scalar offsets, and whitespace",
            "DIRECT_PORT_SYMBOL_AUDIT.md maps all seven exported jv_unicode symbols",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            ".NET Rune algorithms replace native lookup-table layout and macros",
            "No table-byte, pointer, or decoder implementation identity",
        ],
    ),
)

assign_completion(
    ("src/libm.h",),
    completion(
        "All jq 1.8.2 public libm filter signatures and jq-visible binary64 value/sign/serialization behavior across ordinary, boundary, special, Bessel, erf, elementary fdlibm, and gamma-family cases",
        NUMERIC_EVIDENCE,
        [
            "Managed System.Math/fdlibm plus replaceable GlibcCompat code replaces the platform native libm ABI",
            "C errno, floating-point environment flags, symbol-versioning, and non-jq-callable libm entry points",
        ],
    ),
)

assign_completion(
    ("src/linker.c", "src/linker.h"),
    completion(
        "Observable module/include/data linking and ownership contract: search order, relative paths, metadata/dependencies, namespaces, shadowing, cycles, resource limits, source filenames, parsed-module/MODULEMETA/DEPS lifetime, data-import slot transfer, and failure/cancellation unwind through explicit resolver capabilities",
        [
            "ModuleExecutionCompatibilityTests, ModuleFileCompatibilityTests (including immutable path/content snapshots), ModuleResourceLimitCompatibilityTests, and StatefulOriginMetadataCompatibilityTests",
            "LinkerDirectIrCompatibilityTests: 17/17 direct block/inst path/search, stat-layout selection, data binding, recursive load/cycle, transitive dependency order, and modulemeta cases; ModuleExecutionCompatibilityTests: 19/19",
            "ParsedProgramRefcountLifecycleTests verifies modulemeta copy-before-release and imported constant transfer/final teardown",
            "Legacy JqModuleLinker/ModuleMetadataNode AST path removed after migration to direct block/inst and state-owned bytecode lifetime tests",
            "DIRECT_PORT_SYMBOL_AUDIT.md classifies the complete linker.c/linker.h surface",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "JqModuleResolver explicitly supplies filesystem/search/home/cancellation/resource capabilities instead of ambient POSIX process state",
            "Physical path and host I/O diagnostics are normalized by the configured filesystem capability",
        ],
    ),
)

assign_completion(
    ("src/locfile.c", "src/locfile.h"),
    completion(
        "jq observable valid-UTF-8 source-location contract: byte offsets, line maps, source filenames, underline widths, UNKNOWN_LOCATION prefix/callback behavior, retain/free lifecycle, and exact compile/parser diagnostic blocks",
        [
            "SourceLocationCompatibilityTests, ParserTokenLocationCompatibilityTests, CompileValidationRound3Tests, and imported-source diagnostic cases",
            "LocfileDirectPortCompatibilityTests directly verifies UNKNOWN_LOCATION prefix/callback behavior, UTF-8 byte columns/underlines, line maps, and line-boundary clipping",
            "DIRECT_PORT_SYMBOL_AUDIT.md maps location, UNKNOWN_LOCATION, locfile, and all locfile_* symbols",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "Managed typed formatting replaces the C variadic locfile_locate ABI",
            "Malformed source-byte display is outside the valid-UTF-8 parity scope and uses .NET replacement-rune decoding",
            "No native allocation or pointer identity",
        ],
    ),
)

assign_completion(
    ("src/util.c", "src/util.h"),
    completion(
        "Library-visible utility behavior for path expansion/canonicalization, home lookup compatibility, byte search, explicit stream writes, MIN/MAX, and the explicit input/date substitutions described by the mapping",
        [
            "DIRECT_PORT_SYMBOL_AUDIT.md exhaustively classifies util.c/util.h functions, types, constants, conditional branches, and substitutions",
            "UtilDirectPortCompatibilityTests directly verifies pinned-glibc empty-needle memmem behavior, binary search, realpath capability results, exact stream bytes, and MIN/MAX",
            "Module/file/stateful/date/format compatibility tests exercise the jq-visible consumers",
            *BROAD_LIBRARY_EVIDENCE,
        ],
        [
            "The CLI jq_util_input_* loop is mapped separately under src/main.c to CliInputReader; native FILE*, POSIX tm/pointer ABI, Windows console routing, and conditional libc strptime implementation are outside this library target",
            "Production capabilities do not infer ambient home/current-directory/executable-path state",
        ],
    ),
)

assign_completion(
    (
        "vendor/decNumber/decContext.c",
        "vendor/decNumber/decContext.h",
        "vendor/decNumber/decNumber.c",
        "vendor/decNumber/decNumber.h",
        "vendor/decNumber/decNumberLocal.h",
    ),
    completion(
        "The decNumber behavior jq consumes for decimal-literal parsing, exponent range, exact comparison/equality, sign/negation/absolute value, native-double conversion, and quantum-preserving jq serialization",
        [
            "NumericCompatibilityTests, NumericCompatibilityRound2Tests, JvDirectPortSymbolCompatibilityTests, and exact jq-visible numeric corpora",
            "Unchanged jq.test literal-number and extreme-exponent cases pass through the managed runner",
            *NUMERIC_EVIDENCE,
        ],
        [
            "ManagedDecimal is not an IBM decNumber C API, structure-layout, status-word, or arithmetic implementation",
            "Alternate decimal32/64/128, DPD, packed, single/double/quad APIs and examples are separately OMITTED because jq does not link them",
            "Ordinary jq arithmetic intentionally remains binary64, matching jq 1.8.2",
        ],
    ),
)


FIXTURE_RESULTS = {
    "tests/base64.test": (10, "aec7a4812b4bdf937e77ba1261c5d51ece66308258d60ca2227b209a4398a64d"),
    "tests/jq.test": (550, "329689763b651096989bd8260b643731083fc5fd17f6bd7834d158713f738cbd"),
    "tests/man.test": (231, "3c25698e94d5199c75b784cc361cda9c6775389eb61f8f4fb05fde1a757ae3f9"),
    "tests/manonig.test": (19, "62738dd27ce0dbc4f21296e48270f20024274631cae4b9ffe85a0eecaa53af7f"),
    "tests/onig.test": (47, "e82dab356709d4a5e4dfd8c71aced12ed1f42eb23208bac0eaf5e3f05bedef05"),
    "tests/optional.test": (2, "06d7249e3d9e09d572995e66276cd65f2da6f907a15e2f9cb012e672598ac5b9"),
    "tests/uri.test": (20, "530958297b8eac04093e742756ab0ecd57de4364a38ac69fa7fed693c381ec39"),
}

TEST_CATEGORY_FIXTURES = {
    "mantest": ("tests/man.test", 231),
    "jqtest": ("tests/jq.test", 550),
    "base64test": ("tests/base64.test", 10),
    "uritest": ("tests/uri.test", 20),
    "optionaltest": ("tests/optional.test", 2),
    "onigtest": ("tests/onig.test", 47),
    "manonigtest": ("tests/manonig.test", 19),
}


def build_test_categories() -> dict[str, dict[str, object]]:
    categories: dict[str, dict[str, object]] = {}
    for category, (fixture, passed) in TEST_CATEGORY_FIXTURES.items():
        fixture_sha256 = FIXTURE_RESULTS[fixture][1]
        categories[category] = {
            "upstreamDriver": f"tests/{category}",
            "driverSha256": TEST_DRIVER_SHA256[category],
            "fixture": fixture,
            "fixtureSha256": fixture_sha256,
            "strategy": "PROXY",
            "managedTargets": [
                "tools/DotNetJq.CompatibilityRunner/Program.cs",
                "tools/DotNetJq.CompatibilityRunner/WorkerProcess.cs",
                "src/DotNetJq.Cli/ManagedTestRunner.cs",
                "tests/DotNetJq.Cli.Tests/DeveloperModeCompatibilityTests.cs",
            ],
            **completion(
                f"Every expected value/error record in the unchanged pinned {fixture} fixture executed through the managed library runner",
                [
                    f"Pinned driver SHA-256 {TEST_DRIVER_SHA256[category]}",
                    f"Pinned fixture SHA-256 {fixture_sha256}",
                    f"Managed compatibility runner passes {passed}/{passed} records with no exclusions or expected-failure allowlist",
                    "DeveloperModeCompatibilityTests passes all seven official fixtures (879/879) end to end through dotnetjq --run-tests",
                ],
                [
                    "The POSIX shell, Valgrind wrapper, and native jq executable are replaced by managed reporter and --run-tests implementations",
                    "The category proves the behavior represented by its fixture records, not unrepresented CLI or engine-internal identity",
                ],
            ),
        }

    categories["shtest"] = {
        "upstreamDriver": "tests/shtest",
        "driverSha256": TEST_DRIVER_SHA256["shtest"],
        "strategy": "REUSE",
        "managedTargets": [
            "tools/verify-cli-compatibility.sh",
            "src/DotNetJq.Cli/JqCliApplication.cs",
            "tests/DotNetJq.Cli.Tests/PinnedCliContractTests.cs",
            "tests/DotNetJq.Cli.Tests/CliErrorContractTests.cs",
            "tests/DotNetJq.Cli.Tests/CliInputBufferCompatibilityTests.cs",
            "tests/DotNetJq.Cli.Tests/CliFileAndModuleTests.cs",
            "tests/DotNetJq.Cli.Tests/OracleDifferentialTests.cs",
            "tests/DotNetJq.Cli.Tests/CliInputCallbackCompatibilityTests.cs",
            "tests/DotNetJq.Tests/CliFixtureLibrarySemanticsTests.cs",
            "tests/DotNetJq.Tests/JvParserStreamingCompatibilityTests.cs",
            "tests/DotNetJq.Tests/CompilerClosureLimitCompatibilityTests.cs",
        ],
        "coverageAudit": "tests/EXCLUSIONS.md",
        "cliExclusionIds": [entry["id"] for entry in CLI_EXCLUSIONS],
        **completion(
            "The portable executable behavior exercised by the unchanged pinned shtest driver, including jq option/input/output/module/error/status contracts, plus retained library-level coverage for the same semantic paths",
            [
                f"Pinned driver SHA-256 {TEST_DRIVER_SHA256['shtest']}",
                "tools/verify-cli-compatibility.sh executes the archived, unchanged driver with JQ set to the built DotNetJq.Cli apphost and a fixture-local jq alias only for jq-f-test.sh",
                "DotNetJq.Cli.Tests adds byte-exact process assertions and pinned-oracle comparisons for cross-platform file, option, diagnostic, color, raw-NUL, module, halt, and exit behavior",
                "CliFixtureLibrarySemanticsTests plus parser, module, stateful I/O, halt, numeric, location, bytecode, and closure-limit suites independently execute the retained library semantics",
            ],
            [
                "Native-only FILE*/LD_PRELOAD injection, Valgrind accounting, signals, allocator identity, and exact C VM trace identity represented by native-shell-and-memory-tooling",
                "Platform-conditional shtest branches execute only on hosts where the unchanged driver enables them; dedicated Windows and PowerShell tests cover the managed Windows command boundary",
            ],
        ),
    }

    categories["utf8test"] = {
        "upstreamDriver": "tests/utf8test",
        "driverSha256": TEST_DRIVER_SHA256["utf8test"],
        "strategy": "REUSE",
        "managedTargets": [
            "tools/verify-cli-compatibility.sh",
            "src/DotNetJq.Cli/JqCliApplication.cs",
            "tests/DotNetJq.Cli.Tests/CliInputBufferCompatibilityTests.cs",
            "tests/DotNetJq.Cli.Tests/CliFileAndModuleTests.cs",
            "tests/DotNetJq.Tests/CliFixtureLibrarySemanticsTests.cs",
            "tests/DotNetJq.Tests/JsonProxyCompatibilityRound2Tests.cs",
            "tests/DotNetJq.Tests/JvParserStreamingCompatibilityTests.cs",
        ],
        "coverageAudit": "tests/EXCLUSIONS.md",
        "cliExclusionIds": [],
        **completion(
            "All program-file and raw-slurp supplementary-plane preservation loops in the unchanged pinned utf8test driver through the real dotnetjq executable",
            [
                f"Pinned driver SHA-256 {TEST_DRIVER_SHA256['utf8test']}",
                "tools/verify-cli-compatibility.sh executes tests/utf8test unchanged against the built DotNetJq.Cli apphost",
                "CliInputBufferCompatibilityTests covers exact 4091-byte C2/E2/F0 UTF-8 tail reads, per-buffer malformed-unit repair, and raw slurp against the pinned oracle; CliFixtureLibrarySemanticsTests.Utf8testProgramSourcePreservesLongFourByteCharacters plus CLI file, JSON, streaming-parser, and Unicode tests provide the remaining focused diagnostics",
            ],
            [
                "POSIX shell setup is the upstream driver mechanism rather than executable product behavior",
                "Managed UTF-8 and filesystem implementations replace the native C FILE* implementation and ABI",
            ],
        ),
    }

    categories["tests/modules/**"] = {
        "upstreamSourceTree": "tests/modules",
        "upstreamCommit": "34f7186b86743a083a589741b6cea95293524108",
        "gitTree": MODULE_FIXTURE_GIT_TREE,
        "fileCount": len(MODULE_FIXTURE_FILES),
        "fileSha256": MODULE_FIXTURE_FILES,
        "strategy": "REUSE",
        "managedTargets": [
            "tools/verify-cli-compatibility.sh",
            "tests/DotNetJq.Cli.Tests/CliFileAndModuleTests.cs",
            "tools/DotNetJq.CompatibilityRunner/WorkerProcess.cs",
            "tests/DotNetJq.Tests/Compatibility/ModuleExecutionCompatibilityTests.cs",
            "tests/DotNetJq.Tests/CliFixtureLibrarySemanticsTests.cs",
        ],
        "cliExclusionIds": [],
        **completion(
            "Exact pinned 19-file module fixture tree plus CLI and library-visible home startup, -L/import/include/data resolution, metadata, ordering, shadowing, and cycle behavior",
            [
                f"Pinned Git tree {MODULE_FIXTURE_GIT_TREE} at jq commit 34f7186b86743a083a589741b6cea95293524108",
                "All 19 fixture-relative paths and SHA-256 values are recorded and validator-checked",
                "The unchanged shtest driver consumes tests/modules through dotnetjq; CLI process tests and ModuleExecutionCompatibilityTests add direct home, -L, file, ordering, and cycle coverage",
            ],
            [
                "Managed filesystem APIs replace native path, FILE*, and platform error-suffix implementation details",
                "Managed debug disassembly is structural and does not claim native jq bytecode text identity",
            ],
        ),
    }

    categories["tests/torture/**"] = {
        "upstreamSourceTree": "tests/torture",
        "upstreamCommit": "34f7186b86743a083a589741b6cea95293524108",
        "gitTree": TORTURE_FIXTURE_GIT_TREE,
        "fileCount": len(TORTURE_FIXTURE_FILES),
        "fileSha256": TORTURE_FIXTURE_FILES,
        "strategy": "PROXY",
        "managedTargets": [
            "tools/verify-cli-compatibility.sh",
            "src/DotNetJq.Cli/CliInputReader.cs",
            "tests/DotNetJq.Tests/JvParserStreamingCompatibilityTests.cs",
            "tests/DotNetJq.Tests/JsonProxyCompatibilityRound2Tests.cs",
        ],
        "coverageAudit": "tests/EXCLUSIONS.md",
        "cliExclusionIds": ["native-shell-and-memory-tooling"],
        **completion(
            "Pinned torture-input provenance and managed CLI/library parser robustness across every byte prefix, malformed data, nesting, streaming, and stream reconstruction",
            [
                f"Pinned Git tree {TORTURE_FIXTURE_GIT_TREE} and input SHA-256 {TORTURE_FIXTURE_FILES['tests/torture/input0.json']}",
                "The unchanged shtest driver feeds every byte prefix to dotnetjq in ordinary and streaming reconstruction modes and rejects assert/abort/core diagnostics",
                "JvParserStreamingCompatibilityTests exercises every two-chunk split and key single-byte boundaries without native process state",
                "tests/EXCLUSIONS.md identifies only the native Valgrind/allocator/signal identity boundary",
            ],
            [
                "Valgrind leak/error accounting and native heap, allocator, timing, and signal identity",
                "The managed process robustness assertion replaces native memory-tool instrumentation; parser outputs and errors remain in scope",
            ],
        ),
    }
    return categories


TEST_CATEGORIES = build_test_categories()

BUILTIN_RESOURCE_COMPLETION = completion(
    "Byte-for-byte resource identity and production runtime-library binding of all 106 ordered top-level jq definitions into every compiled program",
    [
        "Resource length 9,631 bytes and SHA-256 b8a5fd9579be9b51c9a04e6620f8c1655539aa57eea33a84e202a8dea401f2a4 match pinned upstream",
        "EmbeddedBuiltinResourceBindingTests proves all 106/106 definitions resolve to the parsed source objects with jq lexical ordering and shadowing",
        "BUILTIN_RESOURCE_ANALYSIS.md records assembly loading, strict UTF-8 library parsing, private dependencies, and package isolation",
    ],
    [
        "This REUSE claim covers resource bytes and binding, not native builtin implementations or regex-engine identity",
    ],
)

INVENTORY_POLICY = {
    "authoritativeCheckedInScope": [
        "Every regular file directly under src/",
        "Every .c and .h file directly under vendor/decNumber/",
        "The official .test fixtures enumerated by TEST_REUSE",
    ],
    "makefileBuiltSources": {
        "src/builtin.inc": {
            "disposition": "not separately inventoried",
            "reason": "Ephemeral C include generated from mapped src/builtin.jq; the managed build embeds the jq source instead",
        },
        "src/config_opts.inc": {
            "disposition": "not separately inventoried",
            "reason": "Ephemeral native configure output; the managed CLI reports its .NET runtime, RID, execution engine, and jq compatibility level directly",
        },
        "src/version.h": {
            "disposition": "not separately inventoried",
            "reason": "Ephemeral native version header; the managed CLI has explicit dotnetjq product and jq compatibility versions",
        },
    },
}


def target_for(path: str) -> str:
    if path in TARGET_OVERRIDES:
        return TARGET_OVERRIDES[path]
    if path.startswith("vendor/decNumber/"):
        return "src/DotNetJq/Port/vendor/decNumber/" + path.rsplit("/", 1)[-1] + ".cs"
    if path == "src/jq_test.c":
        return "tests/DotNetJq.Tests/Harness/UpstreamTestFile.cs"
    return "src/DotNetJq/Port/src/" + path.rsplit("/", 1)[-1] + ".cs"


files: dict[str, dict[str, object]] = {}
source_paths = [
    path.relative_to(UPSTREAM).as_posix()
    for base in (UPSTREAM / "src", UPSTREAM / "vendor" / "decNumber")
    for path in base.iterdir()
    if path.is_file() and (base.name == "src" or path.suffix in {".c", ".h"})
]

for source_path in sorted(source_paths):
    if source_path.startswith("vendor/decNumber/") and source_path not in PROXIES:
        files[source_path] = {
            "target": None,
            "strategy": "OMITTED",
            "reason": "Not linked into libjq; alternate decimal formats, shared implementation fragments, or examples",
            **completion(
                "No semantic parity is asserted; this is a completed inventory classification of an upstream decNumber file that pinned jq 1.8.2 does not compile or link",
                [
                    "The pinned jq Makefile/source dependency graph links only decContext.c and decNumber.c from this vendor directory",
                    "TRACEABILITY_AUDIT.md verifies the exact 30-file vendor/decNumber inventory with no missing or extra mapping",
                ],
                ["The omitted alternate decimal format, shared-fragment, or example implementation itself"],
                semantic_parity=False,
            ),
        }
    elif source_path in OMITTED:
        files[source_path] = {
            "target": None,
            "strategy": "OMITTED",
            "reason": OMITTED[source_path],
            **completion(
                "No semantic parity is asserted; this is a completed intentional omission from the managed library deliverable",
                [
                    "TRACEABILITY_AUDIT.md verifies that the file is explicitly inventoried and classified",
                    "The mapping reason identifies the non-library build/developer or CLI boundary",
                ],
                [OMITTED[source_path]],
                semantic_parity=False,
            ),
        }
    elif source_path in REUSED:
        reuse = REUSED[source_path]
        files[source_path] = {
            "target": reuse["target"],
            "strategy": "REUSE",
            "reason": reuse["reason"],
            "status": "done",
            "semanticParity": True,
            "semanticParityScope": reuse["semanticParityScope"],
            "runtimeUsage": reuse["runtimeUsage"],
        }
        files[source_path].update(BUILTIN_RESOURCE_COMPLETION)
    elif source_path in GENERATED:
        generated = GENERATED[source_path]
        files[source_path] = {
            "target": generated["target"],
            "strategy": "GENERATED",
            "sourceOfTruth": generated["sourceOfTruth"],
            "generator": generated["generator"],
        }
        for optional_key in (
            "managedSource",
            "generatorPackage",
            "template",
            "role",
            "generatedOutputs",
        ):
            if optional_key in generated:
                files[source_path][optional_key] = generated[optional_key]
        files[source_path].update(MAPPING_COMPLETION[source_path])
    elif source_path in PROXIES:
        files[source_path] = {
            "target": target_for(source_path),
            "strategy": "PROXY",
            "replacement": PROXIES[source_path],
        }
        if source_path == "src/jv_utf8_tables.h":
            files[source_path]["sharedImplementationWith"] = "src/jv_unicode.c"
            files[source_path]["traceabilityNote"] = (
                "The upstream lookup tables are replaced by the managed UTF-8 algorithms "
                "in the mapped jv_unicode.c proxy; no generated table artifact is required"
            )
        files[source_path].update(MAPPING_COMPLETION[source_path])
    else:
        files[source_path] = {
            "target": target_for(source_path),
            "strategy": "PORT",
        }
        files[source_path].update(MAPPING_COMPLETION[source_path])

for test_path, reason in sorted(TEST_REUSE.items()):
    passed, sha256 = FIXTURE_RESULTS[test_path]
    files[test_path] = {
        "target": "external:pinned-upstream/" + test_path,
        "strategy": "REUSE",
        "reason": reason,
        **completion(
            "Byte-for-byte reuse of the pinned official fixture and complete managed-runner agreement with every expected output/error record in that unchanged file",
            [
                f"Pinned fixture SHA-256 {sha256}",
                f"Managed compatibility runner passes {passed}/{passed} cases from this unchanged fixture",
                "UPSTREAM_ORACLE_VALIDATION.md confirms the official jq 1.8.2 release-source tests pass independently",
            ],
            [
                "The fixture is evidence for the behaviors it contains, not exhaustive proof of unrepresented jq or CLI behavior",
                "Native jq_test.c orchestration is replaced by the separately mapped managed harness",
            ],
        ),
    }

manifest = {
    "schemaVersion": 2,
    "upstream": {
        "repository": "https://github.com/jqlang/jq",
        "tag": "jq-1.8.2",
        "commit": "34f7186b86743a083a589741b6cea95293524108",
        "localCheckout": "upstream/jq",
    },
    "inventoryPolicy": INVENTORY_POLICY,
    "completionPolicy": COMPLETION_POLICY,
    "dependencies": DEPENDENCIES,
    "auxiliaryImplementations": AUXILIARY_IMPLEMENTATIONS,
    "testCategories": TEST_CATEGORIES,
    "cliExclusions": CLI_EXCLUSIONS,
    "files": files,
}


def resolve_target(target: str) -> Path:
    external_prefix = "external:pinned-upstream/"
    if target.startswith(external_prefix):
        return UPSTREAM / target.removeprefix(external_prefix)
    return ROOT / target


def validate_completion(owner: str, entry: dict[str, object], errors: list[str]) -> None:
    status = entry.get("status")
    parity = entry.get("semanticParity")
    scope = entry.get("semanticParityScope")
    exclusions = entry.get("semanticParityExclusions")
    evidence = entry.get("evidence")

    if status not in {"done", "in_progress"}:
        errors.append(f"{owner}: invalid or missing status")
    if not isinstance(parity, bool):
        errors.append(f"{owner}: semanticParity must be boolean")
    if parity is True and status != "done":
        errors.append(f"{owner}: semanticParity true requires status done")
    if not isinstance(scope, str) or not scope.strip():
        errors.append(f"{owner}: missing semanticParityScope")
    if not isinstance(exclusions, list) or not exclusions or not all(
        isinstance(item, str) and item.strip() for item in exclusions
    ):
        errors.append(f"{owner}: semanticParityExclusions must be a non-empty string list")
    if not isinstance(evidence, list) or not evidence or not all(
        isinstance(item, str) and item.strip() for item in evidence
    ):
        errors.append(f"{owner}: evidence must be a non-empty string list")


def validate_manifest() -> None:
    errors: list[str] = []
    expected_strategy_counts = {
        "PORT": 25,
        "PROXY": 17,
        "GENERATED": 6,
        "REUSE": 8,
        "OMITTED": 26,
    }

    if len(files) != 82:
        errors.append(f"files: expected 82 mappings, found {len(files)}")
    actual_strategy_counts = {
        strategy: sum(entry.get("strategy") == strategy for entry in files.values())
        for strategy in expected_strategy_counts
    }
    if actual_strategy_counts != expected_strategy_counts:
        errors.append(
            f"files: strategy counts {actual_strategy_counts!r} do not match {expected_strategy_counts!r}"
        )

    expected_category_names = {
        *TEST_DRIVER_SHA256,
        "tests/modules/**",
        "tests/torture/**",
    }
    if set(TEST_CATEGORIES) != expected_category_names:
        errors.append(
            "testCategories: category inventory does not exactly match pinned drivers, "
            "tests/modules/**, and tests/torture/**"
        )

    cli_exclusion_ids = [entry.get("id") for entry in CLI_EXCLUSIONS]
    if len(cli_exclusion_ids) != len(set(cli_exclusion_ids)):
        errors.append("cliExclusions: ids must be unique")
    for index, exclusion in enumerate(CLI_EXCLUSIONS):
        owner = f"cliExclusions[{index}]"
        if not isinstance(exclusion.get("id"), str) or not exclusion["id"]:
            errors.append(f"{owner}: non-empty id is required")
        if not isinstance(exclusion.get("reason"), str) or not exclusion["reason"]:
            errors.append(f"{owner}: non-empty reason is required")
        upstream_sources = exclusion.get("upstreamSources")
        if not isinstance(upstream_sources, list) or not upstream_sources:
            errors.append(f"{owner}: upstreamSources must be a non-empty list")
        else:
            for upstream_source in upstream_sources:
                if not isinstance(upstream_source, str) or not (UPSTREAM / upstream_source).exists():
                    errors.append(f"{owner}: upstream source does not resolve: {upstream_source!r}")

    proxy_header_fields = (
        "UPSTREAM COMPONENT",
        "REPLACEMENT",
        "WHY",
        "BEHAVIORAL CONTRACT",
        "KNOWN DIFFERENCES",
        "TESTS COVERING THE SUBSTITUTION",
    )

    for source_path, entry in files.items():
        owner = f"files[{source_path}]"
        validate_completion(owner, entry, errors)
        strategy = entry.get("strategy")
        target = entry.get("target")
        if strategy == "OMITTED":
            if target is not None:
                errors.append(f"{owner}: OMITTED target must be null")
            if not entry.get("reason"):
                errors.append(f"{owner}: OMITTED mapping requires reason")
        else:
            if not isinstance(target, str) or not target:
                errors.append(f"{owner}: non-OMITTED mapping requires target")
            elif not resolve_target(target).exists():
                errors.append(f"{owner}: target does not resolve: {target}")
            elif target.endswith(".cs"):
                target_header = resolve_target(target).read_text(encoding="utf-8")
                provenance_fields = (
                    "34f7186b86743a083a589741b6cea95293524108",
                    source_path,
                    f"Strategy: {strategy}",
                    f"Target file: {target}",
                )
                missing_provenance = [
                    field for field in provenance_fields if field not in target_header
                ]
                if missing_provenance:
                    errors.append(
                        f"{owner}: target provenance missing {', '.join(missing_provenance)}"
                    )

        if strategy == "PROXY":
            if not entry.get("replacement"):
                errors.append(f"{owner}: PROXY mapping requires replacement")
            if isinstance(target, str) and not target.startswith("external:"):
                target_path = resolve_target(target)
                if target_path.is_file():
                    header = target_path.read_text(encoding="utf-8")
                    missing = [field for field in proxy_header_fields if field not in header]
                    if missing:
                        errors.append(f"{owner}: proxy header missing {', '.join(missing)}")

        if strategy == "GENERATED":
            source_of_truth = entry.get("sourceOfTruth")
            if not isinstance(source_of_truth, str) or not (UPSTREAM / source_of_truth).exists():
                errors.append(
                    f"{owner}: generated sourceOfTruth does not resolve in pinned upstream: {source_of_truth!r}"
                )
            managed_source = entry.get("managedSource")
            if managed_source is not None and (
                not isinstance(managed_source, str) or not resolve_target(managed_source).is_file()
            ):
                errors.append(f"{owner}: generated managedSource does not resolve: {managed_source!r}")
            generator = entry.get("generator")
            if not isinstance(generator, str) or not resolve_target(generator).exists():
                errors.append(f"{owner}: generated generator does not resolve: {generator!r}")
            for generated_output in entry.get("generatedOutputs", []):
                if not resolve_target(generated_output).exists():
                    errors.append(f"{owner}: generated output does not resolve: {generated_output}")
            template = entry.get("template")
            if isinstance(template, str) and not resolve_target(template).exists():
                errors.append(f"{owner}: template does not resolve: {template}")

    for section_name in ("dependencies", "auxiliaryImplementations"):
        section = manifest[section_name]
        assert isinstance(section, dict)
        for name, entry in section.items():
            assert isinstance(entry, dict)
            owner = f"{section_name}[{name}]"
            validate_completion(owner, entry, errors)
            target = name if section_name == "auxiliaryImplementations" else entry.get("target")
            if not isinstance(target, str) or not resolve_target(target).exists():
                errors.append(f"{owner}: target does not resolve: {target!r}")
            if section_name == "dependencies":
                for field in ("repository", "commit", "strategy"):
                    if not isinstance(entry.get(field), str) or not entry[field]:
                        errors.append(f"{owner}: dependency requires {field}")
            else:
                upstream_sources = entry.get("upstreamSources")
                if not isinstance(upstream_sources, list) or not upstream_sources:
                    errors.append(f"{owner}: auxiliary implementation requires upstreamSources")
            if entry.get("strategy") == "GENERATED":
                source_of_truth = entry.get("sourceOfTruth")
                source_sha256 = entry.get("sourceSha256")
                if isinstance(source_of_truth, str):
                    generated_sources = [source_of_truth]
                    expected_hashes = {source_of_truth: source_sha256}
                elif (
                    isinstance(source_of_truth, list)
                    and source_of_truth
                    and all(isinstance(item, str) and item for item in source_of_truth)
                    and isinstance(source_sha256, dict)
                ):
                    generated_sources = source_of_truth
                    expected_hashes = source_sha256
                else:
                    generated_sources = []
                    expected_hashes = {}
                    errors.append(f"{owner}: generated sourceOfTruth/sourceSha256 schema is invalid")
                for generated_source in generated_sources:
                    source_path = UPSTREAM / generated_source
                    expected_hash = expected_hashes.get(generated_source)
                    if not source_path.is_file():
                        errors.append(f"{owner}: generated sourceOfTruth does not resolve: {generated_source}")
                    elif not isinstance(expected_hash, str) or hashlib.sha256(source_path.read_bytes()).hexdigest() != expected_hash:
                        errors.append(f"{owner}: generated sourceSha256 does not match {generated_source}")
                generator = entry.get("generator")
                if not isinstance(generator, str) or not resolve_target(generator).is_file():
                    errors.append(f"{owner}: generated checker does not resolve")
                range_count = entry.get("rangeCount")
                record_counts = entry.get("recordCounts")
                has_range_count = isinstance(range_count, int) and range_count > 0
                has_record_counts = (
                    isinstance(record_counts, dict)
                    and bool(record_counts)
                    and all(
                        isinstance(key, str)
                        and key
                        and isinstance(value, int)
                        and value > 0
                        for key, value in record_counts.items()
                    )
                )
                if not has_range_count and not has_record_counts:
                    errors.append(f"{owner}: generated entry requires positive rangeCount or recordCounts")
            if entry.get("strategy") == "PROXY":
                target_path = resolve_target(target) if isinstance(target, str) else None
                if target_path is not None and target_path.is_file():
                    header = target_path.read_text(encoding="utf-8")
                    missing = [field for field in proxy_header_fields if field not in header]
                    if missing:
                        errors.append(f"{owner}: proxy header missing {', '.join(missing)}")

    glibc_owner = "dependencies[glibc-gamma-compat].implementationTargets"
    glibc_targets = DEPENDENCIES["glibc-gamma-compat"].get("implementationTargets")
    glibc_directory = ROOT / "src" / "DotNetJq.GlibcCompat"
    discovered_glibc_targets = {
        path.relative_to(ROOT).as_posix()
        for path in glibc_directory.glob("*.cs")
        if path.is_file()
    }
    if not isinstance(glibc_targets, list) or not glibc_targets:
        errors.append(f"{glibc_owner}: non-empty list is required")
    else:
        declared_glibc_targets = [entry.get("target") for entry in glibc_targets]
        if len(declared_glibc_targets) != len(set(declared_glibc_targets)):
            errors.append(f"{glibc_owner}: targets must be unique")
        if set(declared_glibc_targets) != discovered_glibc_targets:
            errors.append(
                f"{glibc_owner}: exact target set does not match production DotNetJq.GlibcCompat/*.cs discovery"
            )
        dependency = DEPENDENCIES["glibc-gamma-compat"]
        provenance_markers = [
            dependency["repository"],
            dependency["tag"],
            dependency["tagObject"],
            dependency["commit"],
            "Strategy: PORT",
        ]
        for index, implementation in enumerate(glibc_targets):
            owner = f"{glibc_owner}[{index}]"
            target = implementation.get("target")
            upstream_sources = implementation.get("upstreamSources")
            role = implementation.get("role")
            if implementation.get("strategy") != "PORT":
                errors.append(f"{owner}: strategy must be PORT")
            if not isinstance(target, str) or not resolve_target(target).is_file():
                errors.append(f"{owner}: target does not resolve: {target!r}")
                continue
            if not (
                isinstance(upstream_sources, list)
                and upstream_sources
                and all(isinstance(source, str) and source for source in upstream_sources)
            ):
                errors.append(f"{owner}: non-empty upstreamSources list is required")
                continue
            if not isinstance(role, str) or not role:
                errors.append(f"{owner}: non-empty role is required")
            header = resolve_target(target).read_text(encoding="utf-8")
            missing_markers = [
                marker for marker in [*provenance_markers, *upstream_sources]
                if marker not in header
            ]
            if missing_markers:
                errors.append(
                    f"{owner}: port header missing {', '.join(missing_markers)}"
                )

    oniguruma_owner = "dependencies[vendor/oniguruma].implementationTargets"
    oniguruma_targets = DEPENDENCIES["vendor/oniguruma"].get("implementationTargets")
    regex_directory = ROOT / "src" / "DotNetJq" / "Compatibility" / "Regex"
    discovered_regex_targets = {
        path.relative_to(ROOT).as_posix()
        for path in regex_directory.glob("*.cs")
        if path.is_file()
    }
    if not isinstance(oniguruma_targets, list) or not oniguruma_targets:
        errors.append(f"{oniguruma_owner}: non-empty list is required")
    else:
        declared_regex_targets = [entry.get("target") for entry in oniguruma_targets]
        if len(declared_regex_targets) != len(set(declared_regex_targets)):
            errors.append(f"{oniguruma_owner}: targets must be unique")
        if set(declared_regex_targets) != discovered_regex_targets:
            errors.append(
                f"{oniguruma_owner}: exact target set does not match production Regex/*.cs discovery"
            )
        strategy_markers = {
            "PORT": ("DOTNETJQ COMPATIBILITY PORT", "Strategy: PORT", "STRATEGY: PORT"),
            "PROXY": ("DOTNETJQ COMPATIBILITY PROXY", "Strategy: PROXY", "STRATEGY: PROXY"),
            "GENERATED": ("DOTNETJQ GENERATED COMPATIBILITY DATA", "Strategy: GENERATED", "STRATEGY: GENERATED"),
        }
        for index, implementation in enumerate(oniguruma_targets):
            owner = f"{oniguruma_owner}[{index}]"
            target = implementation.get("target")
            strategy = implementation.get("strategy")
            upstream_sources = implementation.get("upstreamSources")
            role = implementation.get("role")
            if not isinstance(target, str) or target not in discovered_regex_targets:
                errors.append(f"{owner}: target is not a discovered production Regex/*.cs file")
                continue
            target_path = resolve_target(target)
            if strategy not in strategy_markers:
                errors.append(f"{owner}: strategy must be PORT, PROXY, or GENERATED")
            if not isinstance(role, str) or not role.strip():
                errors.append(f"{owner}: non-empty role is required")
            if not isinstance(upstream_sources, list) or not upstream_sources or not all(
                isinstance(source, str)
                and source.startswith("vendor/oniguruma/")
                and (UPSTREAM / source).is_file()
                for source in upstream_sources
            ):
                errors.append(f"{owner}: upstreamSources must resolve inside pinned vendor/oniguruma")
            header = target_path.read_text(encoding="utf-8")
            if manifest["upstream"]["commit"] not in header:
                errors.append(f"{owner}: target header is missing the pinned jq revision")
            if "Oniguruma" not in header or "source" not in header.lower():
                errors.append(f"{owner}: target header is missing Oniguruma source provenance")
            if strategy in strategy_markers and not any(
                marker in header for marker in strategy_markers[strategy]
            ):
                errors.append(f"{owner}: target header is missing its {strategy} strategy marker")

    for category, entry in TEST_CATEGORIES.items():
        owner = f"testCategories[{category}]"
        validate_completion(owner, entry, errors)
        if entry.get("strategy") not in {"PROXY", "REUSE"}:
            errors.append(f"{owner}: strategy must be PROXY or REUSE")
        managed_targets = entry.get("managedTargets")
        if not isinstance(managed_targets, list) or not managed_targets:
            errors.append(f"{owner}: managedTargets must be a non-empty list")
        else:
            for managed_target in managed_targets:
                if not isinstance(managed_target, str) or not resolve_target(managed_target).exists():
                    errors.append(f"{owner}: managed target does not resolve: {managed_target!r}")
        coverage_audit = entry.get("coverageAudit")
        if coverage_audit is not None and (
            not isinstance(coverage_audit, str) or not resolve_target(coverage_audit).is_file()
        ):
            errors.append(f"{owner}: coverageAudit does not resolve: {coverage_audit!r}")
        exclusion_ids = entry.get("cliExclusionIds", [])
        if not isinstance(exclusion_ids, list) or not all(
            isinstance(exclusion_id, str) and exclusion_id in cli_exclusion_ids
            for exclusion_id in exclusion_ids
        ):
            errors.append(f"{owner}: cliExclusionIds contains an unknown id")

        if category in TEST_DRIVER_SHA256:
            expected_driver = f"tests/{category}"
            driver = entry.get("upstreamDriver")
            expected_driver_hash = TEST_DRIVER_SHA256[category]
            if driver != expected_driver:
                errors.append(f"{owner}: upstreamDriver must be {expected_driver}")
            elif hashlib.sha256((UPSTREAM / driver).read_bytes()).hexdigest() != expected_driver_hash:
                errors.append(f"{owner}: pinned driver SHA-256 does not match checkout")
            if entry.get("driverSha256") != expected_driver_hash:
                errors.append(f"{owner}: driverSha256 does not match pinned value")
            if category in TEST_CATEGORY_FIXTURES:
                expected_fixture, _ = TEST_CATEGORY_FIXTURES[category]
                expected_fixture_hash = FIXTURE_RESULTS[expected_fixture][1]
                if entry.get("fixture") != expected_fixture:
                    errors.append(f"{owner}: fixture must be {expected_fixture}")
                if entry.get("fixtureSha256") != expected_fixture_hash:
                    errors.append(f"{owner}: fixtureSha256 does not match pinned value")

    for category, source_tree, expected_tree, expected_files in (
        ("tests/modules/**", "tests/modules", MODULE_FIXTURE_GIT_TREE, MODULE_FIXTURE_FILES),
        ("tests/torture/**", "tests/torture", TORTURE_FIXTURE_GIT_TREE, TORTURE_FIXTURE_FILES),
    ):
        owner = f"testCategories[{category}]"
        entry = TEST_CATEGORIES[category]
        actual_paths = {
            path.relative_to(UPSTREAM).as_posix()
            for path in (UPSTREAM / source_tree).rglob("*")
            if path.is_file()
        }
        if actual_paths != set(expected_files):
            errors.append(f"{owner}: exact fixture file inventory does not match pinned tree")
        if entry.get("upstreamSourceTree") != source_tree:
            errors.append(f"{owner}: upstreamSourceTree must be {source_tree}")
        if entry.get("upstreamCommit") != manifest["upstream"]["commit"]:
            errors.append(f"{owner}: upstreamCommit does not match manifest pin")
        if entry.get("gitTree") != expected_tree:
            errors.append(f"{owner}: gitTree does not match pinned value")
        if entry.get("fileCount") != len(expected_files):
            errors.append(f"{owner}: fileCount does not match exact fixture inventory")
        if entry.get("fileSha256") != expected_files:
            errors.append(f"{owner}: fileSha256 map does not match pinned inventory")
        for fixture_path, expected_sha256 in expected_files.items():
            fixture_file = UPSTREAM / fixture_path
            if not fixture_file.is_file():
                errors.append(f"{owner}: fixture does not resolve: {fixture_path}")
            elif hashlib.sha256(fixture_file.read_bytes()).hexdigest() != expected_sha256:
                errors.append(f"{owner}: SHA-256 does not match {fixture_path}")

    for fixture, (_, expected_sha256) in FIXTURE_RESULTS.items():
        actual_sha256 = hashlib.sha256((UPSTREAM / fixture).read_bytes()).hexdigest()
        if actual_sha256 != expected_sha256:
            errors.append(
                f"files[{fixture}]: fixture SHA-256 {actual_sha256} does not match {expected_sha256}"
            )

    builtin_resource = ROOT / REUSED["src/builtin.jq"]["target"]
    builtin_sha256 = hashlib.sha256(builtin_resource.read_bytes()).hexdigest()
    expected_builtin_sha256 = "b8a5fd9579be9b51c9a04e6620f8c1655539aa57eea33a84e202a8dea401f2a4"
    if builtin_sha256 != expected_builtin_sha256:
        errors.append(
            f"files[src/builtin.jq]: resource SHA-256 {builtin_sha256} does not match {expected_builtin_sha256}"
        )

    if ARGS.release:
        release_sections: tuple[tuple[str, dict[str, dict[str, object]]], ...] = (
            ("files", files),
            ("dependencies", DEPENDENCIES),
            ("auxiliaryImplementations", AUXILIARY_IMPLEMENTATIONS),
            ("testCategories", TEST_CATEGORIES),
        )
        for section_name, section in release_sections:
            for name, entry in section.items():
                if entry.get("strategy") == "OMITTED":
                    continue
                if entry.get("status") != "done" or entry.get("semanticParity") is not True:
                    errors.append(
                        f"release closure: {section_name}[{name}] is a non-OMITTED open mapping"
                    )

    if errors:
        raise RuntimeError("manifest validation failed:\n  - " + "\n  - ".join(errors))


validate_manifest()

rendered_manifest = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"


def main(args: argparse.Namespace) -> int:
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != rendered_manifest:
            print(f"stale manifest: regenerate {OUTPUT.relative_to(ROOT)} with {Path(__file__).name}")
            return 1
        print(f"verified {len(files)} mappings in {OUTPUT.relative_to(ROOT)}")
        return 0

    OUTPUT.write_text(rendered_manifest, encoding="utf-8")
    print(f"wrote {len(files)} mappings to {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(ARGS))
