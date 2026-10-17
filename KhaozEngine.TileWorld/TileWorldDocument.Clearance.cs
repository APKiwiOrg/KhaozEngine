namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldDocument
{
    /// <summary>The world's clearance declaration, or null when it declares none. Authoring metadata, outside
    /// <see cref="TileWorldHash.OfWorld"/>.</summary>
    public TileClearanceSettings? Clearance { get; private set; }

    /// <summary>Replaces the clearance declaration. Null removes it, and a saved world then has no block.</summary>
    public void SetClearance(TileClearanceSettings? settings) => Clearance = settings;
}
