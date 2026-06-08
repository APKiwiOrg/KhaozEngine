# KhaozEngine.Ecs — seed-stable iteration order (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, additive → version **1.5.0** (independent of the shared engine version).

## Goal

Make entity/component iteration **reproducible**: an identical sequence of world operations yields an
identical iteration order, every run and across clients. This is the foundation for deterministic
(lockstep) simulation and for Cycle B (the deterministic outcome/event buffer). It is a benign default
for non-deterministic games.

This is **Cycle A** of the determinism work requested for SpaceGame. **Cycle B** (deterministic
outcome/event buffer + RNG-draw timing) follows separately. Note SpaceGame still runs its own
order-preserving entity lists and seeded `System.Random`; this cycle makes the *engine* ECS reproducible
for its own native use and for SpaceGame's eventual Phase-4 adoption — it does not change SpaceGame today.

## The gap

Iteration has two layers:

- **Cross-archetype:** queries walk `Dictionary<ArchetypeSignature, Archetype>.Values`, whose
  enumeration order is not contractually stable across processes or after rehashing. **This is the one
  real source of non-determinism.**
- **Within-archetype:** entities are a dense `Entity[]`. Removal uses **swap-remove** (O(1)). Given an
  identical operation sequence this is already reproducible (same swaps ⇒ same layout) — it is *not*
  insertion-ordered, but reproducible is sufficient for lockstep and for cross-client hashing. We keep
  swap-remove (decision: no perf regression; SpaceGame keeps its own ordered lists regardless).

## Design

- `World` maintains `private readonly List<Archetype> _archetypeOrder = new();`. Every time an archetype
  is created (in `GetOrCreateArchetype`, including the initial empty archetype), it is appended.
  Archetype creation order is reproducible because identical operations make new component-sets appear
  in the same order.
- Every place that walks archetypes iterates `_archetypeOrder` instead of `Archetypes.Values`:
  - `Query.Refresh` (the matched-archetype scan)
  - `World.ForEach` overloads (arities 1-8)
  - `World.SaveArchetypes` (so `WorldSerializer.Save` output is byte-stable too)
- The `Archetypes` dictionary stays for O(1) signature lookup in `GetOrCreateArchetype`.

No public API changes. The only observable difference is that iteration order becomes deterministic
(creation-ordered across archetypes; reproducible swap-remove order within each).

## Reproducibility guarantee (documented contract)

> For a `World` built by a given sequence of `Spawn`/`Set`/`Add`/`Remove`/`Despawn` operations,
> `Query`/`ForEach` visit entities in an order that depends only on that sequence — identical run-to-run
> and across processes. Order is creation-ordered across archetypes and swap-remove-stable within an
> archetype. It is reproducible, not insertion-ordered.

## Out of scope

- Change-detection set enumeration (`Added`/`Changed`/`Removed` currently iterate a `HashSet`) — a
  separate small follow-up only if a deterministic sim iterates those.
- Order-preserving (insertion-ordered) within-archetype removal — rejected for now (perf + diff size);
  reproducible swap-remove is sufficient.
- Zero-alloc query enumerators (a perf concern, not correctness) — separate cycle if ever wanted.
- The outcome/event buffer + RNG-draw timing — Cycle B.

## Testing (headless)

- **Reproducibility:** a helper applies an identical scripted sequence of spawns/sets/despawns across
  several archetypes to two fresh `World`s; assert `Query().Entities()` (and a multi-component query)
  yield the **same entity sequence** element-for-element in both.
- **Creation order:** spawn entities so archetypes are created in a known order; assert cross-archetype
  iteration follows that creation order.
- **Swap-remove stability:** after despawns, assert the within-archetype order matches the expected
  swap-remove result and is identical across two identically-scripted worlds.
- **No regression:** existing `Query`/`ForEach` tests still visit every matching entity; full suite green.
- **Save determinism (bonus):** two identically-built worlds produce byte-identical `Save` JSON.

## Packaging

Additive → bump `KhaozEngine.Ecs` to `1.5.0`, changelog entry, pack to the local feed cumulatively;
tag `ecs-v1.5.0` and push from `main` after the branch merges (CI publishes).
