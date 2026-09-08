#!/usr/bin/env python3
"""Generate deterministic, scalable datasets for the dotnetjq macro benchmarks."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import sys
import tempfile
from decimal import Decimal, InvalidOperation, ROUND_FLOOR
from typing import Callable, TextIO


SCHEMA_VERSION = 1
BASE_COUNTS = {
    "records": 100_000,
    "numbers": 250_000,
    "strings": 80_000,
    "regex": 60_000,
    "tree": 97_656,
    "ndjson": 150_000,
    "raw": 200_000,
    "stream": 50_000,
}
PATHS = {
    "records": "records.json",
    "numbers": "numbers.json",
    "strings": "strings.json",
    "regex": "regex.json",
    "tree": "tree.json",
    "ndjson": "records.ndjson",
    "raw": "raw.txt",
    "stream": "stream.json",
}


def parse_scale(value: str) -> Decimal:
    try:
        scale = Decimal(value)
    except InvalidOperation as error:
        raise argparse.ArgumentTypeError(f"invalid decimal scale: {value!r}") from error
    if not scale.is_finite() or scale <= 0:
        raise argparse.ArgumentTypeError("scale must be a positive finite decimal")
    return scale


def canonical_decimal(value: Decimal) -> str:
    rendered = format(value.normalize(), "f")
    return "0" if rendered == "-0" else rendered


def scaled_count(base: int, scale: Decimal) -> int:
    # Round positive values to the nearest integer with exact halves rounded up.
    return max(1, int((Decimal(base) * scale + Decimal("0.5")).to_integral_value(
        rounding=ROUND_FLOOR
    )))


ENCODER = json.JSONEncoder(
    ensure_ascii=False,
    allow_nan=False,
    separators=(",", ":"),
)


def write_value(destination: TextIO, value: object) -> None:
    for fragment in ENCODER.iterencode(value):
        destination.write(fragment)


def write_array(
    destination: TextIO,
    count: int,
    value_at: Callable[[int], object],
) -> None:
    destination.write("[")
    for index in range(count):
        if index:
            destination.write(",")
        write_value(destination, value_at(index))
    destination.write("]")


def write_records(destination: TextIO, count: int) -> None:
    destination.write('{"items":')

    def record(index: int) -> dict[str, object]:
        return {
            "id": index,
            "score": (index * 7_919 + 104_729) % 100_000,
            "category": f"c{(index * 37) % 257:03d}",
            "value": (index * 97) % 1_000,
            "active": index % 3 != 0,
            "owner": {"name": f"user-{index % 10_000:05d}"},
            "payload": {"metrics": {"score": (index * 13) % 1_000}},
        }

    write_array(destination, count, record)
    destination.write("}\n")


def write_numbers(destination: TextIO, count: int) -> None:
    destination.write('{"numbers":')
    write_array(destination, count, lambda index: (index * 48_271 + 17) % 65_521)
    destination.write("}\n")


def write_strings(destination: TextIO, count: int) -> None:
    destination.write('{"strings":')
    write_array(
        destination,
        count,
        lambda index: (
            f"Row {index:06d} — Café\tTAG_{index % 997:03d} / payload!"
        ),
    )
    destination.write("}\n")


def write_regex(destination: TextIO, count: int) -> None:
    def test_string(index: int) -> str:
        if index % 4 == 0:
            return f"invalid value {index:06d}"
        prefix, domain = ("usr", "domain.com") if index % 2 == 0 else ("svc", "example.net")
        suffix = "a" if prefix == "usr" else "z"
        return f"{prefix}-{index:06d}_{suffix}@{domain}"

    roles = ("user", "service", "admin")
    zones = ("EU", "US", "AP")
    destination.write('{"testStrings":')
    write_array(destination, count, test_string)
    destination.write(',"captureStrings":')
    write_array(
        destination,
        count,
        lambda index: f"{roles[index % 3]}-{index:06d}-{zones[index % 3]}",
    )
    destination.write(',"replaceStrings":')
    write_array(
        destination,
        count,
        lambda index: (
            f"alpha{index:06d} beta{(index * 7) % 1_000_000:06d} "
            f"gamma{(index * 13) % 1_000_000:06d}"
        ),
    )
    destination.write("}\n")


def write_tree(destination: TextIO, count: int) -> None:
    def node(index: int) -> None:
        destination.write(f'{{"value":{(index * 17) % 101},"children":[')
        first_child = index * 5 + 1
        for child in range(first_child, min(first_child + 5, count)):
            if child != first_child:
                destination.write(",")
            node(child)
        destination.write("]}")

    node(0)
    destination.write("\n")


def write_ndjson(destination: TextIO, count: int) -> None:
    for index in range(count):
        write_value(
            destination,
            {
                "id": index,
                "value": (index * 97) % 1_000,
                "active": index % 3 != 0,
            },
        )
        destination.write("\n")


def write_raw(destination: TextIO, count: int) -> None:
    for index in range(count):
        disposition = "keep" if index % 3 == 0 else "drop"
        destination.write(
            f"{disposition}:user_{index:06d} value={(index * 97) % 1_000}\n"
        )


def write_stream(destination: TextIO, count: int) -> None:
    destination.write('{"batches":')

    def batch(index: int) -> dict[str, object]:
        return {
            "batch": index % 100,
            "values": [index, (index * 7_919) % 100_000, (index * 97) % 1_000],
            "meta": {
                "active": index % 3 != 0,
                "name": f"item-{index:06d}",
            },
        }

    write_array(destination, count, batch)
    destination.write("}\n")


WRITERS: dict[str, Callable[[TextIO, int], None]] = {
    "records": write_records,
    "numbers": write_numbers,
    "strings": write_strings,
    "regex": write_regex,
    "tree": write_tree,
    "ndjson": write_ndjson,
    "raw": write_raw,
    "stream": write_stream,
}


def write_atomic(path: pathlib.Path, writer: Callable[[TextIO], None]) -> None:
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = pathlib.Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as destination:
            writer(destination)
        os.replace(temporary, path)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def generate(output: pathlib.Path, scale: Decimal) -> pathlib.Path:
    output.mkdir(parents=True, exist_ok=True)
    metadata: dict[str, dict[str, object]] = {}
    for dataset, base_count in BASE_COUNTS.items():
        count = scaled_count(base_count, scale)
        path = output / PATHS[dataset]
        writer = WRITERS[dataset]
        write_atomic(path, lambda destination, w=writer, n=count: w(destination, n))
        metadata[dataset] = {
            "path": path.name,
            "count": count,
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        print(
            f"{dataset}: {count} records, {metadata[dataset]['bytes']} bytes, "
            f"sha256={metadata[dataset]['sha256']}",
            flush=True,
        )

    manifest_path = output / "dataset-sha256.json"
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "scale": canonical_decimal(scale),
        "datasets": metadata,
    }
    write_atomic(
        manifest_path,
        lambda destination: (
            write_value(destination, manifest),
            destination.write("\n"),
        ),
    )
    print(f"manifest: {manifest_path}")
    return manifest_path


def parse_arguments(arguments: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        type=pathlib.Path,
        required=True,
        help="directory in which to create the eight datasets and digest manifest",
    )
    parser.add_argument(
        "--scale",
        type=parse_scale,
        default=Decimal("1"),
        help="positive decimal cardinality multiplier (default: 1)",
    )
    return parser.parse_args(arguments)


def main(arguments: list[str]) -> int:
    options = parse_arguments(arguments)
    generate(options.output.expanduser().resolve(), options.scale)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
