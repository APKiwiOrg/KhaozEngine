using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Removes only feather refinement, retaining each canonical face and its original corner attributes.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneGuardTerrainPaint
{
    internal static SceneOracleObservation Simplify(SceneOracleObservation observation, bool reorder)
    {
        int simplified = 0;
        var resources = observation.DrawSet.Resources.Select(resource =>
        {
            if (resource.Payload is not MapTerrainResource terrain) return resource;
            var mesh = terrain.Mesh;
            var vertices = new List<ModelVertex>(); var keys = new List<MapFaceKey>();
            foreach (var group in terrain.FaceKeys.Select((key, i) => (key, i)).GroupBy(t => t.key))
            {
                var original = group.SelectMany(t => Enumerable.Range(t.i * 3, 3).Select(i => mesh.Vertices[mesh.Indices32[i]])).ToArray();
                if (original.All(v => v.Tangent.W > 0))
                {
                    var a = original.OrderBy(v => v.Position.X).ThenBy(v => v.Position.Z).First();
                    var b = original.MaxBy(v => System.Numerics.Vector3.DistanceSquared(a.Position, v.Position));
                    double Cross(ModelVertex v) => ((double)b.Position.X - a.Position.X) * (v.Position.Z - a.Position.Z) -
                        ((double)b.Position.Z - a.Position.Z) * (v.Position.X - a.Position.X);
                    var c = original.MaxBy(v => Math.Abs(Cross(v)));
                    var triangle = new SceneSurfaceTriangle(a.Position, b.Position, c.Position);
                    var first = new SceneSurfaceTriangle(original[0].Position, original[1].Position, original[2].Position);
                    if (triangle.SignedArea * first.SignedArea < 0) (b, c) = (c, b);
                    if (original.Length <= 3) throw new InvalidOperationException("Fixture needs feather subdivisions.");
                    original = [a, b, c]; simplified++;
                }
                vertices.AddRange(original);
                keys.AddRange(Enumerable.Repeat(group.Key, original.Length / 3));
            }
            var order = Enumerable.Range(0, keys.Count);
            if (reorder) order = order.Reverse();
            int[] triangles = order.ToArray();
            return resource with
            {
                Payload = new MapTerrainResource(new(vertices.ToArray(), triangles.SelectMany(i => new uint[] { (uint)(i * 3), (uint)(i * 3 + 1), (uint)(i * 3 + 2) }).ToArray()),
                    terrain.Materials, triangles.Select(i => keys[i]).ToArray())
            };
        }).ToArray();
        if (simplified == 0) throw new InvalidOperationException("Fixture needs a feather-painted face.");
        return observation with { DrawSet = new(resources, observation.DrawSet.Draws) };
    }

    internal static SceneOracleObservation Reorder(SceneOracleObservation observation) => observation with
    {
        DrawSet = new(observation.DrawSet.Resources.Select(resource => resource.Payload is MapTerrainResource terrain
            ? resource with
            {
                Payload = new MapTerrainResource(new(terrain.Mesh.Vertices,
                Enumerable.Range(0, terrain.FaceKeys.Count).Reverse().SelectMany(i => terrain.Mesh.Indices32.Skip(i * 3).Take(3)).ToArray()),
                terrain.Materials, terrain.FaceKeys.Reverse().ToArray())
            } : resource).ToArray(), observation.DrawSet.Draws)
    };
}
