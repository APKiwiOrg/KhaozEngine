# Approved performance units

Root commits each manifest here only after its own exact owner approval. No executable manifest ships
with the harness. Each run or rerun needs a separate approval, including the workflow run and attempt,
unit ID, exact manifest SHA-256, workload SHA, preset, runner, leg, limits and approval reference.

Each manifest arrives in its own authorization commit. That commit's only parent is the workload commit
the manifest names as `workloadSha`, and it adds only that one JSON file. `.gitattributes` keeps these
files byte for byte on every checkout.

The entrypoint reads only `authorized/<KE_COVERAGE_PERF_UNIT>.json` from its build output. It never scans
for units or reads historical Cost authorizations. The file bytes, the manifest's workload and the
single-parent authorization commit must match the dispatch before any native session opens. See
[the hosted dispatch contract](../../../docs/CROSS-PLATFORM.md#separately-approved-coverage-performance).
