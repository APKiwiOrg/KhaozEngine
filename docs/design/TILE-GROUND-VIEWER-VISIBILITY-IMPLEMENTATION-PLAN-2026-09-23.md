# Ground Item Viewer Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a tile-world server omit a ground item from selected viewers' snapshots without changing public ground items.

**Architecture:** An optional server-config predicate receives the viewer slot and ground net ID. The tile server applies it to its per-viewer interest set before the existing snapshot and delta projector. Games retain ownership and pickup rules.

**Tech Stack:** .NET 10, KhaozEngine.TileWorld.Netcode, xUnit loopback transport.

**Spec:** `docs/design/TILE-GROUND-VIEWER-VISIBILITY-2026-09-23.md`

## Global Constraints

- Work in `/Users/antonio/KhaozEngine/.worktrees/private-ground-visibility` on `feature/private-ground-visibility`.
- `GroundItemVisibleToSlot == null` preserves every existing snapshot byte and interest result.
- Filtering is server-only, keyed by authenticated slot and ground net ID, and excludes the entire entity.
- Do not encode game accounts, item types, or pickup permission into the engine.
- Ride the currently staged engine version only if it remains untagged at integration. Recheck `main` and tags before release.
- Run the Release solution build, non-live-socket test suite, pack script, file-size and documentation guards.

## Review Focus

1. Two viewers in one plane and area of interest see different ground-item sets according to the predicate.
2. A policy transition from visible to hidden removes an item from a prior viewer's client mirror, and the reverse adds it.
3. An item on another plane remains hidden even if the callback returns true.
4. A public item is still visible to both seats when the callback permits it, and a null callback preserves legacy behavior.
5. The callback does not filter actors, players, or object states sharing the same interest set.

---

### Task 1: Filter ground entities at the tile-server interest boundary

**Files:** Modify `KhaozEngine.TileWorld.Netcode/TileWorldServerConfig.cs` and `KhaozEngine.TileWorld.Netcode/TileWorldServer.Tick.cs`. Add `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileGroundItemVisibilityTests.cs`. Update `KhaozEngine.TileWorld.Netcode/README.md` and `docs/USING-KHAOZENGINE.md` with the public API and its pickup-gate responsibility.

**Interfaces:** `TileWorldServerConfig.GroundItemVisibleToSlot` is `Func<int, long, bool>?`. The predicate receives `(viewerSlot, groundNetId)` and is called only for ground entities inside that viewer's ordinary interest set.

- [ ] **Step 1: Write a two-client loopback test.** Spawn one private and one public ground item beside both clients. Set `GroundItemVisibleToSlot = (slot, id) => id != privateId || slot == ownerSlot`. Assert the owner mirror holds both and the other mirror holds only the public item after real server ticks and client polls. Also assert `ServeInterest` agrees.
- [ ] **Step 2: Write transition and safety tests.** Toggle a captured allow flag while both clients stay connected. Assert the normal delta removes and later restores the private item for the other seat. Assert an upstairs drop stays hidden even when the predicate returns true, and an actor remains visible when its net ID is denied by a predicate that must only see ground items.
- [ ] **Step 3: Run the new test class RED.** `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileGroundItemVisibilityTests`. Expected: compile failure because the config member is absent.
- [ ] **Step 4: Add the optional config member and filter.** In `HomeInterestFor`, keep the existing plane and footprint passes. Remove a ground net ID only when the callback is non-null and returns false for the current slot. Use the existing server-owned ground entity lookup and avoid a new world scan. Keep the default path a no-op.
- [ ] **Step 5: Run GREEN and adjacent tests.** Run the new class plus `TileGroundItemsTests`, `TileFootprintInterestTests`, and `TileInterestSlotsTests`. Add a non-vacuity assertion that the second client is connected and sees the public item before checking the private item's absence.
- [ ] **Step 6: Update package and consumer docs, then commit.** Document that filtering replication never authorizes pickup and that the callback is synchronous on the simulation tick. Commit with `netcode(tileworld): filter ground items per viewer`.

### Task 2: Verify, integrate, and release for the waiting consumer

**Files:** Extend the current staged `CHANGELOG.md` entry and any guarded version examples if the engine version moves. No other runtime interface.

- [ ] **Step 1: Fetch and merge current `origin/main` into the task branch.** Recheck the latest tag and `<KhaozEngineVersion>`. If the staged version became tagged, move this change into the next free version with matching changelog and guarded docs.
- [ ] **Step 2: Run required gates from the worktree root.** `mkdir -p local-feed`, `dotnet build KhaozEngine.slnx -c Release`, `dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, `scripts/pack-local-feed.sh`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, and `bash scripts/check-doc-versions.sh`.
- [ ] **Step 3: Review the final diff, fast-forward `main`, and push it.** Rebuild the same package version into the main checkout's `local-feed` after the merge. Verify the new `KhaozEngine.TileWorld.Netcode` nupkg exists there.
- [ ] **Step 4: Tag only under the repository's explicit waiting-consumer exception.** The user authorized a released engine pin for Grimhollow. Use `scripts/tag-release.sh` on current `main`, push the tag, and verify the package publish rather than inferring it from a pushed tag. Record the released version for the game plan.
