namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    /// <summary>Settles ground and selected prop layers without the windowed frame's apply budgets.</summary>
    internal void SettleForCapture(RegionCoord focus)
    {
        Flush(int.MaxValue);
        _propClusters.PrimeForCapture(focus);
    }
}
