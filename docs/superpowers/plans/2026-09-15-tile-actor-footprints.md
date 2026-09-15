# Tile actor footprints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** NxN tile actors (1x1 to 8x8) anchored south-west, with pathing, reach, combat, wander, spawn placement and presentation honouring the whole footprint.

**Architecture:** The size lives on `TileMoveState` (a byte where 0 reads as 1) and rides its existing codec as two optional trailing bytes. One reach predicate (no overlap, some attacker tile in the one-tile reach set) answers every range question on both heads. A footprint may not stand across a wall.

**Tech Stack:** C# net10.0, xUnit, `KhaozEngine.TileWorld`, `KhaozEngine.TileWorld.Netcode`, `KhaozEngine.TileEdit.Tool`.

**Spec:** `docs/design/TILE-ACTOR-FOOTPRINTS-DESIGN-2026-09-15.md` (signed off, every section 13 choice as recommended). Read it before any task.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.claude/worktrees/tile-footprints`, branch `feature/tile-footprints`. Every command starts with `cd` into it.
- Warnings are errors. No `#pragma warning disable`, no `<NoWarn>`.
- No em or en dashes and no semicolons in prose, comments or docs. Run `sh scripts/check-dashes.sh --tree` and `sh scripts/check-prose.sh --tree` before each commit.
- File cap 800 lines (`sh scripts/check-file-size.sh --tree`). `TileCombatResolveTests.cs` (787) and `TileWanderBehaviourTests.cs` (784) are at the cap: new tests go in NEW files named `TileFootprint*Tests.cs`. Never edit `.filesize-baseline`.
- Test namespaces: `KhaozEngine.Tests.TileWorld` in `KhaozEngine.TileWorld.Tests`, `KhaozEngine.Tests.TileNetcode` in `KhaozEngine.TileWorld.Netcode.Tests`.
- Size-1 behaviour, wire bytes and candidate order must stay identical. Every existing test in the three tile test projects must pass unchanged except the three self-attack tests Task 4 names.
- `TileMoveState.MaxFootprintSize = 8`. Anchor is the south-west tile, footprint covers `x..x+N-1`, `z..z+N-1` (z counts north).
- Commit with `git commit -m "<area>(footprints): ..." -- <paths>` (explicit paths, never a bare commit, never `git stash`).
- Build and test: `dotnet test KhaozEngine.TileWorld.Tests -c Release`, `dotnet test KhaozEngine.TileWorld.Netcode.Tests -c Release`, `dotnet test KhaozEngine.TileEdit.Tests -c Release`. If a run fails reading `~/.gitconfig`, use the `.buildhome` workaround in `AGENTS.md`.

---

### Task 1: Standing and stepping with a footprint

**Files:**
- Modify: `KhaozEngine.TileWorld/TileCollision.cs`
- Modify: `KhaozEngine.TileEdit.Tool/QueryService.cs:131-147` (IsWalkable delegates)
- Create: `KhaozEngine.TileWorld.Tests/TileWorld/TileFootprintCollisionTests.cs`
- Modify: `KhaozEngine.TileEdit.Tests/QueryServiceTests.cs` (one added assertion for an internal wall)

**Interfaces:**
- Produces: `public static bool TileCollision.CanStand(TileCollisionMap map, int x, int z, int plane, int agentSize = 1)`. `CanStep(..., agentSize)` for `agentSize > 1` additionally requires `CanStand` at the destination anchor.

- [ ] **Step 1: Write the failing tests** in `TileFootprintCollisionTests.cs`, using the `Map(...)` helper shape from `TileCollisionStepTests` (greybox catalogs, `TileWorldTestData.FlatWorld()`). Wall rotation: 0 W, 1 N, 2 E, 3 S edge of the placed tile, mirrored onto the neighbour.

```csharp
[Theory, InlineData(1), InlineData(2), InlineData(3)]
public void Open_ground_stands_every_size(int n) =>
    Assert.True(TileCollision.CanStand(Map(), 10, 10, 0, n));

[Fact]
public void A_blocked_tile_anywhere_in_the_footprint_refuses_standing()
{
    TileCollisionMap map = Map(("tree", 11, 11, 0));
    Assert.False(TileCollision.CanStand(map, 10, 10, 0, 2));
    Assert.True(TileCollision.CanStand(map, 12, 10, 0, 2));
}

[Fact]
public void A_wall_between_two_tiles_of_the_footprint_refuses_standing_but_not_a_one_tile_body()
{
    TileCollisionMap map = Map(("wall", 10, 10, 2));            // east edge of (10,10)
    Assert.False(TileCollision.CanStand(map, 10, 10, 0, 2));
    Assert.True(TileCollision.CanStand(map, 10, 10, 0, 1));
    Assert.True(TileCollision.CanStand(map, 11, 10, 0, 2));    // the wall is on this footprint's west boundary
}

[Fact]
public void A_two_by_two_cannot_walk_along_a_fence_line_it_straddles()
{
    TileCollisionMap map = Map(("fence", 10, 12, 2));           // east edge of (10,12)
    Assert.False(TileCollision.CanStep(map, 10, 11, 0, TileDirection.N, 2));
    Assert.True(TileCollision.CanStep(map, 10, 11, 0, TileDirection.N, 1));
}

[Fact]
public void A_region_the_map_does_not_hold_refuses_standing() =>
    Assert.False(TileCollision.CanStand(Map(), 63, 63, 0, 2));   // (64, 64) is outside region (0,0)
```

Add pathfinder cases in the same file: a one-tile corridor (walls both sides) that a 1x1 walks and a 2x2 cannot (`FindPath(...).Reached` false for 2, true for 1), a two-wide doorway a 2x2 passes and a 3x3 does not, and determinism (two `FindPath` calls for size 3 return equal tile lists). Check the region size in `RegionCoord` before choosing the out-of-region coordinate and adjust.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test KhaozEngine.TileWorld.Tests -c Release --filter FullyQualifiedName~TileFootprintCollisionTests`
Expected: compile failure, `CanStand` does not exist.

- [ ] **Step 3: Implement** in `TileCollision.cs`

```csharp
/// <summary>Whether an agent anchored at (x, z) with an NxN footprint may STAND there: no footprint tile is Blocked
/// (a region the map does not hold reads Blocked, so an unloaded tile refuses too) and no wall lies on an edge
/// between two tiles of the footprint, so a body never stands across a fence. For a one tile agent this is exactly
/// <see cref="IsBlocked"/> negated.</summary>
public static bool CanStand(TileCollisionMap map, int x, int z, int plane, int agentSize = 1)
{
    ArgumentNullException.ThrowIfNull(map);
    if (agentSize < 1) throw new ArgumentOutOfRangeException(nameof(agentSize));
    for (int dz = 0; dz < agentSize; dz++)
        for (int dx = 0; dx < agentSize; dx++)
        {
            TileCollisionFlags f = map.Get(x + dx, z + dz, plane);
            if ((f & TileCollisionFlags.Blocked) != 0) return false;
            // The baker mirrors every wall onto both tiles of its edge, so the east and north edges of each tile
            // cover every internal edge of the footprint exactly once.
            if (dx + 1 < agentSize && (f & TileCollisionFlags.WallE) != 0) return false;
            if (dz + 1 < agentSize && (f & TileCollisionFlags.WallN) != 0) return false;
        }
    return true;
}
```

In `CanStep`, after the per-tile loop:

```csharp
if (agentSize == 1) return true;
// The per-tile steps cross every internal edge along the axis of travel but not the perpendicular ones, so a
// body could otherwise straddle a wall it walks beside.
(int ddx, int ddz) = TileDirections.Delta(dir);
return CanStand(map, x + ddx, z + ddz, plane, agentSize);
```

Update the `CanStep` summary sentence "NxN: every footprint tile must be able to take the step" to add "and the destination must satisfy CanStand". In `QueryService.IsWalkable`, replace the nested loop with `bool walkable = TileCollision.CanStand(e.Collision, x, z, plane, agentSize);` and reword its summary to name `CanStand`. In `QueryServiceTests.IsWalkable_AppliesTheAgentFootprint`, add one assertion for a 2x2 anchored across a wall if the fixture has one, otherwise add a new `[Fact]` building a wall.

- [ ] **Step 4: Run all three test projects** (commands in Global Constraints). Expected: all pass.
- [ ] **Step 5: Commit** `tileworld(footprints): CanStand, and a large body cannot straddle a wall`

---

### Task 2: The footprint reach predicate

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TileReach.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileFootprintReachTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public static IReadOnlyList<TileCoord> TileReach.Set(TileCollisionMap map, TileRect footprint, int plane, int agentSize)`
  - `public static bool TileReach.Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from, int agentSize)`
  - `public static TileDirection TileReach.FacingToward(TileCollisionMap map, TileRect footprint, int plane, TileCoord from, int agentSize)`
  - `TryNearest(..., agentSize, ...)` (both existing overloads) now path to `Set(map, footprint, plane, agentSize)` and admit up to `maxRadius + agentSize`.

- [ ] **Step 1: Write the failing tests.** Flat world from `TileMoveSimulatorTests.FlatWorld()`, map from `TileMoveSimulatorTests.Bake(doc)`, no objects unless named. Target anchored at (20, 20).

```csharp
public static TheoryData<int, int> Pairings()
{
    var data = new TheoryData<int, int>();
    for (int a = 1; a <= 3; a++) for (int t = 1; t <= 3; t++) data.Add(a, t);
    return data;
}

// Brute force over a window: in range exactly when the rects do not overlap and share a cardinal edge.
[Theory, MemberData(nameof(Pairings))]
public void Contains_is_edge_adjacency_without_overlap_on_open_ground(int n, int m)
{
    TileCollisionMap map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
    var target = new TileRect(20, 20, m, m);
    for (int z = 14; z <= 26; z++)
    for (int x = 14; x <= 26; x++)
    {
        var a = new TileRect(x, z, n, n);
        bool overlap = !a.Intersect(target).IsEmpty;
        bool xTouch = (a.X1 == target.X || target.X1 == a.X) && a.Z < target.Z1 && a.Z1 > target.Z;
        bool zTouch = (a.Z1 == target.Z || target.Z1 == a.Z) && a.X < target.X1 && a.X1 > target.X;
        Assert.Equal(!overlap && (xTouch || zTouch),
            TileReach.Contains(map, target, 0, new TileCoord(x, z, 0), n));
    }
}

[Theory, MemberData(nameof(Pairings))]
public void Set_lists_exactly_the_in_range_anchors_without_repeats(int n, int m) { /* Set(map, target, 0, n) equals
   the brute-force window's in-range anchors as a set, Count equals Distinct().Count(), Count == 4 * (m + n - 1) */ }

[Theory, InlineData(1), InlineData(2), InlineData(3)]
public void Size_one_set_is_the_legacy_set_in_the_legacy_order(int m)
{
    // Assert.Equal on the two lists (order matters), with a wall added so the order is not trivially symmetric.
}

[Fact]
public void A_wall_denies_one_attacker_tile_while_another_still_reaches() { /* 2x2 target at (20,20), 2x2 attacker
   at (18,20): its tiles (19,20) and (19,21) both touch the target's west edge. A wall on the east edge of (19,20)
   (object "wall" at (19,20) rotation 2) leaves Contains true through (19,21). A second wall on the east edge of
   (19,21) makes it false. Against a 1x1 target at (20,20), a 2x2 attacker at (18,19) touches only through
   (19,20), so the first wall alone makes it false. Pin all three. */ }

[Theory, MemberData(nameof(Pairings))]
public void FacingToward_answers_the_touching_side(int n, int m) { /* west anchor (20 - n, 20) faces E, east
   anchor (20 + m, 20) faces W, south anchor (20, 20 - n) faces N, north anchor (20, 20 + m) faces S */ }

[Theory, MemberData(nameof(Pairings))]
public void TryNearest_stops_on_an_in_range_anchor_by_the_shortest_walk(int n, int m)
{
    // From (10, 21): Assert.True, Contains(map, target, 0, reachTile, n) true, path.Tiles.Count equals the
    // Chebyshev distance on open ground, and path.End equals reachTile. From an anchor already in range the path
    // is empty and reachTile == from.
}

[Fact]
public void TryNearest_admits_a_large_agent_whose_nearest_candidate_is_inside_the_window()
{
    // maxRadius 4, 3x3 agent at (10, 20), 1x1 target at (17, 20): footprint distance 7 = maxRadius + 3, the west
    // candidate (14, 20) is 4 away, so the call must return true. The same geometry with agent size 1 at (12,20)
    // (distance 5 = maxRadius + 1) returns true, at (11, 20) returns false.
}

[Theory, MemberData(nameof(Pairings))]
public void An_overlapping_agent_is_never_in_range_and_TryNearest_walks_it_out(int n, int m) { /* anchor at the
   target anchor: Contains false, TryNearest true with a non-empty path ending out of overlap */ }
```

Write the bodies in full (the comments above state exactly what each asserts). Use `CanStep`-legal wall rotations as in Task 1.

- [ ] **Step 2: Run to verify they fail** (`--filter FullyQualifiedName~TileFootprintReachTests`). Expected: compile failure.

- [ ] **Step 3: Implement** in `TileReach.cs`

```csharp
/// <summary>Every ANCHOR tile an NxN agent can act on the footprint from: anchors whose own footprint does not
/// overlap the target and covers at least one tile of the one-tile <see cref="Set(TileCollisionMap, TileRect, int)"/>.
/// Derived from that set in its own order, each reach tile offering the anchors whose footprint holds it (z
/// ascending, then x ascending), first occurrence kept. For a size of 1 the offsets are only (0, 0), nothing overlaps
/// and nothing repeats, so the list is that set element for element, which keeps every one-tile tie break.</summary>
public static IReadOnlyList<TileCoord> Set(TileCollisionMap map, TileRect footprint, int plane, int agentSize)
{
    ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
    IReadOnlyList<TileCoord> reach = Set(map, footprint, plane);
    if (agentSize == 1) return reach;
    var anchors = new List<TileCoord>();
    foreach (TileCoord p in reach)
        for (int dz = 0; dz < agentSize; dz++)
            for (int dx = 0; dx < agentSize; dx++)
            {
                var a = new TileCoord(p.X - dx, p.Z - dz, plane);
                if (Overlaps(a, agentSize, footprint) || anchors.Contains(a)) continue;
                anchors.Add(a);
            }
    return anchors;
}

public static bool Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from, int agentSize)
{
    ArgumentNullException.ThrowIfNull(map);
    ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
    if (agentSize == 1) return Contains(map, footprint, plane, from);
    if (from.Plane != plane || Overlaps(from, agentSize, footprint)) return false;
    foreach (TileCoord c in Set(map, footprint, plane))
        if ((long)c.X >= from.X && (long)c.X < (long)from.X + agentSize
            && (long)c.Z >= from.Z && (long)c.Z < (long)from.Z + agentSize) return true;
    return false;
}

public static TileDirection FacingToward(TileCollisionMap map, TileRect footprint, int plane, TileCoord from,
    int agentSize)
{
    ArgumentNullException.ThrowIfNull(map);
    ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
    if (agentSize == 1) return FacingToward(map, footprint, plane, from);
    long x0 = from.X, x1 = x0 + agentSize, z0 = from.Z, z1 = z0 + agentSize;
    bool zOverlap = z0 < footprint.Z1 && z1 > footprint.Z;
    bool xOverlap = x0 < footprint.X1 && x1 > footprint.X;
    // W, E, S, N, the order the one-tile scan asks in. Two cardinally adjacent rects touch on exactly one side.
    if (zOverlap && x0 == footprint.X1) return TileDirection.W;
    if (zOverlap && x1 == footprint.X) return TileDirection.E;
    if (xOverlap && z0 == footprint.Z1) return TileDirection.S;
    if (xOverlap && z1 == footprint.Z) return TileDirection.N;
    return TileDirection.W;
}

// In long for the overflow reason TryNearest's admission gives.
static bool Overlaps(TileCoord anchor, int size, TileRect r) =>
    (long)anchor.X < r.X1 && (long)anchor.X + size > r.X && (long)anchor.Z < r.Z1 && (long)anchor.Z + size > r.Z;
```

Check `TileRect.X1`/`Z1` are `int` sums that can overflow near `int.MaxValue`, and cast those to long too if so. In `TryNearest` (the scratch overload): change the admission to `if (Math.Max(dx, dz) > (long)maxRadius + agentSize) return false;` and iterate `Set(map, footprint, plane, agentSize)`. Rewrite the three "under-reports" doc paragraphs (type doc `:23-25`, `Set` `:42-43`, `Contains` `:79-81`) and the `agentSize` param docs on both `TryNearest` overloads to say the agent size now shapes the candidates, with a `<see cref>` to the new overloads. Add `<summary>`, `<param>` and `<exception>` docs on the three new members matching the file's style.

- [ ] **Step 4: Run** the Netcode test project in full. Expected: all pass, including the untouched `TileReachTests`.
- [ ] **Step 5: Commit** `netcode(footprints): one reach predicate for an NxN agent against an MxM footprint`

---

### Task 3: The size on the move state and the wire

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TileMoveState.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileProtocol.Components.cs:160-241` (`WriteMove`, `ReadMove`, the comment above them)
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldServer.cs:304` (`SetPlayerState` refuses a footprint above 1, add the `<exception>` line)
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileFootprintWireTests.cs`

**Interfaces:**
- Produces: `public const int TileMoveState.MaxFootprintSize = 8`, `public int TileMoveState.FootprintSize { readonly get; set; }` (0 backing reads 1, setter throws `ArgumentOutOfRangeException` outside 1..8), `public readonly TileRect TileMoveState.Footprint`.

- [ ] **Step 1: Write the failing tests.** Find the encode/decode helper pattern in `TileReplicationTests.cs` (around its `Write7BitEncodedInt(move.Length)` sites) and `TileMoveStateTests.cs`, and round-trip through `TileProtocol.CreateRegistry()` the same way.

```csharp
[Fact] public void A_default_state_is_one_tile() { Assert.Equal(1, default(TileMoveState).FootprintSize);
    Assert.Equal(new TileRect(3, 4, 1, 1), TileMoveState.At(new TileCoord(3, 4, 0), TileDirection.S).Footprint); }

[Theory, InlineData(0), InlineData(9)]
public void The_setter_refuses_a_size_outside_one_to_eight(int n) { var s = default(TileMoveState);
    Assert.Throws<ArgumentOutOfRangeException>(() => s.FootprintSize = n); }

[Fact] public void Equality_normalizes_an_unset_size_to_one() { /* At() vs the same with FootprintSize = 1: equal,
    equal hashes. With FootprintSize = 2: not equal. */ }

[Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(8)]
public void The_size_round_trips_through_the_codec(int n) { /* encode, decode, Assert.Equal(n, decoded.FootprintSize)
    and Assert.Equal(original, decoded) with the route cleared, for a state with an entity interaction pending and
    for one with none */ }

[Fact] public void A_one_tile_state_encodes_to_the_legacy_byte_count() { /* 41 payload bytes with no entity
    interaction, 42 with one */ }

[Fact] public void A_large_state_writes_a_zero_domain_placeholder_then_the_size() { /* payload length 43, byte
    [41] == 0, byte [42] == 2, and InteractDomain decodes AuthoredObject */ }

[Fact] public void A_hostile_size_byte_is_clamped() { /* hand-built 43-byte payload with byte [42] = 0 decodes to 1,
    = 200 decodes to 8 */ }

[Fact] public void SetPlayerState_refuses_a_footprint_above_one() { /* server via TileWorldServerTickTests.Server,
    SpawnPlayer slot 0, state At(...) with FootprintSize = 2, Assert.Throws<ArgumentException> */ }
```

Write the bodies in full.

- [ ] **Step 2: Run to verify they fail.** Expected: compile failure.

- [ ] **Step 3: Implement.** In `TileMoveState` add, next to `CombatTarget`:

```csharp
/// <summary>The largest footprint edge an entity may have, in tiles. Content hygiene rather than a limit of the
/// lattice: reach candidates grow as 4(M + N - 1), and a bigger body deserves its own look at those numbers.</summary>
public const int MaxFootprintSize = 8;

byte footprintSize;

/// <summary>The edge of this entity's square footprint, in tiles, anchored on <see cref="Tile"/> as its SOUTH-WEST
/// corner and covering the tiles north and east of it. One for every player and for every state built without it,
/// because the backing byte's zero reads as one. It rides the move state rather than a simulator so the one stepper,
/// the entity target snapshot, a region handoff and a client's remote sample all hold it with no second lookup.
/// </summary>
/// <exception cref="ArgumentOutOfRangeException">Set outside 1 through <see cref="MaxFootprintSize"/>.</exception>
public int FootprintSize
{
    readonly get => footprintSize == 0 ? 1 : footprintSize;
    set
    {
        if (value < 1 || value > MaxFootprintSize)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"FootprintSize must be 1..{MaxFootprintSize}.");
        footprintSize = (byte)value;
    }
}

/// <summary>The tiles this entity covers: <see cref="FootprintSize"/> square from <see cref="Tile"/>.</summary>
public readonly TileRect Footprint => new(Tile.X, Tile.Z, FootprintSize, FootprintSize);
```

Add `&& FootprintSize == other.FootprintSize` to `Equals`, and fold `FootprintSize` into `GetHashCode` (for example `HashCode.Combine(InteractTarget, InteractDomain, FootprintSize)` in the existing nested combine). Update the struct's type doc if it enumerates the simulation fields.

In `WriteMove`, replace the domain write with:

```csharp
bool entityDomain = v.InteractTarget != 0 && v.InteractDomain == TileInteractionDomain.Entity;
int size = v.FootprintSize;
// A large body always writes the domain slot, as a zero when no entity interaction is pending, so its size sits at
// a fixed offset. A one-tile body writes exactly the bytes it always did.
if (entityDomain || size > 1)
    w.Write((byte)(entityDomain ? TileInteractionDomain.Entity : TileInteractionDomain.AuthoredObject));
if (size > 1) w.Write((byte)size);
```

In `ReadMove`, after the existing domain read:

```csharp
// Clamped rather than checked, the file's rule for a field whose every byte value means something to clamp to.
if (r.BaseStream.Position < r.BaseStream.Length)
    s.FootprintSize = Math.Clamp((int)r.ReadByte(), 1, TileMoveState.MaxFootprintSize);
```

Rewrite the codec comment above `WriteMove` ("41 bytes for every existing state...") to describe both trailing bytes and why an older reader still decodes a large state. In `SetPlayerState`, first line of the body: `if (state.FootprintSize != 1) throw new ArgumentException("A player is one tile. FootprintSize above 1 is for actors.", nameof(state));`.

- [ ] **Step 4: Run** the Netcode test project in full. Expected: all pass.
- [ ] **Step 5: Commit** `netcode(footprints): TileMoveState carries the footprint size on the wire`

---

### Task 4: The stepper steps and follows by footprint, and a self lock clears (#741)

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TileMoveSimulator.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileMoveOptions.cs:10-12` (AgentSize doc: a floor under each state's size)
- Modify: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatTargetTests.cs:177`, `:272`, `:655` (the three self tests)
- Modify: `KhaozEngine.TileWorld.Netcode/TileRemoteTargets.cs` doc paragraph about the local branch's one consumer
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileFootprintFollowTests.cs`

**Interfaces:**
- Consumes: Task 2's `TileReach` overloads, Task 3's `TileMoveState.FootprintSize` and `Footprint`.
- Produces: `public TileRect TileMoveSimulator.FootprintOf(in TileMoveState state)` returning the anchor rect of edge `Math.Max(state.FootprintSize, AgentSize)`.

- [ ] **Step 1: Write the failing tests** in `TileFootprintFollowTests.cs`, stepper only. A fake resolver answering rects:

```csharp
sealed class FakeFootprints : ITileTargets
{
    public readonly Dictionary<long, TileRect> Rects = new();
    public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
    {
        plane = 0;
        return Rects.TryGetValue(target, out footprint);
    }
}

static (TileMoveSimulator sim, FakeFootprints targets) Sim()
{
    var targets = new FakeFootprints();
    TileCollisionMap map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
    return (new TileMoveSimulator(map, TileMoveSimulatorTests.Ticks, null, null, targets), targets);
}
```

Tests, each over `Pairings()` (copy the TheoryData helper from Task 2) where marked:

1. `[Theory] An_approach_from_open_ground_stops_on_an_in_range_anchor_and_stands(n, m)`: attacker `At((10, 21, 0))` with `FootprintSize = n`, target rect `(20, 20, m, m)` for id 42, `Step(Attack(42, Run))` then up to 60 `Continue(Run)`. Assert final state has an idle route, is not stepping, `TileReach.Contains(sim.Map, rect, 0, s.Tile, n)` is true, `CombatTarget == 42`, facing is `E`, and that once in range the tile never changes over 12 more ticks.
2. `[Theory] An_overlapping_attacker_steps_out_then_stands(n, m)`: attacker anchored on the target anchor. After the steps its footprint no longer overlaps (`s.Footprint.Intersect(rect).IsEmpty` is true with `FootprintSize` n), it is in range, and the lock holds.
3. `[Theory] A_target_that_moves_within_range_costs_no_re_path(n, m)`: once standing in range, move the rect one tile along the touching side keeping contact, step once, assert the route stays idle and the tile does not change.
4. `[Fact] A_self_lock_clears_on_the_tick_it_is_applied()`: target 7 at the attacker's own rect, `Step(At, Attack(7, Run), Dt, self: 7)`. Assert `CombatTarget == 0`, route idle, tile unchanged. And a lock written onto the state directly (`s.CombatTarget = 7`) clears on the next `Continue` with `self: 7`.
5. `[Fact] AgentSize_is_a_floor_under_the_state_size()`: `new TileMoveOptions { AgentSize = 2 }` simulator, a size-1 state: `FootprintOf` is 2 wide. A size-3 state: 3 wide.
6. `[Fact] A_large_walker_cannot_enter_a_corridor_a_one_tile_walker_walks()`: world with two parallel wall lines forming a one-wide corridor, `WalkTo` through it for sizes 1 and 2, assert size 1 arrives and size 2 does not (its route ends short or is idle).

- [ ] **Step 2: Run to verify they fail.** Expected: compile failure on `FootprintOf`, then assertion failures.

- [ ] **Step 3: Implement.** In `TileMoveSimulator`:

```csharp
/// <summary>The tiles a state covers as THIS simulator steps it: its own <see cref="TileMoveState.FootprintSize"/>,
/// floored at <see cref="AgentSize"/>. The one definition of an attacker's size, which the server's combat roll asks
/// of the attacker's own simulator so the follow and the roll cannot disagree.</summary>
public TileRect FootprintOf(in TileMoveState state)
{
    int n = SizeOf(state);
    return new TileRect(state.Tile.X, state.Tile.Z, n, n);
}

int SizeOf(in TileMoveState state) => Math.Max(state.FootprintSize, AgentSize);
```

Replace every `AgentSize` argument with `SizeOf(s)` in `BeginWalk` (FindPath), `BeginInteract` (TryNearest), `Follow` (TryNearest), `Start` (CanStep) and `Repath` (FindPath). Pass the size to the reach calls: `BeginInteract`'s `FacingToward(Map, footprint, plane, reachTile, size)`, `FaceTarget`'s `Contains(..., s.Tile, size)` and `FacingToward(..., s.Tile, size)`.

Rewrite `Follow` rule 4 and the rule 5 memo:

```csharp
int size = SizeOf(s);

// 4. A lock on ITSELF can never be in range: its footprint moves with the body, so no tile it could step to is off
//    it. It clears, which is the answer rule 5 gives any target with no reach tile, and the server says CannotReach
//    (#741). Holding it, which R1 did, left the body permanently in combat with no roll ever possible.
if (self != 0 && s.CombatTarget == self)
{
    s.CombatTarget = 0;
    s.Route = TileRoute.None;
    return s;
}

// In range is ONE predicate everywhere a range question is asked: no overlap with the target's footprint, and some
// tile of this body in the target's one-tile reach set. A body overlapping the target is not in range and falls
// through to rule 5, whose search routes it out, which is the OSRS answer for a monster under you (#751).
if (TileReach.Contains(Map, footprint, plane, s.Tile, size))
{
    s.Route = TileRoute.None;
    s.Facing = TileReach.FacingToward(Map, footprint, plane, s.Tile, size);
    return s;
}

// 5. Re-path only when the target moved out from under the route we already have.
if (!s.Route.IsIdle && TileReach.Contains(Map, footprint, plane, s.Route.End, size)) return s;

if (!TileReach.TryNearest(Map, footprint, plane, s.Tile, size, MaxPathRadius, out _, out TilePath path, scratch))
```

Delete the old `inside` variable, the self standstill branch and the long rule 4 comment block it replaces (keep the #753 facing rationale as two sentences). Update the class doc paragraph that says the follow "STEPS OFF the target's own tile" to say it steps a body out of the footprint and clears a self lock. Update the `self` param doc on `Step` ("tells an Attack naming the attacker itself apart"): it now names the lock the follow clears.

Update the three self tests in `TileCombatTargetTests.cs`: rename and invert them to pin the clear. `A_self_attack_clears_and_the_body_never_moves` (server: 120 ticks, single visited tile, `CombatTarget == 0` after the first tick), `A_self_target_clears_where_a_foreign_target_on_the_same_tile_steps_off` (keep the foreign half unchanged), `A_client_predicts_the_self_lock_clearing` (client and server both at `CombatTarget == 0`, `SnapCount == 0`, `CorrectionCount == 0`). Rewrite their leading comments to the new rule. Update `TileMoveOptions.AgentSize`'s summary: a floor under each state's `FootprintSize`, prefer the per-entity size.

- [ ] **Step 4: Run** the Netcode test project in full. Expected: all pass.
- [ ] **Step 5: Commit** `netcode(footprints): the stepper follows by footprint and a self lock clears (#741)`

---

### Task 5: Presentation and the client's view of a footprint

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TilePresenter.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.Snapshots.cs` (`LatestTile` carries the size, two new reads)
- Modify: `KhaozEngine.TileWorld.Netcode/TileRemoteTargets.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileFootprintPresentationTests.cs`

**Interfaces:**
- Consumes: Task 3's `TileMoveState.Footprint`.
- Produces:
  - `public TilePose TilePresenter.PoseAt(TileRect footprint, int plane, TileDirection facing = TileDirection.S)`
  - `public bool TileWorldClient.TryGetRemoteFootprint(long netId, out TileRect footprint, out int plane)` (delayed timeline)
  - `public bool TileWorldClient.TryGetLatestRemoteFootprint(long netId, out TileRect footprint, out int plane)` (newest snapshot)

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory, InlineData(1), InlineData(2), InlineData(3)]
public void A_body_draws_centred_on_its_footprint(int n)
{
    var presenter = new TilePresenter(1f, 3f);
    TileMoveState s = TileMoveState.At(new TileCoord(10, 20, 0), TileDirection.S);
    s.FootprintSize = n;
    TilePose pose = presenter.Pose(s);
    Assert.Equal(presenter.PoseAt(new TileRect(10, 20, n, n), 0).Position, pose.Position);
    Vector3 expected = TileWorldSpace.ToWorld(10 + n * 0.5f, 0f, 20 + n * 0.5f, 1f);
    Assert.Equal(expected, pose.Position);
}

[Fact]
public void A_gliding_large_body_keeps_its_centre_offset_through_the_step() { /* size 2 state stepping from
   (10,20) to (11,20) at StepTicks 2 of 4: Pose.Position equals ToWorld(10.5 + 1, 0, 20 + 1) */ }
```

Plus a loopback test in the same file using `TileCombatHarness` (see `TileCombatTargetTests.cs:640-653` for the pattern). Spawn a size-2 actor straight through the server at (26, 20) with `new TileActorSpawn(100, 4, TileDirection.S) { FootprintSize = 2 }`. This test needs Task 6's spawn field, so mark it `[Fact(Skip = "Task 6")]` here and Task 6 removes the skip. After 8 frames assert `h.Client.TryGetRemoteFootprint(actor, out TileRect r, out _)` gives `(26, 20, 2, 2)`. Then queue `Attack(actor, Run)` from the spawn (20, 20), run 60 frames, and assert: `CorrectionCount == 0`, `SnapCount == 0`, predicted tile equals the server tile, and that tile is `(25, 20)` (the west anchor for a 1x1 player, the first in-range anchor by walk length and scan order).

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.** In `TilePresenter.Pose`, change the final line:

```csharp
// A footprint's centre is half its edge in from the anchor corner. PoseAt already adds the half tile a one-tile
// body wants, so a large body adds the rest, and the offset is constant through a glide because the size is.
float extra = (state.FootprintSize - 1) * 0.5f;
return PoseAt(new Vector2(tileX + extra, tileZ + extra), state.Tile.Plane, state.Facing);
```

Add:

```csharp
/// <summary>A footprint's CENTRE, which is where an overlay that belongs to a large body (a footprint marker, a
/// nameplate anchor, a hitsplat) draws. The rules' answer, like <see cref="PoseAt(TileCoord, TileDirection)"/>, so
/// never a body: a body glides and goes through <see cref="Pose"/>.</summary>
public TilePose PoseAt(TileRect footprint, int plane, TileDirection facing = TileDirection.S) =>
    PoseAt(new Vector2(footprint.X + (footprint.Width - 1) * 0.5f, footprint.Z + (footprint.Height - 1) * 0.5f),
        plane, facing);
```

Update the `A POSE NAMES THE TILE CENTRE` paragraph of the type doc to say a pose names the FOOTPRINT centre, which is the tile centre for a one-tile body. In `TileWorldClient.Snapshots.cs`: `readonly record struct LatestTile(TileCoord Tile, int Size, double At);`, the write site `new LatestTile(now.Tile, now.FootprintSize, latestAt)`, and the two reads (delayed off `remoteSamples[netId].State.Footprint`, latest off the store), each with a doc stating which timeline and pointing at its tile twin. Fix any other `LatestTile` construction the compiler names. In `TileRemoteTargets.TryGetFootprint`, answer `client.Prediction.PredictedState.Footprint` for the local id and `TryGetLatestRemoteFootprint` for a remote, and replace the `new TileRect(tile.X, tile.Z, 1, 1)`.

- [ ] **Step 4: Run** the Netcode test project. Expected: all pass (the loopback test skipped).
- [ ] **Step 5: Commit** `netcode(footprints): presenter centres a large body and the client reads its footprint`

---

### Task 6: Actors, placement, entity targets, wander and the combat roll

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TileActorDefinition.cs`, `TileActorSpawn.cs`, `TileActorSpawner.cs` (door), `TileActorHost.cs` (`Add`, `TrySpawn`, `Decide`, new `CanPlace`), `TileWorldServer.Actors.cs` (`SpawnActorFrom`, placement helpers, `TryGetMoverTraversalMap` becomes `TryGetMoverSimulator`), `TileWorldServer.Combat.cs:256-260`, `TileEntityTargets.cs`, `ITileActorBehaviour.cs` (`TileActorContext.FootprintSize`), `TileWanderBehaviour.cs:108-111`, `TileWorldServerConfig.cs:109` (drop the "would go the day multi-tile actors land" sentence)
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileFootprintActorTests.cs`, `TileFootprintCombatTests.cs`
- Modify: `TileFootprintPresentationTests.cs` (remove Task 5's skip)

**Interfaces:**
- Consumes: Tasks 1 to 5.
- Produces: `TileActorDefinition.FootprintSize { get; init; } = 1`, `TileActorSpawn.FootprintSize { get; init; } = 1`, `TileActorContext.FootprintSize { get; init; } = 1`, `public bool TileActorHost.CanPlace(TileActorDefinition definition, TileCoord home)`.

- [ ] **Step 1: Write the failing tests.**

`TileFootprintActorTests.cs` (server setups from `TileWanderBehaviourTests.Server` and `TileActorHostTests`):

1. `A_spawn_writes_its_size_onto_the_move_state` for sizes 1, 2, 3 through `SpawnActor` and through a spawner (`Actors.Add` then one tick): `TryGetActorState(id).FootprintSize == n`.
2. `The_spawner_door_refuses_a_size_outside_one_to_eight` (0 and 9, `ArgumentOutOfRangeException` from `Actors.Add` and from `SpawnActor`).
3. `Placement_refuses_a_footprint_that_does_not_fit` on the DEFAULT profile at size 2: a tree on the NE tile, a wall between two footprint tiles, a home one tile short of the region edge. Each `Add` throws `ArgumentException`, `CanPlace` answers false, and at size 1 on the default profile a blocked home is still admitted (legacy).
4. `CanPlace_agrees_with_Add` over a small grid of homes around a tree for sizes 1 to 3.
5. `Wander_goals_always_fit_the_whole_footprint`: a world scattered with trees, a size-2 actor with `WanderRadius` 4, 400 ticks with `TileWanderBehaviour`. Every tick `CanStand(map, tile, 2)` holds for the committed tile, and the actor did move.
6. `The_leash_measures_anchor_to_home_anchor`: a size-3 actor latched to walk out, breaks on the tick `Chebyshev(tile, home) > LeashRadius` and not before.
7. `A_region_handoff_keeps_the_size`: follow the handoff pattern in `TileWorldServerShardingTests` with a size-2 actor walked across a cell boundary, assert `FootprintSize == 2` on the destination and that `TileEntityTargets` answers a 2x2 rect (through a locked attacker's follow stopping on an in-range anchor).

`TileFootprintCombatTests.cs` (fixture from `TileCombatResolveTests.Server`, `FixedRules`, `Lock`, which are `internal`):

8. `[Theory, MemberData(Pairings)] The_roll_fires_exactly_when_the_footprints_are_in_range(n, m)`: attacker actor size n, target actor size m, no behaviour, lock attacker onto target, place the attacker on each anchor of a small ring around the target (spawn fresh per position or teleport through the host `Command` walk), tick once, and assert `rules.Rolls.Count` is 1 exactly when `TileReach.Contains(map, targetRect, 0, attackerTile, n)`, including zero on overlap.
9. `[Theory, MemberData(Pairings)] A_chase_closes_and_the_first_roll_lands(n, m)`: attacker 8 tiles west, `TileWanderBehaviour` off, lock, tick up to 80, assert a roll happened and at that tick the attacker's footprint touches without overlap.
10. `A_large_actor_retaliates_against_a_small_player_and_the_player_back` using `TileCombatHarness` with rules and `TileWanderBehaviour`: player attacks a size-2 actor, the actor retaliates, both sides roll within 40 frames, `CorrectionCount == 0`.
11. `A_mutual_kill_between_two_large_bodies_kills_both` (sizes 2 and 3, adjacent, both locked, health 5, damage 5).
12. `An_entity_interaction_with_a_large_actor_arrives_on_an_in_range_anchor` (player `InteractEntity` on a size-3 actor, the server's arrival fires from a tile where `Contains(..., 1)` holds).

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.**

`TileActorDefinition`:

```csharp
/// <summary>The edge of the actor's square footprint in tiles, anchored on its home and every committed tile as the
/// SOUTH-WEST corner. 1 through <see cref="TileMoveState.MaxFootprintSize"/>, refused at the spawner door otherwise.
/// Pathing, reach, the wander and spawn placement all use the whole footprint, and the presenter centres the body on
/// it.</summary>
public int FootprintSize { get; init; } = 1;
```

`TileActorSpawn`: the same property as an init property beside `TraversalProfile`, doc naming `TileActorDefinition.FootprintSize`. `TileActorSpawner` constructor: refuse `FootprintSize` outside 1..8 with `ArgumentOutOfRangeException(nameof(definition), ...)` like its neighbours, and add the `<exception>` wording.

`TileWorldServer.Actors.cs`:

```csharp
internal bool IsActorTraversalPlacementBlocked(TileActorTraversalProfile profile, TileCoord at, int size)
{
    // The default profile keeps the legacy one-tile rule that a blocked home still spawns, because content predates
    // this check. No content predates a footprint, so a large body is checked on every profile.
    if (profile == TileActorTraversalProfile.Default && size == 1) return false;
    return !TryGetActorTraversal(profile, out TileCollisionMap map)
        || !map.HasRegion(at.Region)
        || !TileCollision.CanStand(map, at.X, at.Z, at.Plane, size);
}
```

Thread `size` through `ValidateActorTraversalPlacement(profile, at, size, parameterName)` (message: "blocks the footprint of size N at {at}"). In `SpawnActorFrom`: refuse `spec.FootprintSize` outside 1..8 (`ArgumentOutOfRangeException(nameof(spec))`), set `state.FootprintSize = spec.FootprintSize` after `state.Mode`, pass the size to validation. Replace `TryGetMoverTraversalMap` with:

```csharp
// The simulator an entity's movement uses. Players always use the constructor's. A live actor whose server index
// is unexpectedly missing gets no answer, which keeps combat from falling back after movement already froze.
internal bool TryGetMoverSimulator(long netId, out TileMoveSimulator mover)
{
    if (actorTraversalByNetId.TryGetValue(netId, out TileActorTraversalProfile profile))
    {
        if (actorTraversalProfiles.TryGet(profile, out TileActorTraversalEntry entry))
        {
            mover = entry.Simulator;
            return true;
        }
        mover = null!;
        return false;
    }
    if (actorNetIds.Contains(netId))
    {
        mover = null!;
        return false;
    }
    mover = simulator;
    return true;
}
```

`TileActorHost`: `Add` passes `definition.FootprintSize` to validation. `TrySpawn` passes `d.FootprintSize` to `IsActorTraversalPlacementBlocked` and sets `FootprintSize = d.FootprintSize` on the spawn spec. `Decide` sets `FootprintSize = state.FootprintSize` on the context initializer. Add:

```csharp
/// <summary>Whether <paramref name="definition"/>'s footprint fits at <paramref name="home"/> on its traversal
/// profile, which is the placement check <see cref="Add"/> and every respawn make, without throwing. For a game's
/// content test over its authored spawn markers: the engine does not know which markers are actors.</summary>
public bool CanPlace(TileActorDefinition definition, TileCoord home)
{
    ArgumentNullException.ThrowIfNull(definition);
    int size = definition.FootprintSize;
    if (size < 1 || size > TileMoveState.MaxFootprintSize) return false;
    return server.TryGetActorTraversal(definition.TraversalProfile, out _)
        && !server.IsActorTraversalPlacementBlocked(definition.TraversalProfile, home, size);
}
```

`TileActorContext`: `public int FootprintSize { get; init; } = 1;` with a doc saying it is read off the actor's state because a spawnerless actor is handed the fallback definition. `TileWanderBehaviour.Decide`: replace `TileCollision.IsBlocked(map, goal.X, goal.Z, goal.Plane)` with `!TileCollision.CanStand(map, goal.X, goal.Z, goal.Plane, context.FootprintSize)` and extend the comment: a goal the whole body cannot stand on.

`TileEntityTargets`: capture the footprint rather than the tile. Change `tiles` to `Dictionary<long, (TileCoord tile, int size)>` (and the `migrating` tuple to carry the size), write `(state.Tile, state.FootprintSize)` in `Capture`, and answer `new TileRect(tile.X, tile.Z, size, size)` in `TryGetFootprint`. Replace the `<remarks>` saying "both parties are one tile this round" with the footprint rule.

`TileWorldServer.Combat.cs` roll phase:

```csharp
// Reach is the final geometry check of the chase, so it reads the same topology AND the same attacker size the
// attacker's movement used. The target's size is its own state's, which is what the follow resolved too.
if (!TryGetMoverSimulator(attacker, out TileMoveSimulator mover)) continue;
if (!TileReach.Contains(mover.Map, targetState.Footprint, targetState.Tile.Plane, attackerState.Tile,
        mover.FootprintOf(attackerState).Width)) continue;
```

Remove Task 5's `Skip`. Fix every compile error from the renamed helper and the widened signatures.

- [ ] **Step 4: Run** all three tile test projects and then `dotnet test KhaozEngine.slnx -c Release --no-restore` once. Expected: all pass, zero warnings.
- [ ] **Step 5: Commit** `netcode(footprints): actors spawn, wander, place, fight and resolve as NxN bodies (#897)`

---

### Task 7: Ship (orchestrator, not a subagent)

- [ ] Whole-branch review of Tasks 1 to 6 against the spec, then fix findings.
- [ ] Doc sweep: `KhaozEngine.TileWorld.Netcode/README.md`, `KhaozEngine.TileWorld/README.md`, `docs/USING-KHAOZENGINE.md` tile actor section, the combat design's section 12 multi-tile bullet (point at the footprint design), the footprint design's status line and `docs/INDEX.md` row. Grep every new member name across all `*.md`.
- [ ] File the section 12 follow-ups as issues: footprint-aware `TileDrawPriority`, removing `TileMoveOptions.AgentSize` at the next major, the multi-goal reach search.
- [ ] `git fetch`, merge `origin/main` into the branch, re-run the full solution tests in Release, run the three `--tree` guards.
- [ ] Version: re-read `<KhaozEngineVersion>` and `git tag`. Ride a staged version or cut one minor. `CHANGELOG.md` entry, README `<PackageReference>` lines, `scripts/check-doc-versions.sh`.
- [ ] `scripts/pack-local-feed.sh`, commit `Closes #897` and `Closes #741`, merge to `main`, push.
- [ ] Tag with `scripts/tag-release.sh` (Grimhollow is pinned and waiting on #236), push the tag, confirm the publish run.
- [ ] Comment on https://github.com/APKiwiOrg/Grimhollow/issues/236 naming the release and the consumer-facing API changes from the spec's section 11.
