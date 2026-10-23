using System.Numerics;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    /// <summary>Settles ground, cover, selected prop layers and interiors without the windowed frame's apply budgets. The one
    /// offline exception to background profile builds: every dirty mark is submitted, then full ground and cover
    /// are built and applied on this thread. Never called by a residency update or prime.</summary>
    internal void SettleForCapture(RegionCoord focus, Vector3? interiorFocus = null)
    {
        Flush(int.MaxValue);
        _profileBuilds.PrimeForCapture(focus);
        _propClusters.PrimeForCapture(focus);
        SettleInteriorCutForCapture(interiorFocus ?? TileWorldSpace.ToWorld(
            focus.OriginX + TileRegion.Size * .5f, 0f, focus.OriginZ + TileRegion.Size * .5f, _doc.TileSize));
    }

    // Single-image captures retain their legacy ground and prop settle. Only the opted-in interior field
    // needs this offline completion, so captures without the new options keep their existing submissions.
    internal void SettleInteriorCutForCapture(Vector3 focus)
    {
        if (_interiorMeshes is null || _cutArchetypeIds.Count == 0) return;
        PumpInteriorCut(focus);
        _interiorBuilds.PrimeForCapture(default);
    }
}
