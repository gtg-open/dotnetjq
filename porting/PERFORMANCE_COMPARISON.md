# CLI performance comparison

This report records the final publishable Linux x64 benchmark completed on
2026-09-06. It compares the same DotNetJq source as a framework-dependent .NET
10 application and a NativeAOT executable with the pinned, fully static native
jq 1.8.2 oracle.

Every reported command first passed exact exit-status, stdout-byte, and
stderr-byte comparison against native jq. The fixture run covers all 879
unchanged official cases; the sustained run covers all 21 declared macro
workloads. No timing was retained for a command that failed correctness.

## Result summary

| End-to-end CLI metric | Native jq | DotNetJq NativeAOT | DotNetJq framework |
|---|---:|---:|---:|
| No-input startup median, 30 runs | 1.996 ms | 9.728 ms | 120.972 ms |
| Pooled mean across all fixture samples | 1.099 ms | 6.816 ms | 83.138 ms |
| Sum of 879 per-scenario medians | 0.966 s | 6.017 s | 72.606 s |
| Fixture pooled-mean ratio to native | 1.00x | 6.20x | 75.63x |
| Sum of 20 processing-workload medians | 2.422 s | 8.407 s | 25.720 s |
| Processing equal-workload geometric-mean ratio | 1.00x | 5.82x | 19.36x |
| Processing synthetic aggregate throughput | 6.00 MiB/s | 1.73 MiB/s | 0.57 MiB/s |

NativeAOT was faster than framework .NET in every one of the 879 fixture
scenarios, the separate startup case, and all 21 macro scenarios. Native jq was
faster in every fixture/startup comparison. In the macro suite, native jq was
fastest in 20 of 21 workloads; NativeAOT completed `regex-test` in 1,200.046 ms
versus native jq's 1,354.040 ms, or 0.89x native latency.

For the small fixture programs, NativeAOT reduced the pooled mean from 83.138 ms
to 6.816 ms, a 12.20x improvement over framework .NET, while remaining 6.20x
slower than native jq. These cases start a new process, compile one filter,
evaluate it, serialize output, and shut down, so they are primarily a startup
and compilation comparison rather than an in-process library benchmark.

Across the 20 sustained processing workloads, NativeAOT reduced the sum of
medians from 25.720 s to 8.407 s, a 3.06x improvement over framework .NET. Its
equal-workload geometric-mean latency remained 5.82x native. The aggregate
throughput is a suite score over reused heterogeneous inputs, not the throughput
of one operation.

## Official fixture scenarios

Each unchanged fixture record became an independent compact-output CLI
invocation. The table reports pooled end-to-end means; each ratio uses the
native mean from the same fixture.

| Fixture | Cases | Native mean | NativeAOT mean | AOT/native | Framework mean | Framework/native |
|---|---:|---:|---:|---:|---:|---:|
| `jq.test` | 550 | 1.139 ms | 7.105 ms | 6.24x | 82.357 ms | 72.29x |
| `man.test` | 231 | 1.015 ms | 6.256 ms | 6.17x | 79.300 ms | 78.15x |
| `onig.test` | 47 | 1.125 ms | 6.685 ms | 5.94x | 104.982 ms | 93.35x |
| `manonig.test` | 19 | 1.078 ms | 6.580 ms | 6.10x | 102.181 ms | 94.78x |
| `base64.test` | 10 | 1.015 ms | 6.226 ms | 6.13x | 80.169 ms | 78.96x |
| `uri.test` | 20 | 0.984 ms | 6.215 ms | 6.32x | 81.139 ms | 82.45x |
| `optional.test` | 2 | 1.018 ms | 6.133 ms | 6.02x | 81.607 ms | 80.17x |
| **All** | **879** | **1.099 ms** | **6.816 ms** | **6.20x** | **83.138 ms** | **75.63x** |

The exact preflight covered 852 ordinary successful streams, three successful
empty streams, 19 expected compilation failures, and five expected runtime
failures. It compared the complete `(exit status, stdout bytes, stderr bytes)`
tuple for all three executables before warmup or timing. This serialized-CLI
gate is intentionally stricter than jq's official `jq_test.c` value comparison,
which correctly treats object member order as semantically irrelevant.

## Sustained processing scenarios

The deterministic scale-0.1 corpus contains 10,000 structured records, 25,000
numbers, 8,000 strings, 6,000 regex records, a 9,766-node tree, 15,000 NDJSON
records, 20,000 raw lines, and 5,000 streaming batches. Reused datasets account
for 14.54 MiB of cumulative processing input.

| ID | Workload | Input MiB | Native median | NativeAOT median | AOT/native | Framework median | Framework/native |
|---:|---|---:|---:|---:|---:|---:|---:|
| 00 | startup | 0.000 | 1.017 ms | 6.463 ms | 6.35x | 76.872 ms | 75.58x |
| 01 | parse-small-output | 1.296 | 20.628 ms | 94.346 ms | 4.57x | 261.431 ms | 12.67x |
| 02 | identity-parse-serialize | 1.296 | 31.844 ms | 116.092 ms | 3.65x | 348.028 ms | 10.93x |
| 03 | projection | 1.296 | 24.236 ms | 145.349 ms | 6.00x | 412.529 ms | 17.02x |
| 04 | map-arithmetic | 0.139 | 12.929 ms | 106.677 ms | 8.25x | 451.696 ms | 34.94x |
| 05 | select | 1.296 | 24.925 ms | 148.827 ms | 5.97x | 416.123 ms | 16.69x |
| 06 | sort | 0.139 | 12.273 ms | 113.055 ms | 9.21x | 341.158 ms | 27.80x |
| 07 | sort-by | 1.296 | 29.666 ms | 193.968 ms | 6.54x | 523.958 ms | 17.66x |
| 08 | group-by | 1.296 | 32.100 ms | 202.265 ms | 6.30x | 577.293 ms | 17.98x |
| 09 | unique | 0.139 | 12.577 ms | 114.578 ms | 9.11x | 350.488 ms | 27.87x |
| 10 | reduce | 1.296 | 27.722 ms | 138.252 ms | 4.99x | 374.892 ms | 13.52x |
| 11 | recurse | 0.249 | 9.109 ms | 60.248 ms | 6.61x | 267.734 ms | 29.39x |
| 12 | construct-update | 1.296 | 71.131 ms | 479.030 ms | 6.73x | 1,617.022 ms | 22.73x |
| 13 | strings | 0.328 | 392.514 ms | 2,678.586 ms | 6.82x | 6,045.796 ms | 15.40x |
| 14 | base64 | 0.328 | 7.856 ms | 38.362 ms | 4.88x | 219.729 ms | 27.97x |
| 15 | regex-test | 0.464 | 1,354.040 ms | 1,200.046 ms | 0.89x | 3,882.967 ms | 2.87x |
| 16 | regex-capture | 0.464 | 99.671 ms | 640.504 ms | 6.43x | 1,850.176 ms | 18.56x |
| 17 | regex-gsub | 0.464 | 168.899 ms | 1,269.631 ms | 7.52x | 4,801.840 ms | 28.43x |
| 18 | JSON slurp | 0.550 | 13.468 ms | 88.990 ms | 6.61x | 329.015 ms | 24.43x |
| 19 | raw-line input/output | 0.513 | 47.928 ms | 379.203 ms | 7.91x | 1,792.834 ms | 37.41x |
| 20 | streaming | 0.395 | 28.392 ms | 199.364 ms | 7.02x | 855.159 ms | 30.12x |

The strongest NativeAOT results relative to native jq were regex test (0.89x),
identity parse/serialize (3.65x), parse-small-output (4.57x), and base64 (4.88x).
The largest remaining median ratios were sort (9.21x), unique (9.11x), map
arithmetic (8.25x), raw-line I/O (7.91x), and regex substitution (7.52x). These
are optimization priorities, not correctness exceptions.

## Binaries and environment

| Deployment | Measured payload | Build configuration |
|---|---:|---|
| Framework DotNetJq | 1,325,391 bytes | `net10.0; CoreCLR; linux-x64; jq-1.8.2 compatible` |
| NativeAOT DotNetJq | 6,149,656 bytes | `net10.0; NativeAOT; linux-x64; jq-1.8.2 compatible` |
| Native jq | 2,267,912 bytes | Fully static, built-in Oniguruma, `-O2`, stripped |

The framework payload includes its app and managed dependency files but excludes
the shared .NET 10 runtime, so its size is not directly comparable with the two
self-contained executables. Its apphost alone is 78,256 bytes.

The benchmark used SDK 10.0.400 and runtime 10.0.11 on Ubuntu 26.04, kernel
7.0.0-30, with an AMD Ryzen 9 9950X. Every measured process was pinned to logical
CPU 14. The filesystem cache was warm, output was serialized to the null device
during timing, locale was C, and time zone was UTC. The host retained dynamic
frequency scaling, boost, and normal desktop background activity, so these are
comparative results for this host rather than universal latency guarantees.

Every scenario used one warmup and three timed repetitions. Fixture startup used
30 timed repetitions. Headline processing comparisons use per-scenario medians;
with only three samples, per-scenario p95 is deliberately reported as `n/a`.

## Evidence and reproduction

The certified fixture report has run ID
`2dd5db4c-a93a-4dd8-97af-4961f104a387`, 8,001 timed measurement rows, and
`results.json` SHA-256
`6b30983f9b0e0e1f9adf65dc1e6cf762188f297081e4ef79115b79b70c81674f`.
The certified macro report has run ID
`50d00ca6-3ff0-40f9-b730-bab170b098b1`, 189 timed measurement rows, and
`results.json` SHA-256
`c0961d0b1fff4a1da25e81f203915e1d1f8a253a27bfb940acf50c9bb022a7da`.
Both complete markers bind the exact report inventory, file sizes, hashes,
schemas, scenario counts, implementation counts, repetitions, source/build
attestations, and pinned oracle identity.

Run the complete fixture comparison:

```sh
env -u LD_PRELOAD taskset -c 14 tools/performance/run.sh \
  --native-executable artifacts/test-assets/jq-1.8.2/oracle/jq \
  --upstream upstream/jq \
  --repetitions 3 --warmups 1 --startup-repetitions 30
```

Run the complete sustained comparison against those exact builds:

```sh
env -u LD_PRELOAD taskset -c 14 python3 -B \
  tools/performance/macro_benchmark.py \
  --framework-executable artifacts/performance/framework/DotNetJq.Cli \
  --aot-executable artifacts/performance/native-aot/DotNetJq.Cli \
  --native-executable artifacts/test-assets/jq-1.8.2/oracle/jq \
  --upstream upstream/jq \
  --scale 0.1 --warmups 1 --repetitions 3 --regenerate --publishable
```

The generated raw and machine-readable reports are in
`artifacts/performance/results` and `artifacts/performance/macro-results`.
