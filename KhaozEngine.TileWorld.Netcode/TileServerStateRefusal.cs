namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The stable reason an authoritative tile state was refused at a server admission door.</summary>
public enum TileServerStateRefusal
{
    /// <summary>Refuses a player state whose footprint is not one tile.</summary>
    PlayerFootprintNotOne,
    /// <summary>Refuses a state or component on a plane outside the configured world.</summary>
    PlaneOutsideWorld,
    /// <summary>Refuses a state or component in a region the collision map has not loaded.</summary>
    RegionNotLoaded,
    /// <summary>Refuses a step origin that the simulator could not have produced.</summary>
    StepOriginInvalid,
    /// <summary>Refuses nonzero step progress at or beyond the total step duration.</summary>
    StepProgressNotBelowTotal,
    /// <summary>Refuses a state with more remaining route steps than configured.</summary>
    RouteLimitExceeded,
}
