using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>One queued rigid draw as the main-pass cull sees it. <see cref="Ground"/> marks splat terrain, which the
/// engine tests by its exact world box under a pure translation.</summary>
internal readonly record struct RendererPerfRigidBounds(MeshBounds Bounds, Matrix4x4 World, bool Ground);

/// <summary>The least and most draws the main pass keeps over a frame sequence.</summary>
internal readonly record struct RendererPerfVisibleRange(int Min, int Max);

/// <summary>Main-pass visible counts by the engine's own rigid culling rule, against the absolute view projection
/// Scene3D culls with.</summary>
internal static class RendererPerfMainPassVisibility
{
    // Frozen counts are authored inputs, so they must not depend on platform rounding of the camera matrices.
    // The range narrows the frustum by this clip-space fraction for its minimum and widens it for its maximum,
    // a few centimetres at these frames, so a draw touching the frustum edge widens the range instead of
    // flipping a count between platforms.
    const float Tolerance = 1e-3f;

    internal static int Visible(IEnumerable<RendererPerfRigidBounds> draws, Matrix4x4 viewProjection)
    {
        FrustumPlanes frustum = FrustumPlanes.Extract(viewProjection);
        return draws.Count(draw => Scene3D.IntersectsMainPass(draw.Bounds, draw.World, draw.Ground, frustum));
    }

    internal static RendererPerfVisibleRange Range(IReadOnlyList<RendererPerfRigidBounds> draws,
        IEnumerable<Matrix4x4> viewProjections)
    {
        int min = int.MaxValue, max = 0;
        foreach (Matrix4x4 viewProjection in viewProjections)
        {
            min = Math.Min(min, Visible(draws, Narrowed(viewProjection)));
            max = Math.Max(max, Visible(draws, Widened(viewProjection)));
        }
        return new(min, max);
    }

    // Scale clip x and y about the frame centre, narrowing or widening the frustum by the tolerance.
    internal static Matrix4x4 Narrowed(Matrix4x4 viewProjection) =>
        viewProjection * Matrix4x4.CreateScale(1f + Tolerance, 1f + Tolerance, 1f);
    internal static Matrix4x4 Widened(Matrix4x4 viewProjection) =>
        viewProjection * Matrix4x4.CreateScale(1f - Tolerance, 1f - Tolerance, 1f);
}
