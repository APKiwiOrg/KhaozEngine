namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The stable reason the tile server's interest policy refused a configuration or footprint.</summary>
public enum TileInterestRefusal
{
    /// <summary>Refuses a configured interest radius wider than the overlap margin.</summary>
    ConfiguredRadiusExceedsMargin,
    /// <summary>Refuses a footprint whose inflated interest query radius exceeds the overlap margin.</summary>
    FootprintRadiusExceedsMargin,
}
