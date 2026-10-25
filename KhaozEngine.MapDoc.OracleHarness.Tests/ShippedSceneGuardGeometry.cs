using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

internal static class ShippedSceneGuardGeometry
{
    internal static SceneOracleObservation Mutate(SceneOracleObservation value, string variant)
    {
        var resources = value.DrawSet.Resources.Select(resource =>
        {
            if (resource.Payload is not MapTerrainResource terrain) return resource;
            var mesh = terrain.Mesh;
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                var v = mesh.Vertices[i];
                mesh.Vertices[i] = variant switch
                {
                    "terrain-height" => new(v.Position + Vector3.UnitY, v.Normal, v.Color, v.Uv, v.Tangent),
                    "terrain-normal" => new(v.Position, Vector3.UnitX, v.Color, v.Uv, v.Tangent),
                    "terrain-weights" => new(v.Position, v.Normal, v.Color * .5f, v.Uv, v.Tangent),
                    "terrain-slots" => new(v.Position, v.Normal, v.Color, v.Uv + Vector2.One, v.Tangent),
                    _ => throw new ArgumentException("Unknown synthetic terrain mutation.")
                };
            }
            return resource with { Payload = new MapTerrainResource(mesh, terrain.Materials, terrain.FaceKeys) };
        }).ToArray();
        return value with { DrawSet = new(resources, value.DrawSet.Draws) };
    }
}
