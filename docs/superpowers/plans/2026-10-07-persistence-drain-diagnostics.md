# Persistence drain first-cause diagnostics

Coordinator approved bounded test-only preparation on 2026-10-07 for #1317. This is not runtime
repair, execution, stress, integration or release approval. Base 54a1f35842e1ec9c8af3ad87c7f058bf757da04f.

## Task 1: Prepare first-cause diagnostics and finite sentinel tests

Worktree /Users/antonio/KhaozEngine/.worktrees/wa-persistence-diagnostics,
branch fix/wa-persistence-diagnostics. Guard directory, branch and base before writing.
Read AGENTS.md, contributor test rules, systematic-debugging and TDD skills. User explicitly
authorizes preparation only, so tests are authored first but ALL execution waits for a later slot.
No baseline restore/build/test is permitted. Do not claim RED or GREEN without execution.

Scope is KhaozEngine.Foundation.Tests/Persistence/TrackedPersistenceTests.cs and narrowly named
test-only diagnostic helper/test files in that directory. No production file or package changes.
The failing scenario starts at line 199 on the base. The shared WriteGate is used by other tests,
so either preserve its existing behavior or use a scenario-local diagnostic wrapper. Keep unrelated
tests untouched. Avoid growing any file past its ratchet. Extract the coherent drain scenario to a
test helper if necessary, preserving the four public theory rows and their behavior.

Requirements:

1. Preserve the body's primary exception with ExceptionDispatchInfo and its original stack.
   Independently attempt producer, drain and dispose cleanup, recording every secondary error.
   A failed producer cleanup cannot skip drain cleanup. Dispose cannot replace the primary error.
   Body success plus cleanup failure must fail. No swallowed diagnostic failures or false success.
2. Add bounded monotonic in-memory trace for gate entry/release request/release observation/timeout,
   body phase, scheduling versus producer/drain entry, notification path role/type, drain predicate,
   task states and thread IDs. Use Stopwatch timestamps plus an ordering sequence. Pin finite capacity
   and make truncation visible. Emit once after cleanup, never synchronous output in hot callbacks.
   Catch-and-record callback failures at their origin, then rethrow. Do not attribute starvation.
3. Record all WriteFailed notifications but only the exact deliberately invalid child path satisfies
   the expected signal. An unexpected hold-path failure is explicit evidence, not a successful drain.
   Preserve the first failure while still releasing all gates and attempting every cleanup action.
4. Preserve runtime code, gates, Task.Run scheduling, all ten-second watchdogs, existing four theory
   combinations and behavioral assertions. Do not add a timeout extension, wait loop or retries.
5. Author immediate completed/faulted-task sentinel tests first. Cover primary body plus producer
   and dispose failures, drain cleanup still attempted after producer fails, original throw-site stack,
   body-success cleanup failure, all-success, exact notification discrimination, finite trace capacity
   and monotonic ordering. No sleep or deliberate watchdog expiry. Assert observable behavior and
   diagnostic evidence, not mock-call counts. Keep helper infrastructure narrowly test-owned.

Preparation completion is a reviewed source diff, not a passing claim. Run only git diff --check
and source inspection. Do not restore/build/test/format, invoke repository guards, commit, push,
pack, write feeds, mutate main, dispatch workflows or spawn helpers. Root handles the later hooks,
separate commit and approval requests after source review.

Return exact files and line references, retained behavior comparison, proposed finite filters and
expected counts based on authored cases. Name the production defect each existing theory still
detects. Include a step-by-step sentinel proof for masking and notification discrimination. Provide
a synchronous future command using KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj,
Release and an exact filter, but DO NOT RUN it. Any first-cause uncertainty stays explicit.

## Outcome

Preparation approved. Root inspected the complete source and counted 17 sentinel facts plus 15
retained cases, 32 total. Fresh source review 1 approved with M1 early-subscription and M2
late-notification caveat corrections. Root applied both. Fresh scoped review 2 approved the
corrections, with no regression found. Runtime, gates, Task.Run and watchdogs remain unchanged.

Exact finite verification request sent to coordinator: one Release Foundation project test with
filter `FullyQualifiedName~KhaozEngine.Tests.TrackedDrainDiagnosticsTests|FullyQualifiedName~KhaozEngine.Tests.TrackedPersistenceTests`,
expected 32 executed/passed and zero skips, including fresh dependency compilation. Then one
verify-format command scoped to the six changed source files, with no restore. Both must run
serially through slot-run.sh and stop at the first failure. Coordinator granted exactly these two commands. Both ran once and exited 0. TRX confirms 32 executed, 32 passed and zero skipped. The heavy slot was explicitly released before light guards and commit.
The six-file source hashes are retained in the SDD source-preverification.json record.

First-cause diagnosis, Windows proof and Catalog integration remain pending. Passing this finite
local proof will validate the diagnostics, not establish the original hosted failure's cause.

Proof record: proofs/2026-10-07-persistence-drain-diagnostics.json. All four drain rows recorded
no primary or cleanup failures, the intentional invalid-child notification, a true initial drain
predicate and a false cleanup predicate. The late-notification caveat remains in every report.
The local run compiled the affected project and dependencies without reported warnings. No full
solution suite or Windows run was performed, and no hosted first cause is inferred.
