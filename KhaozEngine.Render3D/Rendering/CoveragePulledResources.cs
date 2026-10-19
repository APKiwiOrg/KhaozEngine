using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct CoverageGpuSurfaceView(IGpuBuffer Current, IGpuBuffer Previous, IGpuBuffer StaticData,
    uint CurrentBase, uint PreviousBase, uint StaticBase, int VertexCount, int TriangleCount);

internal readonly record struct CoveragePulledDraw(CoverageGpuSurfaceView Surface, int FirstCorner, int CornerCount,
    uint InstanceSlot, MotionKey Actor, PointShadowCasterSphere Sphere, bool VisibleMain, ShadowCastKind ShadowKind);

/// <summary>Cold bindings and independent draw windows over the selected GPU surface.</summary>
internal sealed class CoveragePulledResources : IDisposable
{
    const uint SlotBytes = 256;
    readonly IGpuDevice device;
    readonly Action<IDisposable> retire;
    IGpuBuffer? uniforms, current, staticData;
    IGpuResourceSet? set;
    uint[] image = [];
    internal IGpuResourceLayout Layout { get; }

    internal CoveragePulledResources(IGpuDevice device, Action<IDisposable> retire)
    {
        this.device = device; this.retire = retire;
        Layout = device.Factory.CreateResourceLayout(new(
            new GpuResourceLayoutElement("CoverageCurrent", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("CoverageStatic", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("CoverageDraw", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
    }

    internal void Reserve(CoverageGpuBatch batch, int slots)
    {
        int words = checked(Math.Max(1, slots) * (int)(SlotBytes / 4));
        bool grow = image.Length < words;
        if (!grow && ReferenceEquals(current, batch.Current) && ReferenceEquals(staticData, batch.StaticData)) return;
        IGpuBuffer next = grow ? device.Factory.CreateBuffer(new(checked((uint)words * 4), GpuBufferUsage.UniformBuffer)) : uniforms!;
        IGpuResourceSet nextSet;
        try { nextSet = device.Factory.CreateResourceSet(new(Layout, batch.Current, batch.StaticData, new GpuBufferRange(next, 0, 16))); }
        catch { if (grow) next.Dispose(); throw; }
        if (set is not null) retire(set);
        if (grow)
        {
            if (uniforms is not null) retire(uniforms);
            image = new uint[words]; uniforms = next;
        }
        set = nextSet; current = batch.Current; staticData = batch.StaticData;
    }

    internal void Pack(uint slot, in CoveragePulledDraw draw)
    {
        if (!ReferenceEquals(current, draw.Surface.Current) || !ReferenceEquals(staticData, draw.Surface.StaticData))
            throw new InvalidOperationException("Coverage pulling buffers were not reserved before recording.");
        int start = checked((int)slot * (int)(SlotBytes / 4));
        image[start] = draw.Surface.CurrentBase; image[start + 1] = draw.Surface.StaticBase;
        image[start + 2] = checked((uint)draw.FirstCorner); image[start + 3] = draw.Surface.PreviousBase;
    }

    internal void Upload(IGpuCommandList commands) => commands.UpdateBuffer(uniforms!, 0, image);
    internal void Bind(IGpuCommandList commands, uint setIndex, uint slot)
        => commands.SetGraphicsResourceSet(setIndex, set!, checked(slot * SlotBytes));

    public void Dispose() { set?.Dispose(); uniforms?.Dispose(); Layout.Dispose(); }
}
