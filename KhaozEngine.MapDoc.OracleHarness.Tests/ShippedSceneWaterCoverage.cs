using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Independent submitted area from original water-plane domains. Scene-depth occlusion does not trim those inputs.</summary>
[UnsupportedOSPlatform("windows")]
internal static partial class ShippedSceneWaterCoverage
{
    readonly record struct Point(double X, double Y, double Z);
    readonly record struct Cell(int TileX, int TileZ, long X, long Z);
    internal static IReadOnlyDictionary<MapRenderKey, MapSubmissionAnchor> Compare(ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report)
    {
        var required = new Dictionary<MapRenderKey, MapSubmissionAnchor>();
        var recorder = new MapDrawRecorder(); plan.Register(recorder);
        foreach (var mapping in inputs.Manifest.Water)
        {
            LegacyWaterSource? source = null;
            foreach (var region in inputs.World.Regions.Keys)
                for (int plane = 0; plane < inputs.World.PlaneCount; plane++)
                {
                    var bodies = TileWaterBodies.Collect(inputs.World, inputs.Catalogs, region, plane);
                    for (int i = 0; i < bodies.Count; i++)
                        if (LegacyWaterConverter.BodyId(region, plane, i) == mapping.SourceBodyKey) source = new(mapping.SourceBodyKey, region, plane, i, bodies[i]);
                }
            if (source is null) { Check(report, "scene-water-area", false, new { mapping.SourceBodyKey }); continue; }
            var expected = Cells(source.Body, plan.Scene.Scope, inputs.NativeTileSize);
            var actual = plan.Inventory.Water.Cells.Where(c => c.Key.BodyId == mapping.NativeBodyId)
                .GroupBy(c => new Cell(c.Key.StorageOwner.X, c.Key.StorageOwner.Z, c.Key.X, c.Key.Z)).ToDictionary(g => g.Key, g => g.ToArray());
            double total = 0;
            foreach (var key in expected.Union(actual.Keys))
            {
                double minX = Math.Max(key.X * 2d, key.TileX * inputs.NativeTileSize), maxX = Math.Min((key.X + 1d) * 2, (key.TileX + 1d) * inputs.NativeTileSize);
                double minZ = Math.Max(key.Z * 2d, key.TileZ * inputs.NativeTileSize), maxZ = Math.Min((key.Z + 1d) * 2, (key.TileZ + 1d) * inputs.NativeTileSize);
                double want = expected.Contains(key) ? SourceArea(source, minX, minZ, maxX, maxZ) : 0;
                double got = (actual.GetValueOrDefault(key) ?? []).SelectMany(c => c.Regions).Where(r => r.Kind == MapWaterRegionKind.FreeSurface).SelectMany(r => r.Triangles).Sum(Area);
                Check(report, "scene-water-area", Math.Abs(want - got) <= GeometryTolerance, new { key, expected = want, actual = got });
                total += got;
                if (want > 0)
                    foreach (var cell in actual.GetValueOrDefault(key) ?? [])
                    {
                        var points = cell.Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface).SelectMany(r => r.Triangles)
                            .SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                        if (points.Length == 0) continue;
                        var renderKey = new MapRenderKey(MapRenderKind.Water, mapping.NativeBodyId, CellId(cell.Key), 0);
                        var anchor = new MapSubmissionAnchor(points.Min(p => p.X).Floor(), KhaozEngine.MapDoc.Surfaces.MapExactValue.FromSingle(cell.SurfaceY).Floor(), points.Min(p => p.Z).Floor());
                        Check(report, "scene-water-frame", required.TryAdd(renderKey, anchor), new { renderKey });
                    }
            }
            double drawn = recorder.Snapshot().Resources.Where(r => r.Reference.Key.Kind == MapRenderKind.Water && r.Reference.Key.OwnerId == mapping.NativeBodyId)
                .Select(r => ((MapWaterResource)r.Payload).Mesh).Sum(MeshArea);
            Check(report, "scene-water-closed", Math.Abs(drawn - total) <= GeometryTolerance, new { mapping.NativeBodyId, drawn, free = total });
        }
        return required;
    }
    static string CellId(MapWaterCellKey key)
    {
        using var bytes = new System.IO.MemoryStream();
        bytes.Write(System.Text.Encoding.UTF8.GetBytes("kemap/water-cell-key/1\0"));
        using (var writer = new System.Text.Json.Utf8JsonWriter(bytes, MapJsonTextOptions.IndentedWriter with { Indented = false }))
        {
            writer.WriteStartArray(); writer.WriteStringValue(key.BodyId); writer.WriteStringValue(key.SpaceId); writer.WriteStringValue(key.FragmentId);
            writer.WriteNumberValue(key.StorageOwner.X); writer.WriteNumberValue(key.StorageOwner.Z); writer.WriteNumberValue(key.X); writer.WriteNumberValue(key.Z);
            writer.WriteStringValue(key.GeometryPolicy); writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
    }
    static HashSet<Cell> Cells(TileWaterBody body, KhaozEngine.MapDoc.Scene.MapSceneScope scope, double tileSize)
    {
        var result = new HashSet<Cell>(); var b = scope.DrawBounds; double pad = scope.MotionPaddingMetres;
        foreach (var rect in body.Rects)
        {
            double x0 = Math.Max(rect.X, b.MinX - pad), x1 = Math.Min(rect.X + rect.Width, b.MaxX + pad);
            double z0 = Math.Max(-rect.Z - rect.Height, b.MinZ - pad), z1 = Math.Min(-rect.Z, b.MaxZ + pad);
            if (x0 >= x1 || z0 >= z1) continue;
            for (long z = checked((long)Math.Floor(z0 / 2)); z < Math.Ceiling(z1 / 2); z++)
                for (long x = checked((long)Math.Floor(x0 / 2)); x < Math.Ceiling(x1 / 2); x++)
                    for (int tz = checked((int)Math.Floor(z * 2d / tileSize)); tz < Math.Ceiling((z + 1d) * 2 / tileSize); tz++)
                        for (int tx = checked((int)Math.Floor(x * 2d / tileSize)); tx < Math.Ceiling((x + 1d) * 2 / tileSize); tx++)
                            if (Math.Max(x0, Math.Max(x * 2d, tx * tileSize)) < Math.Min(x1, Math.Min((x + 1d) * 2, (tx + 1d) * tileSize)) &&
                                Math.Max(z0, Math.Max(z * 2d, tz * tileSize)) < Math.Min(z1, Math.Min((z + 1d) * 2, (tz + 1d) * tileSize))) result.Add(new(tx, tz, x, z));
        }
        return result;
    }
    static double SourceArea(LegacyWaterSource source, double minX, double minZ, double maxX, double maxZ) =>
        source.Body.Rects.Sum(r => Math.Max(0, Math.Min(maxX, r.X + r.Width) - Math.Max(minX, r.X)) *
            Math.Max(0, Math.Min(maxZ, -r.Z) - Math.Max(minZ, -r.Z - r.Height)));
    static double Area(IReadOnlyList<Point> polygon)
    {
        if (polygon.Count < 3) return 0;
        double result = 0; var p = polygon[0];
        for (int i = 1; i + 1 < polygon.Count; i++) result += Math.Abs((polygon[i].X - p.X) * (polygon[i + 1].Z - p.Z) - (polygon[i].Z - p.Z) * (polygon[i + 1].X - p.X)) / 2;
        return result;
    }
    static double Area(MapExactTriangle t) => Area(new[] { P(t.A), P(t.B), P(t.C) });
    static Point P(MapExactPoint p) => new(p.X.ToDouble(), p.Y.ToDouble(), p.Z.ToDouble());
    static double MeshArea(KhaozEngine.Render3D.GltfMesh mesh)
    {
        double result = 0;
        for (int i = 0; i < mesh.Indices32.Length; i += 3)
        {
            Point P(int n) { var p = mesh.Vertices[mesh.Indices32[n]].Position; return new(p.X, p.Y, p.Z); }
            result += Area(new[] { P(i), P(i + 1), P(i + 2) });
        }
        return result;
    }
}
