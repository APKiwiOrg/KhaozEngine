namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The stable reason a tile component codec refused a value or malformed payload.</summary>
public enum TileComponentCodecRefusal
{
    /// <summary>Refuses encoding a route longer than the wire limit.</summary>
    RouteWriteLimitExceeded,
    /// <summary>Refuses a component declaring more route steps than the wire limit.</summary>
    RouteReadLimitExceeded,
    /// <summary>Refuses a route count larger than its bounded component payload.</summary>
    RoutePayloadTruncated,
    /// <summary>Refuses an unbounded route stream that returns fewer bytes than declared.</summary>
    RouteStreamShortRead,
    /// <summary>Refuses a component declaring a display name above the byte limit.</summary>
    NameReadLimitExceeded,
    /// <summary>Refuses a display-name length larger than its bounded component payload.</summary>
    NamePayloadTruncated,
    /// <summary>Refuses an unbounded display-name stream that returns fewer bytes than declared.</summary>
    NameStreamShortRead,
    /// <summary>Refuses encoding an item-instance payload above the wire limit.</summary>
    InstanceWriteLimitExceeded,
    /// <summary>Refuses a component on a plane outside the configured world.</summary>
    PlaneOutsideWorld,
}
