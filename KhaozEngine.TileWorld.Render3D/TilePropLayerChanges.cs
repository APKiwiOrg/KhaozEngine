namespace KhaozEngine.TileWorld;

/// <summary>Determines whether an override changes a detached layer batch before invalidating its cluster.</summary>
internal static class TilePropLayerChanges
{
    public static bool SameContent(TilePropLayerSnapshot before, TilePropLayerSnapshot after)
    {
        if (before.Placements.Count != after.Placements.Count || before.Plane != after.Plane ||
            before.IsRoofLayer != after.IsRoofLayer) return false;
        for (int i = 0; i < before.Placements.Count; i++)
        {
            if (before.ObjectIds[i] != after.ObjectIds[i] || !before.Placements[i].Equals(after.Placements[i]))
                return false;
            if (before.IsRoofLayer && before.RoofFootprints[i] != after.RoofFootprints[i]) return false;
        }
        return true;
    }
}
