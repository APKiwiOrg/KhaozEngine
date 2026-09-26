using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Last frame's world positions of a CPU-skinned draw, one per deformed vertex, for the CPU-skinned temporal variant
/// (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3). A keyed draw with a usable last frame is re-skinned from the
/// palette <see cref="MotionHistory"/> kept for its key and placed with last frame's world, reduced against this
/// frame's render origin. Anything else gets this frame's own positions, which is camera-only motion. The stream is
/// parallel to the deformed vertices, so a draw's base vertex selects both.
/// </summary>
internal static class CpuSkinnedMotion
{
    /// <summary>Re-skin <paramref name="source"/> with last frame's composed palette and place it with last frame's
    /// world, reduced against this frame's render origin. <see cref="SkinningMath.SkinVertex"/> runs this blend and this
    /// position transform, so each position is the one the draw uploaded last frame, bit for bit before the world
    /// transform.</summary>
    public static void AppendPrevious(ReadOnlySpan<SkinnedVertex> source, ReadOnlySpan<Matrix4x4> previousPalette,
        in Matrix4x4 previousWorld, List<Vector3> destination)
    {
        for (int v = 0; v < source.Length; v++)
        {
            Matrix4x4 skin = SkinningMath.BlendSkinMatrix(previousPalette, source[v].BoneIndices, source[v].BoneWeights);
            destination.Add(Vector3.Transform(Vector3.Transform(source[v].Position, skin), previousWorld));
        }
    }

    /// <summary>This frame's deformed vertices placed with this frame's render-relative world.</summary>
    public static void AppendCurrent(ReadOnlySpan<ModelVertex> skinned, in Matrix4x4 world, List<Vector3> destination)
    {
        for (int v = 0; v < skinned.Length; v++) destination.Add(Vector3.Transform(skinned[v].Position, world));
    }
}
