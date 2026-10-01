# Round 1 D: Fragment Core and Per-Viewer Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A consumer leaving the tile host keeps message fragmentation through a host-neutral core in `KhaozEngine.Netcode`, and a NetWorld server can hide an entity from chosen viewers.

**Architecture:**
- **Fragments:** the fragmenter and reassembler move out of `KhaozEngine.TileWorld.Netcode` into `KhaozEngine.Netcode`, parameterised by chunk payload width. The Tile types become byte-identical wrappers at 1015 bytes.
- **Visibility:** both NetWorld server configs gain an `EntityVisibleToSlot` predicate. It filters each viewer's interest set before the delta writer and the snapshot writer. A new `ShardHost` overload lets the sharded snapshot path take the filtered set.

**Tech Stack:** C#/.NET 10, xUnit, riding 20.17.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-1-DESIGN-2026-10-01.md`, sections 4 and 5 and decision D6. Branch 4 of 4. It is independent of plans B and C, and starts once plan A has opened 20.17.0 on `main`.

## Global Constraints

- **Worktree:** `/Users/antonio/KhaozEngine/.worktrees/round1-d` on `feature/round1-fragments-visibility`, from current `origin/main`. Read `AGENTS.md` and `docs/CONTRIBUTOR-RULES.md` first.
- **Fragment wire format unchanged:**
  - a 5-byte header `[StreamId:byte][Sequence:u16 LE][ChunkIndex:byte][ChunkCount:byte]`
  - `MaxChunks` = 255
  - every non-final chunk exactly full
  - refusal tokens `ke:fragment-malformed` and `ke:fragment-out-of-sequence`
  - `MaxPartialAssemblies` = 4 with least-recently-fed eviction
- **Tile wrappers:** `TileFragmentedMessage` and `TileFragmentReassembler` keep their names, assemblies, public members and the 1015-byte width. Their existing tests (`TileFragmentedMessageTests`, `PageSyncFrameBoundTests`) stay unchanged and green.
- **Visibility:**
  - A null `EntityVisibleToSlot` serves exactly today's bytes.
  - A viewer's own player is never filtered.
  - The predicate takes (viewer slot, net id) and must not be documented as able to read components, because ghosts lack owner-only and server-only components.
- **KESIZE:** `WorldServer.cs` is 718 lines, `ShardedWorldServer.cs` 735 and `ShardHost.cs` 673, against an 800 cap. Put new logic in new types or partials if a file would cross it. Never grow `.filesize-baseline`.
- **Version:** ride the staged 20.17.0. Extend its changelog.
- **Builds and tests:** one building agent at a time, focused tests per task, the full suite once in Task 4. Never loop tests. Workers never push, pack or tag.
- **Prose:** no em or en dashes, no prose semicolons.

## Review Focus

1. **A width that is not the Tile width.** A NetWorld consumer picks a width from its own envelope, and the reassembler must accept exactly that width and refuse a short non-final chunk. Owned by Task 1 `MessageFragmenterTests.ANonTileWidthRoundTripsAndRefusesAShortNonFinalChunk`.
2. **A visibility flip on the snapshot path.** A legacy client served full snapshots must lose and regain the entity too, not only delta clients. Owned by Task 3 `EntityVisibilityTests.PolicyChangesRemoveAndRestoreOnTheSnapshotPath`.
3. **The viewer's own player.** A predicate that returns false for everything must still serve the viewer itself. Owned by Task 3 `EntityVisibilityTests.ThePredicateNeverHidesTheViewersOwnPlayer`.
4. **A null predicate.** Byte-identical frames to today. Owned by Task 3 `EntityVisibilityTests.ANullPredicateServesTodaysBytes`.
5. **Sharded cell boundaries.** An entity seen through a ghost in a neighbouring cell is filtered the same way. Owned by Task 3 `ShardedEntityVisibilityTests.AGhostAcrossACellBoundaryIsFilteredTheSameWay`.

---

### Task 1: `MessageFragmenter` and `MessageReassembler` in Netcode

**Files:**
- Create: `KhaozEngine.Netcode/MessageFragmenter.cs`, `KhaozEngine.Netcode/MessageReassembler.cs`
- Test: `KhaozEngine.Server.Tests/Netcode/MessageFragmenterTests.cs`

**Interfaces:**
- `public static class MessageFragmenter`:
  - `public const int HeaderBytes = 5` and `public const int MaxChunks = 255`
  - `public static int ChunkCount(int payloadLength, int chunkPayloadBytes)`
  - `public static byte[][] Fragment(byte streamId, ushort sequence, ReadOnlySpan<byte> payload, int chunkPayloadBytes)`
  - `public static bool TryReadChunk(ReadOnlySpan<byte> chunk, int chunkPayloadBytes, out byte streamId, out ushort sequence, out int chunkIndex, out int chunkCount, out ReadOnlySpan<byte> bytes)`
  - `public static int MaxPayloadBytes(int chunkPayloadBytes) => MaxChunks * chunkPayloadBytes`
- `public sealed class MessageReassembler(int slot, int chunkPayloadBytes)`:
  - `public const int MaxPartialAssemblies = 4`
  - `public const string MalformedChunk = "ke:fragment-malformed"` and `public const string OutOfSequenceChunk = "ke:fragment-out-of-sequence"`
  - `Slot`, `ChunkPayloadBytes`, `EvictedAssemblies`, `PartialAssemblyCount`
  - both `TryComplete` overloads as the Tile type has them today
  - `bool DropConnection(int slot)`
- The logic is moved from `TileFragmentedMessage.cs` and `TileFragmentReassembler.cs` with the width as a parameter. Today the width is hardcoded at `TileFragmentReassembler.cs:117`, `:216` and `:238`. A width outside `1..ushort.MaxValue` throws `ArgumentOutOfRangeException`.

- [ ] **Step 1: Write the failing tests.**
  - Port the twelve `TileFragmentedMessageTests` facts to the new types, at width 1015.
  - Add `ANonTileWidthRoundTripsAndRefusesAShortNonFinalChunk` (Review Focus 1).
  - Add `AWidthOutOfRangeThrows`.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~MessageFragmenterTests"`. Expected: build failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS, non-zero.
- [ ] **Step 5: Commit.** Message: `feat(netcode): a host-neutral message fragmenter and reassembler`.

### Task 2: The Tile types become wrappers

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TileFragmentedMessage.cs`, `KhaozEngine.TileWorld.Netcode/TileFragmentReassembler.cs`

**Interfaces:**
- Consumes Task 1.
- Every public member keeps its signature, delegating with the existing `MaxChunkPayloadBytes` (1015). `TileFragmentReassembler` wraps a `MessageReassembler` with width 1015.

- [ ] **Step 1:** Replace the bodies with delegation.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter "FullyQualifiedName~TileFragmentedMessageTests|FullyQualifiedName~PageSyncFrameBoundTests"`. Expected: PASS, unchanged tests.
- [ ] **Step 3: Commit.** Message: `refactor(tileworld-netcode): tile fragments delegate to the netcode core`.

### Task 3: `EntityVisibleToSlot` on both NetWorld servers

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServerConfig.cs`, `KhaozEngine.NetWorld/WorldServer.cs` (`WorldServerConfig` around 13-121, serve loop around 546-585)
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs` (serve loop around 556-584)
- Modify: `KhaozEngine.Sharding/ShardHost.cs` (a new overload beside `SnapshotForClient` around 540-550). If the file would cross the cap, the overload goes in a new `ShardHost.Serving.cs` partial.
- Create: `KhaozEngine.NetWorld/InterestVisibility.cs` (the shared filter step, so both loops call one implementation)
- Test: `KhaozEngine.Server.Tests/NetWorld/EntityVisibilityTests.cs`, `ShardedEntityVisibilityTests.cs`

**Interfaces:**
- Both configs gain `public Func<int, long, bool>? EntityVisibleToSlot { get; init; }`, documented like `TileWorldServerConfig.GroundItemVisibleToSlot` and cross-referencing its twin.
- `internal static class InterestVisibility` has `void Filter(HashSet<long> interest, int viewerSlot, long viewerNetId, Func<int, long, bool>? visible)`. It removes every net id the predicate rejects, except `viewerNetId`, using a cached delegate so it does not allocate per tick.
- `WorldServer` filters `set` after `interest.Query`, before the delta and snapshot writers.
- `ShardedWorldServer` filters `interestScratch` after `host.HomeInterest`. On the snapshot path it calls the new overload with the filtered set, instead of the radius overload.
- New overload: `public byte[] ShardHost.SnapshotForClient(int slot, World world, IReadOnlySet<long> interest, long? serveEpoch = null)`. It does the same index and `SnapshotWriter.WriteFiltered` as today with the given interest, and the existing radius overload calls it.

- [ ] **Step 1: Write the failing tests**, modelled on `TileGroundItemVisibilityTests` and the delta tests' `NewServer`, `Pump` and `RawDeltaClient` helpers (`advertiseDelta: false` for the snapshot path).
  - `EntityVisibilityTests` on `WorldServer`:
    - `AnOwnerOnlyEntityReachesOnlyItsViewer`
    - `PolicyChangesRemoveAndRestoreOnTheDeltaPath`
    - `PolicyChangesRemoveAndRestoreOnTheSnapshotPath`
    - `AViewerEnteringRangeLaterStillNeverReceivesAHiddenEntity`
    - `AReconnectingViewerStillNeverReceivesAHiddenEntity`
    - `ANullPredicateServesTodaysBytes`
    - `ThePredicateNeverHidesTheViewersOwnPlayer`
  - `ShardedEntityVisibilityTests` on `ShardedWorldServer`:
    - the same delta and snapshot cases
    - `AGhostAcrossACellBoundaryIsFilteredTheSameWay`
    - `SnapshotForClientWithAnExplicitSetMatchesTheRadiusOverloadForTheSameSet`
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~EntityVisibilityTests|FullyQualifiedName~ShardHostServingTests|FullyQualifiedName~DeltaTests"`. Expected: the new tests FAIL and the existing ones pass.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS. The existing `SnapshotWriterIndexedParityTests`, `WorldServerSnapshotIndexParityTests` and delta tests stay green.
- [ ] **Step 5: Commit.** Message: `feat(networld): hide an entity from chosen viewers`.

### Task 4: Docs, changelog and full verification

**Files:**
- Modify: `KhaozEngine.Netcode/README.md` (a fragments section)
- Modify: `KhaozEngine.TileWorld.Netcode/README.md` (around 1355-1387, saying the Tile types wrap the core)
- Modify: `KhaozEngine.NetWorld/README.md`:
  - `## Game messages` around 351-381: server to client is uncapped on the reliable channel, and the client to server cap is configurable, so fragment with the core when needed
  - the AoI and server-owned entity sections, for the predicate
- Modify: `KhaozEngine.Sharding/README.md` (around 133-152, the new overload)
- Modify: `docs/USING-KHAOZENGINE.md`:
  - around 18011-18101 (fragments)
  - around 20059-20111 and 22150-22254 (visibility and the overload)
  - around 12586 (a cross-reference from the ground item predicate)
- Modify: `CHANGELOG.md` (extend 20.17.0)

- [ ] **Step 1:** Write the docs. Commit message: `docs(netcode): fragments core and per-viewer visibility`.
- [ ] **Step 2:** Full verification, as plan A Task 5 Step 2.

## After all four branches

- **Integration.** The orchestrator merges each verified branch into engine `main` in order A, B, C and D, re-verifies after each merge, pushes, and runs `scripts/pack-local-feed.sh` from `main`.
- **Release.** The owner tags 20.17.0 with `scripts/tag-release.sh`, after the plan C manual check.
- **Grimhollow pin bump.** Grimhollow then bumps its pin, moves `ke-tileedit` and `ke-sfxbake`, refreshes the vendored feed, records the swept range in `docs/ENGINE-INTEGRATION.md`, and runs its full verification. That closes Grimhollow's P2.
- **Where it lands.** The Grimhollow bump lands on Grimhollow's integration branch `feature/continuous-movement` (owner ruling O11 in its spec), not on Grimhollow `main`, so no player version has to be staged for it.
