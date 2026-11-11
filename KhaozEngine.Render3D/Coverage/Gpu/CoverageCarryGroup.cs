using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Immutable shared topology, representatives and all table rows. Actors supply only frame words.</summary>
internal sealed class CoverageCarryGroup : IDisposable
{
    internal readonly CoverageRecessionTable Table;
    internal readonly CoverageRefinement Topology;
    internal readonly int Bones, FrameStride;
    internal readonly IGpuBuffer Static, TableBuffer, Indices;
    internal IGpuBuffer Frames = null!;
    internal IGpuResourceSet[] Sets = [];
    internal uint[] FrameWords = [];
    internal int Active;
    readonly IGpuDevice device;
    internal CoverageCarryGroup(IGpuDevice device, SkinnedHostCoverage owner)
    {
        this.device = device; Table = owner.Binding.Table; Topology = owner.Topology;
        Bones = owner.InverseBind.Length; FrameStride = CoverageCarryLayout.FrameWords(Bones);
        IGpuBuffer? host = null, table = null, indices = null;
        try
        {
            uint[] words = CoverageCarryUpload.Host(Topology, Bones);
            host = ReadOnly(words);
            table = ReadOnly(CoverageCarryUpload.Table(Table));
            indices = ReadOnly(Topology.Indices);
            Static = host; TableBuffer = table; Indices = indices;
        }
        catch { indices?.Dispose(); table?.Dispose(); host?.Dispose(); throw; }
    }
    IGpuBuffer ReadOnly(ReadOnlySpan<uint> words)
    {
        var buffer = device.Factory.CreateBuffer(new(CoverageCarryLayout.Bytes(words.Length),
            GpuBufferUsage.StructuredBufferReadOnly, 4));
        try { device.UpdateBuffer(buffer, 0, words); return buffer; }
        catch { buffer.Dispose(); throw; }
    }
    internal void Reserve(int actors, CoverageCarrySurface storage, IGpuResourceLayout[] layouts, GpuRetireQueue retired)
        => Adopt(Allocate(actors, storage, layouts), retired);

    internal CoverageCarryGroupResources Allocate(int actors, CoverageCarrySurface storage, IGpuResourceLayout[] layouts)
    {
        uint[] words = new uint[checked(5 + actors + FrameStride * actors)];
        words[1] = (uint)CoverageCarryLayout.Round64(Topology.SourceVertexCount);
        words[2] = (uint)CoverageCarryLayout.Round64(Topology.PositionGroupCount);
        words[3] = (uint)CoverageCarryLayout.Round64(Topology.VertexCount);
        words[4] = (uint)CoverageCarryLayout.Round64(Topology.TriangleCount);
        var sets = new IGpuResourceSet[CoverageCarryShaders.StageCount];
        var frames = device.Factory.CreateBuffer(new(CoverageCarryLayout.Bytes(words.Length),
            GpuBufferUsage.StructuredBufferReadOnly, 4));
        try
        {
            for (int stage = 0; stage < sets.Length; stage++)
            {
                IGpuBindableResource[] common = [Static, frames, storage.Work, storage.Results];
                IGpuBindableResource[] resources = stage switch
                { 1 => [.. common, TableBuffer], 4 => [.. common, storage.Surface], _ => common };
                sets[stage] = device.Factory.CreateResourceSet(new(layouts[stage], resources));
            }
        }
        catch { foreach (var set in sets) set?.Dispose(); frames.Dispose(); throw; }
        return new(frames, sets, words);
    }
    internal void Adopt(CoverageCarryGroupResources resources, GpuRetireQueue retired)
    {
        foreach (var set in Sets) retired.Retire(set);
        retired.Retire(Frames);
        Frames = resources.Frames; Sets = resources.Sets; FrameWords = resources.Words;
    }
    internal void Begin() { Active = 0; }
    internal void Add(CoverageCarryActor actor)
    {
        int capacity = (FrameWords.Length - 5) / (FrameStride + 1);
        int offset = 5 + capacity + Active * FrameStride;
        FrameWords[5 + Active] = (uint)offset;
        actor.Frame.CopyTo(FrameWords, offset);
        Active++; FrameWords[0] = (uint)Active;
    }
    internal uint Groups(int stage) => checked(FrameWords[stage == 0 ? 1 : stage == 2 ? 4 : 3] * (uint)Active / 64);
    public void Dispose()
    {
        foreach (var set in Sets) set.Dispose();
        Frames?.Dispose(); Indices.Dispose(); TableBuffer.Dispose(); Static.Dispose();
    }
}

internal sealed record CoverageCarryGroupResources(IGpuBuffer Frames, IGpuResourceSet[] Sets, uint[] Words) : IDisposable
{
    public void Dispose() { foreach (var set in Sets) set.Dispose(); Frames.Dispose(); }
}
