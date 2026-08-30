using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A dropped stack on a tile: what a kill leaves behind, replicated so every client can draw and
/// click it.</summary>
/// <remarks>
/// Deliberately two meaning-free integers rather than a dependency on <c>KhaozEngine.Items</c>: the engine
/// owns a drop's LIFECYCLE (spawn, replicate, expire, despawn) and stays agnostic about what an item IS, so a
/// game with its own item model consumes this identically to one on the engine's containers. The tile rides
/// IN the component because a ground item has no move state and no route: it is born on a tile, never leaves
/// it, and despawns from it, so the position is plain replicated data rather than a simulation.
/// <para>What taking one MEANS is the game's: the engine exposes
/// <see cref="TileWorldServer.TryGetGroundItem"/> and <see cref="TileWorldServer.DespawnGroundItem"/>, and a
/// game's pickup handler validates its own proximity rule, moves the stack into its own storage, and
/// despawns. See the package README's ground-items section.</para>
/// </remarks>
public struct TileGroundItem : IComponent
{
    /// <summary>The game's item id. Opaque to the engine.</summary>
    public int ItemId;

    /// <summary>How many ride the stack. At least 1 on anything a server spawns.</summary>
    public int Count;

    /// <summary>The tile the drop sits on, west-east.</summary>
    public int X;

    /// <summary>The tile the drop sits on, north-south.</summary>
    public int Z;

    /// <summary>The plane the drop sits on.</summary>
    public int Plane;

    /// <summary>The drop's tile as a coordinate.</summary>
    public readonly TileCoord Tile => new(X, Z, Plane);
}
