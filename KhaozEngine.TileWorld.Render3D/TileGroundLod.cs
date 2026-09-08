namespace KhaozEngine.TileWorld;

/// <summary>Ground detail built for one TileWorld region-plane.</summary>
public enum TileGroundLod
{
    /// <summary>The authored triangulation of every drawable tile.</summary>
    Full,

    /// <summary>Uniform four by four interiors collapsed to one pair, with authored boundaries left full.</summary>
    Coarse4,
}
