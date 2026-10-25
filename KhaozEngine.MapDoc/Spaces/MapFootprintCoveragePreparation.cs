using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Retains cold all-footprint checks while restricting compiled context to the requested rectangle.</summary>
internal static class MapFootprintCoveragePreparation
{
    internal static MapFootprintPreparation Prepare(MapScopedSurfaces view, MapSpaceFootprint[] footprints,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits, Water.MapWaterWorkMeter? meter)
    {
        var preparation = new MapBoundPreparation(view, limits, null, meter);
        foreach (var footprint in footprints.OrderBy(f => f.Id, StringComparer.Ordinal))
            foreach (int cell in footprint.SlotCells.Order())
            {
                try
                {
                    var rect = preparation.Rectangle(footprint, cell);
                    var lower = preparation.Bound(footprint, footprint.Lower, rect[0], rect[1]);
                    if (lower.Status == MapRefinementStatus.Complete && footprint.Upper.Kind != MapBoundKind.OpenTop)
                        _ = preparation.Bound(footprint, footprint.Upper, rect[0], rect[1]);
                }
                catch (Exception error) when (error is MapExactOverflowException or MapDocumentException)
                {
                    // The released planner records these cell failures and continues draining other cells.
                    // They are interpreted by refinement only if the cell survives the rectangle filter.
                }
            }
        return MapCommonRefinement.Prepare(view, footprints, Cells, limits, null, meter, preparation);

        IEnumerable<int> Cells(MapSpaceFootprint footprint)
        {
            foreach (int cell in footprint.SlotCells)
            {
                var extent = MapEffectiveGeometry.Bounds(MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, cell));
                if (extent.MaxX.CompareTo(min.X) > 0 && extent.MinX.CompareTo(max.X) < 0 &&
                    extent.MaxZ.CompareTo(min.Z) > 0 && extent.MinZ.CompareTo(max.Z) < 0) yield return cell;
            }
        }
    }
}
