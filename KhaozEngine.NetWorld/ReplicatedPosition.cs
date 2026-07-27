using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;

namespace KhaozEngine.NetWorld;

/// <summary>The one replicated gameplay component: an entity's 3D world position. Interpolatable.
/// <para>Stored as a frame STAMP plus a frame-local offset. <c>default</c> is <see cref="WorldFrame.Origin"/> with
/// the local holding the absolute position, so a game that never leaves the world origin is byte-identical to the
/// pre-frame engine and every existing reader keeps working: <see cref="Value"/> reads absolute, always.</para></summary>
public struct ReplicatedPosition : IComponent
{
    /// <summary>The frame <see cref="Local"/> is expressed against: a STAMP of the owning simulation island's frame
    /// (one world plus one physics world), never an independent per-entity choice. On a framed head the island
    /// writes it, and nothing derives it on the receiving side.</summary>
    public WorldFrame Frame;

    /// <summary>Position relative to <see cref="Frame"/>. X and Z are frame-local, Y is absolute world height
    /// (Y is never framed).</summary>
    public Vector3 Local;

    /// <summary>The absolute world position, READ-ONLY. Every reader (interest grids, cell keying, persistence,
    /// handoff, a consumer's render feed) keeps getting exactly what it got before, and always as precise as
    /// <see cref="Local"/>, since <see cref="WorldFrame.Anchor"/> is exact in float32.
    /// <para>There is deliberately NO setter, and that compile break is the point of the major. A
    /// <c>new ReplicatedPosition { Value = p }</c> silently reset the stamp to <see cref="WorldFrame.Origin"/>,
    /// which was recoverable while the wire was absolute and is not once the stamp rides it: a stale stamp on the
    /// wire is a position a frame-width from where it belongs. Removing the setter turns every such site into a
    /// build error whose fix is one question - where did this position come from? - answered by
    /// <see cref="FromWorld"/> (from outside the simulation) or <see cref="InFrame"/> (out of the simulation, or
    /// out of a physics world already in that frame).</para></summary>
    public readonly Vector3 Value => Frame.Anchor + Local;

    /// <summary>An ABSOLUTE world position converted into <paramref name="frame"/>. For a position arriving from
    /// outside the simulation: an authored spawn, a persisted record, an admin teleport.</summary>
    public static ReplicatedPosition FromWorld(Vector3 world, WorldFrame frame) =>
        new() { Frame = frame, Local = frame.ToLocal(world) };

    /// <summary>A position ALREADY expressed in <paramref name="frame"/>. For a position coming out of the
    /// simulation, or out of a physics world whose <c>Origin</c> is that frame's anchor.</summary>
    public static ReplicatedPosition InFrame(WorldFrame frame, Vector3 local) =>
        new() { Frame = frame, Local = local };

    /// <summary>This position with a new local, same frame. The step's write-back: the frame is preserved by
    /// construction rather than re-derived.</summary>
    public readonly ReplicatedPosition WithLocal(Vector3 local) => new() { Frame = Frame, Local = local };

    /// <summary>This position expressed against <paramref name="target"/>. Exact whenever the conversion does not
    /// grow the local's magnitude, which a re-anchor guarantees by construction (see <see cref="WorldFrame"/>).
    /// Between two arbitrary frames it is exact to half a ULP of the destination magnitude.</summary>
    public readonly ReplicatedPosition ToFrame(WorldFrame target) =>
        new() { Frame = target, Local = Local + Frame.DeltaTo(target) };
}
