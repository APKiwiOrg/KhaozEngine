using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Per-side exact interval coverage for walls, portals and changes between occupied columns.</summary>
internal static class MapSpaceBoundaryCoverage
{
    sealed record Band(string Id, IReadOnlyList<MapWidePoint> Lower, IReadOnlyList<MapWidePoint>? Upper,
        IReadOnlyList<MapSide>? Sides = null);
    readonly record struct Interval(MapWideRational Lower, MapWideRational? Upper, bool Authored);
    [ThreadStatic] static long t_neighbourTests;

    /// <summary>Column neighbour tests run on the calling thread. Bounds the side work of one validation.</summary>
    internal static long ThreadNeighbourTests => t_neighbourTests;

    internal static void Validate(MapScopedSurfaces view, IReadOnlyList<MapTopologyRecord> records,
        IReadOnlyList<MapSpaceColumn> columns, Action<string, int, string> add)
    {
        var strips = new Dictionary<string, Band>(StringComparer.Ordinal);
        var portals = new Dictionary<string, Band>(StringComparer.Ordinal);
        foreach (MapWallStrip strip in records.OfType<MapWallStrip>())
        {
            try
            {
                MapChainResolution lower = Chain(strip.LowerChain), upper = Chain(strip.UpperChain);
                MapCompiledStrip compiled = MapWallStripCompiler.Compile(strip, lower, upper);
                strips.Add(strip.Id, new(strip.Id, lower.Points.Select(MapWidePoint.From).ToArray(), upper.Points.Select(MapWidePoint.From).ToArray(), compiled.Faces.Select(f => f.Key.Side).Distinct().ToArray()));
            }
            catch (MapDocumentException error) { add(strip.Id, -1, $"{error.Message}: strip '{strip.Id}'"); }
        }
        foreach (MapCavePortal portal in records.OfType<MapCavePortal>())
        {
            try
            {
                MapChainResolution bottom = Chain(portal.BandBottom);
                MapChainResolution? top = portal.BandTop is { } reference ? Chain(reference) : null;
                MapWideXz[] interval = portal.Interval.Select(v => MapWideXz.From(MapEffectiveGeometry.Vertex(view, v))).ToArray();
                if (!interval.SequenceEqual(bottom.Points.Select(p => MapWidePoint.From(p).Xz)) ||
                    (top is not null && !interval.SequenceEqual(top.Points.Select(p => MapWidePoint.From(p).Xz))))
                { add(portal.Id, -1, $"vertex sequence: portal '{portal.Id}'"); continue; }
                var band = new Band(portal.Id, bottom.Points.Select(MapWidePoint.From).ToArray(), top?.Points.Select(MapWidePoint.From).ToArray());
                portals.Add(portal.Id, band);
                foreach (MapRecordRef endpoint in new[] { portal.FromSpace, portal.ToSpace })
                {
                    MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(view, endpoint);
                    MapSpaceColumn[] owned = columns.Where(c => c.Space.Id == space.Id).ToArray();
                    if (owned.Length == 0) continue;
                    for (int segment = 0; segment + 1 < interval.Length; segment++)
                    {
                        MapWideXz a = interval[segment], b = interval[segment + 1];
                        var cuts = Crossings(a, b, Cuts(a, b, owned, new[] { band }), owned, new[] { band });
                        for (int i = 0; i + 1 < cuts.Count; i++)
                        {
                            MapWideXz first = Along(a, b, cuts[i]), last = Along(a, b, cuts[i + 1]);
                            // Every bound and band is linear on this piece. Endpoints prove the whole interval.
                            if (!owned.Any(c => Contains(c, first) && Contains(c, last) &&
                                AirContains(c, band, first) && AirContains(c, band, last)))
                                add(portal.Id, -1, $"air interval: portal '{portal.Id}' side '{space.Id}'");
                        }
                    }
                }
            }
            catch (MapDocumentException error) { add(portal.Id, -1, $"missing geometry: portal '{portal.Id}' ({error.Message})"); }
            catch (MapExactOverflowException) { add(portal.Id, -1, $"not representable: portal '{portal.Id}'"); }
        }
        IReadOnlyList<MapSpaceColumn>[] near = Near(columns);
        for (int index = 0; index < columns.Count; index++)
        {
            MapSpaceColumn column = columns[index];
            if (column.Lower.Count == 0) continue;
            try
            {
                Band[] bands = column.Space.Walls.Where(w => strips.TryGetValue(w.Record.Id, out Band? strip) && strip.Sides!.Contains(w.Side))
                    .Select(w => strips[w.Record.Id])
                    .Concat(column.Space.Portals.Where(p => portals.ContainsKey(p.Record.Id)).Select(p => portals[p.Record.Id])).ToArray();
                foreach (MapCellEdge edge in Enum.GetValues<MapCellEdge>())
                {
                    var (a, b) = Edge(column, edge);
                    MapSpaceColumn[] adjacent = near[index].Where(c => Neighbour(column, c, edge)).ToArray();
                    MapSpaceColumn[] peers = adjacent.Where(c => MapSpaceGeometry.Owner(view, c.Space) == MapSpaceGeometry.Owner(view, column.Space)).ToArray();
                    if (column.Space.Kind == MapSpaceKind.Exterior && adjacent.Length == 0) continue;
                    var cuts = Cuts(a, b, adjacent.Append(column), bands);
                    // All interval heights are linear between these vertices. Split again wherever their ordering changes.
                    cuts = Crossings(a, b, cuts, peers.Append(column), bands);
                    for (int i = 0; i + 1 < cuts.Count; i++)
                    {
                        MapWideXz point = Along(a, b, Mid(cuts[i], cuts[i + 1]));
                        // Compatibility coverage has no physical plane. Identical declared bounds still join the exterior domain.
                        if (peers.Any(p => p.Refinement.CompatibilityCells.Count != 0 && Contains(p, point) &&
                            p.Footprint.Lower == column.Footprint.Lower && p.Footprint.Upper == column.Footprint.Upper)) continue;
                        if (column.Bottom(point) is not { } lo) continue;
                        MapWideRational? hi = column.Top(point);
                        var coverage = new List<Interval>();
                        foreach (MapSpaceColumn peer in peers)
                            if (Contains(peer, point) && peer.Bottom(point) is { } bottom)
                                coverage.Add(new(bottom, peer.Top(point), false));
                        foreach (Band band in bands)
                            if (MapSpaceGeometry.ChainHeightWide(band.Lower, point) is { } bottom)
                            {
                                MapWideRational? top = band.Upper is null ? null : MapSpaceGeometry.ChainHeightWide(band.Upper, point);
                                if (band.Upper is not null && top is null) continue;
                                coverage.Add(new(bottom, top, true));
                            }
                        string label = $"footprint '{column.Footprint.Id}' cell {column.Cell}";
                        if (Gap(lo, hi, coverage)) add(column.Footprint.Id, column.Cell, $"gap: {label}");
                        if (Overlap(lo, hi, coverage.Where(c => c.Authored).ToArray()))
                            add(column.Footprint.Id, column.Cell, $"overlap: {label}");
                    }
                }
            }
            catch (MapExactOverflowException) { add(column.Footprint.Id, column.Cell, $"not representable: footprint '{column.Footprint.Id}' cell {column.Cell}"); }
            catch (MapDocumentException error) { add(column.Footprint.Id, column.Cell, $"missing geometry: footprint '{column.Footprint.Id}' cell {column.Cell} ({error.Message})"); }
        }

        MapChainResolution Chain(MapRecordRef reference)
        {
            MapBoundaryChain chain = MapSpaceGeometry.Record<MapBoundaryChain>(view, reference);
            MapChainResolution resolved = MapBoundaryGeometry.ResolveChain(chain, view);
            if (resolved.Status != MapResolveStatus.Resolved) throw new MapDocumentException(resolved.Detail ?? "missing geometry: chain");
            if (chain.SourcePatch is { } key)
            {
                MapBoundarySurface source = MapBoundaryGeometry.Read(key, view, out string? detail)
                    ?? throw new MapDocumentException(detail ?? "missing geometry: chain source");
                for (int i = 0; i + 1 < resolved.Points.Count; i++)
                {
                    MapExactXz a = MapSpaceGeometry.Xz(resolved.Points[i]), b = MapSpaceGeometry.Xz(resolved.Points[i + 1]);
                    if (source.Vertices.Values.Select(MapSpaceGeometry.Xz).Any(p => p != a && p != b && MapSpaceGeometry.OnSegment(p, a, b)))
                        throw new MapDocumentException("vertex sequence: chain omits a source edge vertex");
                }
            }
            return resolved;
        }
    }

    // A neighbour shares a closed rectangle overlap, so each column tests only the columns an exact bounds sweep pairs it
    // with, in column order, rather than every column.
    static IReadOnlyList<MapSpaceColumn>[] Near(IReadOnlyList<MapSpaceColumn> columns)
    {
        var bounds = columns.Select(c => MapWaterPolygons.Bounds.Of(MapWideXz.From(c.Min), MapWideXz.From(c.Max))).ToArray();
        var near = new List<int>[columns.Count];
        for (int i = 0; i < near.Length; i++) near[i] = [];
        foreach (var (a, b) in MapWaterBoundsSweep.Pairs(bounds, touching: true, new MapWaterWorkBudget(long.MaxValue)))
        {
            near[a].Add(b);
            near[b].Add(a);
        }
        return near.Select(list => (IReadOnlyList<MapSpaceColumn>)list.Order().Select(i => columns[i]).ToArray()).ToArray();
    }

    static bool AirContains(MapSpaceColumn column, Band band, MapWideXz point)
    {
        MapWideRational lo = MapSpaceGeometry.ChainHeightWide(band.Lower, point)!.Value;
        MapWideRational? hi = band.Upper is null ? null : MapSpaceGeometry.ChainHeightWide(band.Upper, point);
        return (hi is null || hi.Value.CompareTo(lo) > 0) && column.Bottom(point) is { } bottom && lo.CompareTo(bottom) >= 0 &&
            (column.Upper is null || (hi is { } top && column.Top(point) is { } ceiling && top.CompareTo(ceiling) <= 0));
    }

    static bool Gap(MapWideRational lower, MapWideRational? upper, IEnumerable<Interval> source)
    {
        MapWideRational cursor = lower;
        foreach (Interval interval in source.OrderBy(i => i.Lower))
        {
            if (interval.Upper is { } end && end.CompareTo(cursor) <= 0) continue;
            if (interval.Lower.CompareTo(cursor) > 0) return upper is null || cursor.CompareTo(upper.Value) < 0;
            if (interval.Upper is null) return false;
            cursor = interval.Upper.Value;
            if (upper is { } high && cursor.CompareTo(high) >= 0) return false;
        }
        return upper is null || cursor.CompareTo(upper.Value) < 0;
    }

    static bool Overlap(MapWideRational lower, MapWideRational? upper, IReadOnlyList<Interval> source)
    {
        for (int i = 0; i < source.Count; i++)
            for (int j = i + 1; j < source.Count; j++)
            {
                MapWideRational lo = MapWideRational.Max(lower, MapWideRational.Max(source[i].Lower, source[j].Lower));
                MapWideRational? hi = Minimum(upper, Minimum(source[i].Upper, source[j].Upper));
                if (hi is null || lo.CompareTo(hi.Value) < 0) return true;
            }
        return false;
    }

    static MapWideRational? Minimum(MapWideRational? a, MapWideRational? b) => a is null ? b : b is null ? a : MapWideRational.Min(a.Value, b.Value);
    static MapWideRational Mid(MapWideRational a, MapWideRational b) => a.Add(b.Subtract(a).Divide(MapWideRational.From(2)));
    static MapWideXz Along(MapWideXz a, MapWideXz b, MapWideRational t) =>
        new(a.X.Add(b.X.Subtract(a.X).Multiply(t)), a.Z.Add(b.Z.Subtract(a.Z).Multiply(t)));
    static MapWideRational Parameter(MapWideXz point, MapWideXz a, MapWideXz b) => !a.X.Equals(b.X)
        ? point.X.Subtract(a.X).Divide(b.X.Subtract(a.X)) : point.Z.Subtract(a.Z).Divide(b.Z.Subtract(a.Z));
    static bool Contains(MapSpaceColumn c, MapWideXz p)
    {
        if (!c.WideBounds.Contains(p)) return false;
        int sign = MapEffectiveGeometry.AreaWide(c.Polygon).Sign;
        for (int i = 0; i < c.Polygon.Count; i++)
            if (MapWaterPolygons.Cross(c.Polygon[i], c.Polygon[(i + 1) % c.Polygon.Count], p).Sign * sign < 0) return false;
        return true;
    }
    static (MapWideXz, MapWideXz) Edge(MapSpaceColumn c, MapCellEdge edge) => edge switch
    {
        MapCellEdge.West => (c.Polygon[0], c.Polygon[3]),
        MapCellEdge.East => (c.Polygon[1], c.Polygon[2]),
        MapCellEdge.South => (c.Polygon[0], c.Polygon[1]),
        _ => (c.Polygon[3], c.Polygon[2]),
    };
    static bool Neighbour(MapSpaceColumn a, MapSpaceColumn b, MapCellEdge edge)
    {
        t_neighbourTests++;
        if (MapSpaceGeometry.Max(a.Min.X, b.Min.X).CompareTo(MapSpaceGeometry.Min(a.Max.X, b.Max.X)) > 0 ||
            MapSpaceGeometry.Max(a.Min.Z, b.Min.Z).CompareTo(MapSpaceGeometry.Min(a.Max.Z, b.Max.Z)) > 0) return false;
        var (first, last) = Edge(a, edge);
        int interior = a.Polygon.Select(p => MapWaterPolygons.Cross(first, last, p).Sign).First(sign => sign != 0);
        if (!b.Polygon.Any(p => MapWaterPolygons.Cross(first, last, p).Sign * interior < 0)) return false;
        for (int i = 0; i < b.Polygon.Count; i++)
        {
            var p = b.Polygon[i]; var q = b.Polygon[(i + 1) % b.Polygon.Count];
            if (MapWaterPolygons.Cross(first, last, p).Sign != 0 || MapWaterPolygons.Cross(first, last, q).Sign != 0) continue;
            var lo = MapWideRational.Min(Parameter(p, first, last), Parameter(q, first, last));
            var hi = MapWideRational.Max(Parameter(p, first, last), Parameter(q, first, last));
            if (MapWideRational.Max(lo, default).CompareTo(MapWideRational.Min(hi, MapWideRational.From(1))) < 0) return true;
        }
        return false;
    }
    static bool Between(MapWideRational value, MapWideRational a, MapWideRational b) =>
        value.CompareTo(MapWideRational.Min(a, b)) >= 0 && value.CompareTo(MapWideRational.Max(a, b)) <= 0;

    static List<MapWideRational> Cuts(MapWideXz a, MapWideXz b, IEnumerable<MapSpaceColumn> columns, IEnumerable<Band> bands)
    {
        var cuts = new SortedSet<MapWideRational> { default, MapWideRational.From(1) };
        foreach (MapSpaceColumn column in columns)
        {
            for (int i = 0; i < column.Polygon.Count; i++) AddEdge(column.Polygon[i], column.Polygon[(i + 1) % column.Polygon.Count]);
            foreach (MapWideXz vertex in column.Refinement.Vertices) Add(vertex);
            foreach (MapBoundFace face in column.Lower.Concat(column.Upper ?? Array.Empty<MapBoundFace>()))
            {
                MapWideXz first = MapWideXz.From(new(face.Triangle.A.X, face.Triangle.A.Z)), second = MapWideXz.From(new(face.Triangle.B.X, face.Triangle.B.Z)), third = MapWideXz.From(new(face.Triangle.C.X, face.Triangle.C.Z));
                AddEdge(first, second); AddEdge(second, third); AddEdge(third, first);
            }
        }
        foreach (Band band in bands)
            foreach (MapWidePoint point in band.Lower.Concat(band.Upper ?? Array.Empty<MapWidePoint>())) Add(point.Xz);
        return cuts.ToList();
        void Add(MapWideXz point) { if (MapSpaceGeometry.OnSegmentWide(point, a, b)) cuts.Add(Parameter(point, a, b)); }
        void AddEdge(MapWideXz first, MapWideXz last)
        {
            Add(first); Add(last);
            MapWideRational dx = b.X.Subtract(a.X), dz = b.Z.Subtract(a.Z);
            MapWideRational ex = last.X.Subtract(first.X), ez = last.Z.Subtract(first.Z);
            MapWideRational determinant = dx.Multiply(ez).Subtract(dz.Multiply(ex));
            if (determinant.Sign == 0) return;
            MapWideRational px = first.X.Subtract(a.X), pz = first.Z.Subtract(a.Z);
            MapWideRational t = px.Multiply(ez).Subtract(pz.Multiply(ex)).Divide(determinant);
            MapWideRational u = px.Multiply(dz).Subtract(pz.Multiply(dx)).Divide(determinant);
            if (Between(t, default, MapWideRational.From(1)) && Between(u, default, MapWideRational.From(1))) cuts.Add(t);
        }
    }

    static List<MapWideRational> Crossings(MapWideXz a, MapWideXz b, List<MapWideRational> cuts,
        IEnumerable<MapSpaceColumn> columns, IReadOnlyList<Band> bands)
    {
        var result = new SortedSet<MapWideRational>(cuts);
        for (int step = 0; step + 1 < cuts.Count; step++)
        {
            MapWideXz p = Along(a, b, cuts[step]), q = Along(a, b, cuts[step + 1]);
            var heights = new List<(MapWideRational P, MapWideRational Q)>();
            foreach (MapSpaceColumn column in columns)
            {
                if (!Contains(column, Along(a, b, Mid(cuts[step], cuts[step + 1])))) continue;
                Add(column.Bottom(p), column.Bottom(q)); Add(column.Top(p), column.Top(q));
            }
            foreach (Band band in bands)
            {
                if (MapSpaceGeometry.ChainHeightWide(band.Lower, Along(a, b, Mid(cuts[step], cuts[step + 1]))) is null) continue;
                Add(MapSpaceGeometry.ChainHeightWide(band.Lower, p), MapSpaceGeometry.ChainHeightWide(band.Lower, q));
                if (band.Upper is not null) Add(MapSpaceGeometry.ChainHeightWide(band.Upper, p), MapSpaceGeometry.ChainHeightWide(band.Upper, q));
            }
            for (int i = 0; i < heights.Count; i++)
                for (int j = i + 1; j < heights.Count; j++)
                {
                    MapWideRational dp = heights[i].P.Subtract(heights[j].P), dq = heights[i].Q.Subtract(heights[j].Q);
                    if (dp.Sign * dq.Sign < 0)
                    {
                        MapWideRational t = dp.Divide(dp.Subtract(dq));
                        result.Add(cuts[step].Add(cuts[step + 1].Subtract(cuts[step]).Multiply(t)));
                    }
                }
            void Add(MapWideRational? first, MapWideRational? last) { if (first is { } f && last is { } l) heights.Add((f, l)); }
        }
        return result.ToList();
    }
}
