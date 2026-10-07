using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Writer-maintained spatial records. Non-spatial forward references never create incidence.</summary>
internal sealed class MapSurfaceIncidence
{
    readonly Dictionary<MapPatchKey, IReadOnlyList<MapRecordRef>> _records = new();
    internal MapSurfaceIncidence(MapSurfaceSet set)
    {
        var frames = set.Refs.ToDictionary(s => s.Id, s => s.Frame, StringComparer.Ordinal);
        var patches = set.Patches.Values.Select(p => new Leaf(p.Key,
            MapSurfaceRanges.World(MapSurfaceRanges.Rectangle(p), frames[p.Key.SurfaceId]))).ToArray();
        var lists = patches.ToDictionary(p => p.Key, _ => new HashSet<MapRecordRef>());
        if (patches.Length != 0)
        {
            Node tree = Build(patches, 0);
            foreach (MapSurfacePatch anchor in set.Patches.Values)
                foreach (MapTopologyRecord record in anchor.Records)
                    foreach (Shape shape in Geometry(set, frames, record, new HashSet<string>(StringComparer.Ordinal)))
                        foreach (Leaf leaf in Visit(tree, shape.Bounds))
                            if (leaf.Key != anchor.Key && shape.Touches(leaf.Bounds)) lists[leaf.Key].Add(new(record.Id, anchor.Key));
        }
        foreach (var pair in lists)
            _records.Add(pair.Key, Array.AsReadOnly(pair.Value.OrderBy(r => r.Anchor).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray()));
    }
    internal IReadOnlyList<MapRecordRef> For(MapPatchKey key) => _records[key];
    sealed record Leaf(MapPatchKey Key, MapExactRect Bounds);
    sealed record Node(MapExactRect Bounds, Leaf? Leaf, Node? Left, Node? Right);
    static Node Build(Leaf[] leaves, int depth)
    {
        if (leaves.Length == 1) return new(leaves[0].Bounds, leaves[0], null, null);
        Array.Sort(leaves, (a, b) => depth % 2 == 0 ? a.Bounds.MinX.CompareTo(b.Bounds.MinX) : a.Bounds.MinZ.CompareTo(b.Bounds.MinZ));
        int split = leaves.Length / 2;
        Node left = Build(leaves[..split], depth + 1), right = Build(leaves[split..], depth + 1);
        return new(Union(left.Bounds, right.Bounds), null, left, right);
    }
    static IEnumerable<Leaf> Visit(Node node, MapExactRect bounds)
    {
        if (!node.Bounds.Touches(bounds)) yield break;
        if (node.Leaf is { } leaf) yield return leaf;
        else
        {
            foreach (Leaf found in Visit(node.Left!, bounds)) yield return found;
            foreach (Leaf found in Visit(node.Right!, bounds)) yield return found;
        }
    }
    static MapExactValue Min(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    static MapExactValue Max(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
    static MapExactRect Union(MapExactRect a, MapExactRect b) => new(Min(a.MinX, b.MinX), Min(a.MinZ, b.MinZ), Max(a.MaxX, b.MaxX), Max(a.MaxZ, b.MaxZ));

    static IEnumerable<Shape> Geometry(MapSurfaceSet set, Dictionary<string, MapLatticeFrame> frames,
        MapTopologyRecord record, HashSet<string> visiting)
    {
        if (!visiting.Add(record.Id)) yield break;
        switch (record)
        {
            case MapBoundaryChain chain:
                foreach (Shape shape in Lines(chain.Vertices.Select(v => frames[v.Vertex.SurfaceId].WorldXz(v.Vertex.Address)).ToArray())) yield return shape;
                break;
            case MapSurfaceSeam seam:
                foreach (MapSurfaceEdgeRef edge in new[] { seam.First, seam.Second })
                    yield return Shape.Line(frames[edge.From.SurfaceId].WorldXz(edge.From.Address), frames[edge.To.SurfaceId].WorldXz(edge.To.Address));
                break;
            case MapCavePortal portal:
                foreach (Shape shape in Lines(portal.Interval.Select(v => frames[v.SurfaceId].WorldXz(v.Address)).ToArray())) yield return shape;
                break;
            case MapSpaceFootprint footprint:
                foreach (Shape shape in Cells(frames, footprint.Lattice, footprint.SlotCells)) yield return shape;
                break;
            case MapHorizontalOpening opening:
                foreach (Shape shape in Cells(frames, opening.Patch, opening.SlotCells)) yield return shape;
                break;
            case MapWallStrip strip:
                if (set.TryGetRecord(strip.LowerChain, out MapTopologyRecord? lower) && lower is MapBoundaryChain l &&
                    set.TryGetRecord(strip.UpperChain, out MapTopologyRecord? upper) && upper is MapBoundaryChain u)
                {
                    var bottom = l.Vertices.Select(v => frames[v.Vertex.SurfaceId].WorldXz(v.Vertex.Address)).ToArray();
                    var top = u.Vertices.Select(v => frames[v.Vertex.SurfaceId].WorldXz(v.Vertex.Address)).ToArray();
                    if (bottom.Length == top.Length)
                        for (int i = 1; i < bottom.Length; i++)
                        {
                            yield return Shape.Triangle(bottom[i - 1], bottom[i], top[i]);
                            yield return Shape.Triangle(bottom[i - 1], top[i], top[i - 1]);
                        }
                    else
                    {
                        // Raw saves do not certify strip geometry. Preserve conservative incidence for a malformed pairing.
                        MapExactRect bounds = Shape.Line(bottom[0], top[0]).Bounds;
                        foreach (MapExactXz p in bottom.Concat(top)) bounds = Union(bounds, Shape.Line(p, p).Bounds);
                        yield return Shape.Rectangle(bounds);
                    }
                }
                break;
            case MapVerticalLink link:
                foreach (MapRecordRef reference in link.GeometryOwners.Concat(link.Openings).Concat(link.Portals))
                    if (set.TryGetRecord(reference, out MapTopologyRecord? target))
                        foreach (Shape shape in Geometry(set, frames, target!, visiting)) yield return shape;
                break;
        }
        visiting.Remove(record.Id);
    }
    static IEnumerable<Shape> Lines(MapExactXz[] points)
    {
        for (int i = 1; i < points.Length; i++) yield return Shape.Line(points[i - 1], points[i]);
    }
    static IEnumerable<Shape> Cells(Dictionary<string, MapLatticeFrame> frames, MapPatchKey key, IReadOnlyList<int> slots)
    {
        foreach (int cell in slots)
        {
            long x = checked(key.SlotX * 64 + cell % 64), z = checked(key.SlotZ * 64 + cell / 64);
            yield return Shape.Rectangle(MapSurfaceRanges.World(new(x, z, checked(x + 1), checked(z + 1)), frames[key.SurfaceId]));
        }
    }
    sealed record Shape(MapExactRect Bounds, MapExactXz? A, MapExactXz? B, MapExactXz? C)
    {
        internal static Shape Rectangle(MapExactRect bounds) => new(bounds, null, null, null);
        internal static Shape Line(MapExactXz a, MapExactXz b) => new(new(Min(a.X, b.X), Min(a.Z, b.Z), Max(a.X, b.X), Max(a.Z, b.Z)), a, b, null);
        internal static Shape Triangle(MapExactXz a, MapExactXz b, MapExactXz c) => Line(a, b) with { Bounds = Union(Line(a, b).Bounds, Line(c, c).Bounds), C = c };
        internal bool Touches(MapExactRect r)
        {
            if (!Bounds.Touches(r)) return false;
            if (A is not { } a || B is not { } b) return true;
            if (Segment(a, b, r)) return true;
            if (C is not { } c) return false;
            if (Segment(b, c, r) || Segment(c, a, r)) return true;
            return Inside(new(r.MinX, r.MinZ), a, b, c) || Inside(new(r.MinX, r.MaxZ), a, b, c) ||
                Inside(new(r.MaxX, r.MinZ), a, b, c) || Inside(new(r.MaxX, r.MaxZ), a, b, c);
        }
        static bool Segment(MapExactXz a, MapExactXz b, MapExactRect r)
        {
            MapExactValue low = default, high = new(1, 1);
            return Slab(a.X, b.X.Subtract(a.X), r.MinX, r.MaxX, ref low, ref high) &&
                Slab(a.Z, b.Z.Subtract(a.Z), r.MinZ, r.MaxZ, ref low, ref high);
        }
        static bool Slab(MapExactValue a, MapExactValue d, MapExactValue min, MapExactValue max, ref MapExactValue low, ref MapExactValue high)
        {
            if (d.Sign == 0) return a.CompareTo(min) >= 0 && a.CompareTo(max) <= 0;
            MapExactValue first = min.Subtract(a).Divide(d), last = max.Subtract(a).Divide(d);
            if (first.CompareTo(last) > 0) (first, last) = (last, first);
            low = Max(low, first); high = Min(high, last);
            return low.CompareTo(high) <= 0;
        }
        static bool Inside(MapExactXz p, MapExactXz a, MapExactXz b, MapExactXz c)
        {
            int ab = Cross(a, b, p).Sign, bc = Cross(b, c, p).Sign, ca = Cross(c, a, p).Sign;
            return Cross(a, b, c).Sign != 0 && ((ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0));
        }
        static MapExactValue Cross(MapExactXz a, MapExactXz b, MapExactXz p) => b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z))
            .Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X)));
    }
}
