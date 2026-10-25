using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

internal static class ShippedSceneGuardWaterGeometry
{
    internal static SceneOracleObservation Mutate(SceneOracleObservation value, string variant)
    {
        var resources = value.DrawSet.Resources.Select(resource =>
        {
            if (resource.Payload is not MapWaterResource water) return resource;
            var mesh = water.Mesh; var clip = water.ClipData.ToArray();
            if (variant == "water-clip")
                for (int i = 16; i < clip.Length; i += 16)
                    BinaryPrimitives.WriteSingleLittleEndian(clip.AsSpan(i), BinaryPrimitives.ReadSingleLittleEndian(clip.AsSpan(i)) + 1);
            else
                for (int i = 0; i < mesh.Vertices.Length; i++)
                    mesh.Vertices[i].Position += variant == "water-height" ? Vector3.UnitY : Vector3.UnitX;
            return resource with { Payload = new MapWaterResource(mesh, clip) };
        }).ToArray();
        return value with { DrawSet = new(resources, value.DrawSet.Draws) };
    }
}
