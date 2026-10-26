using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Interiors;

/// <summary>Independent clipping and simultaneous inventory for flat, untransformed rectangular rooms.</summary>
internal static class MapMembershipValidationInventory
{
    internal static long Additional(MapScopedSurfaces view, MapPrismDomain[] prisms)
    {
        long raw = 0, largest = 0;
        foreach (var prism in prisms)
        {
            if (prism.Footprint.Rings.Count != 0 || view.EffectiveContext?.Transform(
                KhaozEngine.MapDoc.Prefabs.MapPrefabChildKind.Record, prism.Id) is not null) continue;
            var parents = new List<MapExactTriangle>();
            foreach (var footprint in MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>().Where(f => f.Space == prism.Parent))
            {
                var surface = view.Surfaces.Single(s => s.Id == footprint.Lower.SurfaceId);
                foreach (int cell in footprint.SlotCells)
                {
                    var patch = view.Patch(footprint.Lattice).Patch!;
                    var compiled = MapEffectiveGeometry.TransformPatch(view, MapSurfaceCompiler.Compile(surface, patch));
                    parents.AddRange(compiled.Faces.Where(f => f.Key.Primitive == cell).Select(compiled.ExactTriangle));
                }
            }
            // Sloped height clipping has another independent sensitivity test. This subtotal counts only
            // flat parents wholly below the room and keeps the unchanged two root triangles in its caller.
            if (parents.Any(t => t.A.Y != t.B.Y || t.A.Y != t.C.Y || t.A.Y.ToDouble() >= prism.LowerY)) continue;
            foreach (var rect in prism.Footprint.Rects)
            {
                var a = prism.Footprint.Frame.WorldXz(MapLatticeAddress.Corner(rect.MinX, rect.MinZ));
                var b = prism.Footprint.Frame.WorldXz(MapLatticeAddress.Corner(rect.MaxX, rect.MaxZ));
                MapWideXz[] ring = [MapWideXz.From(a), MapWideXz.From(new(b.X, a.Z)),
                    MapWideXz.From(b), MapWideXz.From(new(a.X, b.Z))];
                foreach (var parent in parents)
                {
                    var clipped = Clip(ring, [new(parent.A.X, parent.A.Z), new(parent.B.X, parent.B.Z), new(parent.C.X, parent.C.Z)]);
                    if (clipped.Count >= 3) raw += 1024 + 256L * clipped.Count;
                }
            }
            // The validator's domain, the room builder's second domain, all copied raw pieces and the
            // room's convex-part descriptors coexist at the final raw publication. No kernel is counted here.
            long domain = 4096 + 256L * 4 * prism.Footprint.Rects.Count;
            long descriptors = 128L * prism.Footprint.Rects.Count;
            largest = Math.Max(largest, raw + domain + descriptors);
        }
        return largest;
    }

    static List<MapWideXz> Clip(IReadOnlyList<MapWideXz> ring, MapExactXz[] triangle)
    {
        var result = ring.ToList();
        MapWideXz[] bounds = triangle.Select(MapWideXz.From).ToArray();
        if (Cross(bounds[0], bounds[1], bounds[2]).Sign < 0) Array.Reverse(bounds);
        for (int i = 0; i < bounds.Length && result.Count != 0; i++)
        {
            var input = result;
            result = new();
            var a = bounds[i]; var b = bounds[(i + 1) % bounds.Length];
            var previous = input[^1]; var before = Cross(a, b, previous);
            foreach (var point in input)
            {
                var after = Cross(a, b, point);
                if ((before.Sign < 0) != (after.Sign < 0))
                {
                    var t = before.Divide(before.Subtract(after));
                    result.Add(new(previous.X.Add(point.X.Subtract(previous.X).Multiply(t)),
                        previous.Z.Add(point.Z.Subtract(previous.Z).Multiply(t))));
                }
                if (after.Sign >= 0) result.Add(point);
                previous = point; before = after;
            }
            result = result.Distinct().ToList();
        }
        return result;
    }
    static MapWideRational Cross(MapWideXz a, MapWideXz b, MapWideXz p) =>
        b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z)).Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X)));
}
