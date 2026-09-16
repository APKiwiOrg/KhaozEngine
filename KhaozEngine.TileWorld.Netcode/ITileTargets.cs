using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Resolves an interaction target id to the footprint and plane the reach rules run against. A seam rather than a
/// type, because what a target IS belongs to the game (an object in the world document, an npc, a dropped item)
/// while the reach rules over it belong here.
/// <para>Both heads build one from the same world files, so both resolve a target identically. That is what lets
/// the client pre-check a click against the same answer the server will reach and lets a predicted walk toward a
/// target replay without snapping.</para>
/// </summary>
public interface ITileTargets
{
    /// <summary>The target's footprint and plane. False when the id is unknown or not interactive, which is the
    /// answer a stale click gets after the thing it named stopped existing.</summary>
    bool TryGetFootprint(long target, out TileRect footprint, out int plane);

    /// <summary>
    /// Where a body LOOKING at this target points, in the tile units
    /// <see cref="TilePresenter.PoseAt(Vector2, float, TileDirection)"/> takes. PRESENTATION only: the reach rules,
    /// the follow and the wire all read <see cref="TryGetFootprint"/> and are untouched by whatever this answers.
    /// <para>The default is the footprint's CENTRE, the anchor plus half its width and height, which is the point
    /// <see cref="TilePresenter.PoseAt(TileRect, int, TileDirection)"/> already draws an overlay on. A game
    /// OVERRIDES it to nominate an aim tile on a body whose centre is the wrong place to look at (the head of a
    /// long serpent, the door of a building), and gets the continuous aim for free everywhere a pose is drawn.</para>
    /// <para>False for exactly what <see cref="TryGetFootprint"/> refuses, so a body whose target stopped resolving
    /// keeps the tile facing rather than pointing at a remembered place.</para>
    /// </summary>
    /// <param name="target">The target id, in whichever id space this resolver answers.</param>
    /// <param name="tilePlanar">Where to look, in tile units on the lattice (x, z).</param>
    /// <param name="plane">The plane the target stands on.</param>
    /// <returns>True when the target resolves.</returns>
    bool TryGetAimPoint(long target, out Vector2 tilePlanar, out int plane)
    {
        tilePlanar = default;
        if (!TryGetFootprint(target, out TileRect footprint, out plane)) return false;
        tilePlanar = new Vector2(footprint.X + (footprint.Width - 1) * 0.5f,
            footprint.Z + (footprint.Height - 1) * 0.5f);
        return true;
    }
}
