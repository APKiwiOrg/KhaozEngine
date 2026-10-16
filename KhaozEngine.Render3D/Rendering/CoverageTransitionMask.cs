using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct CoverageChangedBounds(MeshBounds? Current, MeshBounds? Previous, bool NearPlaneUncertain);

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

    static void Project(MeshBounds? bounds, in FrameView view, ref Rectangle? rectangle, ref bool uncertain)
    {
        if (bounds is not { } b) return;
        double minX = double.PositiveInfinity, minY = minX, minZ = minX, minW = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX, maxW = maxX;
        Matrix4x4 vp = view.ViewProjection;
        for (int bits = 0; bits < 8; bits++)
        {
            double x = (double)((bits & 1) == 0 ? b.Min.X : b.Max.X) - view.RenderOrigin.X;
            double y = (double)((bits & 2) == 0 ? b.Min.Y : b.Max.Y) - view.RenderOrigin.Y;
            double z = (double)((bits & 4) == 0 ? b.Min.Z : b.Max.Z) - view.RenderOrigin.Z;
            double cx = x * vp.M11 + y * vp.M21 + z * vp.M31 + vp.M41;
            double cy = x * vp.M12 + y * vp.M22 + z * vp.M32 + vp.M42;
            double cz = x * vp.M13 + y * vp.M23 + z * vp.M33 + vp.M43;
            double cw = x * vp.M14 + y * vp.M24 + z * vp.M34 + vp.M44;
            if (!double.IsFinite(cx + cy + cz + cw)) { uncertain = true; return; }
            minZ = Math.Min(minZ, cz); maxZ = Math.Max(maxZ, cz);
            minW = Math.Min(minW, cw); maxW = Math.Max(maxW, cw);
            minX = Math.Min(minX, cx / cw); maxX = Math.Max(maxX, cx / cw);
            minY = Math.Min(minY, cy / cw); maxY = Math.Max(maxY, cy / cw);
        }
        if (maxW < -1e-6 || maxZ < -1e-6) return;
        if (minW <= 1e-6 || minZ <= 1e-6) { uncertain = true; return; }
        int left = (int)Math.Clamp(Math.Floor((minX + 1) * .5 * view.Width) - 2, 0, view.Width);
        int right = (int)Math.Clamp(Math.Ceiling((maxX + 1) * .5 * view.Width) + 2, 0, view.Width);
        int top = (int)Math.Clamp(Math.Floor((1 - maxY) * .5 * view.Height) - 2, 0, view.Height);
        int bottom = (int)Math.Clamp(Math.Ceiling((1 - minY) * .5 * view.Height) + 2, 0, view.Height);
        if (left >= right || top >= bottom) return;
        rectangle = rectangle is { } prior ? new(Math.Min(left, prior.X), Math.Min(top, prior.Y),
            Math.Max(right, prior.Right), Math.Max(bottom, prior.Bottom)) : new(left, top, right, bottom);
    }

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
