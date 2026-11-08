# Catalog tests

This non-packable project checks the catalog, authoring pipeline and SQLite backend. SQL Server
coverage lives in `KhaozEngine.Server.Tests`.

## Upgrade harness refusals

These types and enums are internal to this test project. They describe harness failures rather than
public engine refusals. Existing `TimeoutException` and `InvalidOperationException` catches still match.

`CatalogPhaseTimeoutException` carries `CatalogPhaseRefusal` and the closed `CatalogPhase` identity.
Its `Budget` is the inactivity duration, currently 20 seconds. `PublishStep` identifies the publish
interruption point when `Phase` is `Publish`. `IsRunnerProgress` distinguishes the interval after an
observed operation returns from the operation itself. Diagnostic text retains the existing phase names.

| Reason | Refused condition | Identifiers |
|---|---|---|
| `PhaseStalled` | The producer clock reaches its inactivity budget | `Phase`, `Budget`, applicable `PublishStep` and `IsRunnerProgress` |
| `SafetyPhaseStalled` | The independent safety clock reaches its inactivity budget | `Phase`, `Budget`, applicable `PublishStep` and `IsRunnerProgress` |

`CatalogScriptException` carries `CatalogScriptRefusal`. Freeze indexes are zero-based, and the
observation that first exceeds the bound has `ActualReads` equal to `MaximumReads + 1`.

| Reason | Refused condition | Identifiers |
|---|---|---|
| `ObservationBoundExceeded` | A draft read exceeds the finite script's observation bound | `MaximumReads`, `ActualReads` |
| `FreezeCountExceeded` | A freeze requests a slot beyond the script's last slot | `ActualIndex`, `MaximumCount` |
| `FreezeBaseVersionMismatch` | A freeze supplies the wrong base for its current slot | `ActualIndex`, `ExpectedBaseVersion`, `ActualBaseVersion` |
| `RunnerCompletedBeforePhase` | A producer ends before signalling its requested readiness gate | `Phase` |

`ReportLines` is an owned read-only snapshot for diagnosing an ended run. Its prose is not an equality
contract. A trusted-band validator fault is checked by retaining the injected exception and asserting
that validation propagates that same object.

Phase refusal tests advance `ManualCatalogPhaseClock` explicitly. They never wait for a real timeout
to prove a stall. Successful progress and completed-readiness controls distinguish real progress from a
missing gate. A refused freeze compares the draft's metadata, edit values and text state before the
repaired control, so forwarding the refused write would fail the comparison.
