using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Effective support geometry bounds and physical floor ownership validation. No support installation
/// happens here. The complete view and its exact faces were captured by MapDoc before support sampling.</summary>
public static class MapPrefabSupportBounds
{
    /// <summary>Bounds of all effective physical surfaces and wall strips. Empty geometry has zero bounds.</summary>
    public static MapBox3 Bounds(MapEffectiveResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var view = resources.CompleteView();
        MapBox3? result = null;
        foreach (MapPatchKey key in view.Witness.Present.Select(p => p.Key))
        {
            if (view.Surfaces.Single(s => s.Id == key.SurfaceId).Role != MapSurfaceRole.PaintOverride)
            {
                var patch = MapEffectiveGeometry.Patch(view, key);
                foreach (var face in patch.Faces) Add(patch.ExactVertices[face.A], patch.ExactVertices[face.B], patch.ExactVertices[face.C]);
            }
            foreach (var strip in view.RecordsIn(key).OfType<MapWallStrip>())
            {
                var compiled = MapEffectiveGeometry.Strip(view, strip);
                foreach (var face in compiled.Faces) Add(compiled.ExactVertices[face.A], compiled.ExactVertices[face.B], compiled.ExactVertices[face.C]);
            }
        }
        return result ?? default;

        void Add(params MapExactPoint[] points)
        {
            var box = Of(points);
            result = result?.Union(box) ?? box;
        }
    }

    internal static IEnumerable<MapBox3> OtherBounds(MapEffectiveResources resources, MapAssetClosure assets)
    {
        foreach (MapResolvedPlacement placement in resources.Placements)
        {
            var asset = assets.GetAsset(placement.AssetId);
            var pose = resources.Transforms["Placement:" + placement.PlacementId];
            foreach (MapLocalBounds? local in new MapLocalBounds?[] { asset.RenderBounds, asset.LodBounds, asset.LightBounds })
                if (local is { } box)
                {
                    var kernel = new MapResourceTransform(pose with { Scale = pose.Scale * asset.SourceUnitsToMetres });
                    yield return Of(Corners(box).Select(p => kernel.Point(new(MapExactValue.FromSingle(p.X),
                        MapExactValue.FromSingle(p.Y), MapExactValue.FromSingle(p.Z)))).ToArray());
                }
        }
        foreach (var light in MapEffectiveAnnotations.Lights(resources)) yield return MapEffectiveAnnotations.Bounds(light);
        foreach (var marker in MapEffectiveAnnotations.Markers(resources)) yield return MapEffectiveAnnotations.Bounds(marker);
    }

    internal static void RefuseCoincidentFloors(MapScopedSurfaces view, IReadOnlyList<MapPlacementGeometry> placements)
    {
        var floors = view.Witness.Present.Where(p => view.Surfaces.Single(s => s.Id == p.Key.SurfaceId).Role == MapSurfaceRole.SupportFloor)
            .Select(p => MapEffectiveGeometry.Patch(view, p.Key)).ToArray();
        var triangles = floors.Select(p => (p.Key.SurfaceId, Faces: p.Faces.Select(f => new MapExactTriangle(
            p.ExactVertices[f.A], p.ExactVertices[f.B], p.ExactVertices[f.C])).ToArray(), Bounds: Of(p.ExactVertices))).ToArray();
        for (int i = 0; i < triangles.Length; i++)
            for (int j = i + 1; j < triangles.Length; j++)
            {
                var a = triangles[i]; var b = triangles[j];
                if (a.SurfaceId == b.SurfaceId || !Meets(a.Bounds, b.Bounds)) continue;
                foreach (var x in a.Faces)
                    foreach (var y in b.Faces)
                        if (Coplanar(x, y) && Overlap(x, y))
                            throw new MapDocumentException($"Independent support owners '{a.SurfaceId}' and '{b.SurfaceId}' have coincident faces.");
            }
        foreach (var placement in placements.Where(p => p.Collider is not null))
            foreach (var floor in triangles)
            {
                if (!Meets(floor.Bounds, placement.ColliderBounds!.Value)) continue;
                var transform = placement.EffectiveTransform;
                var (sin, cos) = Math.SinCos(transform.YawRadians * 0.5);
                var pose = new MapDoublePose(new(transform.X, transform.Y, transform.Z), 0, sin, 0, cos);
                double scale = transform.Scale * placement.SourceUnitsToMetres;
                foreach (var collider in ColliderFaces(placement.SourceCollider!, pose, scale))
                    foreach (var support in floor.Faces)
                        if (Coplanar(support, collider) && Overlap(support, collider))
                            throw new MapDocumentException($"Collider '{placement.PlacementId}' and support owner '{floor.SurfaceId}' have coincident floor faces.");
                foreach (var hull in Hulls(placement.SourceCollider!, pose, scale))
                    foreach (var support in floor.Faces)
                        if (HullFloorOverlap(support, hull))
                            throw new MapDocumentException($"Collider '{placement.PlacementId}' and support owner '{floor.SurfaceId}' have coincident floor faces.");
            }
    }

    static IEnumerable<MapExactPoint[]> Hulls(PhysicsShape shape, MapDoublePose pose, double scale)
    {
        if (shape is ConvexHullShape hull) yield return hull.Points.Select(p => Exact(Transform(pose, p, scale))).ToArray();
        else if (shape is CompoundShape compound)
            foreach (var child in compound.Children)
                foreach (var points in Hulls(child.Shape, Compose(pose, child.Local, scale), scale)) yield return points;
    }

    static bool HullFloorOverlap(MapExactTriangle floor, IReadOnlyList<MapExactPoint> points)
    {
        var a = MapWidePoint.From(floor.A); var b = MapWidePoint.From(floor.B); var c = MapWidePoint.From(floor.C);
        var ab = new MapWidePoint(b.X.Subtract(a.X), b.Y.Subtract(a.Y), b.Z.Subtract(a.Z));
        var ac = new MapWidePoint(c.X.Subtract(a.X), c.Y.Subtract(a.Y), c.Z.Subtract(a.Z));
        var nx = ab.Y.Multiply(ac.Z).Subtract(ab.Z.Multiply(ac.Y));
        var ny = ab.Z.Multiply(ac.X).Subtract(ab.X.Multiply(ac.Z));
        var nz = ab.X.Multiply(ac.Y).Subtract(ab.Y.Multiply(ac.X));
        if (ny.Sign == 0) return false;
        var coplanar = new List<MapWideXz>();
        foreach (var point in points)
        {
            var p = MapWidePoint.From(point);
            int side = nx.Multiply(p.X.Subtract(a.X)).Add(ny.Multiply(p.Y.Subtract(a.Y))).Add(nz.Multiply(p.Z.Subtract(a.Z))).Sign * ny.Sign;
            if (side > 0) return false;
            if (side == 0) coplanar.Add(p.Xz);
        }
        var ordered = coplanar.Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToArray();
        if (ordered.Length < 3) return false;
        var lower = Chain(ordered); var upper = Chain(ordered.Reverse());
        var ring = lower.Take(lower.Count - 1).Concat(upper.Take(upper.Count - 1)).ToArray();
        var triangle = new[] { a.Xz, b.Xz, c.Xz };
        if (MapWaterPolygons.SignedArea(triangle).Sign < 0) Array.Reverse(triangle);
        return ring.Length >= 3 && MapWaterPolygons.ClipToConvex(triangle, ring).Count != 0;

        static List<MapWideXz> Chain(IEnumerable<MapWideXz> vertices)
        {
            var chain = new List<MapWideXz>();
            foreach (var p in vertices)
            {
                while (chain.Count >= 2 && MapWaterPolygons.Cross(chain[^2], chain[^1], p).Sign <= 0) chain.RemoveAt(chain.Count - 1);
                chain.Add(p);
            }
            return chain;
        }
    }

    static bool Meets(MapBox3 a, MapBox3 b) => a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinZ < b.MaxZ && b.MinZ < a.MaxZ &&
        a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    static bool Coplanar(MapExactTriangle a, MapExactTriangle b)
    {
        var x = MapWidePoint.From(a.A); var y = MapWidePoint.From(a.B); var z = MapWidePoint.From(a.C);
        var ax = y.X.Subtract(x.X); var ay = y.Y.Subtract(x.Y); var az = y.Z.Subtract(x.Z);
        var bx = z.X.Subtract(x.X); var by = z.Y.Subtract(x.Y); var bz = z.Z.Subtract(x.Z);
        var nx = ay.Multiply(bz).Subtract(az.Multiply(by));
        var ny = az.Multiply(bx).Subtract(ax.Multiply(bz));
        var nz = ax.Multiply(by).Subtract(ay.Multiply(bx));
        if (ny.Sign == 0) return false;
        return new[] { b.A, b.B, b.C }.All(p => nx.Multiply(MapWideRational.From(p.X).Subtract(x.X))
            .Add(ny.Multiply(MapWideRational.From(p.Y).Subtract(x.Y))).Add(nz.Multiply(MapWideRational.From(p.Z).Subtract(x.Z))).Sign == 0);
    }

    static bool Overlap(MapExactTriangle a, MapExactTriangle b)
    {
        if (!Meets(Of(new[] { a.A, a.B, a.C }), Of(new[] { b.A, b.B, b.C }))) return false;
        var subject = new[] { a.A, a.B, a.C }.Select(p => new MapWideXz(MapWideRational.From(p.X), MapWideRational.From(p.Z))).ToArray();
        if (MapWaterPolygons.SignedArea(subject).Sign < 0) Array.Reverse(subject);
        var clip = new[] { b.A, b.B, b.C }.Select(p => new MapWideXz(MapWideRational.From(p.X), MapWideRational.From(p.Z))).ToArray();
        return MapWaterPolygons.ClipToConvex(subject, clip).Count != 0;
    }

    // Only physical planar faces are tested. This does not assign floor owners to primitive edges or vertices.
    static IEnumerable<MapExactTriangle> ColliderFaces(PhysicsShape shape, MapDoublePose pose, double scale)
    {
        if (shape is CompoundShape compound)
        {
            foreach (var child in compound.Children)
                foreach (var face in ColliderFaces(child.Shape, Compose(pose, child.Local, scale), scale)) yield return face;
        }
        else if (shape is BoxShape box)
        {
            var h = box.HalfExtents;
            // A yaw-only building has one upward face. Child orientations may tilt it.
            foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                foreach (int sign in new[] { -1, 1 })
                {
                    if (pose.Rotate(MapDouble3.From(axis * sign)).Y <= 0) continue;
                    Vector3 u = axis.X != 0 ? Vector3.UnitY : Vector3.UnitX;
                    Vector3 v = axis.Z != 0 ? Vector3.UnitY : Vector3.UnitZ;
                    var vertices = new[] { axis * sign - u - v, axis * sign + u - v, axis * sign + u + v, axis * sign - u + v }
                        .Select(p => Exact(Transform(pose, p * h, scale))).ToArray();
                    yield return new(vertices[0], vertices[1], vertices[2]);
                    yield return new(vertices[0], vertices[2], vertices[3]);
                }
        }
        else if (shape is TriangleMeshShape mesh)
            for (int i = 0; i < mesh.Indices.Length; i += 3)
                yield return new(Exact(Transform(pose, mesh.Vertices[mesh.Indices[i]], scale)), Exact(Transform(pose, mesh.Vertices[mesh.Indices[i + 1]], scale)),
                    Exact(Transform(pose, mesh.Vertices[mesh.Indices[i + 2]], scale)));
    }

    static MapDouble3 Transform(MapDoublePose pose, Vector3 point, double scale) =>
        pose.Position.Add(pose.Rotate(MapDouble3.From(point).Scale(scale)));

    static MapDoublePose Compose(MapDoublePose parent, Pose child, double scale) =>
        parent.Compose(new Pose(Vector3.Zero, child.Orientation)) with { Position = Transform(parent, child.Position, scale) };

    static MapExactPoint Exact(MapDouble3 point) => new(MapResourceTransform.Freeze(point.X),
        MapExactValue.FromSingle(MapResourceTransform.Freeze(point.Y).ToSingle()), MapResourceTransform.Freeze(point.Z));

    static IEnumerable<Vector3> Corners(MapLocalBounds box)
    {
        foreach (float x in new[] { box.Min.X, box.Max.X })
            foreach (float y in new[] { box.Min.Y, box.Max.Y })
                foreach (float z in new[] { box.Min.Z, box.Max.Z }) yield return new(x, y, z);
    }

    internal static MapBox3 Of(IReadOnlyList<MapExactPoint> points) => points.Count == 0 ? default : new(
        Outward(points.Min(p => p.X), false), Outward(points.Min(p => p.Y), false), Outward(points.Min(p => p.Z), false),
        Outward(points.Max(p => p.X), true), Outward(points.Max(p => p.Y), true), Outward(points.Max(p => p.Z), true));

    // Compare in the wide domain. Large numerator/denominator conversions can lose more than one double ULP.
    // Move only while inward, preserving exactly representable seams.
    static double Outward(MapExactValue exact, bool upper)
    {
        double value = exact.ToDouble();
        MapWideRational rational = MapWideRational.From(exact);
        int direction = MapWideRational.FromDouble(value).CompareTo(rational);
        while (upper ? direction < 0 : direction > 0)
        {
            value = upper ? Math.BitIncrement(value) : Math.BitDecrement(value);
            direction = MapWideRational.FromDouble(value).CompareTo(rational);
        }
        return value;
    }
}
