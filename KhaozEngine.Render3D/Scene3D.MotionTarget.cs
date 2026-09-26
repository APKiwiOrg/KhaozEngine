using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>
/// The screen-space motion target's per-frame work (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 sections 3 and 4): the
/// previous state each opaque path reads, and the seams the tests read it through. Nothing here runs while the model
/// framebuffer has no motion attachment.
/// </summary>
public sealed partial class Scene3D
{
    /// <summary>The key of this frame's GPU-skinned draw record <paramref name="draw"/>. For tests.</summary>
    internal MotionKey GpuSkinnedMotionForTests(int draw) => _gpuSkinnedDraws[draw].Motion;

    /// <summary>The key, mesh slot and vertex count of this frame's CPU-skinned draw record <paramref name="draw"/>.
    /// For tests.</summary>
    internal (MotionKey Motion, int MeshIndex, int VertexCount) CpuSkinnedMotionForTests(int draw)
    {
        CpuSkinnedDraw record = _cpuSkinnedDraws[draw];
        return (record.Motion, record.MeshIndex, record.VertexCount);
    }
}
