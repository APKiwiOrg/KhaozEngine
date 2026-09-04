using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Replicated mutable state for ONE authored object: what makes a chopped tree a stump on every client
/// rather than only on the client that chopped it.</summary>
/// <remarks>
/// An entity per DEPARTED object rather than per object, which is the whole shape. A world nobody has touched
/// carries no entities at all, so the cost is the number of objects currently away from their authored form
/// rather than the number of objects authored, and a world of a thousand trees with two stumps in it costs two.
/// <para><see cref="State"/> is an opaque int the engine assigns no meaning to, the
/// <see cref="TileGroundItem"/> rule: the engine owns the LIFECYCLE (set, replicate, expire, clear) and stays
/// agnostic about what a state IS, so 1 meaning "stump" and 2 meaning "picked" are the game's constants and
/// never the engine's. A game maps a state to a look, and the renderer half of that is
/// <c>TileWorldView.OverrideArchetype</c>.</para>
/// <para><see cref="X"/>, <see cref="Z"/> and <see cref="Plane"/> are not padding and not a convenience. A
/// replicated component can encode and decode perfectly and never be SERVED: the interest grid asks an entity
/// where it is (<c>TileWorldServer.PositionOf</c>) and the serve asks it which plane it is on
/// (<c>TileWorldServer.CollectPlane</c>), and an entity that answers neither is filtered out of every viewer's
/// frame while every test of its codec passes. An object never moves, so the tile rides IN the component as
/// plain replicated data, exactly as a ground item's does.</para>
/// <para>What the object's id MEANS is the document's: it is a <c>TileObject.Id</c>, and the server never holds
/// a <c>TileWorldDocument</c>, so the engine neither validates the id nor knows what the object was authored
/// as. See the package README's object-states section.</para>
/// </remarks>
public struct TileObjectState : IComponent
{
    /// <summary>The authored object this state belongs to, a <c>TileObject.Id</c>. Opaque to the engine.</summary>
    public long ObjectId;

    /// <summary>What the object has become. Opaque to the engine: the game's own state constant.</summary>
    public int State;

    /// <summary>The tile the object stands on, west-east.</summary>
    public int X;

    /// <summary>The tile the object stands on, north-south.</summary>
    public int Z;

    /// <summary>The plane the object stands on.</summary>
    public int Plane;

    /// <summary>The object's tile as a coordinate.</summary>
    public readonly TileCoord Tile => new(X, Z, Plane);
}
