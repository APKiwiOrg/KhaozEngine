using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfPropPlacement(Vector3 Position, float Azimuth)
{
    internal Matrix4x4 World => Matrix4x4.CreateRotationY(Azimuth) * Matrix4x4.CreateTranslation(Position);

    internal static RendererPerfPropPlacement[] Create(int count, float extent, uint seed)
    {
        var placements = new RendererPerfPropPlacement[count];
        float Next() { seed = unchecked(seed * 1664525u + 1013904223u); return (seed >> 8) / (float)(1 << 24); }
        for (int i = 0; i < count; i++)
        {
            float x = (Next() - .5f) * extent, z = (Next() - .5f) * extent;
            placements[i] = new(new Vector3(x, 0f, z), Next() * 6.28f);
        }
        return placements;
    }
}

internal sealed record RendererPerfTubeInputs(float Radius, float Length, int Rings, int Radial, int Bones, Axis Axis)
{
    internal static readonly RendererPerfTubeInputs Character = new(.5f, 4f,
        StreamedWorldSceneContent.CharacterRings, StreamedWorldSceneContent.CharacterRadial,
        StreamedWorldSceneContent.CharacterBones, Axis.Z);

    internal SkinnedGltfMesh Build() => SkinnedMeshBuilder.BuildTube(Radius, Length, Rings, Radial, Bones, Axis);
}
