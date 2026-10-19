using System;
using System.Numerics;
using System.Runtime.InteropServices;
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
    IGpuBuffer? uniforms, current, previous, staticData;
    IGpuResourceSet? set, reverseSet;
    bool reversed;
    uint[] image = [];
    internal IGpuResourceLayout Layout { get; }

    internal CoveragePulledResources(IGpuDevice device, Action<IDisposable> retire)
    {
        this.device = device; this.retire = retire;
        Layout = device.Factory.CreateResourceLayout(new(
            new GpuResourceLayoutElement("CoverageCurrent", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("CoverageStatic", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("CoverageDraw", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true),
            new GpuResourceLayoutElement("CoveragePrevious", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
    }

    internal void Reserve(CoverageGpuBatch batch, int slots)
    {
        int words = checked(Math.Max(1, slots) * (int)(SlotBytes / 4));
        bool grow = image.Length < words;
        if (!grow && ReferenceEquals(staticData, batch.StaticData)
            && (ReferenceEquals(current, batch.Current) || ReferenceEquals(previous, batch.Current)))
        { reversed = ReferenceEquals(previous, batch.Current); return; }
        IGpuBuffer next = grow ? device.Factory.CreateBuffer(new(checked((uint)words * 4), GpuBufferUsage.UniformBuffer)) : uniforms!;
        IGpuResourceSet nextSet;
        IGpuResourceSet? nextReverse = null;
        try
        {
            nextReverse = device.Factory.CreateResourceSet(new(Layout, batch.Previous, batch.StaticData, new GpuBufferRange(next, 0, 80), batch.Current));
            nextSet = device.Factory.CreateResourceSet(new(Layout, batch.Current, batch.StaticData, new GpuBufferRange(next, 0, 80), batch.Previous));
        }
        catch { nextReverse?.Dispose(); if (grow) next.Dispose(); throw; }
        if (set is not null) retire(set);
        if (reverseSet is not null) retire(reverseSet);
        if (grow)
        {
            if (uniforms is not null) retire(uniforms);
            image = new uint[words]; uniforms = next;
        }
        set = nextSet; reverseSet = nextReverse; current = batch.Current; previous = batch.Previous;
        staticData = batch.StaticData; reversed = false;
    }

    internal void Pack(uint slot, in CoveragePulledDraw draw)
    {
        if (!ReferenceEquals(reversed ? previous : current, draw.Surface.Current) || !ReferenceEquals(staticData, draw.Surface.StaticData))
            throw new InvalidOperationException("Coverage pulling buffers were not reserved before recording.");
        int start = checked((int)slot * (int)(SlotBytes / 4));
        image[start] = draw.Surface.CurrentBase; image[start + 1] = draw.Surface.StaticBase;
        image[start + 2] = checked((uint)draw.FirstCorner); image[start + 3] = draw.Surface.PreviousBase;
    }

    internal void UploadPreviousModel(IGpuCommandList commands, uint slot, in Matrix4x4 model)
    {
        int start = checked((int)slot * (int)(SlotBytes / 4));
        MemoryMarshal.Cast<Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(in model, 1)).CopyTo(image.AsSpan(start + 4));
        commands.UpdateBuffer(uniforms!, checked(slot * SlotBytes), image.AsSpan(start, 20));
    }

    internal void Upload(IGpuCommandList commands) => commands.UpdateBuffer(uniforms!, 0, image);
    internal void Bind(IGpuCommandList commands, uint setIndex, uint slot)
        => commands.SetGraphicsResourceSet(setIndex, (reversed ? reverseSet : set)!, checked(slot * SlotBytes));

    public void Dispose() { set?.Dispose(); reverseSet?.Dispose(); uniforms?.Dispose(); Layout.Dispose(); }
}
