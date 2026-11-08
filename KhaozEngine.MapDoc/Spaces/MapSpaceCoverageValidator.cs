using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Complete-view exact geometry validation for bounded authoring transactions.</summary>
public static class MapSpaceCoverageValidator
{
    public static IReadOnlyList<string> Validate(MapScopedSurfaces completeView) => Validate(completeView, new());
    public static IReadOnlyList<string> Validate(MapScopedSurfaces completeView, MapRefinementLimits limits) =>
        ValidateCore(completeView, limits, null);

    /// <summary>Returns the full ordered immutable snapshot behind coverage diagnostics.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateFindings(MapScopedSurfaces completeView, MapRefinementLimits? limits = null)
    {
        var findings = new List<MapDocumentFinding>();
        _ = ValidateCore(completeView, limits ?? new(), null, findings);
        return findings.AsReadOnly();
    }

    /// <summary>Refuses with the first concrete rule and the full ordered finding snapshot.</summary>
    public static void Require(MapScopedSurfaces completeView, MapRefinementLimits? limits = null)
    {
        var findings = ValidateFindings(completeView, limits);
        if (findings.Count != 0) throw MapDocumentRefusalFactory.FromFinding(findings[0], string.Join("\n", findings.Select(f => f.Message)), findings: findings);
    }
    internal static IReadOnlyList<string> Validate(MapScopedSurfaces completeView, MapRefinementLimits limits, MapBoundFaceWork work) =>
        ValidateCore(completeView, limits, work);
    internal static IReadOnlyList<string> Validate(MapScopedSurfaces completeView, MapRefinementLimits limits,
        List<MapDocumentFinding> findings) => ValidateCore(completeView, limits, null, findings);

    static IReadOnlyList<string> ValidateCore(MapScopedSurfaces view, MapRefinementLimits limits, MapBoundFaceWork? work,
        List<MapDocumentFinding>? typedFindings = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(limits);
        if (!view.Witness.Complete) throw new ArgumentException("coverage validation requires a complete view", nameof(view));
        MapTopologyRecord[] records = MapSpaceGeometry.Records(view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var findings = new List<(string Id, int Cell, string Text)>();
        var typed = new Dictionary<(string Id, int Cell, string Text), MapDocumentFinding>();
        var patches = new MapSpaceGeometry.Patches(view);
        foreach (MapSpaceDiagnostic diagnostic in MapTopologyReferenceValidator.ValidateDiagnostics(view.Surfaces, patches))
            if (diagnostic.Finding is { } finding) AddFinding(diagnostic.SortOwnerId, -1, finding);
            else Add(diagnostic.SortOwnerId, -1, diagnostic.Message);
        foreach (MapSurfacePatch patch in patches)
            foreach (MapDocumentFinding finding in MapSeamValidator.ValidateCornerDependencyFindings(patch, view))
                AddFinding(patch.Key.ToString(), -1, finding);
        foreach (MapSurfaceSeam seam in records.OfType<MapSurfaceSeam>())
            foreach (MapDocumentFinding finding in MapSeamValidator.ValidateFindings(seam, view)) AddFinding(seam.Id, -1, finding);
        MapSpaceFootprint[] footprints = records.OfType<MapSpaceFootprint>().ToArray();
        MapFootprintPreparation prepared = MapCommonRefinement.PrepareFootprints(view, footprints, limits, work);
        var columns = new List<MapSpaceColumn>();
        foreach (MapSpaceFootprint footprint in footprints)
        {
            bool contextFinding = false;
            foreach (int cell in footprint.SlotCells.Order())
            {
                MapWideCellRefinement refinement;
                if (prepared.CellOutcomes.TryGetValue((footprint.Id, cell), out MapCellRefinement? outcome)) refinement = MapWideCellRefinement.FromBounded(outcome);
                else if (prepared.Context is null)
                {
                    if (!contextFinding)
                    {
                        // The shared context refusal names no record. This copy names the footprint its message names.
                        AddFinding(footprint.Id, -1, prepared.Finding! with
                        { Message = $"refinement capacity: footprint '{footprint.Id}' ({prepared.Refusal})", OwnerId = footprint.Id, RecordId = footprint.Id, SpaceId = footprint.Space.Id });
                        contextFinding = true;
                    }
                    continue;
                }
                else refinement = MapCommonRefinement.RefineFootprintCellWide(prepared.Context, footprint, cell, limits);
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
                    // The footprint owns this check. RecordId may still identify a refused bound or opening.
                    var finding = refinement.Finding!;
                    AddFinding(footprint.Id, cell, finding with
                    {
                        Message = $"{kind}: {label}{detail}",
                        OwnerId = footprint.Id,
                        RecordId = finding.RecordId ?? footprint.Id,
                        SpaceId = finding.SpaceId ?? footprint.Space.Id,
                        SlotCell = finding.SlotCell ?? cell,
                    });
                    continue;
                }
                if (!refinement.LowerArea.Add(refinement.CompatibilityArea).Equals(refinement.CellArea) ||
                    (refinement.UpperArea is { } upperArea && !upperArea.Equals(refinement.CellArea)))
                {
                    AddFinding(footprint.Id, cell, new(MapDocumentRefusal.SpaceCoverageMissingBound, $"missing bound: {label}")
                    { RecordId = footprint.Id, SpaceId = footprint.Space.Id, SlotCell = cell });
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
                        MapWideXz vertex = FirstSeparation(refinement, lower.Faces, upper!.Faces);
                        AddFinding(footprint.Id, cell, new(MapDocumentRefusal.SpaceCoverageSeparationInvalid,
                            $"separation: {label} at ({vertex.X}, {vertex.Z})")
                        { RecordId = footprint.Id, SpaceId = footprint.Space.Id, SlotCell = cell });
                    }
                    MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(view, footprint.Space);
                    var lowerKeys = refinement.Faces.Select(f => f.Lower).ToHashSet();
                    var upperKeys = refinement.Faces.Where(f => f.Upper is not null).Select(f => f.Upper!.Value).ToHashSet();
                    columns.Add(new(footprint, cell, space, rect[0], rect[1], lower.Faces.Where(f => lowerKeys.Contains(f.Key)).ToArray(),
                        upper?.Faces.Where(f => upperKeys.Contains(f.Key)).ToArray(), refinement, MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, cell).Select(MapWideXz.From).ToArray()));
                }
                catch (MapExactOverflowException)
                {
                    AddFinding(footprint.Id, cell, new(MapDocumentRefusal.GeometryNotRepresentable,
                    $"not representable: {label}")
                    { RecordId = footprint.Id, SpaceId = footprint.Space.Id, SlotCell = cell });
                }
                catch (MapDocumentException error)
                {
                    string message = $"missing geometry: {label} ({error.Message})";
                    if (MapDocumentFinding.FromException(error) is { } finding)
                        AddFinding(footprint.Id, cell, finding with { Message = message, RecordId = finding.RecordId ?? footprint.Id });
                    else Add(footprint.Id, cell, message);
                }
            }
        }
        MapSpaceBoundaryCoverage.Validate(view, records, columns, Add, RecordFinding);
        MapSpaceOverlapValidator.Validate(view, columns, Add, RecordFinding);
        var prismDiagnostics = new List<MapSpaceDiagnostic>();
        MapPrismCoverage.Validate(view, limits, null, prismDiagnostics);
        foreach (MapSpaceDiagnostic diagnostic in prismDiagnostics)
            if (diagnostic.Finding is { } finding) AddFinding(diagnostic.SortOwnerId, -1, finding);
            else Add(diagnostic.SortOwnerId, -1, diagnostic.Message);
        var ordered = findings.OrderBy(f => f.Id, StringComparer.Ordinal).ThenBy(f => f.Cell)
            .ThenBy(f => f.Text, StringComparer.Ordinal).DistinctBy(f => f.Text).ToArray();
        typedFindings?.AddRange(ordered.Where(f => typed.ContainsKey(f)).Select(f => typed[f]));
        return Array.AsReadOnly(ordered.Select(f => f.Text).ToArray());

        void Add(string id, int cell, string finding) => findings.Add((id, cell, finding));
        void AddFinding(string id, int cell, MapDocumentFinding finding) { Add(id, cell, finding.Message); RecordFinding(id, cell, finding); }
        void RecordFinding(string id, int cell, MapDocumentFinding finding) => typed.TryAdd((id, cell, finding.Message), finding);
    }

    static MapWideXz FirstSeparation(MapWideCellRefinement refinement, IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace> upper)
    {
        var lo = lower.ToDictionary(f => f.Key, f => f.Triangle);
        var hi = upper.ToDictionary(f => f.Key, f => f.Triangle);
        foreach (MapWideXz vertex in refinement.Vertices)
            foreach (MapWideRefinementFace face in refinement.Faces)
            {
                if (!face.Polygon.Contains(vertex) || face.Upper is not { } top) continue;
                MapWideRational bottomHeight = MapEffectiveGeometry.HeightWide(lo[face.Lower], vertex);
                MapWideRational topHeight = MapEffectiveGeometry.HeightWide(hi[top], vertex);
                if (topHeight.Subtract(bottomHeight).Sign <= 0) return vertex;
            }
        throw new InvalidOperationException("separation witness was not found");
    }
}

internal sealed record MapSpaceColumn(MapSpaceFootprint Footprint, int Cell, MapSpaceDoc Space,
    MapExactXz Min, MapExactXz Max, IReadOnlyList<MapBoundFace> Lower, IReadOnlyList<MapBoundFace>? Upper,
    MapWideCellRefinement Refinement, IReadOnlyList<MapWideXz> Polygon)
{
    internal MapWaterPolygons.Bounds WideBounds { get; } = MapWaterPolygons.Bounds.Of(Polygon);
    internal MapWideRational? Bottom(MapWideXz point) => MapSpaceGeometry.HeightWide(Lower, point);
    internal MapWideRational? Top(MapWideXz point) => Upper is null ? null : MapSpaceGeometry.HeightWide(Upper, point);
}
