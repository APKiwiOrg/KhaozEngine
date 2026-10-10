using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Classifies the authored geometry and paint before a conversion can change any cell.</summary>
public static class MapConversionClassifier
{
    public static MapCellConversion Classify(MapSurfaceRef surface, MapSurfacePatch patch,
        long cellX, long cellZ, int subdivision)
    {
        if (subdivision is < 2 or > 64) throw new MapDocumentException("subdivision must be 2 to 64");
        try
        {
            var geometry = new MapConversionCellGeometry(surface, patch, cellX, cellZ);
            return Classify(geometry, subdivision);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("conversion geometry is not representable", error);
        }
    }

    internal static MapCellConversion Classify(MapConversionCellGeometry geometry, int subdivision)
    {
        MapSurfaceCell cell = geometry.Cell;
        MapCellConversionClass kind;
        string? reason = null;
        if (geometry.Surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld &&
            (cell.Underlay == 0 || (cell.Flags & MapCellFlags.NoDraw) != 0))
        {
            kind = MapCellConversionClass.NotRepresentable;
            reason = "legacy fallback has no native non-capture equivalent";
        }
        else if (cell.Overlay != 0 && cell.Cut is MapOverlayCut.CornerQuarter or MapOverlayCut.CornerThreeQuarter)
        {
            bool coplanar = geometry.Coplanar();
            kind = coplanar && subdivision % 2 == 0 ? MapCellConversionClass.ExactCoplanar
                : MapCellConversionClass.UnsupportedEncoding;
            reason = !coplanar ? "corner-cut crease crosses the regular fine lattice"
                : subdivision % 2 != 0 ? "corner-cut paint needs an even subdivision" : null;
        }
        else kind = geometry.Coplanar() ? MapCellConversionClass.ExactCoplanar : MapCellConversionClass.ExactDiagonal;
        return new(geometry.CellX, geometry.CellZ, kind, reason);
    }
}
