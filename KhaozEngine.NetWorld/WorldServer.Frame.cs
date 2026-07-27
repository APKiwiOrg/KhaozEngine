using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The flat head's ISLAND FRAME: the opt-in half of floating origin that keeps simulation precise a long way from
/// the world origin. A simulation island is one <see cref="World"/> plus one <c>IPhysicsWorld</c>, and a frame is a
/// property of that space rather than of any entity in it. <see cref="WorldServer"/> is exactly one island, so it
/// has exactly one frame, and it follows one player.
/// <para>
/// Off by default. What it changes when on: the per-tick step runs on a frame-local position (which is the term the
/// measurement showed accumulating, since a re-anchor bounds the per-tick rounding quantum from the next tick
/// onward), the island's physics world is rebased alongside it so a query never crosses spaces, and every entity in
/// the world carries a stamp of the frame it is expressed in. What it does NOT change: everything the server hands
/// a consumer or puts on the wire is still absolute world metres.
/// </para>
/// </summary>
public sealed partial class WorldServer
{
    // The island's frame. WorldFrame.Origin (and therefore byte-identical to the pre-frame server) unless
    // FrameAnchoring is on and the followed player has walked past the re-anchor radius.
    private WorldFrame islandFrame = WorldFrame.Origin;

    /// <summary>The island's current frame. <see cref="WorldFrame.Origin"/> unless
    /// <see cref="WorldServerConfig.FrameAnchoring"/> is on, in which case it follows the anchored player. Read it
    /// to convert something the engine handed you in island space; everything on the PUBLIC surface
    /// (<see cref="TryGetPlayerState"/>, <see cref="PlayerLeaving"/>, <see cref="ListOnline"/>,
    /// <see cref="ReplicatedPosition.Value"/>, the wire) is already absolute.</summary>
    public WorldFrame IslandFrame => islandFrame;

    /// <summary>Raised after the island has re-anchored, with (from, to, delta): the EXACT translation carrying a
    /// coordinate from the old frame into the new one. Everything the engine owns is already converted by the time
    /// this fires, INCLUDING the island's own <c>IPhysicsWorld</c> (the island rebases it, not the consumer) and
    /// every <see cref="ReplicatedPosition"/> in the world. It is for the tail of state a consumer holds in the old
    /// frame itself: cached poses it read out of the physics world, its own spatial indices, debug overlays. A
    /// consumer that only reads absolute positions needs no handler at all.
    /// <para>Never raised while <see cref="WorldServerConfig.FrameAnchoring"/> is off.</para></summary>
    public event Action<WorldFrame, WorldFrame, Vector3>? FrameChanged;

    // The island's planar anchor as the flat Vector2 a PlayerMoveState stamp carries.
    private Vector2 IslandAnchorXz => new(islandFrame.Anchor.X, islandFrame.Anchor.Z);

    /// <summary>A state re-expressed against <paramref name="targetAnchor"/>, reading the state's OWN stamp to know
    /// where it is coming from. Absolute is just the zero anchor, so this is the one conversion in both directions:
    /// a state written from outside carries a zero stamp (see <see cref="PlayerMoveState.Position"/>) and lands in
    /// the island, and a state handed back to a consumer lands at zero. Y is untouched, always.</summary>
    private static PlayerMoveState Reframe(PlayerMoveState state, Vector2 targetAnchor)
    {
        Vector2 from = state.FrameAnchor;
        if (from == targetAnchor) return state;
        Vector3 p = state.Move.Position;
        state.Move.Position = new Vector3(p.X + (from.X - targetAnchor.X), p.Y, p.Z + (from.Y - targetAnchor.Y));
        state.FrameAnchor = targetAnchor;
        return state;
    }

    // A state coming FROM outside (spawn, teleport, persisted record, a consumer's SetPlayerState) into the island.
    private PlayerMoveState ToIsland(PlayerMoveState state) => Reframe(state, IslandAnchorXz);

    // A state going OUT to a consumer, or to persistence. Absolute, with a zero stamp, so the position and the
    // stamp can never disagree on the public surface.
    private static PlayerMoveState ToAbsolute(PlayerMoveState state) => Reframe(state, Vector2.Zero);

    // The absolute world position of a joined slot, for the area-of-interest query (the interest grid is keyed on
    // absolute positions, because a key built from a local would collide across frames).
    private Vector3 AbsolutePositionOf(int slot) =>
        stateBySlot.TryGetValue(slot, out PlayerMoveState s) ? ToAbsolute(s).Position : Vector3.Zero;

    /// <summary>
    /// Re-anchor the island if the followed player has drifted past <see cref="WorldFrame.ReanchorRadius"/>. Runs
    /// once per tick, AFTER every entity has stepped and BEFORE the area-of-interest pass, so the anchor is a
    /// function of a settled position and no step ever observes a half-rebased island.
    /// <para>The order inside is the whole safety argument: the physics world and every entity move in the same gap
    /// between two steps. The physics rebase is exact and unobservable (velocities, sleep state, contacts and
    /// constraints all survive), and each entity's conversion is exact because a re-anchor rounds to the NEAREST
    /// grid point from a trigger past 96 m, so the local's magnitude strictly shrinks.</para>
    /// <para>The flat head is ONE island, so it follows ONE player: the lowest joined slot, deterministically. A
    /// world with players spread across it needs an island per region, which is the sharded head.</para>
    /// </summary>
    private void ReanchorIsland()
    {
        if (!config.FrameAnchoring || netIdBySlot.Count == 0) return;

        int followed = int.MaxValue;
        foreach (int slot in netIdBySlot.Keys) followed = Math.Min(followed, slot);
        if (!stateBySlot.TryGetValue(followed, out PlayerMoveState state)) return;
        if (!WorldFrame.ShouldReanchor(state.Position)) return;

        WorldFrame previous = islandFrame;
        WorldFrame target = WorldFrame.Nearest(ToAbsolute(state).Position);
        if (target == previous) return;

        physics?.Rebase(target.Anchor);

        Vector2 anchor = new(target.Anchor.X, target.Anchor.Z);
        world.ForEach<ReplicatedPosition>((Entity _, ref ReplicatedPosition pos) =>
        {
            if (pos.Frame != target) pos = pos.ToFrame(target);
        });
        var slots = new List<int>(stateBySlot.Keys);   // snapshot: the loop rewrites every entry
        foreach (int slot in slots)
            stateBySlot[slot] = Reframe(stateBySlot[slot], anchor);

        islandFrame = target;
        simulator.Frame = target;
        FrameChanged?.Invoke(previous, target, previous.DeltaTo(target));
    }

    /// <summary>
    /// The self-heal, folded into the pass that rebuilds the area-of-interest index so it costs no extra iteration:
    /// everything this island owns must be stamped with this island's frame. A component written through the legacy
    /// <see cref="ReplicatedPosition.Value"/> setter (a consumer's <see cref="OnBeforeTick"/> brain, a pickup spawn)
    /// carries <see cref="WorldFrame.Origin"/> with an absolute value, which is a VALID representation of the same
    /// world position rather than a wrong one, so this converts it exactly rather than repairing damage. One
    /// comparison per entity per tick.
    /// </summary>
    private void RebuildInterestAndHealFrames()
    {
        interest.Clear();
        WorldFrame frame = islandFrame;
        world.ForEach<NetId, ReplicatedPosition>((Entity _, ref NetId id, ref ReplicatedPosition pos) =>
        {
            if (pos.Frame != frame) pos = pos.ToFrame(frame);
            Vector3 absolute = pos.Value;
            interest.Insert(id.Value, absolute.X, absolute.Z);
        });
    }
}
