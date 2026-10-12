namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    /// <summary>Settles ground, cover and selected prop layers without the windowed frame's apply budgets. The one
    /// offline exception to background profile builds: every dirty mark is submitted, then full ground and cover
    /// are built and applied on this thread. Never called by a residency update or prime.</summary>
    internal void SettleForCapture(RegionCoord focus)
    {
        Flush(int.MaxValue);
        _profileBuilds.PrimeForCapture(focus);
        _propClusters.PrimeForCapture(focus);
    }
}
