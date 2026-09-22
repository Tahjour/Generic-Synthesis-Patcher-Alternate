# Merge/HPU optimization validation

## Behavior

`ForwardOptions.Merge` now accepts non-collection-crossing `[Flags]` enum leaves.
It ORs selected source values into the **current patched winner**, preserving all
existing bits. It does not apply ancestry-based removals. Ordinary `Merge.Flags`
and selected-source list merging retain their existing semantics.

Source indexes are run-scoped metadata. Override contexts and HPU topology are
scoped to one root `ProcessingKeys`, shared by its group children. First discovery
of a patch absent from the snapshot advances the topology generation. Contexts
that already contain the mutable patch remain live. Field values, equality,
HPU histories/results, and ordinary Merge working graphs are not cached.

Deterministic source lists retain declaration order and duplicates, with indexed
membership and first-occurrence ranks. Whole-record `All` shares the run indexes
but always excludes the output patch. Random retains deferred source enumeration
and its existing number consumption. Cross-record Copy lookups remain separate.

## Automated validation

The original 642-test suite passed before production edits. The expanded suite
contains 678 tests, passing in Debug and Release, targeting .NET 9. Builds use the
installed SDK 10.0.302 and tests run on .NET 9. Existing assembly-version,
architecture, and test-analyzer warnings remain; no dependency changes were made.
Generated schemas and field documentation are checked by the existing generator
tests. The new example is schema-validated.

Focused coverage includes all eight signed/unsigned enum widths, unnamed bits,
null/zero contributions, exclusions/missing/disabled sources, repeated no-ops,
prior patch edits, `OnlyIfDefault`, unsupported combinations, grouped processing,
seeded patches (including undiscovered and master-only seed overrides), topology invalidation, source-set reuse, dynamic-plugin topology,
reinitialized runs with reversed load orders, and Random enumeration/number
consumption. HPU field histories are recomputed after patch-value reversions while
topology is reused. The Worldspace JSON is preserved as an embedded regression
fixture; it preflights and its Worldspace/EncounterZone group runs against binary
overlay sources. Its content matches the supplied file, apart from line-ending/
final-newline normalization; the production file is untouched. Existing nullable-link, WorldModel, deep Quest, list, and `All`
regressions remain in the full suite.

## Synthetic Release benchmark

`OptimizationTests.CellWorkloadBaselineAndBenchmark`, with
`GSP_BENCHMARK_PLUGINS=4000`, builds 4,000 enabled synthetic plugins and 16 Cells.
The origin and five overrides contain those Cells. Each record processes two
ordinary Merge fields (`Flags`, `Regions`) and nine HPU fields matching the
supplied Cell rule. Many HPU fields are null; this deliberately isolates history,
source selection, and topology overhead rather than measuring large-list merging.

Setup, binary writing/loading, preflight, and output normalization are outside
the timed section. Measurements use `Stopwatch` and
`GC.GetAllocatedBytesForCurrentThread`; allocations are not peak process memory.
Each invocation has one warm-up followed by five measured samples, clearing the
output and rebuilding rules for every sample. There are no timing thresholds in CI.

| Sample | Baseline ms | Optimized ms | Baseline allocated bytes | Optimized allocated bytes |
|---|---:|---:|---:|---:|
| 1 | 521.794 | 10.557 | 569,077,656 | 7,397,816 |
| 2 | 541.705 | 23.568 | 569,079,616 | 7,412,432 |
| 3 | 523.456 | 9.296 | 569,074,848 | 7,397,816 |
| 4 | 522.128 | 9.128 | 569,081,632 | 7,397,816 |
| 5 | 531.082 | 21.491 | 569,075,920 | 7,420,176 |

Median loop time: 523.456 ms before, 10.557 ms after. Allocation reduction is
approximately 98.7% in this workload. These are local synthetic measurements,
not an estimate of total Synthesis startup, loading, writing, or real-mod speedup.

Every baseline and optimized sample creates the same 16 overrides and 16 update
entries. The SHA-256 of canonical record data (including FormKeys) plus ordered
field/update counts is pinned in the regression test:

`B47836E643CA969B0287A1C4482FD87A58DD6BC143BAA34B91C430A9EF59D816`

Internal counters assert 32 history resolutions (initial plus refresh per Cell),
16 HPU topology builds, and 16 end-node calculations for all 144 HPU actions.
Separate tests compare cached end-node results with freshly built topology and
verify identical source-set reuse. Baseline operation counters were not recorded;
the before/after comparison uses measured timing, allocations, and output hashes.

Raw logs are retained locally under `artifacts/optimization-*.log` (git-ignored).

## Real load order

An isolated MO2 run uses the supplied config unchanged, copied into
`artifacts/optimization-probe/configs`, with output directed only to
`artifacts/optimization-probe/GSP-Optimization-Test.esp`. No production config or
patch is replaced. MO2 successfully initialized the patcher and loaded all five
groups/six child rules. The probe is still running at handoff (about 13 minutes,
roughly 11 GB resident memory), with no patch output or completed comparison yet.
The user has been asked whether to continue or stop this resource-heavy probe.
Full real-load-order validation remains outstanding; synthetic tests are not a
substitute. The process log is `artifacts/optimization-probe/run.log`.

The final Release tests were built into
`GSPTestProject/bin/OptimizationRelease/net9.0-windows` to avoid replacing binaries
in use by the ongoing probe. The probe predates the final seed-override edge-case
fixes; even a successful probe would not by itself validate those final changes.
