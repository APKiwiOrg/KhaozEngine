# Approved performance units

Root commits each manifest here only after its own exact owner approval. No executable manifest ships
with the harness. Each run or rerun needs a separate approval, including the workflow run and attempt,
unit ID, exact manifest SHA-256, workload SHA, preset, runner, leg, limits and approval reference.

The entrypoint reads only `authorized/<KE_COVERAGE_PERF_UNIT>.json` from its build output. It never scans
for units or reads historical Cost authorizations. The file bytes and checked-out workload must match
the dispatch hashes before any native session opens. See
[the hosted dispatch contract](../../../docs/CROSS-PLATFORM.md#separately-approved-coverage-performance).
