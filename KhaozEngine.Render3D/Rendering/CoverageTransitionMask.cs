using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct CoverageChangedBounds(CoverageSurfaceBounds? Current, CoverageSurfaceBounds? Previous, bool NearPlaneUncertain);

/// <summary>Conservative local history rejection, rendered before temporal accumulation.</summary>
internal sealed class CoverageTransitionMask : IDisposable
{
    readonly IGpuDevice device;
    readonly List<CoverageChangedBounds> changes = new();
    readonly List<Rectangle> rectangles = new();
    IGpuTexture? texture;
    IGpuFramebuffer? framebuffer;
    IGpuShaderSet? shaders;
    IGpuPipeline? pipeline;
    internal IGpuTexture? Texture => texture;
    internal readonly record struct Rectangle(int X, int Y, int Right, int Bottom);
    internal ReadOnlySpan<Rectangle> Rectangles => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rectangles);

    internal CoverageTransitionMask(IGpuDevice device) => this.device = device;
    internal void Reserve(int count) { changes.EnsureCapacity(count); rectangles.EnsureCapacity(count); }
    internal void Clear() { changes.Clear(); rectangles.Clear(); }
    internal void AddChangedBounds(in CoverageChangedBounds bounds)
    {
        if (changes.Count == changes.Capacity) throw new InvalidOperationException("Prepare coverage mask capacity before recording.");
        changes.Add(bounds);
    }

    internal void EnsureSize(int width, int height, GpuRetireQueue? retired)
    {
        if (texture is { } existing && existing.Width == width && existing.Height == height) return;
        if (GpuRecording.OpenOwner(device) is { } owner)
            throw new InvalidOperationException($"Coverage mask growth requires size-aware PrepareFrame before {owner} opens.");
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        IGpuTexture? replacement = null;
        IGpuFramebuffer? target = null;
        IGpuShaderSet? program = null;
        IGpuPipeline? fill = null;
        try
        {
            replacement = device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
                GpuPixelFormat.R8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            target = device.Factory.CreateFramebuffer(null, replacement);
            if (pipeline is null)
            {
                program = device.Factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.CoverageTransitionFrag);
                fill = device.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
                {
                    BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                    DepthStencil = GpuDepthStencilState.Disabled,
                    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                        depthClipEnabled: false, scissorTestEnabled: true),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    ResourceLayouts = [],
                    ShaderSet = program,
                    VertexLayouts = [],
                    Outputs = target.Outputs,
                });
            }
        }
        catch
        {
            fill?.Dispose(); program?.Dispose(); target?.Dispose(); replacement?.Dispose();
            throw;
        }
        if (retired is null && texture is not null) device.WaitForIdle();
        Release(framebuffer, retired); Release(texture, retired);
        framebuffer = target; texture = replacement;
        pipeline ??= fill; shaders ??= program;
    }

    internal bool Matches(int width, int height) => texture is { } t && t.Width == width && t.Height == height;

    internal void Prepare(IGpuCommandList commands, in FrameView current)
    {
        if (!Matches(current.Width, current.Height))
            throw new InvalidOperationException("Call PrepareFrame(viewportWidth, viewportHeight) before recording covered temporal rendering.");
        rectangles.Clear();
        commands.SetFramebuffer(framebuffer!);
        commands.ClearColorTarget(0, Color.Transparent);
        foreach (var changed in changes)
        {
            Rectangle? rectangle = null;
            bool uncertain = changed.NearPlaneUncertain;
            Project(changed.Current, current, ref rectangle, ref uncertain);
            Project(changed.Previous, current, ref rectangle, ref uncertain);
            if (uncertain) rectangle = new(0, 0, current.Width, current.Height);
            if (rectangle is not { } r) continue;
            rectangles.Add(r);
            commands.SetPipeline(pipeline!);
            commands.SetScissorRect(0, (uint)r.X, (uint)r.Y, (uint)(r.Right - r.X), (uint)(r.Bottom - r.Y));
            commands.Draw(3);
        }
        commands.SetScissorRect(0, 0, 0, (uint)current.Width, (uint)current.Height);
    }

    static void Project(CoverageSurfaceBounds? bounds, in FrameView view, ref Rectangle? rectangle, ref bool uncertain)
    {
        if (bounds is not { } b) return;
        if (!b.Weight.IsFinite) { uncertain = true; return; }
        double minX = double.PositiveInfinity, minY = minX, minZ = minX, minW = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX, maxW = maxX;
        Matrix4x4 vp = view.ViewProjection;
        var ox = CoverageIntervals.Subtract(new(b.Origin.X, b.Origin.X), new(view.RenderOrigin.X, view.RenderOrigin.X));
        var oy = CoverageIntervals.Subtract(new(b.Origin.Y, b.Origin.Y), new(view.RenderOrigin.Y, view.RenderOrigin.Y));
        var oz = CoverageIntervals.Subtract(new(b.Origin.Z, b.Origin.Z), new(view.RenderOrigin.Z, view.RenderOrigin.Z));
        for (int bits = 0; bits < 16; bits++)
        {
            float px = (bits & 1) == 0 ? b.Relative.Min.X : b.Relative.Max.X;
            float py = (bits & 2) == 0 ? b.Relative.Min.Y : b.Relative.Max.Y;
            float pz = (bits & 4) == 0 ? b.Relative.Min.Z : b.Relative.Max.Z;
            double weight = (bits & 8) == 0 ? b.Weight.Min : b.Weight.Max;
            var w = new CoverageScalarBounds(weight, weight);
            var x = CoverageIntervals.Add(new(px, px), CoverageIntervals.Multiply(ox, w));
            var y = CoverageIntervals.Add(new(py, py), CoverageIntervals.Multiply(oy, w));
            var z = CoverageIntervals.Add(new(pz, pz), CoverageIntervals.Multiply(oz, w));
            var cx = Clip(x, y, z, w, vp.M11, vp.M21, vp.M31, vp.M41);
            var cy = Clip(x, y, z, w, vp.M12, vp.M22, vp.M32, vp.M42);
            var cz = Clip(x, y, z, w, vp.M13, vp.M23, vp.M33, vp.M43);
            var cw = Clip(x, y, z, w, vp.M14, vp.M24, vp.M34, vp.M44);
            if (!cx.IsFinite || !cy.IsFinite || !cz.IsFinite || !cw.IsFinite) { uncertain = true; return; }
            minZ = Math.Min(minZ, cz.Min); maxZ = Math.Max(maxZ, cz.Max);
            minW = Math.Min(minW, cw.Min); maxW = Math.Max(maxW, cw.Max);
            minX = Math.Min(minX, cx.Min); maxX = Math.Max(maxX, cx.Max);
            minY = Math.Min(minY, cy.Min); maxY = Math.Max(maxY, cy.Max);
        }
        if (maxW < -1e-6 || maxZ < -1e-6) return;
        if (minW <= 1e-6 || minZ <= 1e-6) { uncertain = true; return; }
        // Divide the entire clip intervals, preserving independence of per-coordinate shader rounding.
        var nx = CoverageIntervals.Divide(new(minX, maxX), new(minW, maxW));
        var ny = CoverageIntervals.Divide(new(minY, maxY), new(minW, maxW));
        minX = nx.Min; maxX = nx.Max; minY = ny.Min; maxY = ny.Max;
        int left = (int)Math.Clamp(Math.Floor((minX + 1) * .5 * view.Width) - 2, 0, view.Width);
        int right = (int)Math.Clamp(Math.Ceiling((maxX + 1) * .5 * view.Width) + 2, 0, view.Width);
        int top = (int)Math.Clamp(Math.Floor((1 - maxY) * .5 * view.Height) - 2, 0, view.Height);
        int bottom = (int)Math.Clamp(Math.Ceiling((1 - minY) * .5 * view.Height) + 2, 0, view.Height);
        if (left >= right || top >= bottom) return;
        rectangle = rectangle is { } prior ? new(Math.Min(left, prior.X), Math.Min(top, prior.Y),
            Math.Max(right, prior.Right), Math.Max(bottom, prior.Bottom)) : new(left, top, right, bottom);
    }

    static CoverageScalarBounds Clip(CoverageScalarBounds x, CoverageScalarBounds y, CoverageScalarBounds z,
        CoverageScalarBounds w, float a, float b, float c, float translation) => CoverageShaderPosition.FloatDot4(
            x, new(a, a), y, new(b, b), z, new(c, c), w, new(translation, translation));

    internal bool Rejects(int x, int y)
    {
        foreach (var r in rectangles) if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return true;
        return false;
    }

    static void Release(IDisposable? resource, GpuRetireQueue? retired)
    {
        if (resource is null) return;
        if (retired is null) resource.Dispose(); else retired.Retire(resource);
    }

    public void Dispose()
    {
        framebuffer?.Dispose(); texture?.Dispose(); pipeline?.Dispose(); shaders?.Dispose();
    }
}
