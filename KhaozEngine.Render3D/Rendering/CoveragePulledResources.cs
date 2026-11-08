using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct CoveragePulledDraw(CoverageSurfaceRange Surface, int FirstCorner, int CornerCount,
    uint InstanceSlot, MotionKey Actor, PointShadowCasterSphere Sphere, bool VisibleMain, ShadowCastKind ShadowKind);

/// <summary>Pass observations contain immutable values, never borrowed GPU resource owners.</summary>
internal readonly record struct CoverageDrawEvidence(MotionKey Actor, int FirstCorner, int CornerCount, bool VisibleMain)
{
    internal CoverageDrawEvidence(in CoveragePulledDraw draw)
        : this(draw.Actor, draw.FirstCorner, draw.CornerCount, draw.VisibleMain) { }
}

/// <summary>Cold bindings over batch-owned surfaces. Every material keeps its own dynamic draw window.</summary>
internal sealed class CoveragePulledResources : IDisposable
{
    const uint SlotBytes = 256;
    readonly IGpuDevice device;
    readonly Action<IDisposable> retire;
    IGpuBuffer? uniforms;
    IGpuResourceSet?[] sets = [];
    CoverageSurfaceRange[] surfaces = [];
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

    internal void Reserve(int count)
    {
        if (sets.Length >= count) return;
        count = Math.Max(count, Math.Max(8, sets.Length * 2));
        var next = device.Factory.CreateBuffer(new(checked((uint)count * SlotBytes), GpuBufferUsage.UniformBuffer));
        foreach (var set in sets) if (set is not null) retire(set);
        if (uniforms is not null) retire(uniforms);
        uniforms = next; sets = new IGpuResourceSet[count]; surfaces = new CoverageSurfaceRange[count];
        image = new uint[checked(count * (int)(SlotBytes / 4))];
    }

    internal void ReserveSurface(int slot, in CoverageSurfaceRange surface)
    {
        var old = surfaces[slot];
        if (sets[slot] is not null && ReferenceEquals(old.Current, surface.Current)
            && ReferenceEquals(old.Previous, surface.Previous) && ReferenceEquals(old.Indices, surface.Indices)) return;
        var next = device.Factory.CreateResourceSet(new(Layout, surface.Current, surface.Indices,
            new GpuBufferRange(uniforms!, 0, 80), surface.Previous));
        if (sets[slot] is { } previous) retire(previous);
        sets[slot] = next; surfaces[slot] = surface;
    }

    internal void Pack(uint slot, in CoveragePulledDraw draw)
    {
        var surface = surfaces[slot];
        if (sets[slot] is null || !ReferenceEquals(surface.Current, draw.Surface.Current)
            || !ReferenceEquals(surface.Previous, draw.Surface.Previous) || !ReferenceEquals(surface.Indices, draw.Surface.Indices))
            throw new InvalidOperationException("Coverage pulling buffers must be reserved before recording.");
        int start = checked((int)slot * (int)(SlotBytes / 4));
        image[start] = draw.Surface.CurrentBase;
        image[start + 1] = draw.Surface.CurrentBase == draw.Surface.PreviousBase && ReferenceEquals(draw.Surface.Current, draw.Surface.Previous) ? 1u : 0u;
        image[start + 2] = checked((uint)draw.FirstCorner); image[start + 3] = draw.Surface.PreviousBase;
    }

    internal void UploadPreviousModel(IGpuCommandList commands, uint slot, in Matrix4x4 model)
    {
        int start = checked((int)slot * (int)(SlotBytes / 4));
        MemoryMarshal.Cast<Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(in model, 1)).CopyTo(image.AsSpan(start + 4));
        commands.UpdateBuffer(uniforms!, checked(slot * SlotBytes), image.AsSpan(start, 20));
    }
    internal void Upload(IGpuCommandList commands) => commands.UpdateBuffer<uint>(uniforms!, 0, image);
    internal void Bind(IGpuCommandList commands, uint setIndex, uint slot)
        => commands.SetGraphicsResourceSet(setIndex, sets[slot]!, checked(slot * SlotBytes));
    public void Dispose()
    {
        foreach (var set in sets) set?.Dispose();
        uniforms?.Dispose(); Layout.Dispose();
    }
}
