# Benchmarks

The historical workstation report was measured on 2026-09-06 and compares the same
source as a framework-dependent .NET 10 CLI and a Linux x64 NativeAOT CLI with
the pinned fully static jq 1.8.2 oracle.

| End-to-end CLI metric | jq 1.8.2 | NativeAOT | Framework .NET |
|---|---:|---:|---:|
| No-input startup median, 30 runs | 1.996 ms | 9.728 ms | 120.972 ms |
| Pooled mean across 879 fixture scenarios | 1.099 ms | 6.816 ms | 83.138 ms |
| Sum of 20 processing-workload medians | 2.422 s | 8.407 s | 25.720 s |
| Processing equal-workload geometric-mean ratio | 1.00x | 5.82x | 19.36x |
| Processing synthetic aggregate throughput | 6.00 MiB/s | 1.73 MiB/s | 0.57 MiB/s |

Every command first passed exact exit-status, stdout-byte, and stderr-byte
comparison against native jq. The fixture suite contains 879 separate CLI
processes; the sustained suite contains 21 deterministic workloads over 14.54
MiB of cumulative input. NativeAOT was faster than framework .NET in every
scenario. Native jq won every fixture/startup case and 20 of 21 sustained cases;
NativeAOT won the sustained `regex-test` workload on this host.

These numbers measure whole processes—startup, filter compilation, execution,
serialization, and shutdown—not in-process library calls. They were captured on
an AMD Ryzen 9 9950X running Ubuntu 26.04 with one pinned logical CPU, warm
filesystem cache, ordinary boost/frequency scaling, one warmup, and three timed
repetitions. They are comparative evidence for that host, not universal latency
or throughput guarantees.

The [full performance report](../porting/PERFORMANCE_COMPARISON.md) contains all
fixture and workload rows, binary sizes, environment details, run IDs, hashes,
and reproduction commands. The [harness guide](../tools/performance/README.md)
documents correctness-only and timed modes. PR and release CI repeat all 879
fixture scenarios and all 21 sustained workloads with five interleaved subjects:
candidate and baseline builds in framework/AOT forms, plus native jq. Startup
uses 300 samples per subject. The baseline is the reviewed initial public source
commit, freshly rebuilt on the **same runner**, not the workstation timings above.

Publication stops when a candidate's fixture mean, startup median, or
processing-only geometric mean exceeds 1.30 times its corresponding same-runner
DotNetJq baseline. This is a material-regression allowance, not a universal speed
guarantee. Native jq comparisons remain visible for both versions. The policy,
pinned baseline commit, source/binary proofs and report checks are documented in
the [harness guide](../tools/performance/README.md#same-runner-regression-policy)
and [`release-thresholds.json`](../tools/performance/release-thresholds.json).
