using System;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;

namespace KhaozEngine.Sharding;

/// <summary>
/// The singleton component carrying a simulation island's <see cref="WorldFrame"/>, so the frame is reachable from
/// the island's <see cref="World"/> alone. An island is one <see cref="World"/> plus one physics world, and a frame
/// is a property of that space rather than of any entity in it.
/// <para>
/// It lives on a reserved entity carrying no <c>NetId</c>, which is what keeps it invisible to everything that keys
/// on one: replication snapshots, persistence blobs, interest-grid rebuilds, border ghosting and handoff all iterate
/// <c>NetId</c>, so none of them sees it. Read it with <see cref="IslandFrames.GetIslandFrame"/> rather than by
/// querying this type directly.
/// </para>
/// </summary>
/// <remarks>
/// The alternative was threading a frame parameter through each seam that needs one, and there are at least four:
/// a pickup spawn callback (<c>Action&lt;World, Entity&gt;</c>), a consumer's per-tick brain, a consumer system
/// reading a ghost for cross-border collision, and the dynamic-body sampler. That is four signature changes, four
/// consumer-visible breaks, and four chances to miss a fifth site. A component travels with the world it describes
/// and cannot be reordered against it, so anything holding the world holds the frame, in the same reach.
/// </remarks>
public struct IslandFrame : IComponent
{
    /// <summary>The island's frame. <see cref="WorldFrame.Origin"/> on an unframed head.</summary>
    public WorldFrame Frame;
}

/// <summary>Reads and publishes the <see cref="IslandFrame"/> singleton on a <see cref="World"/>.</summary>
public static class IslandFrames
{
    /// <summary>
    /// The frame <paramref name="world"/> is expressed in, or <see cref="WorldFrame.Origin"/> when nothing published
    /// one - which is exactly right for every unframed head and every plain test world, since Origin's anchor is
    /// <c>Vector3.Zero</c> and a local expressed against it is already absolute.
    /// </summary>
    public static WorldFrame GetIslandFrame(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        WorldFrame frame = WorldFrame.Origin;
        world.ForEach<IslandFrame>((Entity _, ref IslandFrame f) => frame = f.Frame);
        return frame;
    }

    /// <summary>
    /// Publishes <paramref name="frame"/> as <paramref name="world"/>'s island frame, updating the reserved entity in
    /// place if one already exists so a re-anchor never accumulates entities. Called by the island that owns the
    /// world - a cell at construction, a single-island head at construction and on every re-anchor - never by a
    /// consumer, which reads it.
    /// </summary>
    public static void SetIslandFrame(this World world, WorldFrame frame)
    {
        ArgumentNullException.ThrowIfNull(world);
        bool published = false;
        world.ForEach<IslandFrame>((Entity _, ref IslandFrame f) => { f.Frame = frame; published = true; });
        if (!published) world.Set(world.Spawn(), new IslandFrame { Frame = frame });
    }
}
