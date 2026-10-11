# Explicit motion profiles and generic proof

The current profile identifies `contact-shell-footprint-swim-v1`. The
[phase 4b handoff](CONTACT-CONTROLLER-PHASE-4B-SWIMMING-HANDOFF-2026-10-11.md) implements the
[2026-10-09 owner amendment](SWIM-STEP-SUPPORT-DEPENDENCY-2026-10-09.md) with #438 shell solids,
certified footprint support and swimming's full-body medium proofs. Runtime and probe use the
same kernel. Earlier `f3-full-capsule-v1` fingerprints cannot certify the new behavior.
The profile remains independent of local query leases and frame epochs.

## Identity

The fixed contact recipe includes footprint radius fraction 0.5, vertical placement offset
0.001 m, handoff recipe version 1 and route-proof recipe version 1.

The encoding reuses the existing canonical MoveTuning field writer, then includes the actual walk
speed, run speed and air-momentum setting that the legacy unit probe omits. It also includes water
capability, independent launch speed, contact/surface tolerances, solver/projection versions and
limits, area masks, exact probe cadence/budget, boundary identity and semantic source digests.
Sources are copied and sorted ordinally. The engine version is normalized as in existing bake identity.

The caller supplies query/clearance recipe, shore-rule and actual route-cost dependencies. These are
identity inputs, not new cost knobs. Native geometry payloads, area admission and complete search
scope remain separate producer responsibilities. Changing an input cannot silently reuse a profile.

## Runtime and local proof

GroundMoveContext.StepExplicit and ProbeExplicitMotion call the same explicit kernel at the same
cadence with the same configured tuning. They require the exact physics query view captured by the
lease, not merely a view of an equal or identical source world. A boundary fingerprint must also match.
The caller retains the lease until it records the pure result.

The probe is bounded and proves a directed motion edge only. Proven carries an endpoint. Blocked
means that local attempt was obstructed. Unresolved, Invalid, FrameMismatch and BudgetExceeded remain
distinct, and none carries a usable prefix. No local result alone means globally unreachable.
No legacy height/medium sampling or deepest-only swimming clearance is used by this opt-in path.

## Evidence and limits

Generic fixtures prove dry/surface parity, a live wall, view and boundary mismatch, failed late proof,
finite budget handling, every tuning input, semantic digest snapshot/order and equivalent motion
across two adjacent synthetic physics frames. The tests link the existing bounded analytic environment
fixtures rather than creating a native sampler. Native tiled navigation, real seams, area coverage,
engine release and game adoption remain separate gates. The unchanged short-step regression passes.
Adoption remains blocked by #1355 certified sweep shapes, #1334 cost, #1347 hidden support and #1344
small bodies. Phase 5 still owns the public selector and navigation bake identity.
