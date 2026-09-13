namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Which presentation rule <see cref="TileDrawPriority"/> applies to bodies sharing tiles.</summary>
public enum TileDrawPriorityPolicy : byte
{
    /// <summary>
    /// One body owns every occupied tile, including while it moves. Bodies cross between visible and hidden using
    /// their presented step progress or <see cref="TileDrawPriority.FadeSeconds"/>. This is the default.
    /// </summary>
    OneBodyPerTile = 0,

    /// <summary>
    /// Every moving body remains fully visible and owns no tile. Only bodies settled on the same tile are reduced
    /// to one winner, with binary visibility weights and no fade.
    /// </summary>
    SettledStacksOnly = 1,
}
