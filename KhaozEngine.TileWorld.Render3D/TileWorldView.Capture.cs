using System.Numerics;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    /// <summary>Blocks the calling scene thread until queued ground, ground cover, selected prop layers and
    /// HLOD meshes, and the opted-in interior cut are built and applied for an offline or headless capture.</summary>
    /// <remarks>Ignores windowed apply budgets. Call before the first capture frame and after region loads,
    /// unloads or announced edits. Does not change residency, the camera or <see cref="Observer"/>. Repeating
    /// an unchanged settle retains applied meshes and cover. Never call from a windowed frame loop.
    /// Regular residency updates and primes do not call this blocking operation.</remarks>
    /// <param name="focus">Region used to order result application by proximity.</param>
    /// <param name="interiorFocus">World-space focus for the interior-cut window. Null uses the centre of
    /// <paramref name="focus"/>.</param>
    public void SettleForCapture(RegionCoord focus, Vector3? interiorFocus = null)
    {
        Flush(int.MaxValue);
        _profileBuilds.PrimeForCapture(focus);
        _propClusters.PrimeForCapture(focus);
        SettleInteriorCutForCapture(interiorFocus ?? TileWorldSpace.ToWorld(
            focus.OriginX + TileRegion.Size * .5f, 0f, focus.OriginZ + TileRegion.Size * .5f, _doc.TileSize));
    }

    /// <summary>Blocks the calling scene thread until the opted-in interior cut is built and applied at
    /// <paramref name="focus"/> for an offline or headless capture.</summary>
    /// <remarks>Ignores the interior apply budget without settling ground, cover or selected prop layers.
    /// Use before an offline draw when only the camera focus moves. Does nothing when interior cutting is
    /// disabled and retains the applied field when no rebuild is needed. Never call from a windowed frame loop.</remarks>
    /// <param name="focus">World-space focus for the interior-cut window.</param>
    public void SettleInteriorCutForCapture(Vector3 focus)
    {
        if (_interiorMeshes is null || _cutArchetypeIds.Count == 0) return;
        PumpInteriorCut(focus);
        _interiorBuilds.PrimeForCapture(default);
    }
}
