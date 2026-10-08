using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Exact aperture geometry and anchored provenance from acquired records only.</summary>
internal static class MapRelationApertures
{
    internal static MapPortalFact Compile(MapScopedSurfaces view, MapRecordRef reference,
        MapRecordRef from, MapRecordRef to, MapRecordRef? link)
    {
        MapTopologyRecord record = MapSpaceGeometry.Record<MapTopologyRecord>(view, reference);
        var provenance = new List<MapRecordRef> { reference };
        if (link is not null)
        {
            _ = MapSpaceGeometry.Record<MapVerticalLink>(view, link);
            provenance.Add(link);
        }
        MapApertureGeometry geometry;
        MapApertureSummary summary;
        if (record is MapCavePortal portal)
        {
            IReadOnlyList<MapExactPoint> bottom = Chain(portal.BandBottom);
            IReadOnlyList<MapExactPoint>? top = portal.BandTop is { } upper ? Chain(upper) : null;
            MapExactXz[] interval = portal.Interval.Select(v => MapSpaceGeometry.Surface(view, v.SurfaceId).Frame.WorldXz(v.Address)).ToArray();
            if (interval.Length < 2 || !interval.SequenceEqual(bottom.Select(MapSpaceGeometry.Xz)) ||
                (top is not null && !interval.SequenceEqual(top.Select(MapSpaceGeometry.Xz))))
                throw new MapDocumentException($"invalid vertex sequence: portal '{portal.Id}'");
            if (top is null && (MapSpaceGeometry.Record<MapSpaceDoc>(view, from).Kind != MapSpaceKind.Exterior ||
                MapSpaceGeometry.Record<MapSpaceDoc>(view, to).Kind != MapSpaceKind.Exterior))
                throw new MapDocumentException($"invalid open top: portal '{portal.Id}'");
            var columns = new MapApertureColumn[interval.Length];
            double width = 0;
            MapExactValue bottomY = bottom[0].Y;
            MapExactValue? topY = top?[0].Y, clear = top?[0].Y.Subtract(bottomY);
            for (int i = 0; i < columns.Length; i++)
            {
                columns[i] = new(portal.Interval[i], bottom[i], top?[i]);
                bottomY = MapSpaceGeometry.Min(bottomY, bottom[i].Y);
                if (top is not null)
                {
                    MapExactValue height = top[i].Y.Subtract(bottom[i].Y);
                    if (height.Sign <= 0) throw new MapDocumentException($"invalid aperture height: portal '{portal.Id}'");
                    topY = MapSpaceGeometry.Max(topY!.Value, top[i].Y);
                    clear = MapSpaceGeometry.Min(clear!.Value, height);
                }
                if (i > 0)
                {
                    double dx = bottom[i].X.Subtract(bottom[i - 1].X).ToDouble();
                    double dz = bottom[i].Z.Subtract(bottom[i - 1].Z).ToDouble();
                    if (dx == 0 && dz == 0) throw new MapDocumentException($"invalid repeated aperture vertex: portal '{portal.Id}'");
                    width += Math.Sqrt(dx * dx + dz * dz);
                }
            }
            geometry = new(MapPortalKind.WallPortal, Array.AsReadOnly(columns),
                Array.AsReadOnly(Array.Empty<MapExactTriangle>()), provenance.AsReadOnly());
            summary = new(bottomY.ToSingle(), topY?.ToSingle(), clear?.ToSingle(), (float)width);
        }
        else if (record is MapHorizontalOpening opening)
        {
            if (!view.TryAcquiredPatch(opening.Patch, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
                throw new MapDocumentException($"missing geometry: opening '{opening.Id}' patch {opening.Patch} ({status})");
            MapOpeningPlane plane;
            try { plane = MapOpeningBoundary.Compile(MapSpaceGeometry.Surface(view, opening.Patch.SurfaceId), patch, opening); }
            catch (MapDocumentException error) { throw new MapDocumentException($"invalid aperture: opening '{opening.Id}' ({error.Message})", error); }
            MapExactPoint[] points = plane.ExactTriangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
            MapExactValue minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
            MapExactValue minZ = points.Min(p => p.Z), maxZ = points.Max(p => p.Z);
            geometry = new(MapPortalKind.HorizontalOpening, Array.AsReadOnly(Array.Empty<MapApertureColumn>()),
                Array.AsReadOnly(plane.ExactTriangles.ToArray()), provenance.AsReadOnly());
            summary = new(points.Min(p => p.Y).ToSingle(), points.Max(p => p.Y).ToSingle(), null,
                MapSpaceGeometry.Max(maxX.Subtract(minX), maxZ.Subtract(minZ)).ToSingle());
        }
        else throw new MapDocumentException($"invalid aperture record '{reference.Id}'");
        foreach (MapRecordRef source in provenance)
            if (!view.Witness.Records.Contains(source)) throw new MapDocumentException($"missing geometry: provenance record '{source.Id}' {source.Anchor}");
        return new(reference, geometry.Kind, from, to, link, geometry, summary, MapPortalStateSource.AuthoredOpen);

        IReadOnlyList<MapExactPoint> Chain(MapRecordRef source)
        {
            MapBoundaryChain chain = MapSpaceGeometry.Record<MapBoundaryChain>(view, source);
            MapChainResolution resolved = MapBoundaryGeometry.ResolveChain(chain, view);
            if (resolved.Status != MapResolveStatus.Resolved)
                throw new MapDocumentException($"{(resolved.Status == MapResolveStatus.Invalid ? "invalid" : "missing geometry")}: chain '{source.Id}' {source.Anchor} ({resolved.Detail})");
            provenance.Add(source);
            return resolved.Points;
        }
    }
}
