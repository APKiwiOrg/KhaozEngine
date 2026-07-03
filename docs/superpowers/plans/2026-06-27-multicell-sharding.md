# Multi-cell server sharding (6b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the authoritative overworld movement stack across a grid of cells via `ShardHost`, with seamless cross-cell ghosting + exactly-once handoff, while the existing `WorldClient` and `MoveProtocol` stay byte-for-byte unchanged.

**Architecture:** A new `ShardedWorldServer` in `KhaozEngine.NetWorld` wires the movement stack onto `ShardHost` (mirroring `MmoServerSample.MmoServer`, but with `CharacterMovement`/`MoveProtocol` instead of the toy 2D position). Per-cell movement runs as an ECS `ISystem` (`PlayerMovementSystem`) so `ShardHost.Tick` fans it across cores; authority follows entities across boundaries via the shipped `ProcessHandoffs`; each client is served its home-cell AoI snapshot framed with the existing `[localNetId][ack]` header. `WorldPersistence` is reused unchanged against a new `IWorldPersistenceHost` interface that both `WorldServer` and `ShardedWorldServer` implement.

**Tech Stack:** net10.0, KhaozEngine.{Ecs, Replication, Simulation, Sharding, Locomotion, Netcode, WorldStore, Serialization}, xUnit.

## Global Constraints

- **Engine version:** one shared `<KhaozEngineVersion>` in `Directory.Build.props`; this ships a **minor** bump `7.49.1` → `7.50.0` (additive API). Confirm `origin/main` + `git tag` have not taken 7.50.0 before tagging; bump past any race.
- **No new package** (additive API in existing `KhaozEngine.NetWorld`). `KhaozEngine.NetWorld` gains a project reference to `KhaozEngine.Sharding` (acyclic: Sharding does not depend on NetWorld).
- **Client protocol IDENTICAL.** `WorldClient`, `MoveProtocol`, `ReplicatedPosition`, the `[localNetId][ack]` frame are unchanged. The single-`World` `WorldServer` path stays intact (only gains an interface it already satisfies).
- **No em-dashes** anywhere (code, comments, docs, commits). Terse commit subjects `area(scope): summary`; on the release commit the scope is the new version, e.g. `networld(7.50.0): ...`.
- **New behaviour ships with a headless test** in `KhaozEngine.Tests` over `InProcessCellLink` (ShardHost default) + `LoopbackTransport`/`InMemoryHub`. No GPU/window.
- **Stay in scope.** No multi-process/cross-machine `ICellLink`, no per-cell world-state snapshot persistence (player records only), no dynamic cell spawn/despawn/load-scaling, no NPCs/creatures/combat/chat/animation.
- **Full doc sweep** on the bump: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, the 3 guard declarations (`docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` PackageReference), `docs/USING-KHAOZENGINE.md` (sharded-server usage), `KhaozEngine/CLAUDE.md` (NetWorld package map + Sharding dep + Server umbrella), and any stale "single-World / sharding folds in later" prose. Run `scripts/check-doc-versions.sh`.

---

## File Structure

**Create (KhaozEngine.NetWorld):**
- `KhaozEngine.NetWorld/PendingMove.cs` — transient per-tick command component (NOT replication-registered).
- `KhaozEngine.NetWorld/PlayerMovementSystem.cs` — per-cell `ISystem` stepping `CharacterMovement` on owned players.
- `KhaozEngine.NetWorld/IWorldPersistenceHost.cs` — the surface `WorldPersistence` consumes.
- `KhaozEngine.NetWorld/ShardedWorldServer.cs` — `ShardedWorldServerConfig` + `ShardedWorldServer`.

**Modify:**
- `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj` — add `ProjectReference` to Sharding; update `<Description>`.
- `KhaozEngine.NetWorld/WorldServer.cs` — `: IWorldPersistenceHost` (no behaviour change) + class-doc tweak.
- `KhaozEngine.NetWorld/WorldPersistence.cs` — ctor/field type `WorldServer` → `IWorldPersistenceHost`.
- `NetworkedWalkServer/Program.cs` — drive a multi-cell `ShardedWorldServer`.

**Create (tests):**
- `KhaozEngine.Tests/NetWorld/PlayerMovementSystemTests.cs`
- `KhaozEngine.Tests/NetWorld/ShardedWorldServerTests.cs`
- `KhaozEngine.Tests/NetWorld/ShardedWorldPersistenceTests.cs`

**Docs (release task):** `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine/CLAUDE.md`.

---

### Task 1: `PendingMove` component + add Sharding reference

**Files:**
- Create: `KhaozEngine.NetWorld/PendingMove.cs`
- Modify: `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`

**Interfaces:**
- Produces: `struct PendingMove : IComponent { MoveCommand Command; }` in namespace `KhaozEngine.NetWorld`.

- [ ] **Step 1: Add the Sharding project reference.** In `KhaozEngine.NetWorld.csproj`, inside the `<ItemGroup>` with the other `ProjectReference`s, add:

```xml
    <ProjectReference Include="../KhaozEngine.Sharding/KhaozEngine.Sharding.csproj" />
```

- [ ] **Step 2: Create `PendingMove.cs`.**

```csharp
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The movement command a cell's <see cref="PlayerMovementSystem"/> applies to an owned player on the next
/// fixed tick. Server-local and transient (set each tick by <see cref="ShardedWorldServer"/> on the owning
/// cell's player entity, overwritten the next tick); deliberately NOT registered for replication, so it is
/// neither sent to clients nor carried across an authority handoff (the post-handoff cell re-routes the next
/// command itself). A ghost or migrating entity never carries one.
/// </summary>
public struct PendingMove : IComponent
{
    /// <summary>The camera-relative input to apply this tick.</summary>
    public MoveCommand Command;
}
```

- [ ] **Step 3: Build to confirm it compiles.**

Run: `dotnet build KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit.**

```bash
git add KhaozEngine.NetWorld/PendingMove.cs KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj
git commit -m "networld: PendingMove component + Sharding project reference"
```

---

### Task 2: `PlayerMovementSystem` (per-cell movement as an ECS system)

**Files:**
- Create: `KhaozEngine.NetWorld/PlayerMovementSystem.cs`
- Test: `KhaozEngine.Tests/NetWorld/PlayerMovementSystemTests.cs`

**Interfaces:**
- Consumes: `PendingMove`, `ReplicatedPosition`, `CharacterMovement.Step`, `MoveTuning`, ECS `World`, Sharding `Ghost`/`Migrating`, `NetId`.
- Produces: `public sealed class PlayerMovementSystem : ISystem` with ctor `(Func<float,float,float> groundHeight, MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null)` and `void Update(World world, float dt)`. It steps every owned (non-`Ghost`, non-`Migrating`) entity that has `NetId` + `ReplicatedPosition` + `PendingMove`, writing the advanced position back into `ReplicatedPosition`.

- [ ] **Step 1: Write the failing test.** Create `KhaozEngine.Tests/NetWorld/PlayerMovementSystemTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerMovementSystemTests
{
    private static float Flat(float x, float z) => 0f;

    private static Entity SpawnPlayer(World w, int netId, Vector3 pos, MoveCommand cmd)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new ReplicatedPosition { Value = pos });
        w.Set(e, new PendingMove { Command = cmd });
        return e;
    }

    [Fact]
    public void Step_AdvancesOwnedPlayer_AlongCommand()
    {
        var w = new World();
        var sys = new PlayerMovementSystem(Flat, MoveTuning.Default);
        // Move +X (camera-relative right at yaw 0), run speed 6 m/s.
        Entity e = SpawnPlayer(w, 1, new Vector3(0f, 0f, 0f), new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f));

        sys.Update(w, 0.1f);

        Vector3 after = w.Get<ReplicatedPosition>(e).Value;
        Assert.True(after.X > 0.05f, $"expected +X motion, got {after.X}");
        Assert.Equal(MoveTuning.Default.CapsuleHalfHeight, after.Y, 3); // clamped onto flat ground + half-height
    }

    [Fact]
    public void Step_SkipsGhostsAndMigrating()
    {
        var w = new World();
        var sys = new PlayerMovementSystem(Flat, MoveTuning.Default);
        var cmd = new MoveCommand(new Vector2(1f, 0f), true, 0f);

        Entity ghost = SpawnPlayer(w, 2, new Vector3(5f, 0f, 0f), cmd);
        w.Set(ghost, new Ghost { Source = new CellCoord(0, 0) });
        Entity migrating = SpawnPlayer(w, 3, new Vector3(7f, 0f, 0f), cmd);
        w.Set(migrating, new Migrating { Destination = new CellCoord(1, 0) });

        sys.Update(w, 0.1f);

        Assert.Equal(5f, w.Get<ReplicatedPosition>(ghost).Value.X, 3);     // unchanged
        Assert.Equal(7f, w.Get<ReplicatedPosition>(migrating).Value.X, 3); // unchanged
    }

    [Fact]
    public void NullGroundHeight_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlayerMovementSystem(null!, MoveTuning.Default));
    }
}
```

- [ ] **Step 2: Run the test, expect failure (type missing).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PlayerMovementSystemTests`
Expected: compile error / FAIL — `PlayerMovementSystem` does not exist.

- [ ] **Step 3: Implement `PlayerMovementSystem.cs`.**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-cell authoritative movement step. Added to every <see cref="KhaozEngine.Sharding.CellSim"/>'s
/// <see cref="World"/> by <see cref="ShardedWorldServer"/>, so <see cref="KhaozEngine.Sharding.ShardHost.Tick"/>
/// runs it for every cell (fanned across the opt-in scheduler - cells are disjoint worlds, so the result is
/// scheduler-independent). For each owned entity carrying a <see cref="PendingMove"/> it advances the
/// <see cref="ReplicatedPosition"/> via the shared <see cref="CharacterMovement.Step"/> (the same step the
/// single-<see cref="World"/> <see cref="WorldServer"/> and the client's prediction run, so they stay in
/// lockstep). Read-only <see cref="Ghost"/>s and in-flight <see cref="Migrating"/> entities are skipped: the
/// owning cell is the sole simulator. Stateless - one instance is shared across all cells.
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;

    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
    }

    public void Update(World world, float dt)
    {
        world.ForEach<NetId, ReplicatedPosition, PendingMove>((Entity e, ref NetId _, ref ReplicatedPosition pos, ref PendingMove move) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;   // owner is the only simulator
            pos.Value = CharacterMovement.Step(pos.Value, move.Command, dt, groundHeight, tuning, groundNormal);
        });
    }
}
```

- [ ] **Step 4: Run the test, expect pass.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PlayerMovementSystemTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.NetWorld/PlayerMovementSystem.cs KhaozEngine.Tests/NetWorld/PlayerMovementSystemTests.cs
git commit -m "networld: PlayerMovementSystem - per-cell CharacterMovement step over owned players"
```

---

### Task 3: `IWorldPersistenceHost` + reuse `WorldPersistence` unchanged

**Files:**
- Create: `KhaozEngine.NetWorld/IWorldPersistenceHost.cs`
- Modify: `KhaozEngine.NetWorld/WorldServer.cs` (class declaration only)
- Modify: `KhaozEngine.NetWorld/WorldPersistence.cs` (ctor + field type)

**Interfaces:**
- Produces: `public interface IWorldPersistenceHost` with members `event Action<int,string>? PlayerJoined; event Action<int,string,PlayerMoveState>? PlayerLeaving; void SetPlayerState(int slot, in PlayerMoveState state); IReadOnlyCollection<int> JoinedSlots { get; } bool TryGetAccountId(int slot, out string accountId); bool TryGetPlayerState(int slot, out PlayerMoveState state);`
- `WorldServer` and `ShardedWorldServer` both implement it. `WorldPersistence(IWorldPersistenceHost, IWorldStore, WorldPersistenceConfig?)`.

- [ ] **Step 1: Create `IWorldPersistenceHost.cs`.**

```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="WorldPersistence"/> drives, so the same persistence wiring (load-on-join,
/// save-on-leave, periodic dirty snapshot, keyed <c>player:{accountId}</c>) serves both the single-<see cref="World"/>
/// <see cref="WorldServer"/> and the multi-cell <see cref="ShardedWorldServer"/>. Player-keyed and cell-agnostic:
/// <see cref="SetPlayerState"/> places a loaded player at its saved position wherever that falls (a sharded host
/// relocates it to the containing cell on its next handoff pass).
/// </summary>
public interface IWorldPersistenceHost
{
    /// <summary>Raised after a player entity has spawned: (slot, accountId). Persistence loads the saved record here.</summary>
    event Action<int, string>? PlayerJoined;

    /// <summary>Raised just before a player despawns: (slot, accountId, final state). Persistence saves the final state here.</summary>
    event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <summary>Overrides a joined player's authoritative state (load-on-join placement). No-op for an unknown slot.</summary>
    void SetPlayerState(int slot, in PlayerMoveState state);

    /// <summary>The slots of all currently joined players.</summary>
    IReadOnlyCollection<int> JoinedSlots { get; }

    /// <summary>The account id for a joined slot (connect token or fallback).</summary>
    bool TryGetAccountId(int slot, out string accountId);

    /// <summary>The current authoritative movement state for a joined slot.</summary>
    bool TryGetPlayerState(int slot, out PlayerMoveState state);
}
```

- [ ] **Step 2: Mark `WorldServer` as implementing it.** In `WorldServer.cs`, change the class declaration:

```csharp
public sealed class WorldServer : IWorldPersistenceHost
```

(WorldServer already declares every member with the exact signatures; no other change.)

- [ ] **Step 3: Point `WorldPersistence` at the interface.** In `WorldPersistence.cs`, change the field and constructor parameter from `WorldServer` to `IWorldPersistenceHost`:

```csharp
    private readonly IWorldPersistenceHost server;
```

```csharp
    public WorldPersistence(IWorldPersistenceHost server, IWorldStore store, WorldPersistenceConfig? config = null)
```

(Body unchanged - it only uses interface members.)

- [ ] **Step 4: Build + run the existing NetWorld persistence tests (must stay green).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "WorldPersistenceTests|WorldServerPersistenceHooksTests"`
Expected: PASS (unchanged behaviour; WorldServer still satisfies the ctor).

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.NetWorld/IWorldPersistenceHost.cs KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.NetWorld/WorldPersistence.cs
git commit -m "networld: extract IWorldPersistenceHost so WorldPersistence serves both server shapes"
```

---

### Task 4: `ShardedWorldServer` core (spawn, route, tick, handoff, serve)

**Files:**
- Create: `KhaozEngine.NetWorld/ShardedWorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/ShardedWorldServerTests.cs`

**Interfaces:**
- Consumes: `ShardHost`, `CellSim`, `CellCoord`, `NetServer`, `AllowAllAuthenticator`, `RemoteCommandQueue<MoveCommand>`, `MoveProtocol`, `ReplicatedPosition`, `PendingMove`, `PlayerMovementSystem`, `PlayerMoveSimulator` (spawn clamp), `IJobScheduler`.
- Produces:
  - `public sealed class ShardedWorldServerConfig` with `float TickSeconds=1f/30f; float CellSize=60f; float OverlapMargin=24f; float InterestRadius=24f; int MaxPlayers=64; Func<int,Vector3>? SpawnPosition;`
  - `public sealed class ShardedWorldServer : IWorldPersistenceHost` with ctor `(INetTransport transport, ShardedWorldServerConfig config, Func<float,float,float> groundHeight, MoveTuning tuning, Func<float,float,Vector3>? groundNormal=null)` and members `ShardHost Host { get; }`, `ReplicationRegistry Registry { get; }`, `int PlayerCount { get; }`, `bool TryGetPlayerNetId(int slot, out int netId)`, `IJobScheduler Scheduler { get; set; }`, `void Poll()`, `void Tick(float dt)`, plus the `IWorldPersistenceHost` surface.

- [ ] **Step 1: Write the failing test.** Create `KhaozEngine.Tests/NetWorld/ShardedWorldServerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerTests
{
    private static float Flat(float x, float z) => 0f;

    // Small cells so a player crosses a boundary in a handful of ticks.
    private static ShardedWorldServerConfig SmallCells(Func<int, Vector3>? spawn = null) => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
    };

    private static int JoinClient(ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg)
    {
        for (int i = 0; i < 200; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(cfg.TickSeconds);
            if (client.Slot >= 0 && server.TryGetPlayerNetId(client.Slot, out _)) return client.Slot;
        }
        throw new Xunit.Sdk.XunitException("client never joined");
    }

    private static readonly MoveCommand East = new(new Vector2(1f, 0f), run: true, cameraYaw: 0f);

    [Fact]
    public void Join_SpawnsPlayer_OwnedByItsCell()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(5f, 0f, 5f));   // cell (0,0)
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct);

        int slot = JoinClient(server, client, cfg);
        Assert.True(server.TryGetPlayerNetId(slot, out int netId));
        Assert.Equal(1, server.PlayerCount);
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out _));
        Assert.Equal(new CellCoord(0, 0), cell.Coord);
    }

    [Fact]
    public void Crossing_Boundary_OwnedByExactlyOneCell_NetIdStable_PositionContinuous()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(8f, 0f, 5f));   // cell (0,0), near east edge x=10
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct);
        int slot = JoinClient(server, client, cfg);
        Assert.True(server.TryGetPlayerNetId(slot, out int netId));

        float maxStep = MoveTuning.Default.RunSpeed * cfg.TickSeconds * 1.5f;
        bool crossed = false;
        float prevX = OwnedX(server, netId);
        for (int i = 0; i < 120; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, East), NetChannelReliability.ReliableOrdered);
            server.Poll();
            server.Tick(cfg.TickSeconds);
            client.Poll();

            Assert.Equal(1, server.Host.OwnerCount(netId));        // never 0 (loss) or 2 (dup)
            Assert.True(server.TryGetPlayerNetId(slot, out int stillNetId));
            Assert.Equal(netId, stillNetId);                        // NetId stable across handoff

            float x = OwnedX(server, netId);
            Assert.True(x - prevX <= maxStep + 1e-3f, $"position jumped {prevX}->{x} (handoff teleport)");
            prevX = x;
            if (server.Host.TryGetOwner(netId, out CellSim owner, out _) && owner.Coord.X >= 1) crossed = true;
        }
        Assert.True(crossed, "player never crossed into the neighbour cell");
    }

    [Fact]
    public void Ghosting_AdjacentPlayersSeeEachOther_FarPlayerDoesNot()
    {
        var hub = new InMemoryHub();
        // slot0 @ x=8.5 (cell0), slot1 @ x=11.5 (cell1) - 3 m apart across x=10; slot2 @ x=55 (cell5) far.
        var cfg = SmallCells(slot => slot switch
        {
            0 => new Vector3(8.5f, 0f, 5f),
            1 => new Vector3(11.5f, 0f, 5f),
            _ => new Vector3(55f, 0f, 5f),
        });
        var server = new ShardedWorldServer(hub.Server, cfg, Flat, MoveTuning.Default);
        var c0 = new NetClient(hub.CreateClient());
        var c1 = new NetClient(hub.CreateClient());
        var c2 = new NetClient(hub.CreateClient());

        for (int i = 0; i < 50; i++) { c0.Poll(); c1.Poll(); c2.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.Equal(3, server.PlayerCount);
        Assert.True(server.TryGetPlayerNetId(0, out int n0));
        Assert.True(server.TryGetPlayerNetId(1, out int n1));
        Assert.True(server.TryGetPlayerNetId(2, out int n2));

        Assert.True(Sees(server, slot: 0, n1));   // adjacent across border -> ghost in home AoI
        Assert.True(Sees(server, slot: 1, n0));
        Assert.False(Sees(server, slot: 2, n0));  // far player pulls no distant ghost
        Assert.False(Sees(server, slot: 2, n1));
        Assert.True(Sees(server, slot: 2, n2));   // ...but sees itself
    }

    [Fact]
    public void Determinism_SingleThreaded_Matches_ThreadPool()
    {
        List<(Vector3 pos, CellCoord cell)> Run(IJobScheduler sched)
        {
            var hub = new InMemoryHub();
            var cfg = SmallCells(slot => new Vector3(7f + slot * 2f, 0f, 5f));
            var server = new ShardedWorldServer(hub.Server, cfg, Flat, MoveTuning.Default) { Scheduler = sched };
            var a = new NetClient(hub.CreateClient());
            var b = new NetClient(hub.CreateClient());
            for (int i = 0; i < 60; i++) { a.Poll(); b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }

            var ar = new MoveCommand(new Vector2(1f, 0f), true, 0f);
            var br = new MoveCommand(new Vector2(0f, 1f), false, 0f);
            for (int i = 0; i < 120; i++)
            {
                a.Send(MoveProtocol.EncodeMove(i, ar), NetChannelReliability.ReliableOrdered);
                b.Send(MoveProtocol.EncodeMove(i, br), NetChannelReliability.ReliableOrdered);
                a.Poll(); b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            }
            var outp = new List<(Vector3, CellCoord)>();
            foreach (int slot in new[] { 0, 1 })
            {
                Assert.True(server.TryGetPlayerNetId(slot, out int id));
                Assert.True(server.Host.TryGetOwner(id, out CellSim cell, out Entity e));
                outp.Add((cell.World.Get<ReplicatedPosition>(e).Value, cell.Coord));
            }
            return outp;
        }

        Assert.Equal(Run(new SingleThreadedJobScheduler()), Run(new ThreadPoolJobScheduler()));
    }

    private static float OwnedX(ShardedWorldServer server, int netId)
    {
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        return cell.World.Get<ReplicatedPosition>(e).Value.X;
    }

    private static bool Sees(ShardedWorldServer server, int slot, int netId)
    {
        byte[] snap = server.Host.SnapshotForClient(slot, server: out _ /* placeholder */);
        return false; // replaced below
    }
}
```

  NOTE: the `Sees` helper above is a placeholder. Replace it with this real implementation (the test file's final `Sees`):

```csharp
    private static bool Sees(ShardedWorldServer server, int slot, int netId)
    {
        byte[] snap = server.Host.SnapshotForClient(slot, ((ShardedWorldServerConfigProbe)0).Radius);
        var view = new ClientReplicationView(server.Registry);
        view.Apply(new World(), snap);
        return view.TryGetEntity(netId, out _);
    }
```

  That still references a nonexistent probe. Use the simplest correct form instead - the interest radius is fixed at 4f in `SmallCells`:

```csharp
    private static bool Sees(ShardedWorldServer server, int slot, int netId)
    {
        byte[] snap = server.Host.SnapshotForClient(slot, 4f);
        var view = new ClientReplicationView(server.Registry);
        view.Apply(new World(), snap);
        return view.TryGetEntity(netId, out _);
    }
```

  (Delete the two placeholder `Sees` drafts; keep only the last one. `using KhaozEngine.Replication;` is already imported for `ClientReplicationView`/`ReplicationRegistry`.)

- [ ] **Step 2: Run the test, expect failure (type missing).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ShardedWorldServerTests`
Expected: compile error - `ShardedWorldServer` does not exist.

- [ ] **Step 3: Implement `ShardedWorldServer.cs`.**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="ShardedWorldServer"/>.</summary>
public sealed class ShardedWorldServerConfig
{
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>World-grid cell edge length (world units). Align to the terrain/streaming chunk grid.</summary>
    public float CellSize { get; init; } = 60f;
    /// <summary>Border-overlap distance for ghosting. Must be &gt;= <see cref="InterestRadius"/>.</summary>
    public float OverlapMargin { get; init; } = 24f;
    /// <summary>Per-client area-of-interest radius (world units).</summary>
    public float InterestRadius { get; init; } = 24f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 64;
    /// <summary>Per-slot spawn position (XZ used; Y is ground-clamped). Default spreads players along +X near origin.</summary>
    public Func<int, Vector3>? SpawnPosition { get; init; }
}

/// <summary>
/// Multi-cell authoritative movement server: the single-<see cref="World"/> <see cref="WorldServer"/> stack run
/// across a <see cref="ShardHost"/> grid of cells, so the world scales to many players / a large area without one
/// giant world, while the <see cref="WorldClient"/> and <see cref="MoveProtocol"/> stay unchanged. Each tick it
/// routes every client's <see cref="MoveCommand"/> to the cell that <b>owns</b> its player, steps every cell's
/// <see cref="PlayerMovementSystem"/> via <see cref="ShardHost.Tick"/> (ground-clamped, scheduler-fanned),
/// transfers authority for boundary crossers exactly-once (<see cref="ShardHost.ProcessHandoffs"/>), refreshes
/// border ghosts (<see cref="ShardHost.SyncGhosts"/>), then serves each client its single <b>home-cell</b>
/// area-of-interest snapshot (owned + ghosts) framed with the existing <c>[localNetId][ack]</c> header. A player's
/// <see cref="NetId"/> is stable across handoff, so the client's replication view + prediction continue without a
/// respawn. Headless, transport-injected. Persistence is the shipped <see cref="WorldPersistence"/> via
/// <see cref="IWorldPersistenceHost"/>, player-keyed across cells.
/// </summary>
public sealed class ShardedWorldServer : IWorldPersistenceHost
{
    private readonly ShardedWorldServerConfig config;
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly ShardHost host;
    private readonly NetServer net;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly PlayerMovementSystem movement;
    private readonly PlayerMoveSimulator spawnClamp;

    private readonly Dictionary<int, int> netIdBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private readonly Dictionary<int, string> accountIdBySlot = new();
    private readonly HashSet<CellCoord> wiredCells = new();
    private int nextNetId = 1;

    public ShardedWorldServer(INetTransport transport, ShardedWorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning, Func<float, float, Vector3>? groundNormal = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        if (config.InterestRadius > config.OverlapMargin)
            throw new ArgumentException(
                $"InterestRadius {config.InterestRadius} must be <= OverlapMargin {config.OverlapMargin} so the home cell can hold the full AoI as ghosts.",
                nameof(config));

        movement = new PlayerMovementSystem(groundHeight, tuning, groundNormal);
        spawnClamp = new PlayerMoveSimulator(groundHeight, tuning, groundNormal);
        host = new ShardHost(
            cellSize: config.CellSize,
            tickSeconds: config.TickSeconds,
            registry: registry,
            interestCellSize: config.CellSize,
            overlapMargin: config.OverlapMargin,
            positionAccessor: PositionAccessor);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
    }

    /// <summary>The shard topology (cells, ownership, ghosts).</summary>
    public ShardHost Host => host;
    /// <summary>The replicated-component registry; clients build the matching one via MoveProtocol.</summary>
    public ReplicationRegistry Registry => registry;
    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out int netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>The worker pool the per-cell movement tick fans across (defaults to single-threaded).</summary>
    public IJobScheduler Scheduler { get => host.Scheduler; set => host.Scheduler = value; }

    public event Action<int, string>? PlayerJoined;
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    public bool TryGetAccountId(int slot, out string accountId) => accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>The current authoritative state for a joined slot, read from its owning cell (cell-agnostic).</summary>
    public bool TryGetPlayerState(int slot, out PlayerMoveState state)
    {
        if (netIdBySlot.TryGetValue(slot, out int netId)
            && host.TryGetOwner(netId, out CellSim cell, out Entity e)
            && cell.World.TryGet(e, out ReplicatedPosition rp))
        {
            state = new PlayerMoveState { Position = rp.Value };
            return true;
        }
        state = default;
        return false;
    }

    /// <summary>Places a joined player at <paramref name="state"/> (load-on-join). Writes its owning cell's
    /// <see cref="ReplicatedPosition"/>; if that position falls in another cell the next <see cref="Tick"/>'s
    /// handoff relocates the entity there (NetId stable). No-op for an unknown slot.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state)
    {
        if (netIdBySlot.TryGetValue(slot, out int netId) && host.TryGetOwner(netId, out CellSim cell, out Entity e))
            cell.World.Set(e, new ReplicatedPosition { Value = state.Position });
    }

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot, ev.Data);
                    break;
                case ServerSessionEventKind.Left:
                    OnLeave(ev.Slot);
                    break;
                case ServerSessionEventKind.Data:
                    if (netIdBySlot.ContainsKey(ev.Slot)
                        && MoveProtocol.TryDecodeMove(ev.Data, out int seq, out MoveCommand cmd))
                        commands.Store(ev.Slot, seq, cmd);
                    break;
            }
        }
    }

    /// <summary>Steps one authoritative server frame across every cell, then serves each client its home-cell AoI.</summary>
    public void Tick(float dt)
    {
        var slots = new List<int>(netIdBySlot.Keys);

        // 1. Route each client's input to the cell that owns its player.
        foreach (int slot in slots)
        {
            MoveCommand cmd = commands.Dequeue(slot, out int ack);
            lastAckBySlot[slot] = ack;
            if (host.TryGetOwner(netIdBySlot[slot], out CellSim cell, out Entity e))
                cell.World.Set(e, new PendingMove { Command = cmd });
        }

        // 2. Make sure every (possibly newly-created) cell runs the movement system.
        foreach (CellSim cell in host.Cells) EnsureWired(cell);

        // 3. Authoritative movement: one fixed sub-tick per frame, fanned across the scheduler.
        host.Tick(dt, maxTicksPerFrame: 1);

        // 4. Authority follows entities across boundaries (exactly-once), then refresh border ghosts.
        host.ProcessHandoffs();
        host.SyncGhosts();

        // 5. Serve each client its home-cell area-of-interest, framed for the unchanged WorldClient.
        foreach (int slot in slots)
        {
            if (!netIdBySlot.TryGetValue(slot, out int netId)) continue;
            byte[] snapshot = host.SnapshotForClient(slot, config.InterestRadius);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        }
    }

    private void OnJoin(int slot, byte[] token)
    {
        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = spawnClamp.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        int netId = nextNetId++;
        Entity e = host.SpawnAt(state.Position.X, state.Position.Z, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new ReplicatedPosition { Value = state.Position });
        EnsureWired(cell);

        string accountId = token is { Length: > 0 } ? Encoding.UTF8.GetString(token) : $"guest:{slot}";
        netIdBySlot[slot] = netId;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;
        host.BindClient(slot, netId);

        PlayerJoined?.Invoke(slot, accountId);
    }

    private void OnLeave(int slot)
    {
        if (netIdBySlot.TryGetValue(slot, out int netId))
        {
            if (accountIdBySlot.TryGetValue(slot, out string? acct) && TryGetPlayerState(slot, out PlayerMoveState final))
                PlayerLeaving?.Invoke(slot, acct, final);
            if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
                cell.World.Despawn(e);
        }
        host.UnbindClient(slot);
        netIdBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
        accountIdBySlot.Remove(slot);
    }

    private void EnsureWired(CellSim cell)
    {
        if (wiredCells.Add(cell.Coord)) cell.World.AddSystem(movement);
    }

    private static bool PositionAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out ReplicatedPosition p)) { x = p.Value.X; y = p.Value.Z; return true; }
        x = y = 0f;
        return false;
    }
}
```

- [ ] **Step 4: Run the test, expect pass.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ShardedWorldServerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.Tests/NetWorld/ShardedWorldServerTests.cs
git commit -m "networld: ShardedWorldServer - movement stack over a ShardHost cell grid"
```

---

### Task 5: WorldClient continuity through a handoff (client unchanged)

**Files:**
- Modify: `KhaozEngine.Tests/NetWorld/ShardedWorldServerTests.cs` (add test)

**Interfaces:**
- Consumes: `ShardedWorldServer`, the real `WorldClient`, `EntityRenderState`.

- [ ] **Step 1: Write the failing/championship test.** Add to `ShardedWorldServerTests.cs`:

```csharp
    [Fact]
    public void RealWorldClient_WalksAcrossBoundary_NoSnap_NetIdStable()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(8f, 0f, 5f));   // cell (0,0), near east edge
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = cfg.TickSeconds });

        // Connect + first serves to seed the prediction basis.
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Assert.True(client.LocalNetId > 0);
        int localNetId = client.LocalNetId;

        float maxStep = MoveTuning.Default.RunSpeed * cfg.TickSeconds * 2f;
        float prevX = LocalX(client);
        bool crossed = false;
        for (int i = 0; i < 120; i++)
        {
            client.SendInput(East);
            server.Poll();
            server.Tick(cfg.TickSeconds);
            client.Poll();

            Assert.Equal(localNetId, client.LocalNetId);          // stable identity across the migrate
            float x = LocalX(client);
            Assert.True(x - prevX <= maxStep + 1e-3f, $"client view snapped {prevX}->{x} at handoff");
            prevX = x;
            if (server.TryGetPlayerNetId(client.Slot, out int id) &&
                server.Host.TryGetOwner(id, out CellSim owner, out _) && owner.Coord.X >= 1) crossed = true;
        }
        Assert.True(crossed, "player never crossed the boundary");
        Assert.True(LocalX(client) > 10f, "client's local avatar should be past the x=10 boundary");
    }

    private static float LocalX(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.X;
        throw new Xunit.Sdk.XunitException("no local entity in client snapshot");
    }
```

- [ ] **Step 2: Run it.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "ShardedWorldServerTests.RealWorldClient_WalksAcrossBoundary_NoSnap_NetIdStable"`
Expected: PASS (no `WorldClient` change needed; if it snaps, investigate before adding any re-anchor - the spec says add the smallest re-anchor ONLY if a real gap is found).

- [ ] **Step 3: Commit.**

```bash
git add KhaozEngine.Tests/NetWorld/ShardedWorldServerTests.cs
git commit -m "networld: verify unchanged WorldClient stays continuous across a sharded handoff"
```

---

### Task 6: Persistence across cells (load-on-join in the right cell, restart-survival)

**Files:**
- Create: `KhaozEngine.Tests/NetWorld/ShardedWorldPersistenceTests.cs`

**Interfaces:**
- Consumes: `ShardedWorldServer`, `WorldPersistence`, `InMemoryWorldStore`, `WorldPersistenceConfig`.

- [ ] **Step 1: Write the test.** Create `KhaozEngine.Tests/NetWorld/ShardedWorldPersistenceTests.cs`:

```csharp
using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldPersistenceTests
{
    private static float Flat(float x, float z) => 0f;

    private static ShardedWorldServerConfig Cfg(Func<int, Vector3>? spawn = null) => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
    };

    [Fact]
    public void LoadOnJoin_SpawnsAtSavedPosition_InTheContainingCell()
    {
        var store = new InMemoryWorldStore();
        byte[] token = Encoding.UTF8.GetBytes("acct-1");

        // Pre-seed a save at x=35 (cell 3), z=5 - a different cell from the default spawn at x=5 (cell 0).
        var saved = new PlayerMoveState { Position = new Vector3(35f, MoveTuning.Default.CapsuleHalfHeight, 5f) };
        store.SaveAsync("player:acct-1", PlayerRecord.From(saved).Encode()).GetAwaiter().GetResult();

        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = Cfg(_ => new Vector3(5f, 0f, 5f));          // default spawn cell (0,0)
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        var client = new NetClient(ct, token);

        // Join, then drive enough frames for the async load to apply AND the handoff to relocate the entity.
        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }

        Assert.True(server.TryGetPlayerNetId(client.Slot, out int netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.Equal(new CellCoord(3, 0), cell.Coord);        // ended owned by the saved position's cell
        Vector3 pos = cell.World.Get<ReplicatedPosition>(e).Value;
        Assert.Equal(35f, pos.X, 2);
        Assert.Equal(5f, pos.Z, 2);
    }

    [Fact]
    public void SaveOnLeave_ThenRestart_RestoresPositionAcrossCells()
    {
        var store = new InMemoryWorldStore();          // shared across the two "runs" = a restart
        byte[] token = Encoding.UTF8.GetBytes("acct-roam");

        // Run 1: join at cell 0, walk east into cell 1+, leave (save-on-leave from the owner cell).
        Vector3 leftAt;
        {
            var (st, ct) = LoopbackTransport.CreatePair();
            var cfg = Cfg(_ => new Vector3(8f, 0f, 5f));
            var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
            var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
            var client = new NetClient(ct, token);
            for (int i = 0; i < 60; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }

            var east = new MoveCommand(new Vector2(1f, 0f), true, 0f);
            for (int i = 0; i < 120; i++)
            {
                client.Send(MoveProtocol.EncodeMove(i, east), NetChannelReliability.ReliableOrdered);
                client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds);
            }
            Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState before));
            leftAt = before.Position;
            Assert.True(leftAt.X > 10f, "should have crossed into cell 1+");

            client.Disconnect();
            for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }
            persistence.FlushAsync().GetAwaiter().GetResult();   // ensure save-on-leave landed
        }

        // Run 2: fresh server, SAME store. Same account reconnects, lands back where it left, in that cell.
        {
            var (st, ct) = LoopbackTransport.CreatePair();
            var cfg = Cfg(_ => new Vector3(5f, 0f, 5f));        // default spawn is cell 0
            var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
            var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
            var client = new NetClient(ct, token);
            for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }

            Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState restored));
            Assert.Equal(leftAt.X, restored.Position.X, 1);
            Assert.Equal(leftAt.Z, restored.Position.Z, 1);
            Assert.True(server.TryGetPlayerNetId(client.Slot, out int netId));
            Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out _));
            Assert.True(cell.Coord.X >= 1, "restored into the cell containing the saved position");
        }
    }
}
```

  (Check during impl that `NetClient` exposes a `Disconnect()`; if the method name differs, use the transport's disconnect or drop the client and let the server's Left event fire. If no clean disconnect is available headlessly, drive save via `persistence.SaveDirtyPass()` + `FlushAsync()` instead of leave, and assert restore in Run 2 - the restart-survival property still holds.)

- [ ] **Step 2: Run it.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ShardedWorldPersistenceTests`
Expected: PASS (2 tests).

- [ ] **Step 3: Commit.**

```bash
git add KhaozEngine.Tests/NetWorld/ShardedWorldPersistenceTests.cs
git commit -m "networld: persistence across cells - load-on-join in the right cell + restart-survival"
```

---

### Task 7: Demo - `NetworkedWalkServer` becomes a multi-cell shard host

**Files:**
- Modify: `NetworkedWalkServer/Program.cs`

**Interfaces:**
- Consumes: `ShardedWorldServer`, `ShardedWorldServerConfig`, unchanged `WorldPersistence` + `SqliteWorldStore`.

- [ ] **Step 1: Rewrite `NetworkedWalkServer/Program.cs`** to drive a 3x3-ish shard grid over `TerrainPresets.Clearing()` (`CellSize` 60 = one terrain chunk; `OverlapMargin`/`InterestRadius` 24; spawn two players adjacent across the x=60 border on the walkable meadow):

```csharp
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;
using KhaozEngine.WorldStore.Sqlite;

// Headless authoritative server for the networked walkable slice, now SHARDED: the shipped analytic terrain
// (TerrainPresets.Clearing) is the ground, and a multi-cell ShardedWorldServer runs PlayerMoveSimulator across a
// grid of authoritative cells (cellSize 60 = one terrain chunk) over a LiteNetLib UDP socket. Players are owned by
// the cell containing them; walking across a cell boundary hands authority off seamlessly (NetId stable, no hitch),
// and two players in adjacent cells see each other via border ghosting. Players persist to an embedded SQLite DB via
// WorldPersistence (keyed player:{accountId}, cell-agnostic), so disconnect/reconnect (or a process restart) restores
// position - in whatever cell now contains it. Connect two NetworkedWalkSample clients to see it.
// Usage: NetworkedWalkServer [port] [dbPath].
int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47700;
string dbPath = args.Length > 1 ? args[1] : "networked-walk-world.db";

var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new ShardedWorldServerConfig
{
    TickSeconds = 1f / 30f,
    CellSize = 60f,              // one terrain chunk (TerrainChunkRegion.DefaultSize) per cell
    OverlapMargin = 24f,        // border ghost band; >= InterestRadius
    InterestRadius = 24f,
    MaxPlayers = 16,
    // Spread joiners across the central cells on the walkable meadow (z<48): slot 0 in cell (0,0), slot 1 in (1,0),
    // both near the shared x=60 border and within view of each other. Walk one east to cross the border.
    SpawnPosition = slot => new Vector3(48f + slot * 20f, 0f, 24f),
};

using var transport = new LiteNetLibServerTransport(port);
var server = new ShardedWorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

// Persist players keyed by the account token the client presents in its Hello. Swap SqliteWorldStore for
// SqlServerWorldStore (KhaozEngine.WorldStore.SqlServer) to persist to Azure SQL instead - same IWorldStore.
using var store = new SqliteWorldStore($"Data Source={dbPath}");
var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 10f });

var clock = new FixedTickHost(config.TickSeconds);
var sw = Stopwatch.StartNew();
double last = 0;
Console.WriteLine($"Sharded walk server on UDP {port} (tick {1f / config.TickSeconds:0} Hz, cellSize {config.CellSize}), persisting to {dbPath}. Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    persistence.FlushAsync().GetAwaiter().GetResult();   // save everyone before exit
    Console.WriteLine("Saved world. Bye.");
    Environment.Exit(0);
};

while (true)
{
    server.Poll();
    double now = sw.Elapsed.TotalSeconds;
    float elapsed = (float)(now - last);
    last = now;
    clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));
    persistence.Update(elapsed);
    Thread.Sleep(5);
}
```

- [ ] **Step 2: Build the demo.**

Run: `dotnet build NetworkedWalkServer/NetworkedWalkServer.csproj`
Expected: Build succeeded. (`NetworkedWalkServer.csproj` already references NetWorld + Terrain + WorldStore.Sqlite; no csproj change.)

- [ ] **Step 3: Commit.**

```bash
git add NetworkedWalkServer/Program.cs
git commit -m "demo(networkedwalk): drive a multi-cell ShardedWorldServer over Clearing terrain"
```

---

### Task 8: Full suite green + class-doc/description sweep

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldServer.cs` (class-doc sentence)
- Modify: `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj` (`<Description>`)

- [ ] **Step 1: Update the stale "sharding folds in later" prose.** In `WorldServer.cs` class summary, change the trailing sentence "Multi-cell sharding folds in with world streaming later; this is the single-world slice." to: "The multi-cell variant is <see cref="ShardedWorldServer"/>; this is the single-world slice."

- [ ] **Step 2: Update the csproj `<Description>`** final sentence from "Single-World slice of the MMO overworld; multi-cell sharding folds in with world streaming later." to "Single-World WorldServer plus the multi-cell ShardedWorldServer (the same movement stack run across a ShardHost cell grid: per-cell movement, exactly-once handoff, border ghosting, home-cell AoI), sharing WorldPersistence via IWorldPersistenceHost."

- [ ] **Step 3: Run the WHOLE test suite (excluding live sockets).**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"`
Expected: PASS, 0 failures. (If any pre-existing test referenced the old `WorldServer`-typed `WorldPersistence` ctor, it still compiles via the interface.)

- [ ] **Step 4: Commit.**

```bash
git add KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj
git commit -m "networld: point single-World doc at ShardedWorldServer; refresh package description"
```

---

### Task 9: Release - version bump, changelog, doc sweep, pack

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine/CLAUDE.md` (i.e. `./CLAUDE.md`).

- [ ] **Step 1: Bump the version.** In `Directory.Build.props`: `<KhaozEngineVersion>7.49.1</KhaozEngineVersion>` → `7.50.0`. (First re-confirm `git fetch && git tag | sort -V | tail` did not already publish 7.50.0; if so, bump past it.)

- [ ] **Step 2: `CHANGELOG.md`** - add a newest-first entry under the top:

```markdown
## 7.50.0

- **Multi-cell server sharding (overworld sub-project 6b).** New `KhaozEngine.NetWorld.ShardedWorldServer`
  (+ `ShardedWorldServerConfig`): the authoritative overworld movement stack run across a `KhaozEngine.Sharding`
  `ShardHost` grid of cells instead of one giant `World`. Each tick routes every client's `MoveCommand` to the
  cell that owns its player, steps each cell's new `PlayerMovementSystem` (`CharacterMovement.Step`, ground-clamped)
  via `ShardHost.Tick` (scheduler-fanned across cores), transfers authority for boundary crossers exactly-once
  (`ProcessHandoffs`, `NetId` stable), refreshes border ghosts (`SyncGhosts`), then serves each client its single
  home-cell area-of-interest snapshot (owned + ghosts) framed with the existing `[localNetId][ack]` header. The
  `WorldClient` and `MoveProtocol` are unchanged: a player's `NetId` is stable across handoff, so the client's
  replication view + prediction continue without a respawn. `KhaozEngine.NetWorld` now references
  `KhaozEngine.Sharding`.
- **`IWorldPersistenceHost`.** Extracted from `WorldServer` so the shipped `WorldPersistence` (load-on-join,
  save-on-leave, periodic dirty snapshot, keyed `player:{accountId}`) drives both the single-`World` `WorldServer`
  and the new `ShardedWorldServer` unchanged. Player-keyed and cell-agnostic: a loaded player spawns at its saved
  position in whatever cell contains it. `WorldServer` now implements `IWorldPersistenceHost` (no behaviour change).
- **`PendingMove`** component: the per-tick command a cell's `PlayerMovementSystem` applies to an owned player
  (server-local, not replication-registered, not carried across a handoff).
- Demo: `NetworkedWalkServer` now drives a multi-cell `ShardedWorldServer` (cellSize 60 = one terrain chunk) over
  `TerrainPresets.Clearing()`; `NetworkedWalkSample` (client) is unchanged.
```

- [ ] **Step 3: `CHANGENOTES.md`** - add a newest-first one-line digest:

```markdown
- 7.50.0: Multi-cell server sharding (overworld 6b). NetWorld.ShardedWorldServer runs the movement stack across a
  Sharding ShardHost cell grid (per-cell PlayerMovementSystem, exactly-once handoff, border ghosting, home-cell AoI)
  with the WorldClient/MoveProtocol unchanged; WorldPersistence reused via the new IWorldPersistenceHost, player-keyed
  across cells. NetWorld now depends on Sharding.
```

- [ ] **Step 4: Guard declarations (the 3 the script checks).**
  - `docs/CONSUMERS.md`: set `**Engine current version:** ` to `7.50.0`. Update the Server-umbrella row prose to note `NetWorld` now bundles the sharded server (depends on `Sharding`).
  - `docs/ROADMAP.md`: set `Current released version: **7.50.0**`; mark overworld sub-project 6b (multi-cell sharding) done (finishes "6").
  - `README.md`: bump the `<PackageReference ... Version="...">` example(s) to `7.50.0`; if the NetWorld/Server catalog row says "single-World", add the sharded server.

- [ ] **Step 5: `docs/USING-KHAOZENGINE.md`** - add a "Sharded authoritative server" subsection near the networked-world / WorldServer usage, e.g.:

```markdown
### Sharded authoritative server (many players / a large world)

`KhaozEngine.NetWorld.ShardedWorldServer` runs the same movement stack as `WorldServer` but across a
`KhaozEngine.Sharding.ShardHost` grid of authoritative cells, so the world scales past a single `World`. The
`WorldClient` and `MoveProtocol` are identical - a client cannot tell it is talking to a sharded server.

```csharp
var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new ShardedWorldServerConfig
{
    CellSize = 60f,          // align to the terrain/streaming chunk grid
    OverlapMargin = 24f,     // border ghost band; must be >= InterestRadius
    InterestRadius = 24f,
};
var server = new ShardedWorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

// Optional: tick cells across cores (deterministic - same result as single-threaded).
server.Scheduler = new ThreadPoolJobScheduler();

// Persistence is identical to the single-World server (player-keyed, cell-agnostic):
var persistence = new WorldPersistence(server, store);

while (running)
{
    server.Poll();
    server.Tick(config.TickSeconds);
    persistence.Update(config.TickSeconds);
}
```

Walking across a cell boundary hands authority to the neighbour cell exactly-once with the player's `NetId`
preserved (no respawn, no hitch); two players in adjacent cells see each other via border ghosting. `WorldServer`
remains the single-`World` option for a modest player count.
```

- [ ] **Step 6: `KhaozEngine/CLAUDE.md` package map.** In the NetWorld bullet of the package catalog: add `ShardedWorldServer` beside `WorldServer`, note `NetWorld` now also deps `Sharding`, and mention `IWorldPersistenceHost` as the persistence seam shared by both server shapes. In the Server-umbrella line, note it now bundles the sharded server. Remove any "single-World only" framing for NetWorld.

- [ ] **Step 7: Mechanical stale-prose grep.**

Run: `grep -rIn "single-World\|single-world\|sharding folds in\|folds in with world streaming" --include=*.md --include=*.cs . | grep -v "/obj/" | grep -v "/bin/"`
Fix any remaining doc/comment that still says sharding is future for the overworld server (the single-`World` framing of `WorldServer` itself is fine; only the "sharding is later" claims change).

- [ ] **Step 8: Run the doc-version guard.**

Run: `bash scripts/check-doc-versions.sh`
Expected: all `ok`, exit 0.

- [ ] **Step 9: Full test suite once more.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"`
Expected: PASS, 0 failures.

- [ ] **Step 10: Pack (cumulative into local-feed).**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: succeeds; `KhaozEngine.NetWorld.7.50.0.nupkg` (and the rest) written to `local-feed/`.

- [ ] **Step 11: Commit the release.**

```bash
git add -A
git commit -m "networld(7.50.0): multi-cell server sharding (overworld 6b) + IWorldPersistenceHost"
```

---

### Task 10: Merge, tag, push, clean up

- [ ] **Step 1:** From the main checkout root `/Users/antonio/KhaozEngine`, `git fetch`, confirm `origin/main` has not moved under you and 7.50.0 is free.
- [ ] **Step 2:** Merge `worktree-feature+multicell-sharding` into `main` (no-ff is fine).
- [ ] **Step 3:** Repack from the main root into `local-feed` (the worktree's local-feed is removed on worktree cleanup): `dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 4:** Run the suite once on merged `main`: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"` (green).
- [ ] **Step 5:** `git tag v7.50.0`; push `main` + the tag (CI publishes to GitHub Packages on `v*`).
- [ ] **Step 6:** Remove the worktree + delete the merged branch (local; the branch was never pushed, so no remote branch to delete).

---

## Self-Review

**Spec coverage:**
- Sharded `WorldServer` over `ShardHost` (per-cell movement, ghosting, exactly-once handoff, home-cell AoI, existing `MoveProtocol`): Tasks 1-4, 7.
- `WorldClient` unchanged + NetId-stable handoff continuity: Task 5 (no client change unless a real gap is found).
- `WorldPersistence` player-keyed across cells: Tasks 3, 6.
- Sharded `NetworkedWalkServer` demo; `NetworkedWalkSample` unchanged: Task 7.
- Headless tests over InProcessCellLink + Loopback/InMemoryHub: Tasks 2, 4, 5, 6.
- Spec Testing list: handoff exactly-once (T4), ghosting near/far (T4), AoI = owned+ghosts (T4), movement continuity/no-snap (T5), persistence across cells + restart-survival (T6), multi-cell determinism single-thread vs threadpool (T4).
- Out-of-scope items: none built (no multi-process link, no per-cell world snapshot, no dynamic cells, no NPCs/animation).
- Release: minor bump, no new package, full doc sweep, guard: Tasks 9-10.

**Type consistency:** `ShardedWorldServer`/`ShardedWorldServerConfig`, `PlayerMovementSystem`, `PendingMove`, `IWorldPersistenceHost` names used identically across tasks. `Host`/`Registry`/`Scheduler`/`TryGetPlayerNetId`/`TryGetPlayerState`/`SetPlayerState`/`Poll`/`Tick` signatures match between Task 4's definition and Tasks 5-7's use. `PositionAccessor` reads `ReplicatedPosition` XZ, matching the registry built by `MoveProtocol.CreateRegistry()`.

**Open verification (do during impl, do not pre-assume):**
- `NetClient.Disconnect()` existence (Task 6 note has a fallback).
- `World.AddSystem(ISystem)` runs the system with the owning world passed in on `World.Update(dt)` (confirmed by `ShardHostParallelTests`); sharing one `PlayerMovementSystem` instance across cells is safe because the world is a method parameter.
- `ServerSessionEventKind` / `ServerSessionEvent.Slot/Data/Kind` names (confirmed against `WorldServer.cs`/`MmoServer.cs`).
</content>
</invoke>
