using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// One rendered frame's view, latched once per render by <c>Scene3D.LatchFrameView</c> immediately after the internal
/// size is final (Scene3D.FrameView.cs). It carries every matrix a frame reads twice. The unjittered matrices are what
/// the camera produced, for every CPU-side spatial computation and every pass drawn at display size after post. The
/// jittered matrices add this frame's sub-pixel offset (<see cref="TemporalJitter"/>), for everything rasterised into
/// the internal target. With a zero jitter the two are bit-identical.
/// <para>
/// Render-relative means expressed against <see cref="RenderOrigin"/>, like every GPU-bound position (see
/// Scene3D.RenderOrigin.cs). <see cref="AbsoluteViewProjection"/> is the one absolute matrix, for culling and the
/// shadow cascade fit against absolute bounds.
/// </para>
/// </summary>
internal readonly struct FrameView
{
    const float ProjectionKindEpsilon = 1e-5f;

    internal FrameView(in Matrix4x4 view, in Matrix4x4 projection, in Matrix4x4 viewProjection,
        in Matrix4x4 absoluteViewProjection, Vector3 renderOrigin, int width, int height, long frameIndex,
        Vector2 jitterPixels)
    {
        View = view;
        Projection = projection;
        ViewProjection = viewProjection;
        AbsoluteViewProjection = absoluteViewProjection;
        RenderOrigin = renderOrigin;
        Width = width;
        Height = height;
        FrameIndex = frameIndex;
        JitterPixels = jitterPixels;
        JitterClip = TemporalJitter.ClipOffset(jitterPixels, width, height);
        JitteredProjection = TemporalJitter.Apply(projection, jitterPixels, width, height);
        JitteredViewProjection = TemporalJitter.Apply(viewProjection, jitterPixels, width, height);
        IsOrthographic = MathF.Abs(projection.M34) <= ProjectionKindEpsilon
            && MathF.Abs(projection.M44) > ProjectionKindEpsilon;
    }

    /// <summary>The camera's view, unjittered and render-relative.</summary>
    public Matrix4x4 View { get; }

    /// <summary>The camera's projection, unjittered.</summary>
    public Matrix4x4 Projection { get; }

    /// <summary>The camera's view-projection, unjittered and render-relative.</summary>
    public Matrix4x4 ViewProjection { get; }

    /// <summary>The camera's view-projection against absolute world space, unjittered, for CPU spatial work.</summary>
    public Matrix4x4 AbsoluteViewProjection { get; }

    /// <summary><see cref="Projection"/> with this frame's jitter.</summary>
    public Matrix4x4 JitteredProjection { get; }

    /// <summary><see cref="ViewProjection"/> with this frame's jitter, render-relative: what the rasteriser uses.</summary>
    public Matrix4x4 JitteredViewProjection { get; }

    /// <summary>The jitter in internal pixels, x right and y down, each in <c>[-0.5, 0.5)</c>. Zero unless temporal
    /// rendering is active.</summary>
    public Vector2 JitterPixels { get; }

    /// <summary>The jitter as the clip-space translation <see cref="TemporalJitter.Apply"/> adds,
    /// <c>(2 * px / width, -2 * py / height)</c>.</summary>
    public Vector2 JitterClip { get; }

    /// <summary>The render origin every render-relative matrix is expressed against.</summary>
    public Vector3 RenderOrigin { get; }

    /// <summary>The internal render target width in pixels.</summary>
    public int Width { get; }

    /// <summary>The internal render target height in pixels.</summary>
    public int Height { get; }

    /// <summary>Whether the projection is orthographic, read from its <c>M34</c> and <c>M44</c>.</summary>
    public bool IsOrthographic { get; }

    /// <summary>The frame index, advanced once per <c>Scene3D.Begin</c>.</summary>
    public long FrameIndex { get; }

    /// <summary>
    /// This snapshot expressed against <paramref name="renderOrigin"/> instead of the origin it was latched against. A
    /// point <c>q</c> in the new render frame sits at <c>q + d</c> in the old one, with
    /// <c>d = renderOrigin - RenderOrigin</c>, so each render-relative matrix becomes <c>T(d) * M</c>. Engine render
    /// origins are whole multiples of the 128 m frame grid, so the step is exact in float32, and the product leaves rows
    /// 1 to 3 untouched, so the only rounding is in the translation row. The projection, the absolute matrix, the size,
    /// the index and the jitter do not depend on the origin and are kept. A zero step returns the snapshot unchanged,
    /// bit for bit.
    /// </summary>
    internal FrameView RebasedTo(Vector3 renderOrigin)
    {
        Vector3 step = renderOrigin - RenderOrigin;
        if (step == Vector3.Zero) return this;
        Matrix4x4 shift = Matrix4x4.CreateTranslation(step);
        return new FrameView(shift * View, Projection, shift * ViewProjection, AbsoluteViewProjection, renderOrigin,
            Width, Height, FrameIndex, JitterPixels);
    }
}
