using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>GPU-free scene inputs shared by engine fixtures and the renderer evidence tooling.</summary>
internal static class RendererPerfSceneInputs
{
    // BuildTube's vertex count is (rings + 1) * radial, matching the reference character's scale.
    internal const int CharacterRings = 340, CharacterRadial = 40, CharacterBones = 48;
    internal const int ShadowResolution = 2048, Cascades = 4;

    internal static GltfMesh ShrubMesh() => MeshPrimitives.Tile(.8f, 2f);

    internal static Matrix4x4[] BentPose(SkinnedGltfMesh tube, float angle = 0.5f)
    {
        Matrix4x4[] pose = (Matrix4x4[])tube.RestPose.Clone();
        Matrix4x4 accumulated = Matrix4x4.Identity;
        Vector3 previousRest = tube.RestPose[0].Translation;
        Vector3 jointPosition = previousRest;
        for (int bone = 0; bone < tube.BoneCount; bone++)
        {
            Vector3 restPosition = tube.RestPose[bone].Translation;
            jointPosition += Vector3.Transform(restPosition - previousRest, accumulated);
            if (bone >= tube.BoneCount - 2)
                accumulated = Matrix4x4.CreateRotationZ(angle) * accumulated;
            pose[bone] = Matrix4x4.CreateTranslation(-restPosition)
                * accumulated * Matrix4x4.CreateTranslation(jointPosition);
            previousRest = restPosition;
        }
        return pose;
    }

    internal static void BaseSea(WaterSeaState sea)
    {
        ArgumentNullException.ThrowIfNull(sea);
        sea.Seed = 20260726;
        sea.CascadeCount = 2;
        sea.CascadeResolution = 64;
    }

    internal static Vector2 BuildingCentre(int index, int columns, int rows, float spacing) =>
        new((index % columns - (columns - 1) * .5f) * spacing,
            (index / columns - (rows - 1) * .5f) * spacing);

    internal static Matrix4x4[] PropWorlds(int count, float extent, uint seed)
    {
        var worlds = new Matrix4x4[count];
        float Next() { seed = unchecked(seed * 1664525u + 1013904223u); return (seed >> 8) / (float)(1 << 24); }
        for (int i = 0; i < count; i++)
        {
            float x = (Next() - 0.5f) * extent, z = (Next() - 0.5f) * extent;
            worlds[i] = Matrix4x4.CreateRotationY(Next() * 6.28f) * Matrix4x4.CreateTranslation(x, 0f, z);
        }
        return worlds;
    }

    internal static SkinnedGltfMesh CharacterTube() => SkinnedMeshBuilder.BuildTube(
        0.5f, 4f, CharacterRings, CharacterRadial, CharacterBones, Axis.Z);

    internal static ShadowSettings Shadows() => new()
    {
        Mode = ShadowMode.ShadowMap,
        ShadowMapResolution = ShadowResolution,
        ShadowCascadeCount = Cascades,
    };
}
