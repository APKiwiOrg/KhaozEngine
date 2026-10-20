# Approved performance units

Root commits each manifest here only after its own exact owner approval. No executable manifest ships
with the harness. A manifest binds only approval-time facts: unit ID, exact manifest SHA-256, workload
SHA, baseline main SHA, preset, runner, workflow, leg, limits, approver and approval reference. It
authorizes one dispatch. The run does not exist when the manifest is committed, so the report names it
by workflow SHA, run ID, attempt and process ID. A GitHub re-run attempt is refused. Any further run
needs a new approval and a new unit.

Each manifest arrives in its own authorization commit. That commit's only parent is the workload commit
the manifest names as `workloadSha`, and it adds only that one JSON file. `.gitattributes` keeps these
files byte for byte on every checkout.

The entrypoint reads only `authorized/<KE_COVERAGE_PERF_UNIT>.json` from its build output. It never scans
for units or reads historical Cost authorizations. The file bytes, the manifest's workload and the
single-parent authorization commit must match the dispatch before any native session opens. See
[the hosted dispatch contract](../../../docs/CROSS-PLATFORM.md#separately-approved-coverage-performance).
