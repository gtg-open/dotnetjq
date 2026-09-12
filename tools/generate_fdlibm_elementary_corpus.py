#!/usr/bin/env python3
"""Reproduce the frozen jq-1.8.2 elementary-fdlibm exact-bit corpus.

This is offline test tooling only. Production and managed tests never execute jq.

Input construction is deterministic:

* broad inputs use CPython's MT19937 ``random.Random`` with seeds 18082 and
  1808205;
* every fdlibm high-word branch boundary is supplemented with the immediately
  adjacent binary64 values on both sides (including the effective next-high-
  word transitions used by strict ``>`` comparisons);
* function order is acosh, asinh, atanh, expm1, log1p, followed by input order;
* each decompressed record is little-endian ``<input_uint64, output_uint64>``;
* gzip uses compression level 9 and mtime 0 before standard base64 encoding.

Expected outputs are produced only by the pinned jq-1.8.2 executable built from
commit 34f7186b86743a083a589741b6cea95293524108. Its expected SHA-256 is
b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f.

Reproduction check:

    python3 tools/generate_fdlibm_elementary_corpus.py --check

Use ``--emit-json`` to print replacement metadata and the deterministic base64
payload after intentionally changing this recipe.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import math
import os
from pathlib import Path
import random
import re
import struct
import subprocess


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_ORACLE = Path(os.environ.get("DOTNETJQ_ORACLE") or
                      ROOT / "artifacts/test-assets/jq-1.8.2/oracle/jq")
DEFAULT_TEST = ROOT / "tests" / "DotNetJq.Tests" / "FdlibmElementaryExactOracleCorpusTests.cs"
EXPECTED_ORACLE_VERSION = "jq-1.8.2"
EXPECTED_ORACLE_SHA256 = "b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--oracle", type=Path, default=DEFAULT_ORACLE)
    parser.add_argument("--test-file", type=Path, default=DEFAULT_TEST)
    parser.add_argument("--check", action="store_true", help="compare the regenerated corpus with the checked-in test")
    parser.add_argument("--emit-json", action="store_true", help="print metadata and the regenerated base64 payload")
    return parser.parse_args()


def from_bits(bits: int) -> float:
    return struct.unpack(">d", struct.pack(">Q", bits))[0]


def bits(value: float) -> int:
    return struct.unpack("<Q", struct.pack("<d", value))[0]


def adjacent(bit_pattern: int) -> list[float]:
    return [from_bits(bit_pattern - 1), from_bits(bit_pattern), from_bits(bit_pattern + 1)]


def signed_adjacent(bit_pattern: int) -> list[float]:
    positive = adjacent(bit_pattern)
    return positive + [-value for value in positive]


def distinct(values: list[float], predicate=lambda _: True) -> list[float]:
    result: list[float] = []
    seen: set[int] = set()
    for value in values:
        value_bits = bits(value)
        if value_bits in seen or not predicate(value):
            continue
        seen.add(value_bits)
        result.append(value)
    return result


def create_inputs() -> list[tuple[str, list[float]]]:
    rng = random.Random(18082)
    maximum = from_bits(0x7FEFFFFFFFFFFFFF)

    acosh = [
        1.0, math.nextafter(1.0, math.inf), 2.0,
        math.nextafter(2.0, math.inf), 2**28, maximum,
    ]
    acosh += [1.0 + (10.0 ** rng.uniform(-15, 308)) for _ in range(400)]
    # Documented mathematical regions x >= 1, x > 2, and x >= 2**28,
    # plus the historical next-high-word seam as an additional stress point.
    acosh += adjacent(0x3FF0000000000000)
    acosh += adjacent(0x4000000100000000)
    acosh += adjacent(0x41B0000000000000)
    acosh = distinct(acosh, lambda value: math.isfinite(value) and value >= 1)

    asinh = [
        0.0, math.ulp(0.0), -math.ulp(0.0), 2**-28, -(2**-28),
        2.0, -2.0, 2**28, -(2**28), maximum, -maximum,
    ]
    asinh += [
        math.copysign(10.0 ** rng.uniform(-320, 308), rng.choice([-1, 1]))
        for _ in range(400)
    ]
    # ix < 0x3e300000, ix > 0x40000000, and ix > 0x41b00000.
    asinh += signed_adjacent(0x3E30000000000000)
    asinh += signed_adjacent(0x4000000100000000)
    asinh += signed_adjacent(0x41B0000100000000)
    asinh = distinct(asinh, math.isfinite)

    atanh = [
        math.nextafter(-1.0, 0.0), -0.5, math.nextafter(-0.5, 0.0),
        0.0, 2**-28, -(2**-28), 0.5, math.nextafter(0.5, 0.0),
        math.nextafter(1.0, 0.0),
    ]
    atanh += [rng.uniform(-1, 1) for _ in range(400)]
    # ix < 0x3e300000 and ix < 0x3fe00000; finite domain edges are included.
    atanh += signed_adjacent(0x3E30000000000000)
    atanh += signed_adjacent(0x3FE0000000000000)
    atanh = distinct(atanh, lambda value: math.isfinite(value) and abs(value) < 1)

    log_two = math.log(2)
    expm1 = [
        -1000.0, -56 * log_two, -2 * log_two, -log_two, -0.5,
        -(2**-54), 0.0, 2**-54, 0.5, log_two, 2 * log_two,
        20 * log_two, 56 * log_two, 709.0, 709.782712893384,
    ]
    expm1 += [rng.uniform(-1000, 709.7827128933839) for _ in range(350)]
    expm1 += [
        math.copysign(10.0 ** rng.uniform(-320, -1), rng.choice([-1, 1]))
        for _ in range(100)
    ]
    # hx < 0x3c900000, hx > 0x3fd62e42, hx < 0x3ff0a2b2,
    # hx >= 0x4043687a, and hx >= 0x40862e42.
    for boundary in (
        0x3C90000000000000,
        0x3FD62E4300000000,
        0x3FF0A2B200000000,
        0x4043687A00000000,
        0x40862E4200000000,
    ):
        expm1 += signed_adjacent(boundary)
    for exponent in (-56, -20, -2, -1, 1, 2, 20, 56):
        value = exponent * log_two
        expm1 += [math.nextafter(value, -math.inf), value, math.nextafter(value, math.inf)]
    overflow_threshold = 7.09782712893383973096e02
    expm1 = distinct(expm1, lambda value: math.isfinite(value) and value <= overflow_threshold)

    rng = random.Random(1808205)
    log1p: list[float] = []
    # hx < 0x3fda827a, the signed 0xbfd2bec3 shortcut, ix < 0x3e200000,
    # ix < 0x3c900000, and the 0x43400000 large-input normalization path.
    for value in (
        math.nextafter(-1.0, math.inf),
        from_bits(0xBFD2BEC300000000),
        -(2**-29),
        -(2**-54),
        0.0,
        2**-54,
        2**-29,
        from_bits(0x3FDA827A00000000),
        0.5,
        1.0,
        2.0,
        2**53,
    ):
        log1p += [math.nextafter(value, -math.inf), value, math.nextafter(value, math.inf)]
    log1p += [math.ulp(0.0), -math.ulp(0.0), 1e-100, -1e-100, maximum]
    log1p += [rng.uniform(-0.9999999999999999, 10) for _ in range(250)]
    log1p += [-1.0 + (10.0 ** rng.uniform(-16, -1e-9)) for _ in range(150)]
    log1p += [10.0 ** rng.uniform(-320, 308) for _ in range(250)]
    log1p += [
        math.copysign(10.0 ** rng.uniform(-320, -1), rng.choice([-1, 1]))
        for _ in range(150)
    ]
    log1p = distinct(
        log1p,
        lambda value: math.isfinite(value)
        and value > -1
        and not (value == 0 and math.copysign(1.0, value) < 0),
    )

    return [
        ("acosh", acosh),
        ("asinh", asinh),
        ("atanh", atanh),
        ("expm1", expm1),
        ("log1p", log1p),
    ]


def verify_oracle(path: Path) -> None:
    executable = path.read_bytes()
    actual_hash = hashlib.sha256(executable).hexdigest()
    if actual_hash != EXPECTED_ORACLE_SHA256:
        raise SystemExit(f"oracle SHA-256 mismatch: expected {EXPECTED_ORACLE_SHA256}, got {actual_hash}")
    version = subprocess.run(
        [path, "--version"], check=True, capture_output=True, text=True
    ).stdout.strip()
    if version != EXPECTED_ORACLE_VERSION:
        raise SystemExit(f"oracle version mismatch: expected {EXPECTED_ORACLE_VERSION}, got {version}")


def create_corpus(oracle: Path) -> dict[str, object]:
    inputs = create_inputs()
    pair_blob = bytearray()
    input_hash = hashlib.sha256()
    output_hash = hashlib.sha256()

    for function_index, (name, values) in enumerate(inputs):
        for start in range(0, len(values), 180):
            chunk = values[start : start + 180]
            filter_source = "[" + ",".join(repr(value) for value in chunk) + f"]|map({name})"
            encoded = subprocess.run(
                [oracle, "-nc", filter_source], check=True, capture_output=True, text=True
            ).stdout
            outputs = json.loads(encoded)
            if len(outputs) != len(chunk):
                raise SystemExit(f"{name}: oracle returned the wrong result count")

            for input_value, output_value in zip(chunk, outputs, strict=True):
                if output_value is None or not math.isfinite(output_value):
                    raise SystemExit(f"{name}({input_value!r}) did not produce a finite oracle result")
                input_bits = bits(input_value)
                output_bits = bits(float(output_value))
                pair_blob += struct.pack("<QQ", input_bits, output_bits)
                input_hash.update(bytes([function_index]) + struct.pack("<Q", input_bits))
                output_hash.update(bytes([function_index]) + struct.pack("<Q", output_bits))

    raw = bytes(pair_blob)
    return {
        "counts": [len(values) for _, values in inputs],
        "total": sum(len(values) for _, values in inputs),
        "input_hash": input_hash.hexdigest(),
        "output_hash": output_hash.hexdigest(),
        "blob_hash": hashlib.sha256(raw).hexdigest(),
        "base64": base64.b64encode(gzip.compress(raw, compresslevel=9, mtime=0)).decode(),
    }


def checked_in_metadata(path: Path) -> dict[str, object]:
    source = path.read_text(encoding="utf-8")

    def constant(name: str) -> str:
        match = re.search(rf'{name} = (?:"([0-9a-f]+)"|(\d+));', source)
        if match is None:
            raise SystemExit(f"cannot find {name} in {path}")
        return match.group(1) or match.group(2)

    function_block = source.split("private static readonly FunctionCorpus[] Functions =", 1)[1]
    function_block = function_block.split("];", 1)[0]
    counts = [int(value) for value in re.findall(r'new\("[a-z0-9]+", (\d+),', function_block)]
    base64_block = source.split("private const string ExpectedGzipBase64 =", 1)[1]
    payload = "".join(re.findall(r'"([A-Za-z0-9+/=]+)"', base64_block))
    return {
        "counts": counts,
        "total": int(constant("ExpectedCaseCount")),
        "input_hash": constant("ExpectedInputSha256"),
        "output_hash": constant("ExpectedOutputSha256"),
        "blob_hash": constant("ExpectedPairBlobSha256"),
        "base64": payload,
    }


def main() -> int:
    args = parse_args()
    verify_oracle(args.oracle)
    generated = create_corpus(args.oracle)
    if args.emit_json:
        print(json.dumps(generated))
    if args.check:
        checked_in = checked_in_metadata(args.test_file)
        if generated != checked_in:
            differing = [key for key in generated if generated[key] != checked_in.get(key)]
            print("stale fdlibm elementary corpus: " + ", ".join(differing))
            return 1
        print(
            f"verified {generated['total']} exact fdlibm elementary cases "
            f"against {args.oracle} ({EXPECTED_ORACLE_SHA256})"
        )
    if not args.check and not args.emit_json:
        print(json.dumps({key: value for key, value in generated.items() if key != "base64"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
