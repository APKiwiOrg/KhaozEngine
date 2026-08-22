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
}
