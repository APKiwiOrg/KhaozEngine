using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Complete-view exact geometry validation for bounded authoring transactions.</summary>
public static class MapSpaceCoverageValidator
{
    public static IReadOnlyList<string> Validate(MapScopedSurfaces completeView) => Validate(completeView, new());
    public static IReadOnlyList<string> Validate(MapScopedSurfaces completeView, MapRefinementLimits limits) =>
        ValidateCore(completeView, limits, null);
    internal static IReadOnlyList<string> Validate(MapScopedSurfaces completeView, MapRefinementLimits limits, MapBoundFaceWork work) =>
        ValidateCore(completeView, limits, work);

    static IReadOnlyList<string> ValidateCore(MapScopedSurfaces view, MapRefinementLimits limits, MapBoundFaceWork? work)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(limits);
        if (!view.Witness.Complete) throw new ArgumentException("coverage validation requires a complete view", nameof(view));
        MapTopologyRecord[] records = MapSpaceGeometry.Records(view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var findings = new List<(string Id, int Cell, string Text)>();
        var patches = new MapSpaceGeometry.Patches(view);
        foreach (string finding in MapTopologyReferenceValidator.Validate(view.Surfaces, patches))
            Add(OwnerOf(finding), -1, finding);
        foreach (MapSurfacePatch patch in patches)
            foreach (string finding in MapSeamValidator.ValidateCornerDependencies(patch, view))
                Add(patch.Key.ToString(), -1, finding);
        foreach (MapSurfaceSeam seam in records.OfType<MapSurfaceSeam>())
            foreach (string finding in MapSeamValidator.Validate(seam, view)) Add(seam.Id, -1, finding);
        MapSpaceFootprint[] footprints = records.OfType<MapSpaceFootprint>().ToArray();
        MapFootprintPreparation prepared = MapCommonRefinement.PrepareFootprints(view, footprints, limits, work);
        var columns = new List<MapSpaceColumn>();
        foreach (MapSpaceFootprint footprint in footprints)
        {
            bool contextFinding = false;
            foreach (int cell in footprint.SlotCells.Order())
            {
                MapCellRefinement refinement;
                if (prepared.CellOutcomes.TryGetValue((footprint.Id, cell), out MapCellRefinement? outcome)) refinement = outcome;
                else if (prepared.Context is null)
                {
                    if (!contextFinding)
                    {
                        Add(footprint.Id, -1, $"refinement capacity: footprint '{footprint.Id}' ({prepared.Refusal})");
                        contextFinding = true;
                    }
                    continue;
                }
                else refinement = MapCommonRefinement.RefineFootprintCell(prepared.Context, footprint, cell, limits);
                string label = $"footprint '{footprint.Id}' cell {cell}";
                if (refinement.Status != MapRefinementStatus.Complete)
                {
                    string kind = refinement.Status switch
                    {
                        MapRefinementStatus.CapacityExceeded => "refinement capacity",
                        MapRefinementStatus.NotRepresentable => "not representable",
                        MapRefinementStatus.Invalid => "invalid",
                        _ => "missing geometry",
                    };
                    string detail = refinement.Status == MapRefinementStatus.Invalid && refinement.Detail is not null
                        ? $" ({refinement.Detail})" : "";
                    Add(footprint.Id, cell, $"{kind}: {label}{detail}");
                    continue;
                }
                if (refinement.LowerArea.Add(refinement.CompatibilityArea) != refinement.CellArea ||
                    (refinement.UpperArea is { } upperArea && upperArea != refinement.CellArea))
                {
                    Add(footprint.Id, cell, $"missing bound: {label}");
                    continue;
                }
                try
                {
                    MapBoundFaceContext context = prepared.Context!;
                    MapExactXz[] rect = new MapBoundPreparation(context, limits).Rectangle(footprint, cell);
                    MapBoundFaceSet lower = MapCommonRefinement.BoundFaces(context, footprint, footprint.Lower, rect[0], rect[1], limits);
                    MapBoundFaceSet? upper = footprint.Upper.Kind == MapBoundKind.OpenTop ? null :
                        MapCommonRefinement.BoundFaces(context, footprint, footprint.Upper, rect[0], rect[1], limits);
                    if (refinement.MinSeparation is { Sign: <= 0 })
                    {
                        MapExactXz vertex = FirstSeparation(refinement, lower.Faces, upper!.Faces);
                        Add(footprint.Id, cell, $"separation: {label} at ({vertex.X}, {vertex.Z})");
                    }
                    MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(view, footprint.Space);
                    columns.Add(new(footprint, cell, space, rect[0], rect[1], lower.Faces, upper?.Faces, refinement));
                }
                catch (MapExactOverflowException) { Add(footprint.Id, cell, $"not representable: {label}"); }
                catch (MapDocumentException error) { Add(footprint.Id, cell, $"missing geometry: {label} ({error.Message})"); }
            }
        }
        MapSpaceBoundaryCoverage.Validate(view, records, columns, Add);
        MapSpaceOverlapValidator.Validate(view, columns, Add);
        return Array.AsReadOnly(findings.OrderBy(f => f.Id, StringComparer.Ordinal).ThenBy(f => f.Cell)
            .ThenBy(f => f.Text, StringComparer.Ordinal).Select(f => f.Text).Distinct(StringComparer.Ordinal).ToArray());

        void Add(string id, int cell, string finding) => findings.Add((id, cell, finding));
        string OwnerOf(string finding) => records.FirstOrDefault(r => finding.Contains($"'{r.Id}'", StringComparison.Ordinal))?.Id ?? "";
    }

    static MapExactXz FirstSeparation(MapCellRefinement refinement, IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace> upper)
    {
        var lo = lower.ToDictionary(f => f.Key, f => f.Triangle);
        var hi = upper.ToDictionary(f => f.Key, f => f.Triangle);
        foreach (MapExactXz vertex in refinement.Vertices)
            foreach (MapRefinementFace face in refinement.Faces)
            {
                if (!face.Polygon.Contains(vertex) || face.Upper is not { } top) continue;
                MapExactValue bottomHeight = MapSubdividedTriangle.Height(lo[face.Lower], vertex.X, vertex.Z)!.Value;
                MapExactValue topHeight = MapSubdividedTriangle.Height(hi[top], vertex.X, vertex.Z)!.Value;
                if (topHeight.Subtract(bottomHeight).Sign <= 0) return vertex;
            }
        throw new InvalidOperationException("separation witness was not found");
    }
}

internal sealed record MapSpaceColumn(MapSpaceFootprint Footprint, int Cell, MapSpaceDoc Space,
    MapExactXz Min, MapExactXz Max, IReadOnlyList<MapBoundFace> Lower, IReadOnlyList<MapBoundFace>? Upper,
    MapCellRefinement Refinement)
{
    internal MapExactValue? Bottom(MapExactXz point) => MapSpaceGeometry.Height(Lower, point);
    internal MapExactValue? Top(MapExactXz point) => Upper is null ? null : MapSpaceGeometry.Height(Upper, point);
}
