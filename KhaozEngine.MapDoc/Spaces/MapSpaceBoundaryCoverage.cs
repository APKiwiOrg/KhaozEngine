using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Per-side exact interval coverage for walls, portals and changes between occupied columns.</summary>
internal static class MapSpaceBoundaryCoverage
{
    sealed record Band(string Id, IReadOnlyList<MapExactPoint> Lower, IReadOnlyList<MapExactPoint>? Upper,
        IReadOnlyList<MapSide>? Sides = null);
    readonly record struct Interval(MapExactValue Lower, MapExactValue? Upper, bool Authored);

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
                strips.Add(strip.Id, new(strip.Id, lower.Points, upper.Points, compiled.Faces.Select(f => f.Key.Side).Distinct().ToArray()));
            }
            catch (MapDocumentException error) { add(strip.Id, -1, $"{error.Message}: strip '{strip.Id}'"); }
        }
        foreach (MapCavePortal portal in records.OfType<MapCavePortal>())
        {
            try
            {
                MapChainResolution bottom = Chain(portal.BandBottom);
                MapChainResolution? top = portal.BandTop is { } reference ? Chain(reference) : null;
                MapExactXz[] interval = portal.Interval.Select(v => MapSpaceGeometry.Surface(view, v.SurfaceId).Frame.WorldXz(v.Address)).ToArray();
                if (!interval.SequenceEqual(bottom.Points.Select(MapSpaceGeometry.Xz)) ||
                    (top is not null && !interval.SequenceEqual(top.Points.Select(MapSpaceGeometry.Xz))))
                { add(portal.Id, -1, $"vertex sequence: portal '{portal.Id}'"); continue; }
                var band = new Band(portal.Id, bottom.Points, top?.Points);
                portals.Add(portal.Id, band);
                foreach (MapRecordRef endpoint in new[] { portal.FromSpace, portal.ToSpace })
                {
                    MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(view, endpoint);
                    MapSpaceColumn[] owned = columns.Where(c => c.Space.Id == space.Id).ToArray();
                    if (owned.Length == 0) continue;
                    for (int segment = 0; segment + 1 < interval.Length; segment++)
                    {
                        MapExactXz a = interval[segment], b = interval[segment + 1];
                        var cuts = Crossings(a, b, Cuts(a, b, owned, new[] { band }), owned, new[] { band });
                        for (int i = 0; i + 1 < cuts.Count; i++)
                        {
                            MapExactXz first = Along(a, b, cuts[i]), last = Along(a, b, cuts[i + 1]);
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
        foreach (MapSpaceColumn column in columns)
        {
            if (column.Lower.Count == 0) continue;
            try
            {
                Band[] bands = column.Space.Walls.Where(w => strips.TryGetValue(w.Record.Id, out Band? strip) && strip.Sides!.Contains(w.Side))
                    .Select(w => strips[w.Record.Id])
                    .Concat(column.Space.Portals.Where(p => portals.ContainsKey(p.Record.Id)).Select(p => portals[p.Record.Id])).ToArray();
                foreach (MapCellEdge edge in Enum.GetValues<MapCellEdge>())
                {
                    var (a, b) = Edge(column, edge);
                    MapSpaceColumn[] adjacent = columns.Where(c => !ReferenceEquals(c, column) && Neighbour(column, c, edge)).ToArray();
                    MapSpaceColumn[] peers = adjacent.Where(c => MapSpaceGeometry.Owner(view, c.Space) == MapSpaceGeometry.Owner(view, column.Space)).ToArray();
                    if (column.Space.Kind == MapSpaceKind.Exterior && adjacent.Length == 0) continue;
                    var cuts = Cuts(a, b, adjacent.Append(column), bands);
                    // All interval heights are linear between these vertices. Split again wherever their ordering changes.
                    cuts = Crossings(a, b, cuts, peers.Append(column), bands);
                    for (int i = 0; i + 1 < cuts.Count; i++)
                    {
                        MapExactXz point = Along(a, b, Mid(cuts[i], cuts[i + 1]));
                        // Compatibility coverage has no physical plane. Identical declared bounds still join the exterior domain.
                        if (peers.Any(p => p.Refinement.CompatibilityCells.Count != 0 && Contains(p, point) &&
                            p.Footprint.Lower == column.Footprint.Lower && p.Footprint.Upper == column.Footprint.Upper)) continue;
                        if (column.Bottom(point) is not { } lo) continue;
                        MapExactValue? hi = column.Top(point);
                        var coverage = new List<Interval>();
                        foreach (MapSpaceColumn peer in peers)
                            if (Contains(peer, point) && peer.Bottom(point) is { } bottom)
                                coverage.Add(new(bottom, peer.Top(point), false));
                        foreach (Band band in bands)
                            if (MapSpaceGeometry.ChainHeight(band.Lower, point) is { } bottom)
                            {
                                MapExactValue? top = band.Upper is null ? null : MapSpaceGeometry.ChainHeight(band.Upper, point);
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

    static bool AirContains(MapSpaceColumn column, Band band, MapExactXz point)
    {
        MapExactValue lo = MapSpaceGeometry.ChainHeight(band.Lower, point)!.Value;
        MapExactValue? hi = band.Upper is null ? null : MapSpaceGeometry.ChainHeight(band.Upper, point);
        return (hi is null || hi.Value.CompareTo(lo) > 0) && column.Bottom(point) is { } bottom && lo.CompareTo(bottom) >= 0 &&
            (column.Upper is null || (hi is { } top && column.Top(point) is { } ceiling && top.CompareTo(ceiling) <= 0));
    }

    static bool Gap(MapExactValue lower, MapExactValue? upper, IEnumerable<Interval> source)
    {
        MapExactValue cursor = lower;
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

    static bool Overlap(MapExactValue lower, MapExactValue? upper, IReadOnlyList<Interval> source)
    {
        for (int i = 0; i < source.Count; i++)
            for (int j = i + 1; j < source.Count; j++)
            {
                MapExactValue lo = MapSpaceGeometry.Max(lower, MapSpaceGeometry.Max(source[i].Lower, source[j].Lower));
                MapExactValue? hi = Minimum(upper, Minimum(source[i].Upper, source[j].Upper));
                if (hi is null || lo.CompareTo(hi.Value) < 0) return true;
            }
        return false;
    }

    static MapExactValue? Minimum(MapExactValue? a, MapExactValue? b) => a is null ? b : b is null ? a : MapSpaceGeometry.Min(a.Value, b.Value);
    static MapExactValue Mid(MapExactValue a, MapExactValue b) => a.Add(b.Subtract(a).Divide(new(2, 1)));
    static MapExactXz Along(MapExactXz a, MapExactXz b, MapExactValue t) =>
        new(a.X.Add(b.X.Subtract(a.X).Multiply(t)), a.Z.Add(b.Z.Subtract(a.Z).Multiply(t)));
    static MapExactValue Parameter(MapExactXz point, MapExactXz a, MapExactXz b) => a.X != b.X
        ? point.X.Subtract(a.X).Divide(b.X.Subtract(a.X)) : point.Z.Subtract(a.Z).Divide(b.Z.Subtract(a.Z));
    static bool Contains(MapSpaceColumn c, MapExactXz p) => MapSpaceGeometry.Between(p.X, c.Min.X, c.Max.X) && MapSpaceGeometry.Between(p.Z, c.Min.Z, c.Max.Z);
    static (MapExactXz, MapExactXz) Edge(MapSpaceColumn c, MapCellEdge edge) => edge switch
    {
        MapCellEdge.West => (c.Min, new(c.Min.X, c.Max.Z)),
        MapCellEdge.East => (new(c.Max.X, c.Min.Z), c.Max),
        MapCellEdge.South => (c.Min, new(c.Max.X, c.Min.Z)),
        _ => (new(c.Min.X, c.Max.Z), c.Max),
    };
    static bool Neighbour(MapSpaceColumn a, MapSpaceColumn b, MapCellEdge edge) => edge switch
    {
        MapCellEdge.West => a.Min.X == b.Max.X && Positive(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z),
        MapCellEdge.East => a.Max.X == b.Min.X && Positive(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z),
        MapCellEdge.South => a.Min.Z == b.Max.Z && Positive(a.Min.X, a.Max.X, b.Min.X, b.Max.X),
        _ => a.Max.Z == b.Min.Z && Positive(a.Min.X, a.Max.X, b.Min.X, b.Max.X),
    };
    static bool Positive(MapExactValue a, MapExactValue b, MapExactValue c, MapExactValue d) =>
        MapSpaceGeometry.Max(a, c).CompareTo(MapSpaceGeometry.Min(b, d)) < 0;

    static List<MapExactValue> Cuts(MapExactXz a, MapExactXz b, IEnumerable<MapSpaceColumn> columns, IEnumerable<Band> bands)
    {
        var cuts = new SortedSet<MapExactValue> { default, new(1, 1) };
        foreach (MapSpaceColumn column in columns)
        {
            MapExactXz northwest = new(column.Min.X, column.Max.Z), southeast = new(column.Max.X, column.Min.Z);
            AddEdge(column.Min, southeast); AddEdge(southeast, column.Max);
            AddEdge(column.Max, northwest); AddEdge(northwest, column.Min);
            foreach (MapExactXz vertex in column.Refinement.Vertices) Add(vertex);
            foreach (MapBoundFace face in column.Lower.Concat(column.Upper ?? Array.Empty<MapBoundFace>()))
            {
                MapExactXz first = MapSpaceGeometry.Xz(face.Triangle.A), second = MapSpaceGeometry.Xz(face.Triangle.B), third = MapSpaceGeometry.Xz(face.Triangle.C);
                AddEdge(first, second); AddEdge(second, third); AddEdge(third, first);
            }
        }
        foreach (Band band in bands)
            foreach (MapExactPoint point in band.Lower.Concat(band.Upper ?? Array.Empty<MapExactPoint>())) Add(MapSpaceGeometry.Xz(point));
        return cuts.ToList();
        void Add(MapExactXz point) { if (MapSpaceGeometry.OnSegment(point, a, b)) cuts.Add(Parameter(point, a, b)); }
        void AddEdge(MapExactXz first, MapExactXz last)
        {
            Add(first); Add(last);
            MapExactValue dx = b.X.Subtract(a.X), dz = b.Z.Subtract(a.Z);
            MapExactValue ex = last.X.Subtract(first.X), ez = last.Z.Subtract(first.Z);
            MapExactValue determinant = dx.Multiply(ez).Subtract(dz.Multiply(ex));
            if (determinant.Sign == 0) return;
            MapExactValue px = first.X.Subtract(a.X), pz = first.Z.Subtract(a.Z);
            MapExactValue t = px.Multiply(ez).Subtract(pz.Multiply(ex)).Divide(determinant);
            MapExactValue u = px.Multiply(dz).Subtract(pz.Multiply(dx)).Divide(determinant);
            if (MapSpaceGeometry.Between(t, default, new(1, 1)) && MapSpaceGeometry.Between(u, default, new(1, 1))) cuts.Add(t);
        }
    }

    static List<MapExactValue> Crossings(MapExactXz a, MapExactXz b, List<MapExactValue> cuts,
        IEnumerable<MapSpaceColumn> columns, IReadOnlyList<Band> bands)
    {
        var result = new SortedSet<MapExactValue>(cuts);
        for (int step = 0; step + 1 < cuts.Count; step++)
        {
            MapExactXz p = Along(a, b, cuts[step]), q = Along(a, b, cuts[step + 1]);
            var heights = new List<(MapExactValue P, MapExactValue Q)>();
            foreach (MapSpaceColumn column in columns)
            {
                if (!Contains(column, Along(a, b, Mid(cuts[step], cuts[step + 1])))) continue;
                Add(column.Bottom(p), column.Bottom(q)); Add(column.Top(p), column.Top(q));
            }
            foreach (Band band in bands)
            {
                if (MapSpaceGeometry.ChainHeight(band.Lower, Along(a, b, Mid(cuts[step], cuts[step + 1]))) is null) continue;
                Add(MapSpaceGeometry.ChainHeight(band.Lower, p), MapSpaceGeometry.ChainHeight(band.Lower, q));
                if (band.Upper is not null) Add(MapSpaceGeometry.ChainHeight(band.Upper, p), MapSpaceGeometry.ChainHeight(band.Upper, q));
            }
            for (int i = 0; i < heights.Count; i++)
                for (int j = i + 1; j < heights.Count; j++)
                {
                    MapExactValue dp = heights[i].P.Subtract(heights[j].P), dq = heights[i].Q.Subtract(heights[j].Q);
                    if (dp.Sign * dq.Sign < 0)
                    {
                        MapExactValue t = dp.Divide(dp.Subtract(dq));
                        result.Add(cuts[step].Add(cuts[step + 1].Subtract(cuts[step]).Multiply(t)));
                    }
                }
            void Add(MapExactValue? first, MapExactValue? last) { if (first is { } f && last is { } l) heights.Add((f, l)); }
        }
        return result.ToList();
    }
}
