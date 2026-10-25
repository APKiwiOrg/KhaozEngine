using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal sealed record SceneTerrainFace(SceneSurfaceTriangle Triangle, Vector3 Normal, ushort Underlay, ushort Overlay,
    bool OverlayCovers, TileOverlayBoundary? Boundary, int X, int Z);

/// <summary>Source ground triangles and the released overlay boundary supply all expected payload values.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneTerrainCoverage
{
    internal static SceneTerrainFace Source(TileWorldDocument world, Vector3 a, Vector3 b, Vector3 c, int x, int z, int plane)
    {
        var triangle = new SceneSurfaceTriangle(a, b, c);
        var center = new SceneSurfacePoint(((double)a.X + b.X + c.X) / 3, ((double)a.Y + b.Y + c.Y) / 3, ((double)a.Z + b.Z + c.Z) / 3);
        Span<TileLatticeTriangle> cuts = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        TileGroundTriangles.TryDescribe(world, x, z, plane, out var cell, cuts);
        bool overlay = false;
        for (int i = 0; i < cell.TriangleCount; i++)
        {
            Vector3 P(TileLatticePoint p) => TileGroundTriangles.LatticePosition(world, x, z, plane, p, 0, 0);
            var cut = new SceneSurfaceTriangle(P(cuts[i].A), P(cuts[i].B), P(cuts[i].C));
            if (cut.Contains(center)) { overlay = cuts[i].Overlay; break; }
        }
        ushort underlay = world.GetUnderlay(x, z, plane), over = world.GetOverlay(x, z, plane);
        bool feather = overlay && over != 0 && underlay != over && (world.GetSettings(x, z, plane) & TileSettings.FeatherOverlay) != 0;
        return new(triangle, Vector3.Normalize(Vector3.Cross(b - a, c - a)), underlay, over, overlay && over != 0,
            feather ? new(world, x, z, plane) : null, x, z);
    }

    internal static void Compare(IReadOnlyDictionary<MapFaceKey, SceneTerrainFace> expected, MapDrawSet actual, PrivateOracleReport report)
    {
        var triangles = new Dictionary<MapFaceKey, List<SceneTerrainPaintTriangle>>();
        foreach (var resource in actual.Resources.Where(r => r.Payload is MapTerrainResource))
        {
            var payload = (MapTerrainResource)resource.Payload;
            var draws = actual.Draws.Where(d => d.Resource == resource.Reference).ToArray();
            if (draws.Length == 0) continue;
            var draw = draws[0];
            var mesh = payload.Mesh;
            for (int i = 0; i < mesh.Indices32.Length; i += 3)
            {
                var key = payload.FaceKeys[i / 3];
                bool known = expected.TryGetValue(key, out var source);
                Check(report, "scene-terrain-geometry", known, new { key });
                if (!known) continue;
                var vertices = new[] { mesh.Vertices[mesh.Indices32[i]], mesh.Vertices[mesh.Indices32[i + 1]], mesh.Vertices[mesh.Indices32[i + 2]] };
                var points = vertices.Select(v => SceneSurfacePoint.World(v.Position, draw)).ToArray();
                var triangle = new SceneSurfaceTriangle(points[0], points[1], points[2]);
                Check(report, "scene-terrain-geometry", points.All(source!.Triangle.Contains) && triangle.SignedArea * source.Triangle.SignedArea > 0 &&
                    vertices.All(v => Math.Abs(v.Normal.X - source.Normal.X) <= NormalTolerance && Math.Abs(v.Normal.Y - source.Normal.Y) <= NormalTolerance && Math.Abs(v.Normal.Z - source.Normal.Z) <= NormalTolerance), new { key });
                for (int v = 0; v < 3; v++)
                    Check(report, "scene-terrain-paint", Paint(source!, payload, points[v], vertices[v].Color, vertices[v].Uv, vertices[v].Tangent), new { key });
                if (!triangles.TryGetValue(key, out var rows)) triangles.Add(key, rows = []);
                rows.Add(new(triangle, payload, vertices[0], vertices[1], vertices[2]));
            }
        }
        foreach (var pair in expected)
        {
            var rows = triangles.GetValueOrDefault(pair.Key) ?? [];
            Check(report, "scene-terrain-geometry", SceneSurfaceCoverage.Covers([pair.Value.Triangle], rows.Select(t => t.Triangle).ToArray()), new { pair.Key });
            Check(report, "scene-terrain-paint", ShippedSceneTerrainPaint.Matches(pair.Value, rows), new { pair.Key });
        }
    }

    internal static bool Paint(SceneTerrainFace source, MapTerrainResource payload, SceneSurfacePoint p, Vector4 weights, Vector2 uv, Vector4 tangent)
    {
        float[] slots = [uv.X, uv.Y, tangent.X, tangent.Y]; float[] values = [weights.X, weights.Y, weights.Z, weights.W];
        var amounts = new Dictionary<ushort, double>();
        for (int i = 0; i < 4; i++)
        {
            if (!float.IsFinite(slots[i]) || slots[i] != MathF.Truncate(slots[i]) || slots[i] < 0 || slots[i] >= payload.Materials.Layers.Count ||
                !float.IsFinite(values[i]) || values[i] < 0 || values[i] > 1) return false;
            ushort id = payload.Materials.Layers[(int)slots[i]].MaterialId;
            amounts[id] = amounts.GetValueOrDefault(id) + values[i];
        }
        if (tangent.Z != 1) return false;
        double blend = 0;
        if (source.Boundary is { } boundary)
        {
            float distance = boundary.Distance(new((float)(p.X - source.X), (float)(-p.Z - source.Z)), source.X, source.Z);
            double t = Math.Clamp(distance / .2, 0, 1); blend = t * t * (3 - 2 * t);
            float slot = tangent.W - 1;
            if (!float.IsFinite(slot) || slot != MathF.Truncate(slot) || slot < 0 || slot >= payload.Materials.Layers.Count ||
                payload.Materials.Layers[(int)slot].MaterialId != source.Overlay) return false;
        }
        else if (tangent.W != 0) return false;
        ushort material = source.OverlayCovers && source.Boundary is null ? source.Overlay : source.Underlay;
        return amounts.All(a => a.Key == material || a.Value == 0) && Math.Abs(amounts.GetValueOrDefault(material) - (1 - blend)) <= NormalTolerance;
    }
}
