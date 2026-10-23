using System;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    // Cold registration also covers ordinary replacement meshes. Unsupported data keeps its existing load behavior.
    void CacheCoveragePaletteBounds(int index, int generation, SkinnedGltfMesh mesh)
    {
        if (mesh.Profile.Method != SkinningMethod.LinearBlend) return;
        if (mesh.InverseBind.Length < 1 || mesh.InverseBind.Length > SkinningMath.MaxBonesPerDraw) return;
        foreach (var vertex in mesh.Vertices)
            for (int slot = 0; slot < 4; slot++)
            {
                float bone = vertex.BoneIndices[slot];
                if (!float.IsFinite(bone) || bone < 0 || bone >= mesh.InverseBind.Length || bone != MathF.Truncate(bone)) return;
            }
        CoverageActorBounds bounds;
        try
        {
            bounds = mesh.Coverage is { } part ? new CoverageActorBounds(part.Source) : new CoverageActorBounds(mesh.Vertices);
        }
        catch (ArgumentException) { return; }
        catch (OverflowException) { return; }
        _coveragePaletteBounds.Add((index, generation), bounds);
    }
}
