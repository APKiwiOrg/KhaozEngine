using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>One geometry stream for colour and shadows. Feather samples subdivide each exact canonical plane,
/// with the released 0.2 m inward smoothstep band and no added coplanar overlay.</summary>
internal static class MapTerrainMesh
{
    const double FeatherWidthMetres = .2;

    internal static MapTerrainResource Build(IEnumerable<MapTerrainPartition> partitions,
        IReadOnlyDictionary<MapFaceKey, MapTerrainCanonicalFace> canonical, MapSubmissionAnchor anchor,
        MapTerrainMaterialSet materials, IReadOnlyDictionary<MapTerrainPaint.FeatherKey, IReadOnlyList<MapTerrainPaint.Edge>> boundaries,
        MapSceneAdmission admission)
    {
        var vertices = new List<ModelVertex>();
        var keys = new List<MapFaceKey>();
        var slots = materials.Layers.Select((m, i) => (m.MaterialId, i)).ToDictionary(p => p.MaterialId, p => p.i);
        foreach (var partition in partitions)
        {
            var paint = partition.Paint;
            if (NativeMapTerrain.LayerIds(paint).Length == 0) continue;
            var face = canonical[partition.Face];
            bool feather = paint.Feather && paint.OverlayCovers && paint.Overlay != 0 && paint.Underlay != 0 && paint.Underlay != paint.Overlay;
            var boundary = feather ? boundaries[MapTerrainPaint.Key(partition)] : Array.Empty<MapTerrainPaint.Edge>();
            int underlaySlot = slots[paint.OverlayCovers && paint.Overlay != 0 && !feather ? paint.Overlay : paint.Underlay];
            int overlaySlot = feather ? slots[paint.Overlay] : -1;
            for (int i = 1; i + 1 < partition.Polygon.Count; i++)
            {
                MapExactPoint a = partition.Polygon[0], b = partition.Polygon[i], c = partition.Polygon[i + 1];
                if (face.Face.Role != MapFaceRole.Wall && MapEffectiveGeometry.AreaWide(new[]
                    { MapWidePoint.From(a).Xz, MapWidePoint.From(b).Xz, MapWidePoint.From(c).Xz }).Sign == 0) continue;
                int divisions = 1;
                if (feather)
                {
                    double extent = Math.Max(Distance(a, b), Math.Max(Distance(a, c), Distance(b, c)));
                    while (divisions < 64 && divisions * FeatherWidthMetres < extent * 2) divisions *= 2;
                }
                admission.ReserveCompilation(checked((long)divisions * divisions));
                var grid = new ModelVertex[(divisions + 1) * (divisions + 1)];
                for (int y = 0; y <= divisions; y++)
                    for (int x = 0; x <= divisions - y; x++)
                    {
                        var point = Interpolate(a, b, c, x, y, divisions);
                        float blend = 0;
                        if (feather)
                        {
                            double distance = double.PositiveInfinity;
                            foreach (var edge in boundary) distance = Math.Min(distance, EdgeDistance(point.Xz, edge));
                            double weight = Math.Clamp(distance / FeatherWidthMetres, 0, 1);
                            blend = (float)(weight * weight * (3 - 2 * weight));
                        }
                        var position = new Vector3(Local(point.X, anchor.X), Local(point.Y, anchor.Y), Local(point.Z, anchor.Z));
                        grid[y * (divisions + 1) + x] = new(position, face.Face.Normal, new(1 - blend, 0, 0, 0),
                            new(underlaySlot, underlaySlot), new(underlaySlot, underlaySlot, 1, overlaySlot + 1));
                    }
                for (int y = 0; y < divisions; y++)
                    for (int x = 0; x < divisions - y; x++)
                    {
                        int index = y * (divisions + 1) + x, next = index + divisions + 1;
                        Emit(grid[index], grid[index + 1], grid[next]);
                        if (x + y < divisions - 1) Emit(grid[index + 1], grid[next + 1], grid[next]);
                    }

                void Emit(ModelVertex first, ModelVertex second, ModelVertex third)
                {
                    var cross = Vector3.Cross(second.Position - first.Position, third.Position - first.Position);
                    if (cross.LengthSquared() == 0) throw new MapDocumentException("Terrain render subdivision collapsed.");
                    if (Vector3.Dot(cross, face.Face.Normal) < 0) (second, third) = (third, second);
                    vertices.Add(first); vertices.Add(second); vertices.Add(third); keys.Add(face.Face.Key);
                }
            }
        }
        return new(new(vertices.ToArray(), Enumerable.Range(0, vertices.Count).Select(i => (uint)i).ToArray()), materials, keys);
    }

    static float Local(MapWideRational coordinate, long anchor) =>
        MapTerrainPaint.Narrow(coordinate.Subtract(MapWideRational.From(anchor))).ToSingle();

    static MapWidePoint Interpolate(MapExactPoint a, MapExactPoint b, MapExactPoint c, int x, int y, int divisions)
    {
        var wb = MapWideRational.From(new MapExactValue(x, divisions));
        var wc = MapWideRational.From(new MapExactValue(y, divisions));
        MapWideRational Component(MapExactValue av, MapExactValue bv, MapExactValue cv) =>
            MapWideRational.From(av).Add(MapWideRational.From(bv).Subtract(MapWideRational.From(av)).Multiply(wb))
                .Add(MapWideRational.From(cv).Subtract(MapWideRational.From(av)).Multiply(wc));
        return new(Component(a.X, b.X, c.X), Component(a.Y, b.Y, c.Y), Component(a.Z, b.Z, c.Z));
    }

    static double Distance(MapExactPoint a, MapExactPoint b)
    {
        double x = MapWideRational.From(b.X).Subtract(MapWideRational.From(a.X)).ToDouble();
        double z = MapWideRational.From(b.Z).Subtract(MapWideRational.From(a.Z)).ToDouble();
        return Math.Sqrt(x * x + z * z);
    }

    static double EdgeDistance(MapWideXz point, MapTerrainPaint.Edge edge)
    {
        double dx = edge.B.X.Subtract(edge.A.X).ToDouble(), dz = edge.B.Z.Subtract(edge.A.Z).ToDouble();
        double x = point.X.Subtract(edge.A.X).ToDouble(), z = point.Z.Subtract(edge.A.Z).ToDouble();
        double t = Math.Clamp((x * dx + z * dz) / (dx * dx + dz * dz), 0, 1);
        x -= t * dx; z -= t * dz;
        return Math.Sqrt(x * x + z * z);
    }
}
