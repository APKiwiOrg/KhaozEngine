using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Prepared incident tests and the same endpoint-owned chain segments as the cold point adapter.</summary>
internal sealed class MapMembershipPreparedPortal
{
    internal long InventoryBytes => checked(128L + _interval.Concat(_bottom).Concat(_top).Sum(s => 8L + s.InventoryBytes));
    internal sealed class Segment
    {
        internal long InventoryBytes => checked(128L + 8L * Demands.Length + _line.InventoryBytes + (_height?.InventoryBytes ?? 0));
        readonly MapExactXz _a, _b;
        readonly MapMembershipAffine _line;
        readonly MapMembershipTriangle? _height;
        internal readonly MapMembershipPreparedGeometry.Cell[] Demands;
        internal Segment(MapExactPoint a, MapExactPoint b, MapMembershipPreparedGeometry.Cell[]? demands = null, bool height = false)
        {
            MapMembershipBuild.Inventory(checked(128L + 8L * (demands?.Length ?? 0)));
            _a = new(a.X, a.Z); _b = new(b.X, b.Z); _line = MapMembershipAffine.Edge(_a, _b);
            Demands = demands ?? Array.Empty<MapMembershipPreparedGeometry.Cell>();
            if (height)
            {
                var third = a.X != b.X ? new MapExactPoint(a.X, a.Y, a.Z == default ? new(1, 1) : default)
                    : new MapExactPoint(a.X == default ? new(1, 1) : default, a.Y, a.Z);
                _height = new(new(a, b, third));
            }
        }
        internal bool Contains(MapExactXz point, MapMembershipExact exact) => _a != _b &&
            exact.Sign(_line, point) == 0 && MapMembershipPolygon.Between(point.X, _a.X, _b.X) &&
            MapMembershipPolygon.Between(point.Z, _a.Z, _b.Z);
        internal bool End(MapExactXz point) => point == _b;
        internal MapExactValue? Height(MapExactXz point, MapMembershipExact exact) => _height!.Height(point, exact);
    }
    internal readonly MapCavePortal Record;
    internal readonly bool Ignored;
    readonly Segment[] _interval;
    readonly Segment[] _bottom, _top;
    internal MapMembershipPreparedPortal(MapScopedSurfaces view, MapMembershipPreparedGeometry geometry,
        MapCavePortal portal, MapPrismDomain[] prisms)
    {
        MapMembershipBuild.Inventory(checked(128L + 8L * (portal.Interval.Count - 1)));
        Record = portal;
        Ignored = prisms.Any(p => p.Space.Id == portal.FromSpace.Id || p.Space.Id == portal.ToSpace.Id);
        _interval = new Segment[portal.Interval.Count - 1];
        for (int i = 0; i < _interval.Length; i++)
        {
            var a = MapEffectiveGeometry.Vertex(view, portal.Interval[i]); var b = MapEffectiveGeometry.Vertex(view, portal.Interval[i + 1]);
            _interval[i] = new(new(a.X, default, a.Z), new(b.X, default, b.Z));
        }
        _bottom = Ignored ? Array.Empty<Segment>() : Chain(portal.BandBottom);
        _top = Ignored || portal.BandTop is null ? Array.Empty<Segment>() : Chain(portal.BandTop);
        Segment[] Chain(MapRecordRef reference)
        {
            var chain = MapSpaceGeometry.Record<MapBoundaryChain>(view, reference);
            MapMembershipBuild.Inventory(checked(8L * (chain.Vertices.Count - 1)));
            using var resolutionWorkspace = MapMembershipBuild.Kernel(checked(32768L + 256L * chain.Vertices.Count));
            var resolution = MapBoundaryGeometry.ResolveChain(chain, view, key =>
                new MapBoundarySurface(geometry.Validated[key], MapSlotCellMask.All));
            if (resolution.Status != MapResolveStatus.Resolved) throw new MapDocumentException(resolution.Detail ?? "missing geometry: portal band");
            var result = new Segment[chain.Vertices.Count - 1];
            var bounds = new MapSpacePointBounds(view, new(MaxContextPatches: geometry.PatchCount + 1), null);
            for (int i = 0; i < result.Length; i++)
            {
                var segment = chain with { Vertices = new[] { chain.Vertices[i], chain.Vertices[i + 1] } };
                var demands = bounds.SourceCells(segment).Select(d => geometry.ByCell[d]).ToArray();
                result[i] = new(resolution.Points[i], resolution.Points[i + 1], demands, height: true);
            }
            return result;
        }
    }
    internal bool Incident(MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < _interval.Length; i++) if (_interval[i].Contains(point, exact)) return true;
        return false;
    }
    internal Segment? Bottom(MapExactXz point, MapMembershipExact exact) => Select(_bottom, point, exact);
    internal Segment? Top(MapExactXz point, MapMembershipExact exact) => Select(_top, point, exact);
    static Segment? Select(Segment[] segments, MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < segments.Length; i++)
            if (segments[i].Contains(point, exact) && !(segments[i].End(point) && i + 1 < segments.Length)) return segments[i];
        return null;
    }
}
