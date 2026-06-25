# Scoping — MMO 3E: Dedicated-server template + ICellLink network seam

**Status:** TO-DO (Phase 3 capstone). **Depends on:** 3A–3D. **Fresh-chat kickoff:**
*"Execute `docs/superpowers/scoping/mmo-3e-server-template-celllink.md`."*

## Read first
Phase 3 design spec (whole), the program map spec, `CLAUDE.md`, `MMO-EXECUTION-ORDER.md` (policy B).

## Goal
A runnable **reference dedicated server** that wires the whole stack together, plus making the `ICellLink` seam
explicitly network-ready (in-process impl shipped; a network impl is infra, represented as a clean interface /
stub so a real deployment can drop one in).

## Deliverable
- A reference headless server sample (a project on the `KhaozEngine.Server` umbrella, e.g. under
  `samples/` or a `MmoServerSample` project — match the repo's existing sample layout) that stands up: a
  `ShardHost` (multi-cell), the `NetServer` session layer over a real `INetTransport` (LiteNetLib), per-client
  home-cell serving (3D), `WorldStore` persistence, all on `FixedTickHost`. `dotnet run` boots it; a thin client
  connects and moves across a cell boundary.
- `ICellLink` finalized as the inter-cell seam with the in-process impl as the shipped default and a documented
  network-impl contract (the infra extension point). No actual network impl here (that's infra).
- End-to-end headless test: a two-cell `ShardHost` + two simulated clients over `LoopbackTransport`, a player
  crossing a boundary, asserting continuity + single-ownership end to end.

## Acceptance
- The sample boots a multi-cell authoritative server a client connects to and plays against (manual smoke OK for
  the live-socket path; keep it `Category=LiveSocket` so CI excludes it — see `ci.yml`).
- The headless end-to-end test (loopback) passes: connect → join → move across a boundary → re-bind → consistent
  world view, single ownership throughout.

## Conventions
Worktree `feature/mmo-3e-server-template`. TDD headless (live-socket smoke traited `LiveSocket`). This is the
natural point to **publish the Phase 3 batch** (3A–3E) — at that point do the single version bump +
CHANGELOG/CHANGENOTES + full doc sweep + pack + tag, and **confirm the push with the user** (policy B). Delete
this doc (and any remaining 3x docs) when the batch ships; tick the status table.
