# Networked Overworld Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect the shipped authoritative server stack to the 3D walkable client so two clients see each other walking the same terrain.

**Architecture:** A new render-free `KhaozEngine.Locomotion` leaf holds the pure movement step (`CharacterMovement.Step`); the shipped `CharacterController3D` is refactored to wrap it. A new render-free `KhaozEngine.NetWorld` package holds `PlayerMoveSimulator` (server-authoritative + client-prediction sim), `WorldServer` (single `World` + `NetServer` + `SnapshotWriter`/`InterestGrid` AoI), and `WorldClient` (wraps `NetClient` + `ClientReplicationView` + `ClientPrediction`, exposes `EntityRenderState[]`). Two throwaway sample exes — a headless server and a networked walk client — demonstrate it over LiteNetLib.

**Tech Stack:** net10.0, C# latest, xUnit. KhaozEngine.{Netcode, Replication, Ecs, Simulation, Terrain, Render3D, Game.Render3D}, LiteNetLib UDP transports.

## Global Constraints

- Engine version bumps ONE minor: `7.46.0` → `7.47.0` (`<KhaozEngineVersion>` in `Directory.Build.props`).
- TWO packages added (`Locomotion`, `NetWorld`) → FULL added-package doc sweep (README catalog + repo-layout, `CLAUDE.md` package map + umbrella descriptions, `docs/CONSUMERS.md`, `docs/USING-KHAOZENGINE.md`, the 3 guard declarations, `CHANGELOG.md` + `CHANGENOTES.md`).
- `Locomotion` is a render-free leaf, deps: `KhaozEngine.Primitives` only (System.Numerics). → Foundation umbrella.
- `NetWorld` is render-free + reusable, deps: `Locomotion` + `Netcode` + `Replication` + `Ecs`. → Server umbrella. NOT in the sample.
- Every new behaviour ships with a headless test in `KhaozEngine.Tests` (no GPU/window).
- No em-dashes anywhere. Conventional-commit subjects `area(7.47.0): summary`.
- Both demo exes are `IsPackable=false`.
- Design deviation from the spec's literal type list, permitted by the spec's "Open items": `CharacterMovement.Step` takes/returns `Vector3` (not `PlayerMoveState`); `PlayerMoveState : IPredictedState<PlayerMoveState>` lives in `NetWorld` (not `Locomotion`). Rationale: `IPredictedState`/`ITickSimulator` are in `KhaozEngine.Netcode`; keeping `Step` netcode-free keeps `Locomotion` a pure leaf and the local `CharacterController3D` path networking-free, preserving the acyclic graph and Foundation's "no networking" guarantee.
- Single authoritative `World`. NO multi-cell sharding/handoff, combat, chat, names/UI, NPCs, persistence, prop-as-entity, animation.
- Replication strategy: full-state per-AoI snapshots via `SnapshotWriter.WriteFiltered` + `InterestGrid` + `ClientReplicationView.Apply` (the shipped `MmoServer.SnapshotForClient` pattern). Server stamps a per-client header `[localNetId][ackSeq]` so the client can drive `ClientPrediction.Reconcile`.

---

## File Structure

- `KhaozEngine.Locomotion/` (new pkg): `MoveCommand.cs`, `MoveTuning.cs`, `CharacterMovement.cs`, csproj.
- `KhaozEngine.Game.Render3D/CharacterController3D.cs` (modify): wrap `CharacterMovement.Step`.
- `KhaozEngine.NetWorld/` (new pkg): `PlayerMoveState.cs`, `ReplicatedPosition.cs`, `MoveProtocol.cs`, `PlayerMoveSimulator.cs`, `WorldServer.cs`, `WorldClient.cs`, `EntityRenderState.cs`, csproj.
- `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj` (modify): + Locomotion ref.
- `KhaozEngine.Server/KhaozEngine.Server.csproj` (modify): + NetWorld ref.
- `KhaozEngine.Tests/` : `Locomotion/CharacterMovementTests.cs`, `NetWorld/MoveProtocolTests.cs`, `NetWorld/PlayerMoveSimulatorTests.cs`, `NetWorld/InMemoryHub.cs` (test helper), `NetWorld/WorldRoundTripTests.cs`, `NetWorld/ClientReconcileTests.cs`. Modify csproj refs.
- `NetworkedWalkServer/` (new exe): `Program.cs`, csproj.
- `NetworkedWalkSample/` (new exe): `Program.cs`, csproj, copies `assets/props/**`.
- `KhaozEngine.slnx` (modify): + 4 projects.
- Docs (modify): `Directory.Build.props`, `README.md`, `CLAUDE.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `docs/USING-KHAOZENGINE.md`, `CHANGELOG.md`, `CHANGENOTES.md`.

---

## Task 1: `KhaozEngine.Locomotion` — movement core

**Files:**
- Create: `KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj`, `MoveCommand.cs`, `MoveTuning.cs`, `CharacterMovement.cs`
- Create: `KhaozEngine.Tests/Locomotion/CharacterMovementTests.cs`
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

**Interfaces:**
- Produces: `KhaozEngine.Locomotion.MoveCommand(Vector2 Move, bool Run, float CameraYaw)`; `MoveTuning(float WalkSpeed, float RunSpeed, float CapsuleHalfHeight, float MaxSlopeRadians)` with `MoveTuning.Default`; `Vector3 CharacterMovement.Step(Vector3 position, in MoveCommand cmd, float dt, Func<float,float,float> groundHeight, in MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null)`.

- [ ] **Step 1: csproj**

`KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Locomotion</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>Render-free character locomotion core for KhaozEngine. CharacterMovement.Step is a pure XZ-plane move: a MoveCommand (camera-relative WASD axis + run + camera yaw) advances a Vector3 position over a timestep, normalized diagonals, ground-clamped via a height delegate (feet on the ground) with an optional slope gate. One MoveTuning is the single source of truth shared by the local CharacterController3D, the authoritative server sim, and client-side prediction, so local and networked movement run identical code. Depends only on System.Numerics; no input, no render, no netcode.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: MoveCommand + MoveTuning**

`KhaozEngine.Locomotion/MoveCommand.cs`:
```csharp
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// One frame/tick of locomotion intent. <see cref="Move"/> is the camera-relative input axis
/// (X = right/strafe, Y = forward, each nominally in [-1,1]); <see cref="Run"/> selects run speed;
/// <see cref="CameraYaw"/> is the follow-camera yaw used to resolve the axis into a world direction.
/// The timestep is NOT carried on the command (it is passed to <see cref="CharacterMovement.Step"/>),
/// so a hostile client cannot dilate time and the authoritative server and client prediction step the
/// same fixed dt. <c>default</c> is a no-input (idle) command.
/// </summary>
public readonly struct MoveCommand
{
    public MoveCommand(Vector2 move, bool run, float cameraYaw)
    {
        Move = move;
        Run = run;
        CameraYaw = cameraYaw;
    }

    public Vector2 Move { get; }
    public bool Run { get; }
    public float CameraYaw { get; }

    /// <summary>A no-input command (zero move).</summary>
    public static MoveCommand Idle => default;
}
```

`KhaozEngine.Locomotion/MoveTuning.cs`:
```csharp
using System;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Feel constants for <see cref="CharacterMovement"/>. The single source of truth shared by the local
/// controller, the server sim, and client prediction. <see cref="Default"/> matches the walkable-slice
/// CharacterController3D defaults (walk 3, run 6, half-height 0.9 for a 1.8 m capsule, ~50 deg max slope).
/// </summary>
public readonly record struct MoveTuning(
    float WalkSpeed,
    float RunSpeed,
    float CapsuleHalfHeight,
    float MaxSlopeRadians)
{
    public static MoveTuning Default => new(
        WalkSpeed: 3f,
        RunSpeed: 6f,
        CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: MathF.PI * 50f / 180f);
}
```

- [ ] **Step 3: failing test**

`KhaozEngine.Tests/Locomotion/CharacterMovementTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class CharacterMovementTests
{
    static readonly Func<float, float, float> FlatGround = (x, z) => 0f;
    static readonly MoveTuning Tuning = MoveTuning.Default with { CapsuleHalfHeight = 0f };

    static MoveCommand Cmd(float x, float y, bool run = false, float yaw = 0f) =>
        new(new Vector2(x, y), run, yaw);

    [Fact]
    public void W_at_yaw_zero_moves_toward_negative_z()
    {
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, FlatGround, Tuning);
        Assert.True(p.Z < 0f, p.ToString());
        Assert.True(MathF.Abs(p.X) < 1e-4f, p.ToString());
        Assert.Equal(Tuning.WalkSpeed, MathF.Abs(p.Z), 4);
    }

    [Fact]
    public void Diagonal_is_normalized()
    {
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(1f, 1f), 1f, FlatGround, Tuning);
        float horiz = new Vector2(p.X, p.Z).Length();
        Assert.Equal(Tuning.WalkSpeed, horiz, 3);
    }

    [Fact]
    public void Run_is_faster_than_walk()
    {
        Vector3 walk = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, FlatGround, Tuning);
        Vector3 run = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f, run: true), 1f, FlatGround, Tuning);
        Assert.True(MathF.Abs(run.Z) > MathF.Abs(walk.Z));
        Assert.Equal(Tuning.RunSpeed, MathF.Abs(run.Z), 3);
    }

    [Fact]
    public void Idle_does_not_move_horizontally()
    {
        Vector3 p = CharacterMovement.Step(new Vector3(5f, 0f, 7f), Cmd(0f, 0f), 1f, FlatGround, Tuning);
        Assert.Equal(5f, p.X, 6);
        Assert.Equal(7f, p.Z, 6);
    }

    [Fact]
    public void Y_clamps_to_ground_plus_half_height()
    {
        Func<float, float, float> bumpy = (x, z) => 5f;
        var t = MoveTuning.Default with { CapsuleHalfHeight = 0.9f };
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 0.5f, bumpy, t);
        Assert.Equal(5f + 0.9f, p.Y, 4);
    }

    [Fact]
    public void Camera_relative_yaw_rotates_movement()
    {
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f, yaw: MathF.PI / 2f), 1f, FlatGround, Tuning);
        Assert.True(p.X < 0f, p.ToString());
        Assert.True(MathF.Abs(p.Z) < 1e-3f, p.ToString());
    }

    [Fact]
    public void Step_onto_too_steep_ground_is_rejected()
    {
        Func<float, float, Vector3> steep = (x, z) => Vector3.Normalize(new Vector3(1f, 0.05f, 0f));
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, FlatGround, Tuning, steep);
        Assert.True(MathF.Abs(p.X) < 1e-6f && MathF.Abs(p.Z) < 1e-6f, p.ToString());
    }

    [Fact]
    public void Deterministic_same_inputs_same_output()
    {
        Vector3 a = CharacterMovement.Step(Vector3.Zero, Cmd(1f, 1f, run: true, yaw: 0.7f), 0.123f, FlatGround, Tuning);
        Vector3 b = CharacterMovement.Step(Vector3.Zero, Cmd(1f, 1f, run: true, yaw: 0.7f), 0.123f, FlatGround, Tuning);
        Assert.Equal(a, b);
    }
}
```

Add to `KhaozEngine.Tests/KhaozEngine.Tests.csproj` `<ItemGroup>` of project refs:
```xml
    <ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
```
Add to `KhaozEngine.slnx` (alphabetical-ish, near Localization):
```xml
  <Project Path="KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
```

- [ ] **Step 4: run, expect FAIL** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CharacterMovementTests"` → compile error (CharacterMovement not defined).

- [ ] **Step 5: implement CharacterMovement**

`KhaozEngine.Locomotion/CharacterMovement.cs`:
```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Pure XZ-plane character locomotion: the single movement step run by the local controller, the
/// authoritative server sim, and client-side prediction alike. <see cref="Step"/> resolves a
/// camera-relative <see cref="MoveCommand"/> into a world move, normalizes diagonals, applies walk/run
/// speed over <paramref name="dt"/>, optionally rejects a step onto too-steep ground, then clamps Y onto
/// the ground delegate plus the capsule half-height. No input, render, physics, or netcode dependency.
/// </summary>
public static class CharacterMovement
{
    /// <param name="position">Current capsule-centre world position.</param>
    /// <param name="cmd">Movement intent (camera-relative axis + run + camera yaw).</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Speed/half-height/slope constants.</param>
    /// <param name="groundNormal">Optional ground normal at (x, z); when given, gates a step by slope.</param>
    /// <returns>The advanced position (Y on the ground + half-height).</returns>
    public static Vector3 Step(Vector3 position, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        // Camera-relative ground basis (matches FollowCamera3D's yaw convention).
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);

        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);   // normalized diagonals
            float speed = cmd.Run ? tuning.RunSpeed : tuning.WalkSpeed;
            float nx = position.X + move.X * speed * dt;
            float nz = position.Z + move.Z * speed * dt;

            bool blocked = false;
            if (groundNormal is not null)
            {
                float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
                if (MathF.Acos(ny) > tuning.MaxSlopeRadians) blocked = true;
            }
            if (!blocked) { position.X = nx; position.Z = nz; }
        }

        position.Y = groundHeight(position.X, position.Z) + tuning.CapsuleHalfHeight;
        return position;
    }
}
```

- [ ] **Step 6: run, expect PASS** — same filter, all green.

- [ ] **Step 7: commit**
```bash
git add KhaozEngine.Locomotion KhaozEngine.Tests/Locomotion KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.slnx
git commit -m "feat(7.47.0): KhaozEngine.Locomotion movement core (CharacterMovement.Step)"
```

---

## Task 2: Refactor `CharacterController3D` to wrap `CharacterMovement`

**Files:**
- Modify: `KhaozEngine.Game.Render3D/CharacterController3D.cs`, `KhaozEngine.Game.Render3D/KhaozEngine.Game.Render3D.csproj`

**Interfaces:**
- Consumes: `CharacterMovement.Step`, `MoveCommand`, `MoveTuning` (Task 1).
- Produces: unchanged public API of `CharacterController3D` (`WalkSpeed`/`RunSpeed`/`CapsuleHalfHeight`/`MaxSlopeRadians` fields, `Position`, `SetXZ`, `Update(in InputState, float, float, Func, Func?)`). The existing `CharacterController3DTests` must stay green.

- [ ] **Step 1: add Locomotion ref** to `KhaozEngine.Game.Render3D.csproj` `<ItemGroup>` of project refs:
```xml
    <ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
```

- [ ] **Step 2: run existing controller tests to confirm green baseline** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CharacterController3DTests"` → PASS (pre-refactor).

- [ ] **Step 3: rewrite the body of `Update`** to build a `MoveCommand` + `MoveTuning` and delegate. Replace the method body (lines ~38-71) so the file reads:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Terrain-agnostic third-person locomotion for the walkable slice. WASD moves the character on the XZ
    /// plane relative to a camera yaw; diagonals are normalized; shift runs. A thin input adapter over the
    /// shared <see cref="CharacterMovement.Step"/> core (KhaozEngine.Locomotion): the same code runs the
    /// local feel and the networked authoritative/predicted movement, with one <see cref="MoveTuning"/>
    /// source of truth. Reads only the immutable input snapshot; no physics beyond ground-clamp.
    /// </summary>
    public sealed class CharacterController3D
    {
        Vector3 _position;

        /// <summary>Current world position (the capsule centre: ground height + <see cref="CapsuleHalfHeight"/>).</summary>
        public Vector3 Position => _position;

        /// <summary>Metres per second while walking. Default 3.</summary>
        public float WalkSpeed = 3f;
        /// <summary>Metres per second while running (shift held). Default 6.</summary>
        public float RunSpeed = 6f;
        /// <summary>Half the capsule height, added to the ground so the feet sit on the ground. Default 0.9 (a 1.8 m capsule).</summary>
        public float CapsuleHalfHeight = 0.9f;
        /// <summary>Reject a step onto ground steeper than this (angle between surface normal and +Y), when a
        /// ground-normal delegate is supplied. Default ~50 deg.</summary>
        public float MaxSlopeRadians = MathF.PI * 50f / 180f;

        /// <summary>
        /// Advance the character for one frame. <paramref name="cameraYaw"/> is the follow camera's yaw (radians);
        /// <paramref name="groundHeight"/> returns terrain height at (x, z); <paramref name="groundNormal"/> is
        /// optional and, when given, gates moves by slope. Touches no input statics.
        /// </summary>
        public void Update(in InputState input, float dt, float cameraYaw,
                           Func<float, float, float> groundHeight,
                           Func<float, float, Vector3>? groundNormal = null)
        {
            if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

            Vector2 move = Vector2.Zero;
            if (input.IsDown(Key.W)) move.Y += 1f;
            if (input.IsDown(Key.S)) move.Y -= 1f;
            if (input.IsDown(Key.D)) move.X += 1f;
            if (input.IsDown(Key.A)) move.X -= 1f;
            bool run = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);

            var cmd = new MoveCommand(move, run, cameraYaw);
            var tuning = new MoveTuning(WalkSpeed, RunSpeed, CapsuleHalfHeight, MaxSlopeRadians);
            _position = CharacterMovement.Step(_position, cmd, dt, groundHeight, tuning, groundNormal);
        }

        /// <summary>Teleport the character; Y is recomputed from the ground delegate on the next <see cref="Update"/>.</summary>
        public void SetXZ(float x, float z) { _position.X = x; _position.Z = z; }
    }
}
```

- [ ] **Step 4: run existing controller tests, expect PASS** — same filter, all green (behaviour preserved).

- [ ] **Step 5: commit**
```bash
git add KhaozEngine.Game.Render3D/CharacterController3D.cs KhaozEngine.Game.Render3D/KhaozEngine.Game.Render3D.csproj
git commit -m "refactor(7.47.0): CharacterController3D wraps CharacterMovement.Step"
```

---

## Task 3: `KhaozEngine.NetWorld` skeleton + `PlayerMoveState`/`ReplicatedPosition`/`MoveProtocol`

**Files:**
- Create: `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`, `PlayerMoveState.cs`, `ReplicatedPosition.cs`, `MoveProtocol.cs`
- Create: `KhaozEngine.Tests/NetWorld/MoveProtocolTests.cs`
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

**Interfaces:**
- Consumes: `MoveCommand` (Task 1); `IPredictedState<TSelf>`, `IComponent`, `ReplicationRegistry`.
- Produces: `KhaozEngine.NetWorld.PlayerMoveState { Vector3 Position; }` implementing `IPredictedState<PlayerMoveState>`; `ReplicatedPosition : IComponent { Vector3 Value; }`; `static MoveProtocol { ReplicationRegistry CreateRegistry(); byte[] EncodeMove(int seq, in MoveCommand); bool TryDecodeMove(ReadOnlySpan<byte>, out int seq, out MoveCommand); byte[] EncodeSnapshotFrame(int localNetId, int ackSeq, byte[] snapshot); bool TryDecodeSnapshotFrame(ReadOnlySpan<byte>, out int localNetId, out int ackSeq, out byte[] snapshot); const ushort PositionTypeId = 1; }`.

- [ ] **Step 1: csproj**

`KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.NetWorld</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>Render-free networked-world layer wiring the movement core to the authoritative netcode stack. PlayerMoveSimulator (ITickSimulator) runs CharacterMovement.Step both server-authoritatively and inside client prediction. WorldServer is a single-World authoritative server: NetServer session layer, a per-player command queue, ground-clamped movement, and per-client area-of-interest snapshots (SnapshotWriter + InterestGrid) with a [localNetId][ack] header. WorldClient wraps NetClient + ClientReplicationView + ClientPrediction and exposes EntityRenderState[] (local predicted, remotes replicated). No render, window, or GPU dependency. Single-World slice of the MMO overworld; multi-cell sharding folds in with world streaming later.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
    <ProjectReference Include="../KhaozEngine.Netcode/KhaozEngine.Netcode.csproj" />
    <ProjectReference Include="../KhaozEngine.Replication/KhaozEngine.Replication.csproj" />
    <ProjectReference Include="../KhaozEngine.Ecs/KhaozEngine.Ecs.csproj" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```
Also create `KhaozEngine.NetWorld/README.md` (one paragraph mirroring the Description; required by `PackageReadmeFile`).

- [ ] **Step 2: PlayerMoveState + ReplicatedPosition**

`KhaozEngine.NetWorld/PlayerMoveState.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The predicted/authoritative movement state of one player: a 3D world position (Y ground-clamped).
/// Implements <see cref="IPredictedState{T}"/> over its XZ plane, so client prediction measures and
/// smooths reconciliation error on the ground plane while Y is a pure function of XZ via the ground
/// delegate (re-derived each step).
/// </summary>
public struct PlayerMoveState : IPredictedState<PlayerMoveState>
{
    /// <summary>Capsule-centre world position.</summary>
    public Vector3 Position;

    Vector2 IPredictedState<PlayerMoveState>.Position => new(Position.X, Position.Z);

    /// <summary>Returns a copy with the planar (XZ) position replaced; Y is kept from this state.</summary>
    public PlayerMoveState WithPosition(Vector2 position) =>
        new() { Position = new Vector3(position.X, Position.Y, position.Y) };
}
```

`KhaozEngine.NetWorld/ReplicatedPosition.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>The one replicated gameplay component: an entity's 3D world position. Interpolatable.</summary>
public struct ReplicatedPosition : IComponent
{
    public Vector3 Value;
}
```

- [ ] **Step 3: failing test**

`KhaozEngine.Tests/NetWorld/MoveProtocolTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MoveProtocolTests
{
    [Fact]
    public void Move_round_trips()
    {
        var cmd = new MoveCommand(new Vector2(0.5f, -1f), run: true, cameraYaw: 1.23f);
        byte[] wire = MoveProtocol.EncodeMove(seq: 42, cmd);
        Assert.True(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand back));
        Assert.Equal(42, seq);
        Assert.Equal(cmd.Move, back.Move);
        Assert.Equal(cmd.Run, back.Run);
        Assert.Equal(cmd.CameraYaw, back.CameraYaw, 5);
    }

    [Fact]
    public void Move_decode_rejects_short_payload()
    {
        Assert.False(MoveProtocol.TryDecodeMove(new byte[] { 1, 2, 3 }, out _, out _));
    }

    [Fact]
    public void Snapshot_frame_round_trips()
    {
        byte[] snap = { 9, 8, 7, 6, 5 };
        byte[] frame = MoveProtocol.EncodeSnapshotFrame(localNetId: 3, ackSeq: 11, snap);
        Assert.True(MoveProtocol.TryDecodeSnapshotFrame(frame, out int id, out int ack, out byte[] back));
        Assert.Equal(3, id);
        Assert.Equal(11, ack);
        Assert.Equal(snap, back);
    }

    [Fact]
    public void Snapshot_frame_rejects_short_payload()
    {
        Assert.False(MoveProtocol.TryDecodeSnapshotFrame(new byte[] { 1, 2 }, out _, out _, out _));
    }
}
```
Add to `KhaozEngine.Tests/KhaozEngine.Tests.csproj`:
```xml
    <ProjectReference Include="../KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />
```
Add to `KhaozEngine.slnx`:
```xml
  <Project Path="KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />
```

- [ ] **Step 4: run, expect FAIL** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~MoveProtocolTests"` → compile error.

- [ ] **Step 5: implement MoveProtocol**

`KhaozEngine.NetWorld/MoveProtocol.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Shared wire encodings so a <see cref="WorldServer"/> and its <see cref="WorldClient"/> agree.</summary>
public static class MoveProtocol
{
    /// <summary>Type id of <see cref="ReplicatedPosition"/> in the shared registry.</summary>
    public const ushort PositionTypeId = 1;

    /// <summary>The replicated-component registry (must match on server and client).</summary>
    public static ReplicationRegistry CreateRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<ReplicatedPosition>(
            PositionTypeId,
            write: (p, bw) => { bw.Write(p.Value.X); bw.Write(p.Value.Y); bw.Write(p.Value.Z); },
            read: br => new ReplicatedPosition { Value = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()) },
            lerp: (a, b, t) => new ReplicatedPosition { Value = Vector3.Lerp(a.Value, b.Value, t) });
        return r;
    }

    // Move: [seq:int][move.x:float][move.y:float][run:byte][cameraYaw:float] = 17 bytes.
    private const int MoveSize = 4 + 4 + 4 + 1 + 4;

    /// <summary>Encodes a client move command.</summary>
    public static byte[] EncodeMove(int seq, in MoveCommand cmd)
    {
        var b = new byte[MoveSize];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), seq);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), cmd.Move.X);
        BitConverter.TryWriteBytes(b.AsSpan(8, 4), cmd.Move.Y);
        b[12] = cmd.Run ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(b.AsSpan(13, 4), cmd.CameraYaw);
        return b;
    }

    /// <summary>Decodes a client move command. False (hostile-safe) if the payload is malformed.</summary>
    public static bool TryDecodeMove(ReadOnlySpan<byte> data, out int seq, out MoveCommand cmd)
    {
        if (data.Length >= MoveSize)
        {
            seq = BitConverter.ToInt32(data.Slice(0, 4));
            var move = new Vector2(BitConverter.ToSingle(data.Slice(4, 4)), BitConverter.ToSingle(data.Slice(8, 4)));
            bool run = data[12] != 0;
            float yaw = BitConverter.ToSingle(data.Slice(13, 4));
            cmd = new MoveCommand(move, run, yaw);
            return true;
        }
        seq = -1;
        cmd = default;
        return false;
    }

    // Server->client frame: [localNetId:int][ackSeq:int][snapshot bytes...].
    private const int FrameHeader = 8;

    /// <summary>Prepends the per-client header (the receiver's own net id + last-acked move seq) to a snapshot.</summary>
    public static byte[] EncodeSnapshotFrame(int localNetId, int ackSeq, byte[] snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var b = new byte[FrameHeader + snapshot.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), localNetId);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), ackSeq);
        snapshot.CopyTo(b.AsSpan(FrameHeader));
        return b;
    }

    /// <summary>Splits a server frame into its header and the replication snapshot. False if too short.</summary>
    public static bool TryDecodeSnapshotFrame(ReadOnlySpan<byte> data, out int localNetId, out int ackSeq, out byte[] snapshot)
    {
        if (data.Length >= FrameHeader)
        {
            localNetId = BitConverter.ToInt32(data.Slice(0, 4));
            ackSeq = BitConverter.ToInt32(data.Slice(4, 4));
            snapshot = data.Slice(FrameHeader).ToArray();
            return true;
        }
        localNetId = -1;
        ackSeq = -1;
        snapshot = Array.Empty<byte>();
        return false;
    }
}
```

- [ ] **Step 6: run, expect PASS.**

- [ ] **Step 7: commit**
```bash
git add KhaozEngine.NetWorld KhaozEngine.Tests/NetWorld/MoveProtocolTests.cs KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.slnx
git commit -m "feat(7.47.0): NetWorld package + PlayerMoveState/ReplicatedPosition/MoveProtocol"
```

---

## Task 4: `PlayerMoveSimulator`

**Files:**
- Create: `KhaozEngine.NetWorld/PlayerMoveSimulator.cs`, `KhaozEngine.Tests/NetWorld/PlayerMoveSimulatorTests.cs`

**Interfaces:**
- Consumes: `PlayerMoveState`, `MoveCommand`, `MoveTuning`, `CharacterMovement.Step`, `ITickSimulator<,>`.
- Produces: `sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>` with ctor `(Func<float,float,float> groundHeight, MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null)` and `PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt)`.

- [ ] **Step 1: failing test**

`KhaozEngine.Tests/NetWorld/PlayerMoveSimulatorTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerMoveSimulatorTests
{
    static readonly Func<float, float, float> Ground = (x, z) => 2f;

    [Fact]
    public void Step_advances_and_ground_clamps()
    {
        var sim = new PlayerMoveSimulator(Ground, MoveTuning.Default);
        var s0 = new PlayerMoveState { Position = new Vector3(0f, 0f, 0f) };
        var s1 = sim.Step(s0, new MoveCommand(new Vector2(0f, 1f), false, 0f), 1f);
        Assert.True(s1.Position.Z < 0f);
        Assert.Equal(2f + MoveTuning.Default.CapsuleHalfHeight, s1.Position.Y, 4);
    }

    [Fact]
    public void Step_is_pure_does_not_mutate_input()
    {
        var sim = new PlayerMoveSimulator(Ground, MoveTuning.Default);
        var s0 = new PlayerMoveState { Position = Vector3.Zero };
        sim.Step(s0, new MoveCommand(new Vector2(1f, 0f), false, 0f), 0.5f);
        Assert.Equal(Vector3.Zero, s0.Position);
    }

    [Fact]
    public void Multi_tick_accumulates()
    {
        var sim = new PlayerMoveSimulator((x, z) => 0f, MoveTuning.Default);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        var cmd = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 3; i++) s = sim.Step(s, cmd, 1f / 30f);
        Assert.Equal(-MoveTuning.Default.WalkSpeed * 3f / 30f, s.Position.Z, 4);
    }
}
```

- [ ] **Step 2: run, expect FAIL.**

- [ ] **Step 3: implement**

`KhaozEngine.NetWorld/PlayerMoveSimulator.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-tick player movement step, plugged into the shipped prediction/reconciliation seam. The same
/// instance configuration (ground delegate + tuning) drives the authoritative server tick and the client's
/// prediction replay, so they stay in lockstep. Wraps <see cref="CharacterMovement.Step"/>.
/// </summary>
public sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
    }

    /// <summary>Advances one player by one command over <paramref name="dt"/> seconds, ground-clamped.</summary>
    public PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt) =>
        new() { Position = CharacterMovement.Step(state.Position, command, dt, groundHeight, tuning, groundNormal) };
}
```

- [ ] **Step 4: run, expect PASS.**

- [ ] **Step 5: commit**
```bash
git add KhaozEngine.NetWorld/PlayerMoveSimulator.cs KhaozEngine.Tests/NetWorld/PlayerMoveSimulatorTests.cs
git commit -m "feat(7.47.0): PlayerMoveSimulator (ITickSimulator over CharacterMovement)"
```

---

## Task 5: `WorldServer` + `EntityRenderState` + `WorldClient` (+ single-client loopback round-trip)

**Files:**
- Create: `KhaozEngine.NetWorld/EntityRenderState.cs`, `WorldServer.cs`, `WorldClient.cs`
- Create: `KhaozEngine.Tests/NetWorld/WorldRoundTripTests.cs`

**Interfaces:**
- Consumes: `PlayerMoveSimulator`, `PlayerMoveState`, `ReplicatedPosition`, `MoveProtocol`, `MoveCommand`, `MoveTuning`; `NetServer`, `NetClient`, `INetTransport`, `AllowAllAuthenticator`, `RemoteCommandQueue<>`, `ClientPrediction<,>`, `PredictionSettings`, `NetChannelReliability`, `ServerSessionEvent*`, `ClientSessionEvent*`; `World`, `Entity`, `NetId`, `ReplicationRegistry`, `SnapshotWriter`, `InterestGrid`, `ClientReplicationView`.
- Produces:
  - `readonly struct EntityRenderState { NetId Id; Vector3 Position; bool IsLocal; }` (ctor `(NetId, Vector3, bool)`).
  - `sealed class WorldServerConfig { float TickSeconds = 1/30f; float InterestRadius = 200f; int MaxPlayers = 16; Func<int,Vector3>? SpawnPosition; }`.
  - `sealed class WorldServer` ctor `(INetTransport, WorldServerConfig, Func<float,float,float> groundHeight, MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null)`; `void Poll()`, `void Tick(float dt)`, `World World`, `ReplicationRegistry Registry`, `bool TryGetPlayerNetId(int slot, out int netId)`, `int PlayerCount`.
  - `sealed class WorldClientConfig { float TickSeconds = 1/30f; PredictionSettings? Prediction; }`.
  - `sealed class WorldClient` ctor `(INetTransport, Func<float,float,float> groundHeight, MoveTuning tuning, WorldClientConfig? config = null, byte[]? token = null, Func<float,float,Vector3>? groundNormal = null)`; `void Poll()`, `int SendInput(in MoveCommand cmd)`, `void AdvancePresentation(float dt)`, `IReadOnlyList<EntityRenderState> Snapshot()`, `int LocalNetId`, `bool Joined`.

- [ ] **Step 1: EntityRenderState**

`KhaozEngine.NetWorld/EntityRenderState.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>One renderable entity as the client sees it: its net id, world position, and whether it is the
/// local (predicted) player. The render-free contract a sample renders a capsule from.</summary>
public readonly struct EntityRenderState
{
    public EntityRenderState(NetId id, Vector3 position, bool isLocal)
    {
        Id = id;
        Position = position;
        IsLocal = isLocal;
    }

    public NetId Id { get; }
    public Vector3 Position { get; }
    public bool IsLocal { get; }
}
```

- [ ] **Step 2: WorldServer**

`KhaozEngine.NetWorld/WorldServer.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldServer"/>.</summary>
public sealed class WorldServerConfig
{
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Per-client area-of-interest radius (world units).</summary>
    public float InterestRadius { get; init; } = 200f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 16;
    /// <summary>Per-slot spawn position (XZ used; Y is ground-clamped). Default spreads players along +X.</summary>
    public Func<int, Vector3>? SpawnPosition { get; init; }
}

/// <summary>
/// Reference single-<see cref="World"/> authoritative movement server. A <see cref="NetServer"/> session layer
/// spawns one player entity per connection; each tick it drains that client's queued <see cref="MoveCommand"/>,
/// runs the shared <see cref="PlayerMoveSimulator"/> (ground-clamped), and serves every client a per-area-of-
/// interest snapshot (<see cref="SnapshotWriter.WriteFiltered"/> over an <see cref="InterestGrid"/>) prefixed
/// with that client's net id + last-acked move seq so the client can reconcile. Headless, transport-injected.
/// Multi-cell sharding folds in with world streaming later; this is the single-world slice.
/// </summary>
public sealed class WorldServer
{
    private readonly WorldServerConfig config;
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly World world = new();
    private readonly NetServer net;
    private readonly InterestGrid interest;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly PlayerMoveSimulator simulator;
    private readonly Func<float, float, float> groundHeight;

    private readonly Dictionary<int, int> netIdBySlot = new();
    private readonly Dictionary<int, Entity> entityBySlot = new();
    private readonly Dictionary<int, PlayerMoveState> stateBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private int nextNetId = 1;

    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
        interest = new InterestGrid(MathF.Max(1f, config.InterestRadius));
    }

    /// <summary>The authoritative ECS world.</summary>
    public World World => world;
    /// <summary>The replicated-component registry; clients build the matching one via MoveProtocol.</summary>
    public ReplicationRegistry Registry => registry;
    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out int netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot);
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

    /// <summary>Steps one authoritative frame: apply each client's queued input, then serve every client its AoI.</summary>
    public void Tick(float dt)
    {
        // Authoritative movement: one command per player per tick.
        var slots = new List<int>(netIdBySlot.Keys);
        foreach (int slot in slots)
        {
            MoveCommand cmd = commands.Dequeue(slot, out int ack);
            lastAckBySlot[slot] = ack;
            PlayerMoveState state = simulator.Step(stateBySlot[slot], cmd, dt);
            stateBySlot[slot] = state;
            world.Set(entityBySlot[slot], new ReplicatedPosition { Value = state.Position });
        }

        // Rebuild AoI index from current positions.
        interest.Clear();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (world.TryGet(e, out ReplicatedPosition p)) interest.Insert(id.Value, p.Value.X, p.Value.Z);
        });

        // Serve each client its area-of-interest snapshot, headered with its own net id + ack.
        foreach (int slot in slots)
        {
            int netId = netIdBySlot[slot];
            Vector3 p = stateBySlot[slot].Position;
            HashSet<int> set = interest.Query(p.X, p.Z, config.InterestRadius);
            byte[] snapshot = SnapshotWriter.WriteFiltered(world, registry, set);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        }
    }

    private void OnJoin(int slot)
    {
        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = simulator.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        int netId = nextNetId++;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new ReplicatedPosition { Value = state.Position });

        netIdBySlot[slot] = netId;
        entityBySlot[slot] = e;
        stateBySlot[slot] = state;
        lastAckBySlot[slot] = -1;
    }

    private void OnLeave(int slot)
    {
        if (entityBySlot.TryGetValue(slot, out Entity e) && world.IsAlive(e)) world.Despawn(e);
        netIdBySlot.Remove(slot);
        entityBySlot.Remove(slot);
        stateBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
    }
}
```

- [ ] **Step 3: WorldClient**

`KhaozEngine.NetWorld/WorldClient.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldClient"/>.</summary>
public sealed class WorldClientConfig
{
    /// <summary>Fixed client prediction tick, seconds. Must match the server tick for clean reconciliation.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Override prediction settings; defaults to <see cref="PredictionSettings.Default"/> at <see cref="TickSeconds"/>.</summary>
    public PredictionSettings? Prediction { get; init; }
}

/// <summary>
/// Client glue over the shipped netcode: wraps a <see cref="NetClient"/> session, a
/// <see cref="ClientReplicationView"/> for remote entities, and <see cref="ClientPrediction{TState,TCommand}"/>
/// for the local avatar. Per frame the sample <see cref="Poll"/>s (ingests AoI snapshots; reconciles the local
/// player against the authoritative basis), <see cref="SendInput"/>s once per tick (predicts + transmits), and
/// reads <see cref="Snapshot"/> to render a capsule per entity (local predicted, remotes replicated). Render-free.
/// </summary>
public sealed class WorldClient
{
    private readonly NetClient net;
    private readonly World world = new();
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly ClientReplicationView view;
    private readonly ClientPrediction<PlayerMoveState, MoveCommand> prediction;
    private int authoritativeTick;

    public WorldClient(INetTransport transport, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        config ??= new WorldClientConfig();
        net = new NetClient(transport, token);
        view = new ClientReplicationView(registry);
        var simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
    }

    /// <summary>Net id of the local player, or -1 until the first snapshot identifies it.</summary>
    public int LocalNetId { get; private set; } = -1;
    /// <summary>True once the session handshake has joined.</summary>
    public bool Joined { get; private set; }

    /// <summary>Pumps the session: ingests AoI snapshots, applies remote replication, reconciles the local avatar.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    Joined = true;
                    break;
                case ClientSessionEventKind.Data:
                    OnSnapshot(ev.Data);
                    break;
                case ClientSessionEventKind.Disconnected:
                    Joined = false;
                    break;
            }
        }
    }

    /// <summary>Predicts one command forward and transmits it. Returns the assigned seq.</summary>
    public int SendInput(in MoveCommand cmd)
    {
        int seq = prediction.Predict(cmd);
        net.Send(MoveProtocol.EncodeMove(seq, cmd), NetChannelReliability.ReliableOrdered);
        return seq;
    }

    /// <summary>Advances the prediction's inter-tick smoothing (call once per render frame).</summary>
    public void AdvancePresentation(float dt) => prediction.AdvancePresentation(dt);

    /// <summary>The current renderable set: local player predicted, remotes from the latest replicated position.</summary>
    public IReadOnlyList<EntityRenderState> Snapshot()
    {
        var list = new List<EntityRenderState>();
        foreach (KeyValuePair<int, Entity> kv in view.Entities)
        {
            if (!world.IsAlive(kv.Value)) continue;
            bool isLocal = kv.Key == LocalNetId;
            Vector3 pos;
            if (isLocal)
            {
                pos = prediction.RenderedState.Position;
            }
            else
            {
                world.TryGet(kv.Value, out ReplicatedPosition rp);
                pos = rp.Value;
            }
            list.Add(new EntityRenderState(new NetId(kv.Key), pos, isLocal));
        }
        return list;
    }

    private void OnSnapshot(byte[] data)
    {
        if (!MoveProtocol.TryDecodeSnapshotFrame(data, out int localNetId, out int ackSeq, out byte[] snapshot)) return;
        bool first = LocalNetId < 0;
        LocalNetId = localNetId;
        view.Apply(world, snapshot);

        if (view.TryGetEntity(localNetId, out Entity local) && world.TryGet(local, out ReplicatedPosition p))
        {
            var basis = new PlayerMoveState { Position = p.Value };
            if (first) prediction.Reset(basis);                  // seed prediction at the authoritative spawn
            prediction.Reconcile(authoritativeTick++, basis, ackSeq);
        }
    }
}
```

- [ ] **Step 4: failing test — single-client round-trip**

`KhaozEngine.Tests/NetWorld/WorldRoundTripTests.cs`:
```csharp
using System;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldRoundTripTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static (WorldServer server, WorldServerConfig config) NewServer(INetTransport t)
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        return (new WorldServer(t, config, Flat, MoveTuning.Default), config);
    }

    [Fact]
    public void Client_move_command_moves_its_server_entity_and_returns_via_replication()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        (WorldServer server, WorldServerConfig config) = NewServer(st);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        // Connect + first serve (no input) to establish the prediction basis.
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Assert.True(client.LocalNetId > 0);

        float zBefore = LocalZ(client);

        // Walk forward (W = +Y axis at yaw 0 -> -Z) for several ticks.
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        for (int i = 0; i < 10; i++)
        {
            client.SendInput(forward);
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();
        }

        float zAfter = LocalZ(client);
        Assert.True(zAfter < zBefore - 0.1f, $"expected forward motion, before {zBefore} after {zAfter}");
    }

    static float LocalZ(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.Z;
        throw new Xunit.Sdk.XunitException("no local entity in client snapshot");
    }
}
```

- [ ] **Step 5: run, expect FAIL then implement (Steps 1-3 already provide impl) — run** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldRoundTripTests"`. If red due to timing, increase the connect-pump iterations. Expected: PASS.

- [ ] **Step 6: commit**
```bash
git add KhaozEngine.NetWorld/EntityRenderState.cs KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.NetWorld/WorldClient.cs KhaozEngine.Tests/NetWorld/WorldRoundTripTests.cs
git commit -m "feat(7.47.0): WorldServer + WorldClient + EntityRenderState (single-client round-trip)"
```

---

## Task 6: Two-client loopback (in-memory hub) + reconcile-misprediction tests

**Files:**
- Create: `KhaozEngine.Tests/NetWorld/InMemoryHub.cs` (test helper), append to `WorldRoundTripTests.cs`, create `KhaozEngine.Tests/NetWorld/ClientReconcileTests.cs`

**Interfaces:**
- Produces (test-only): `InMemoryHub` with `INetTransport Server` and `INetTransport CreateClient()`; deterministic in-memory fan-out so one `NetServer` serves N distinct connections.

- [ ] **Step 1: InMemoryHub helper**

`KhaozEngine.Tests/NetWorld/InMemoryHub.cs`:
```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Deterministic in-memory multi-client transport for headless multi-client server tests: one server
/// endpoint fans out to N client endpoints, each a distinct <see cref="NetConnectionId"/>. Mirrors
/// LoopbackTransport's poll/drain model (a Send is surfaced on the peer's next Poll). No sockets, no threads.
/// </summary>
public sealed class InMemoryHub
{
    private readonly ServerEndpoint server;
    private readonly List<ClientEndpoint> clients = new();

    public InMemoryHub() => server = new ServerEndpoint(this);

    /// <summary>The single server transport (hand to a NetServer).</summary>
    public INetTransport Server => server;

    /// <summary>Adds a client endpoint with a fresh connection id (hand to a NetClient/WorldClient).</summary>
    public INetTransport CreateClient()
    {
        int connId = clients.Count + 1;            // distinct positive id per client
        var c = new ClientEndpoint(this, connId);
        clients.Add(c);
        server.OnClientAdded(connId);
        return c;
    }

    private void ServerSend(int connId, byte[] data, NetChannelReliability r)
    {
        if (connId - 1 >= 0 && connId - 1 < clients.Count)
            clients[connId - 1].EnqueueFromServer(data, r);
    }

    private void ClientSend(int connId, byte[] data, NetChannelReliability r) =>
        server.EnqueueFromClient(connId, data, r);

    private sealed class ServerEndpoint : INetTransport
    {
        private readonly InMemoryHub hub;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(int connId, byte[] data, NetChannelReliability r)> pending = new();
        private readonly Queue<int> newClients = new();

        public ServerEndpoint(InMemoryHub hub) => this.hub = hub;

        public void OnClientAdded(int connId) => newClients.Enqueue(connId);

        public void EnqueueFromClient(int connId, byte[] data, NetChannelReliability r) =>
            pending.Add((connId, data, r));

        public void Poll()
        {
            while (newClients.Count > 0)
                inbox.Enqueue(NetEvent.Connected(new NetConnectionId(newClients.Dequeue())));
            foreach ((int connId, byte[] data, NetChannelReliability r) in pending)
                inbox.Enqueue(NetEvent.FromData(new NetConnectionId(connId), data, r));
            pending.Clear();
        }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
            ev = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
            hub.ServerSend(target.Value, payload.ToArray(), reliability);

        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }

    private sealed class ClientEndpoint : INetTransport
    {
        private static readonly NetConnectionId ServerId = new(1);
        private readonly InMemoryHub hub;
        private readonly int connId;
        private readonly Queue<NetEvent> inbox = new();
        private readonly List<(byte[] data, NetChannelReliability r)> pending = new();
        private bool announced;

        public ClientEndpoint(InMemoryHub hub, int connId) { this.hub = hub; this.connId = connId; }

        public void EnqueueFromServer(byte[] data, NetChannelReliability r) => pending.Add((data, r));

        public void Poll()
        {
            if (!announced) { announced = true; inbox.Enqueue(NetEvent.Connected(ServerId)); }
            foreach ((byte[] data, NetChannelReliability r) in pending)
                inbox.Enqueue(NetEvent.FromData(ServerId, data, r));
            pending.Clear();
        }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
            ev = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
            hub.ClientSend(connId, payload.ToArray(), reliability);

        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: two-client test (append to `WorldRoundTripTests.cs`)**
```csharp
    [Fact]
    public void Two_clients_each_see_the_other_move()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        // Connect both + establish bases.
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);
        Assert.Equal(2, server.PlayerCount);
        Assert.NotEqual(a.LocalNetId, b.LocalNetId);

        Vector3 bSeenByA_before = RemotePos(a, b.LocalNetId);
        Vector3 aSeenByB_before = RemotePos(b, a.LocalNetId);

        var aForward = new MoveCommand(new Vector2(0f, 1f), false, 0f);   // -Z
        var bRight = new MoveCommand(new Vector2(1f, 0f), false, 0f);     // +X
        for (int i = 0; i < 12; i++)
        {
            a.SendInput(aForward);
            b.SendInput(bRight);
            server.Poll();
            server.Tick(config.TickSeconds);
            a.Poll();
            b.Poll();
        }

        Vector3 aSeenByB_after = RemotePos(b, a.LocalNetId);
        Vector3 bSeenByA_after = RemotePos(a, b.LocalNetId);
        Assert.True(aSeenByB_after.Z < aSeenByB_before.Z - 0.1f, "B should see A move -Z");
        Assert.True(bSeenByA_after.X > bSeenByA_before.X + 0.1f, "A should see B move +X");
    }

    static Vector3 RemotePos(WorldClient observer, int remoteNetId)
    {
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == remoteNetId) return e.Position;
        throw new Xunit.Sdk.XunitException($"remote {remoteNetId} not visible");
    }
```

- [ ] **Step 3: reconcile-misprediction test**

`KhaozEngine.Tests/NetWorld/ClientReconcileTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ClientReconcileTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Reconcile_converges_local_to_authoritative_basis()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);

        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var forward = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 3; i++) pred.Predict(forward);          // client predicts forward
        int lastSeq = 2;

        // Inject a misprediction: the server says the player is somewhere else (all commands acked).
        var serverBasis = new PlayerMoveState { Position = new Vector3(4f, 0f, -1f) };
        ReconciliationResult result = pred.Reconcile(authoritativeTick: 1, serverBasis, lastAcknowledgedSeq: lastSeq);

        Assert.True(result.PositionError > settings.CorrectionDeadZone, $"error {result.PositionError}");
        // All commands acked + no pending => predicted snaps to the authoritative basis.
        Assert.Equal(4f, pred.PredictedState.Position.X, 4);
        Assert.Equal(-1f, pred.PredictedState.Position.Z, 4);

        // The visible correction decays toward the authoritative position over time.
        for (int i = 0; i < 240; i++) pred.AdvancePresentation(1f / 60f);
        Assert.Equal(4f, pred.RenderedState.Position.X, 2);
        Assert.Equal(-1f, pred.RenderedState.Position.Z, 2);
    }

    [Fact]
    public void Unacked_commands_replay_on_top_of_basis()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);

        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var forward = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 3; i++) pred.Predict(forward);

        // Server has acked nothing; basis at origin -> the 3 unacked commands replay on top.
        var basis = new PlayerMoveState { Position = Vector3.Zero };
        pred.Reconcile(authoritativeTick: 1, basis, lastAcknowledgedSeq: -1);

        float expectedZ = -MoveTuning.Default.WalkSpeed * settings.TickSeconds * 3f;
        Assert.Equal(expectedZ, pred.PredictedState.Position.Z, 4);
    }
}
```

- [ ] **Step 4: run all NetWorld tests, expect PASS** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorld"`.

- [ ] **Step 5: commit**
```bash
git add KhaozEngine.Tests/NetWorld/InMemoryHub.cs KhaozEngine.Tests/NetWorld/WorldRoundTripTests.cs KhaozEngine.Tests/NetWorld/ClientReconcileTests.cs
git commit -m "test(7.47.0): two-client loopback + client reconciliation NetWorld tests"
```

---

## Task 7: LiveSocket round-trip smoke (LiteNetLib)

**Files:**
- Append to `KhaozEngine.Tests/NetWorld/WorldRoundTripTests.cs`; add LiteNetLib ref to `KhaozEngine.Tests.csproj` if missing (it already references it via the Sharding tests — verify).

**Interfaces:** Consumes `LiteNetLibServerTransport`, `LiteNetLibClientTransport`.

- [ ] **Step 1: verify** `KhaozEngine.Tests.csproj` references `KhaozEngine.Netcode.LiteNetLib` (the MmoServer tests use it). If not present, add it.

- [ ] **Step 2: add the test (append to `WorldRoundTripTests.cs`)**
```csharp
    [Trait("Category", "LiveSocket")]
    [Fact]
    public void LiveSocket_client_connects_and_is_served_its_player()
    {
        const int port = 47720;
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        using var st = new KhaozEngine.Netcode.LiteNetLib.LiteNetLibServerTransport(port);
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        using var ct = new KhaozEngine.Netcode.LiteNetLib.LiteNetLibClientTransport("127.0.0.1", port);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool served = false;
        while (sw.ElapsedMilliseconds < 3000 && !served)
        {
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();
            if (client.Joined && client.LocalNetId > 0)
                foreach (EntityRenderState e in client.Snapshot())
                    if (e.IsLocal) served = true;
            System.Threading.Thread.Sleep(10);
        }
        Assert.True(served, "client never received its player over a live socket");
    }
```

- [ ] **Step 3: run, expect PASS** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LiveSocket_client_connects_and_is_served_its_player"`.

- [ ] **Step 4: commit**
```bash
git add KhaozEngine.Tests/NetWorld/WorldRoundTripTests.cs KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "test(7.47.0): live-socket WorldServer/WorldClient smoke"
```

---

## Task 8: Demo server exe `NetworkedWalkServer`

**Files:**
- Create: `NetworkedWalkServer/NetworkedWalkServer.csproj`, `NetworkedWalkServer/Program.cs`
- Modify: `KhaozEngine.slnx`

**Interfaces:** Consumes `WorldServer`, `WorldServerConfig`, `MoveTuning`, `FixedTickHost`, `TerrainField`, `TerrainCollision`, `TerrainPresets`, `LiteNetLibServerTransport`.

- [ ] **Step 1: csproj**

`NetworkedWalkServer/NetworkedWalkServer.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />
    <ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
    <ProjectReference Include="../KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj" />
    <ProjectReference Include="../KhaozEngine.Simulation/KhaozEngine.Simulation.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Program.cs**

`NetworkedWalkServer/Program.cs`:
```csharp
using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;

// Headless authoritative server for the networked walkable slice: the shipped analytic terrain
// (TerrainPresets.Clearing) is the ground, a single-World WorldServer runs PlayerMoveSimulator on a
// FixedTickHost over a LiteNetLib UDP socket, and one player entity spawns per connection. Connect two
// NetworkedWalkSample clients to see them walk the same terrain. Usage: NetworkedWalkServer [port].
int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47700;

var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 200f, MaxPlayers = 16 };

using var transport = new LiteNetLibServerTransport(port);
var server = new WorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

var clock = new FixedTickHost(config.TickSeconds);
var sw = Stopwatch.StartNew();
double last = 0;
Console.WriteLine($"Networked walk server on UDP {port} (tick {1f / config.TickSeconds:0} Hz). Ctrl+C to stop.");
while (true)
{
    server.Poll();
    double now = sw.Elapsed.TotalSeconds;
    float elapsed = (float)(now - last);
    last = now;
    clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));
    Thread.Sleep(5);
}
```
Add to `KhaozEngine.slnx`:
```xml
  <Project Path="NetworkedWalkServer/NetworkedWalkServer.csproj" />
```

- [ ] **Step 3: build, expect success** — `dotnet build NetworkedWalkServer/NetworkedWalkServer.csproj -c Debug`.

- [ ] **Step 4: commit**
```bash
git add NetworkedWalkServer KhaozEngine.slnx
git commit -m "sample(7.47.0): NetworkedWalkServer headless authoritative server exe"
```

---

## Task 9: Networked client sample `NetworkedWalkSample`

**Files:**
- Create: `NetworkedWalkSample/NetworkedWalkSample.csproj`, `NetworkedWalkSample/Program.cs`
- Reuse the prop assets by referencing `../TerrainWalkSample/assets/props/**`
- Modify: `KhaozEngine.slnx`

**Interfaces:** Consumes `WorldClient`, `WorldClientConfig`, `MoveCommand`, `MoveTuning`, `EntityRenderState`, `LiteNetLibClientTransport`, `FixedTickHost`, `GameApp3D`, `FollowCamera3D`, `FollowCameraController`, terrain + prop pipeline (`TerrainChunkBuilder`, `PropScatter`, `AssetManifest`, `PropLoader`, `DrawProps`).

- [ ] **Step 1: csproj**

`NetworkedWalkSample/NetworkedWalkSample.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <!-- Reuse the committed CC0 prop kit + manifest from TerrainWalkSample (same deterministic scatter). -->
    <None Include="../TerrainWalkSample/assets/props/**" CopyToOutputDirectory="PreserveNewest" LinkBase="assets/props" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Game/KhaozEngine.Game.csproj" />
    <ProjectReference Include="../KhaozEngine.Game.Render3D/KhaozEngine.Game.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Render3D/KhaozEngine.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Windowing/KhaozEngine.Windowing.csproj" />
    <ProjectReference Include="../KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />
    <ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
    <ProjectReference Include="../KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj" />
    <ProjectReference Include="../KhaozEngine.Simulation/KhaozEngine.Simulation.csproj" />
    <ProjectReference Include="../KhaozEngine.Ecs/KhaozEngine.Ecs.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Program.cs**

`NetworkedWalkSample/Program.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

// Networked walkable overworld client: connects to a NetworkedWalkServer, drives the local player through a
// WorldClient (predicted + reconciled), and renders a capsule per replicated EntityRenderState over the same
// analytic terrain + deterministic prop scatter as the solo TerrainWalkSample (props are NOT networked). Run
// the server, then two of these clients on localhost to see two players. Usage: NetworkedWalkSample [host] [port].
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 47700;

Console.WriteLine($"NetworkedWalkSample -> {host}:{port} | WASD move | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new NetworkedWalkApp(host, port))
    app.Run();
return 0;

sealed class NetworkedWalkApp : GameApp3D
{
    const int GridRadius = 3;
    const float CapsuleRadius = 0.3f;
    const float CapsuleHalfHeight = 0.9f;
    const float PropDrawRadius = 90f;
    const float TickSeconds = 1f / 30f;

    readonly string _host;
    readonly int _port;

    TerrainField _field = null!;
    TerrainCollision _terrain = null!;
    readonly List<MeshHandle> _chunks = new();
    MeshHandle _capsule;
    readonly Dictionary<string, MeshHandle> _propMeshes = new();
    IReadOnlyList<PropPlacement> _placements = Array.Empty<PropPlacement>();

    FollowCamera3D _camera = null!;
    FollowCameraController _camController = null!;

    WorldClient _client = null!;
    LiteNetLibClientTransport _transport = null!;
    FixedTickHost _clientClock = null!;
    Vector3 _localPos = Vector3.Zero;

    public NetworkedWalkApp(string host, int port)
        : base(new GameAppOptions
        {
            Title = "KhaozEngine - Networked walk",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.45f, 0.62f, 0.85f, 1f),
        })
    { _host = host; _port = port; }

    protected override void OnLoad()
    {
        var sc = Scene;
        _field = new TerrainField(TerrainPresets.Clearing());
        _terrain = new TerrainCollision(_field);

        float size = TerrainChunkRegion.DefaultSize;
        for (int gz = -GridRadius; gz <= GridRadius; gz++)
            for (int gx = -GridRadius; gx <= GridRadius; gx++)
            {
                var region = new TerrainChunkRegion { OriginX = gx * size, OriginZ = gz * size, Size = size };
                var chunk = TerrainChunkBuilder.Build(_field, region, lod: 0);
                _chunks.Add(sc.LoadTerrainChunk(chunk));
            }

        _capsule = sc.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

        _camera = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
        _camera.Distance = 9f;
        _camController = new FollowCameraController(_camera);
        sc.CameraOverride = _camera;

        string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json");
        AssetManifest manifest = AssetManifest.Load(manifestPath);
        foreach (AssetEntry entry in manifest.Props)
            _propMeshes[entry.Id] = sc.LoadMesh(PropLoader.LoadProp(entry));
        _placements = PropScatter.Generate(_field, ScatterConfig.ForestRing(), new RectArea(-58f, -58f, 58f, 16f));

        // Connect: same terrain field on both ends keeps client prediction identical to the server.
        _transport = new LiteNetLibClientTransport(_host, _port);
        _client = new WorldClient(_transport, _terrain.GroundHeight, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = TickSeconds });
        _clientClock = new FixedTickHost(TickSeconds);
    }

    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) { Quit(); return; }

        _client.Poll();

        // Drive prediction + input transmit at the fixed tick rate.
        Vector2 move = Vector2.Zero;
        if (Input.IsDown(Key.W)) move.Y += 1f;
        if (Input.IsDown(Key.S)) move.Y -= 1f;
        if (Input.IsDown(Key.D)) move.X += 1f;
        if (Input.IsDown(Key.A)) move.X -= 1f;
        bool run = Input.IsDown(Key.LeftShift) || Input.IsDown(Key.RightShift);
        var cmd = new MoveCommand(move, run, _camera.Yaw);
        _clientClock.Advance(dt, _ => _client.SendInput(cmd));

        _client.AdvancePresentation(dt);

        foreach (EntityRenderState e in _client.Snapshot())
            if (e.IsLocal) _localPos = e.Position;

        _camera.Target = _localPos;
        _camera.AspectRatio = FrameHeight > 0 ? (float)FrameWidth / FrameHeight : _camera.AspectRatio;
        _camController.Update(Input, dt);
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        foreach (var chunk in _chunks)
            scene.DrawTerrainChunk(chunk);

        scene.DrawProps(_placements, _propMeshes, _localPos, PropDrawRadius);

        foreach (EntityRenderState e in _client.Snapshot())
        {
            Vector3 p = e.Position;
            Color tint = e.IsLocal ? new Color(0.85f, 0.55f, 0.25f, 1f) : new Color(0.30f, 0.55f, 0.85f, 1f);
            scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), tint);
        }
    }

    protected override void OnDispose()
    {
        _transport?.Dispose();
        base.OnDispose();
    }
}
```
Add to `KhaozEngine.slnx`:
```xml
  <Project Path="NetworkedWalkSample/NetworkedWalkSample.csproj" />
```

- [ ] **Step 3: build, expect success** — `dotnet build NetworkedWalkSample/NetworkedWalkSample.csproj -c Debug`. Fix any name mismatches against the real `MeshPrimitives`/`MeshHandle`/`Scene3D.Draw`/`DrawProps`/`AssetManifest`/`PropLoader`/`ScatterConfig`/`RectArea`/`PropPlacement`/`TerrainChunkRegion`/`TerrainChunkBuilder` APIs (cross-check against `TerrainWalkSample/Program.cs`, which compiles).

- [ ] **Step 4: commit**
```bash
git add NetworkedWalkSample KhaozEngine.slnx
git commit -m "sample(7.47.0): NetworkedWalkSample networked walk client exe"
```

---

## Task 10: Version bump + full doc sweep + pack + release

**Files:**
- Modify: `Directory.Build.props`, `README.md`, `CLAUDE.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `docs/USING-KHAOZENGINE.md`, `CHANGELOG.md`, `CHANGENOTES.md`, `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`, `KhaozEngine.Server/KhaozEngine.Server.csproj`

- [ ] **Step 1: umbrella refs**
  - `KhaozEngine.Foundation.csproj`: add `<ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />`.
  - `KhaozEngine.Server.csproj`: add `<ProjectReference Include="../KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />`.

- [ ] **Step 2: bump version** — `Directory.Build.props`: `<KhaozEngineVersion>7.46.0</KhaozEngineVersion>` → `7.47.0`. Update the foundation-package enumeration comment to include `Locomotion`.

- [ ] **Step 3: guard declarations (the 3)**
  - `docs/CONSUMERS.md`: `**Engine current version:** \`7.47.0\``.
  - `docs/ROADMAP.md`: `Current released version: **7.47.0**`.
  - `README.md`: every `<PackageReference Include="KhaozEngine..." Version="7.47.0" />` example.

- [ ] **Step 4: package catalog / repo-layout / package map / umbrella docs**
  - `README.md`: add `KhaozEngine.Locomotion` and `KhaozEngine.NetWorld` to the package-catalog table and the repo-layout block.
  - `CLAUDE.md`: package map — add `Locomotion` to the Foundation leaves, `NetWorld` to the Server/netcode listing; update Foundation + Server umbrella descriptions; mention the networked-overworld slice under the terrain/overworld notes.
  - `docs/CONSUMERS.md`: umbrella/package table — add both packages (Locomotion → Foundation, NetWorld → Server).
  - `docs/USING-KHAOZENGINE.md`: add a "Networked overworld" usage section (CharacterMovement.Step, WorldServer, WorldClient + EntityRenderState, the demo exes).

- [ ] **Step 5: CHANGELOG + CHANGENOTES** (newest-first)
  - `CHANGELOG.md`: a `## 7.47.0` entry describing the two new packages, the controller refactor, the demos, and the design deviation (Step over Vector3, PlayerMoveState in NetWorld).
  - `CHANGENOTES.md`: a one/two-sentence 7.47.0 digest line.

- [ ] **Step 6: grep for stale references** — `grep -rn "7.46.0" *.md docs CLAUDE.md` and confirm every spot that should advance to 7.47.0 has (consumer pins legitimately lag — leave those). `grep -rn "Locomotion\|NetWorld" *.md docs CLAUDE.md` and confirm the new packages are documented everywhere a package list lives.

- [ ] **Step 7: run the guard** — `bash scripts/check-doc-versions.sh` → "all engine-version declarations match 7.47.0".

- [ ] **Step 8: full test suite** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → all green.

- [ ] **Step 9: build the samples** — `dotnet build NetworkedWalkServer/NetworkedWalkServer.csproj -c Debug && dotnet build NetworkedWalkSample/NetworkedWalkSample.csproj -c Debug`.

- [ ] **Step 10: pack** — `dotnet pack -c Release -o ./local-feed` (cumulative).

- [ ] **Step 11: commit the bump + docs**
```bash
git add -A
git commit -m "docs(7.47.0): networked overworld release notes + USING + package catalog; umbrella refs"
```

---

## Task 11: Merge + tag + push (autonomous release)

- [ ] **Step 1: merge to main** — from the main checkout root: `git checkout main && git merge --no-ff worktree-feature+networked-overworld`.
- [ ] **Step 2: re-pack from main root** — `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 3: full test on merged result** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → all green.
- [ ] **Step 4: tag + push** — `git tag v7.47.0 && git push origin main && git push origin v7.47.0`.
- [ ] **Step 5: clean up** — remove the worktree + delete the merged local branch (and the remote branch if it was ever pushed).

---

## Self-Review notes

- **Spec coverage:** Locomotion leaf (Task 1) ✓; controller refactor (Task 2) ✓; PlayerMoveSimulator (Task 4) ✓; WorldServer + WorldClient + EntityRenderState (Task 5) ✓; demos (Tasks 8-9) ✓; tests — CharacterMovement (T1), PlayerMoveSimulator (T4), round-trip + two-client + reconcile (T5-6), WorldClient render-state (T5/T6) ✓; minor bump + added-package doc sweep + guard + pack + release (T10-11) ✓.
- **Deviation logged:** Step over Vector3 + PlayerMoveState in NetWorld (Global Constraints) — permitted by the spec's open items, required by dependency hygiene.
- **Type consistency:** `WorldClient.Snapshot()`, `EntityRenderState(NetId, Vector3, bool)`, `MoveProtocol.{EncodeMove,TryDecodeMove,EncodeSnapshotFrame,TryDecodeSnapshotFrame,CreateRegistry}`, `WorldServer.Tick/Poll`, `PlayerMoveSimulator.Step(in,in,float)` consistent across tasks.
- **API risk:** the networked sample (Task 9) uses render-side APIs by analogy to `TerrainWalkSample`; Step 3 mandates cross-checking the real signatures since that sample compiles today.
