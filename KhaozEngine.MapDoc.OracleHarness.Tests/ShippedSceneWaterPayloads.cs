using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneWaterCoverage
{
    internal static void ComparePayloads(ShippedSceneInputs inputs, MapRenderPlan plan, MapDrawSet actual, PrivateOracleReport report)
    {
        if (inputs.Manifest.Water.Count == 0) return;
        var settings = inputs.SourcePolicy.Water.ResolveInto(new WaterSettings(), new WaterSettings());
        double halo = .001 + WaterSurfaceDisplacement.MaximumHorizontal(new()
        {
            WaveSource = settings.WaveSource, SwellAmplitude = settings.SwellAmplitude, SwellWavelength = settings.SwellWavelength,
            SwellSteepness = settings.SwellSteepness, SwellDirectionDegrees = settings.SwellDirectionDegrees,
            SwellSpreadDegrees = settings.SwellSpreadDegrees, SwellSpeed = settings.SwellSpeed, SwellSeed = settings.SwellSeed
        });
        foreach (var mapping in inputs.Manifest.Water)
        {
            var body = SourceBody(inputs, mapping.SourceBodyKey);
            var expected = Cells(body, plan.Scene.Scope, inputs.NativeTileSize);
            var keys = plan.Inventory.Water.Cells.Where(c => c.Key.BodyId == mapping.NativeBodyId).ToDictionary(
                c => new MapRenderKey(MapRenderKind.Water, mapping.NativeBodyId, CellId(c.Key), 0),
                c => new Cell(c.Key.StorageOwner.X, c.Key.StorageOwner.Z, c.Key.X, c.Key.Z));
            var submitted = new Dictionary<Cell, List<SceneSurfaceTriangle>>();
            foreach (var resource in actual.Resources.Where(r => r.Reference.Key.Kind == MapRenderKind.Water && r.Reference.Key.OwnerId == mapping.NativeBodyId))
            {
                bool bound = resource.Payload is MapWaterResource && keys.TryGetValue(resource.Reference.Key, out _);
                Check(report, "scene-water-geometry", bound, new { resource.Reference.Key });
                if (!bound) continue;
                var cell = keys[resource.Reference.Key]; var payload = (MapWaterResource)resource.Payload;
                foreach (var draw in actual.Draws.Where(d => d.Resource == resource.Reference))
                {
                    var mesh = payload.Mesh; var triangles = new List<SceneSurfaceTriangle>();
                    for (int i = 0; i < mesh.Indices32.Length; i += 3)
                    {
                        SceneSurfacePoint P(int n) => SceneSurfacePoint.World(mesh.Vertices[mesh.Indices32[n]].Position, draw);
                        var triangle = new SceneSurfaceTriangle(P(i), P(i + 1), P(i + 2));
                        Check(report, "scene-water-geometry", triangle.Points.All(p => p.Finite && Math.Abs(p.Y - body.SurfaceY) <= GeometryTolerance), new { resource.Reference.Key });
                        triangles.Add(triangle);
                    }
                    if (!submitted.TryGetValue(cell, out var rows)) submitted.Add(cell, rows = []);
                    rows.AddRange(triangles);
                    var bounds = CellBounds(cell, inputs.NativeTileSize);
                    var scope = plan.Scene.Scope with
                    {
                        DrawBounds = new(bounds.MinX - halo, double.MinValue, bounds.MinZ - halo, bounds.MaxX + halo, double.MaxValue, bounds.MaxZ + halo),
                        MotionPaddingMetres = 0
                    };
                    var clipExpected = Cells(body, scope, inputs.NativeTileSize).SelectMany(c => Domain(body, c, inputs.NativeTileSize)).ToArray();
                    bool decoded = DecodeClip(payload, draw, body.SurfaceY, out var clip);
                    Check(report, "scene-water-clip", decoded && SceneSurfaceCoverage.Covers(clipExpected, clip), new { resource.Reference.Key });
                }
            }
            foreach (var cell in expected.Union(submitted.Keys))
                Check(report, "scene-water-geometry", SceneSurfaceCoverage.Covers(expected.Contains(cell) ? Domain(body, cell, inputs.NativeTileSize) : [],
                    submitted.GetValueOrDefault(cell) ?? []), new { mapping.NativeBodyId, cell });
        }
    }

    static TileWaterBody SourceBody(ShippedSceneInputs inputs, string id)
    {
        foreach (var region in inputs.World.Regions.Keys)
            for (int plane = 0; plane < inputs.World.PlaneCount; plane++)
            {
                var bodies = TileWaterBodies.Collect(inputs.World, inputs.Catalogs, region, plane);
                for (int i = 0; i < bodies.Count; i++)
                    if (KhaozEngine.Tests.MapDocCompatibility.LegacyWaterConverter.BodyId(region, plane, i) == id) return bodies[i];
            }
        throw new InvalidOperationException("Missing source water body.");
    }
    static (double MinX, double MinZ, double MaxX, double MaxZ) CellBounds(Cell key, double tileSize) =>
        (Math.Max(key.X * 2d, key.TileX * tileSize), Math.Max(key.Z * 2d, key.TileZ * tileSize),
         Math.Min((key.X + 1d) * 2, (key.TileX + 1d) * tileSize), Math.Min((key.Z + 1d) * 2, (key.TileZ + 1d) * tileSize));

    static IReadOnlyList<SceneSurfaceTriangle> Domain(TileWaterBody source, Cell cell, double tileSize)
    {
        var result = new List<SceneSurfaceTriangle>(); var b = CellBounds(cell, tileSize);
        foreach (var rect in source.Rects)
        {
            double x0 = Math.Max(b.MinX, rect.X), x1 = Math.Min(b.MaxX, rect.X + rect.Width);
            double z0 = Math.Max(b.MinZ, -rect.Z - rect.Height), z1 = Math.Min(b.MaxZ, -rect.Z);
            if (x0 >= x1 || z0 >= z1) continue;
            var a = new SceneSurfacePoint(x0, source.SurfaceY, z0); var c = new SceneSurfacePoint(x1, source.SurfaceY, z1);
            result.Add(new(a, new SceneSurfacePoint(x1, source.SurfaceY, z0), c));
            result.Add(new(a, c, new SceneSurfacePoint(x0, source.SurfaceY, z1)));
        }
        return result;
    }

    static bool DecodeClip(MapWaterResource payload, MapDrawRecord draw, float level, out IReadOnlyList<SceneSurfaceTriangle> triangles)
    {
        triangles = [];
        byte[] bytes = payload.ClipData.ToArray();
        try { WaterSurfaceDomain.Validate(bytes); }
        catch (ArgumentException) { return false; }
        SceneSurfacePoint P(int at) => SceneSurfacePoint.World(new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at)),
            (float)(level - (double)draw.Anchor.Y), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 4))), draw);
        var result = new List<SceneSurfaceTriangle>();
        for (int at = 16; at < bytes.Length; at += 48) result.Add(new(P(at), P(at + 16), P(at + 32)));
        triangles = result;
        return true;
    }
}
