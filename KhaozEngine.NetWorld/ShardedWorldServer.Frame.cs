using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>The sharded head's floating-origin knobs: the per-cell island frame, the space its samplers read, and
/// the factory that builds each cell's own physics world.</summary>
public sealed partial class ShardedWorldServerConfig
{
    /// <summary>
    /// Give every cell its own ISLAND FRAME, anchored at the frame nearest that cell's centre and fixed for the
    /// cell's life, so the positions it simulates stay a couple of hundred metres from their anchor however far the
    /// world extends. <b>ON by default.</b> This is the shape a 100 km world needs: a shard server simulates many
    /// players spread across the whole map, and one frame per process (or per player) cannot serve them, while one
    /// frame per cell can.
    /// <para>Because a cell never moves, its frame never moves either, so the sharded head performs NO runtime
    /// rebase at all: no physics translate mid-run, no sleeping-body wake risk, no re-anchor ordering. An entity's
    /// frame changes only at a cell handoff, which <c>ShardHost.ProcessHandoffs</c> already runs as a discrete,
    /// exactly-once, ordered event.</para>
    /// <para>Everything on the public surface (<see cref="ShardedWorldServer.TryGetPlayerState"/>,
    /// <see cref="ShardedWorldServer.ListOnline"/>, <see cref="ReplicatedPosition.Value"/>, cell keying, the
    /// interest grid) stays absolute world metres. Set false for byte-identical pre-frame behaviour.</para>
    /// </summary>
    public bool FrameAnchoring { get; init; } = true;

    /// <summary>The coordinate space this server's sampler delegates (ground height, ground normal, medium) read.
    /// Mirrors <see cref="WorldServerConfig.SamplerSpace"/> and is only meaningful with
    /// <see cref="FrameAnchoring"/> on. <see cref="NetWorld.SamplerSpace.World"/> (the default) keeps them on
    /// absolute coordinates and each per-cell step converts for them. <see cref="NetWorld.SamplerSpace.Frame"/>
    /// hands them frame-local coordinates instead, which is REQUIRED for a sampler backed by the cell's own physics
    /// world (a <c>PhysicsGroundProbe</c>): that world raycasts in its own space, so wrapping the call back out to
    /// absolute makes every ray miss and the probe silently flattens the ground.</summary>
    public SamplerSpace SamplerSpace { get; init; } = SamplerSpace.World;

    /// <summary>
    /// Builds each cell's OWN physics world, called once per cell at creation with that cell's coordinate. This
    /// REPLACES the single <c>IPhysicsWorld</c> the pre-16 server took: a frame is a property of a space and a
    /// physics world IS a space, so cells stepping in their own frames cannot share one - two players a grid step
    /// apart would query the same colliders from spaces 128 m away from each other, which is falling through
    /// terrain and walking through walls, not a rounding artifact.
    /// <para>Null (the default) leaves every cell without physics, exactly as passing no physics world did.</para>
    /// <para><b>The consumer populates the returned world, and the engine never adds a static to a cell world.</b> The
    /// contract, in four points:</para>
    /// <list type="bullet">
    /// <item><b>Extent.</b> It must contain every static whose geometry comes within
    /// <c>CellSize / 2 + OverlapMargin</c> of the cell centre, per axis. Anything nearer than that can be queried by
    /// an entity this cell owns or ghosts. Anything further cannot.</item>
    /// <item><b>Space.</b> Poses are relative to <c>IPhysicsWorld.Origin</c>, which the consumer sets to the cell
    /// frame's anchor - read it from <c>ShardHost.FrameFor(coord).Anchor</c> rather than re-deriving it. With
    /// <see cref="FrameAnchoring"/> OFF, a world left at <c>Vector3.Zero</c> is a correct but unframed cell, which is
    /// supported (every cell's anchor is <see cref="WorldFrame.Origin"/> then, so <c>Vector3.Zero</c> IS the frame
    /// anchor). With <see cref="FrameAnchoring"/> ON, the anchor is per-cell and a world whose <c>Origin</c> does not
    /// equal it is a misconfiguration <see cref="ShardHost"/> validates at cell creation and throws on, naming this
    /// contract - it is never silently supported.</item>
    /// <item><b>Lifetime.</b> The world is disposed with the cell. Sharing one between two cells rebuilds the
    /// failure above.</item>
    /// <item><b>Duplication is expected.</b> A static within <c>OverlapMargin</c> of a border legitimately exists in
    /// both neighbours' worlds. Nothing reconciles the copies, because a static does not move.</item>
    /// </list>
    /// </summary>
    public Func<CellCoord, IPhysicsWorld>? PhysicsWorldFactory { get; init; }
}

/// <summary>
/// The sharded head's ISLAND FRAMES: one island per <see cref="CellSim"/>, each with its own frame and its own
/// physics world. A simulation island is one <see cref="World"/> plus one <c>IPhysicsWorld</c>, and a frame is a
/// property of that space rather than of any entity in it, which is exactly why a shard server can serve a 100 km
/// world when a single-island head cannot: it has as many frames as it has cells.
/// <para>
/// A cell's frame is fixed at its creation and never moves, so this head performs NO runtime rebase. What it does
/// instead is give each cell its own stepper, holding that cell's physics world, that cell's frame and that cell's
/// frame-adapted samplers, all readonly for the instance's life - which is what keeps the per-cell fan-out free of
/// shared mutable state.
/// </para>
/// </summary>
public sealed partial class ShardedWorldServer
{
    // The pieces every cell's stepper shares, captured from the constructor. The per-cell parts (physics world,
    // frame) come off the cell itself.
    private readonly Func<float, float, float> cellGroundHeight;
    private readonly Func<float, float, Vector3>? cellGroundNormal;
    private readonly WorldBounds? cellBounds;
    private readonly Func<float, float, float, MovementMedium>? cellMedium;

    // One runtime per live cell, built on first use and dropped when the cell is unloaded (a recreated coordinate is
    // a genuinely fresh CellSim with an empty world, so its runtime has to be fresh too).
    private readonly Dictionary<CellCoord, CellRuntime> cellRuntime = new();

    /// <summary>
    /// One cell's simulation runtime: the <see cref="PlayerMovementSystem"/> added to that cell's world, plus the
    /// spawn ground-clamp that runs in the same space. Both are constructed against the cell's OWN physics world and
    /// frame, so nothing mutable is shared across the parallel cell fan-out.
    /// </summary>
    private sealed class CellRuntime
    {
        public required PlayerMovementSystem Movement { get; init; }
        public required PlayerMoveSimulator Clamp { get; init; }
        public required WorldFrame Frame { get; init; }

        /// <summary>Ground-clamps an ABSOLUTE spawn position and hands back an ABSOLUTE state: the clamp steps in
        /// the cell's frame (so it queries the cell's own colliders in their own space) and the conversion happens on
        /// both sides of it, because the caller keys the owning cell off the absolute position.</summary>
        public PlayerMoveState SpawnClamp(in PlayerMoveState absolute, float dt)
        {
            Vector3 anchor = Frame.Anchor;
            PlayerMoveState seeded = absolute.ToAnchor(new Vector2(anchor.X, anchor.Z));
            return Clamp.Step(seeded, MoveCommand.Idle, dt).Absolute;
        }
    }

    // Builds (once) and returns the runtime for a cell. EnsureWired adds the movement system to the cell's world the
    // first time, which is also the first time this runs for that coordinate.
    private CellRuntime RuntimeFor(CellSim cell)
    {
        if (cellRuntime.TryGetValue(cell.Coord, out CellRuntime? runtime)) return runtime;
        runtime = new CellRuntime
        {
            Movement = new PlayerMovementSystem(cellGroundHeight, tuning, cellGroundNormal, cellBounds,
                cell.Physics, cellMedium, cell.Frame, config.SamplerSpace),
            Clamp = new PlayerMoveSimulator(cellGroundHeight, tuning, cellGroundNormal, cellBounds,
                cell.Physics, cellMedium, config.SamplerSpace) { Frame = cell.Frame },
            Frame = cell.Frame,
        };
        cellRuntime[cell.Coord] = runtime;
        cell.World.AddSystem(runtime.Movement);
        return runtime;
    }

    private void EnsureWired(CellSim cell) => RuntimeFor(cell);

    /// <summary>
    /// The island frame of the cell at <paramref name="coord"/>: <c>WorldFrame.Nearest(cell centre)</c> when
    /// <see cref="ShardedWorldServerConfig.FrameAnchoring"/> is on, <see cref="WorldFrame.Origin"/> otherwise. Pure,
    /// so a <see cref="ShardedWorldServerConfig.PhysicsWorldFactory"/> can call it to learn the <c>Origin</c> the
    /// world it is building must be expressed against.
    /// </summary>
    public WorldFrame FrameFor(CellCoord coord) => host.FrameFor(coord);

    // A cell's frame is fixed at its CENTRE, so the worst frame-local coordinate a cell can hold is bounded by its
    // own size rather than by how far a player walks, and a large enough CellSize puts that past the float32
    // divergence ceiling with nothing at runtime to notice. The operand is the PLANAR magnitude, and the sqrt(2) is
    // load-bearing rather than decorative: CellSize 600 with the default OverlapMargin 24 gives a per-axis worst of
    // 388 m, which clears a 512 m per-axis ceiling comfortably, while its planar magnitude of 549 m sits in the next
    // binade up and predicts 13.1 mm of divergence per 20 s window against a 10 mm budget.
    //
    // Gated on FrameAnchoring, deliberately: an unframed cell has no frame-local coordinate at all, so the ceiling
    // does not apply to it and refusing its config would be a throw with no failure behind it.
    private static void ValidateCellSizeAgainstFrameGrid(ShardedWorldServerConfig config)
    {
        if (!config.FrameAnchoring) return;
        float worstAxis = config.CellSize * 0.5f + config.OverlapMargin + WorldFrame.Grid * 0.5f;
        float worstPlanar = worstAxis * MathF.Sqrt(2f);
        if (worstPlanar > WorldFrame.MaxLocalRadius)
            throw new ArgumentException(
                $"CellSize {config.CellSize} with OverlapMargin {config.OverlapMargin} puts a cell's worst frame-local "
              + $"coordinate at {worstAxis} m per axis, a planar magnitude of {worstPlanar} m, past the "
              + $"{WorldFrame.MaxLocalRadius} m float32 divergence ceiling (the ceiling is on the planar magnitude). "
              + "Reduce CellSize, or turn FrameAnchoring off and accept the precision.", nameof(config));
    }

    /// <summary>
    /// Converts an entity arriving in a cell into that cell's frame, by re-expressing its
    /// <see cref="ReplicatedPosition"/>. Stateless and shared by every cell, since the frame is a parameter rather
    /// than instance state. This is the layer that owns the framed component supplying the conversion the topology
    /// layer cannot write for itself.
    /// </summary>
    private sealed class ReplicatedPositionFrameAdapter : ICellFrameAdapter
    {
        public static readonly ReplicatedPositionFrameAdapter Instance = new();

        public void ToFrame(World world, Entity entity, WorldFrame frame)
        {
            if (world.TryGet(entity, out ReplicatedPosition pos) && pos.Frame != frame)
                world.Set(entity, pos.ToFrame(frame));
        }
    }
}
