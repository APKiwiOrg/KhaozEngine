# Scoping — Jobs 0: Server-tick benchmark harness

**Status:** TO-DO (first job-system sub-project; unblocks 1–3 — it's the measurement every later layer is justified
against). **Fresh-chat kickoff:** *"Execute `docs/superpowers/scoping/jobs-0-benchmark-harness.md`."*

## Read first
- `docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md` (the program map + the big-O rationale: the work
  is `O(S·N)` on one core today; this harness measures it).
- `CLAUDE.md` (worktree, TDD, release ritual, doc sweep) + `JOBS-EXECUTION-ORDER.md`.

## Goal
A headless, repeatable benchmark of **one server tick** across a matrix of (cells `C`, entities/cell `E`, systems `S`),
reporting wall-clock and entities/sec for the **single-threaded baseline**. This is the number every later layer must
move; without it we'd be optimizing blind (the program design + the MMO spec both say "decide with a benchmark").

## Deliverable
- A benchmark project (no shipped package; an exe or a `LiveSocket`-style trait so CI excludes it — see `ci.yml` for the
  `Category!=LiveSocket` filter pattern). Stands up a `ShardHost` with a configurable grid + entity population + a
  configurable number of trivial representative systems (e.g. an integrate-position system over a `Position`-like
  component), warms up, then times N ticks and reports per-tick wall-clock + entities/sec.
- Parameterized over (C, E, S) so a later layer can compare. Deterministic population (seeded `DeterministicRng`).
- A short README / output format documenting how to run it and read the numbers.

## Acceptance (headless)
- `dotnet run` (or the traited test) produces stable per-tick timings for a given (C, E, S) on the single-threaded path.
- Re-running the same config yields consistent numbers (seeded population; war-up excluded from timing).
- Covers at least: many small cells (large C, small E), one hot cell (C=1, large E), and a mid case — the three regimes
  the program's big-O table distinguishes.

## Conventions
Worktree `feature/jobs-0-benchmark`. Headless. Not a shipped package (`IsPackable=false`); keep it out of CI's timed
path (trait it `LiveSocket` or equivalent, or make it a manual exe). Doc sweep only if it adds public API (it shouldn't).
Delete this doc when merged; tick the status table.
