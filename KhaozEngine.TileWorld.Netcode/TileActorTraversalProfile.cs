namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Opaque key selecting the registered collision topology a server-owned actor moves over. The engine compares
/// keys and never assigns meaning to their values.
/// </summary>
/// <param name="Value">A game-owned stable value. Zero is <see cref="Default"/>.</param>
public readonly record struct TileActorTraversalProfile(int Value)
{
    /// <summary>The server's original actor topology, backed by the collision map passed to its constructor.</summary>
    public static TileActorTraversalProfile Default => default;

    // Written when server-owned profile metadata is unexpectedly missing. It can never be registered, so the
    // movement system gives the entity the same safe answer as any other unknown key instead of falling back.
    internal static TileActorTraversalProfile Unresolved => new(int.MinValue);
}
