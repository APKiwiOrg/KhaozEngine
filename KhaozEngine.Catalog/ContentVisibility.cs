namespace KhaozEngine.Catalog;

/// <summary>
/// The CONTENT visibility vocabulary of contracts 11.1, which is deliberately not the replication one: a
/// row or a field is either public data a client may hold or data only the server ever sees.
/// </summary>
public enum ContentVisibility
{
    /// <summary>The client manifest carries it and a client may decode it.</summary>
    Client = 0,

    /// <summary>The client manifest omits it entirely, so a client never receives the bytes.</summary>
    ServerOnly = 1,
}

/// <summary>
/// The band a registering caller CLAIMS, and the mechanism behind contracts 4.4's MAY NOT (spec 3.6).
/// Prose was not a mechanism: without this, a game registering type 300 would have succeeded and collided
/// with the item-instances range at the next engine release, silently until the collision.
/// <para>
/// A band is NOT a capability. It does not let an <see cref="Engine"/> caller do anything a
/// <see cref="Game"/> caller cannot, it only says which ids the caller is entitled to. A game that wants an
/// engine type's behaviour still adds a validator rather than claiming a band.
/// </para>
/// </summary>
public enum ContentRegistrationBand
{
    /// <summary>Type ids <c>1</c> to <c>255</c>, passed by the engine's own registration helper.</summary>
    Engine,

    /// <summary>Type ids <c>256</c> to <c>1023</c>, passed by the item-instances registration helper.</summary>
    Instances,

    /// <summary>Type ids <c>1024</c> to <c>65535</c>, passed by every consumer directly.</summary>
    Game,
}
