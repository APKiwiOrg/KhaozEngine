using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Research-only retained compute storage. No Scene3D, production outcome or drawing resource is exposed.</summary>
internal sealed class CoverageCarryDevice : IDisposable
{
    readonly IGpuDevice device;
    readonly CoverageCarryFixture fixture;
    readonly IGpuResourceLayout[] layouts;
    readonly IGpuResourceSet[] sets;
    readonly IGpuComputeShader[] shaders;
    readonly IGpuComputePipeline[] pipelines;
    readonly IGpuBuffer[] buffers;
    readonly uint[] frameWords, resultReset, header;
    readonly int frameStride, workStride, surfaceStride;
    int resultSlot;
    internal CoverageCarryInputs[] Actors { get; }
    internal int Count => Actors.Length;
    internal long UploadBytes => (frameWords.Length + resultReset.Length) * 4L;
    internal long ScratchBytes => buffers[2].SizeInBytes;
    internal long PersistentBytes => buffers.Where((_, i) => i != 2).Sum(b => (long)b.SizeInBytes);
    internal long AnchorBytes => buffers[6].SizeInBytes;
    internal uint[] Groups { get; }

    internal CoverageCarryDevice(IGpuDevice device, CoverageCarryFixture fixture, int count)
    {
        this.device = device; this.fixture = fixture;
        if (count is not (1 or 32)) throw new ArgumentOutOfRangeException(nameof(count));
        Actors = Enumerable.Range(0, count).Select(i => new CoverageCarryInputs(fixture, i)).ToArray();
        var t = fixture.Topology;
        var host = CoverageGpuUpload.Host(t, 2);
        frameStride = (int)CoverageGpuLayout.FrameWords(2);
        frameWords = new uint[5 + count + frameStride * count];
        frameWords[0] = (uint)count;
        int[] extents = [t.SourceVertexCount, t.PositionGroupCount, t.VertexCount, t.TriangleCount];
        for (int i = 0; i < extents.Length; i++) frameWords[i + 1] = (uint)((extents[i] + 63) / 64 * 64);
        Groups = [frameWords[1] * (uint)count / 64, frameWords[3] * (uint)count / 64,
            frameWords[4] * (uint)count / 64, frameWords[3] * (uint)count / 64];
        header = new uint[64];
        int cursor = 64;
        Row(0, t.SourceVertexCount, 16); header[2] = header[0]; header[3] = header[1];
        Row(4, t.VertexCount, 16); Row(8, t.VertexCount, 16); Row(10, t.VertexCount, 16);
        Row(42, t.TriangleCount, 46);
        workStride = cursor;
        surfaceStride = t.VertexCount * 16;
        var anchors = new uint[t.PositionGroupCount * 2 * count];
        resultReset = new uint[count * 8];
        for (int actor = 0; actor < count; actor++)
        {
            frameWords[5 + actor] = (uint)(5 + count + frameStride * actor);
            for (int g = 0; g < t.PositionGroupCount; g++)
            {
                int at = (actor * t.PositionGroupCount + g) * 2;
                anchors[at] = BitConverter.SingleToUInt32Bits(Actors[actor].Anchor.Recession[g]);
                anchors[at + 1] = unchecked((uint)Actors[actor].Anchor.Representatives[g]);
            }
            for (int axis = 0; axis < 3; axis++) resultReset[actor * 8 + axis] = uint.MaxValue;
        }
        buffers = [Storage(host.Words.Length, false), Storage(frameWords.Length, false), Storage(workStride * count, true),
            Storage(surfaceStride * count, true), Storage(surfaceStride * count, true), Storage(resultReset.Length * 3, true), Storage(anchors.Length, false)];
        device.UpdateBuffer(buffers[0], 0, host.Words);
        device.UpdateBuffer(buffers[6], 0, anchors);
        for (int actor = 0; actor < count; actor++) device.UpdateBuffer(buffers[2], (uint)(workStride * actor * 4), header);
        var zero = new uint[surfaceStride * count];
        device.UpdateBuffer(buffers[3], 0, zero); device.UpdateBuffer(buffers[4], 0, zero);
        var kernels = CoverageCarryShaders.Kernels().ToArray();
        layouts = new IGpuResourceLayout[kernels.Length]; sets = new IGpuResourceSet[kernels.Length];
        shaders = new IGpuComputeShader[kernels.Length]; pipelines = new IGpuComputePipeline[kernels.Length];
        try
        {
            for (int i = 0; i < kernels.Length; i++)
            {
                var options = new GpuShaderCompileOptions(PreciseMath: i != 4);
                var description = SpirvCrossCompile.GlslComputeToHlsl(kernels[i].ComputeGlsl, kernels[i].Name, options)
                    .Reflection.ResourceLayouts.Single();
                layouts[i] = device.Factory.CreateResourceLayout(description);
                // Ordinary compilation may strip names. The carry ABI binds these buffers positionally.
                sets[i] = device.Factory.CreateResourceSet(new(layouts[i],
                    buffers.Take(description.Elements.Length).Cast<IGpuBindableResource>().ToArray()));
                shaders[i] = device.Factory.CreateComputeShaderFromSpirv(kernels[i].ComputeGlsl, options);
                pipelines[i] = device.Factory.CreateComputePipeline(new(shaders[i], layouts[i]));
            }
        }
        catch { Dispose(); throw; }
        return;

        void Row(int pair, int count, int stride)
        { header[pair] = (uint)cursor; header[pair + 1] = (uint)count; cursor += count * stride; }

    }

    IGpuBuffer Storage(int words, bool writable) => device.Factory.CreateBuffer(new((uint)words * 4,
        writable ? GpuBufferUsage.StructuredBufferReadWrite : GpuBufferUsage.StructuredBufferReadOnly, 4));

    internal void Prepare(int frame, float weight = 1, bool rotate = false)
    {
        resultSlot = frame % 3;
        for (int actor = 0; actor < Count; actor++)
        {
            Actors[actor].Prepare(frame, weight, rotate);
            Actors[actor].Write(frameWords.AsSpan((int)frameWords[5 + actor], frameStride), (uint)(workStride * actor),
                (uint)(surfaceStride * actor), (uint)((resultSlot * Count + actor) * 8), (uint)(actor * fixture.Topology.PositionGroupCount * 2), frame + 1);
        }
    }

    internal void Record(IGpuCommandList commands, bool ordinary = false)
    {
        commands.UpdateBuffer<uint>(buffers[1], 0, frameWords);
        commands.UpdateBuffer<uint>(buffers[5], (uint)(resultSlot * resultReset.Length * 4), resultReset);
        int start = ordinary ? 4 : 0, end = ordinary ? 5 : 4;
        for (int i = start; i < end; i++)
        {
            commands.SetComputePipeline(pipelines[i]); commands.SetComputeResourceSet(0, sets[i]);
            commands.Dispatch(i == 4 ? Groups[0] : Groups[i], 1, 1);
        }
    }

    internal void PoisonScratch()
    {
        var poison = new uint[workStride * Count];
        Array.Fill(poison, 0x7fc01234u);
        for (int actor = 0; actor < Count; actor++) header.CopyTo(poison, actor * workStride);
        device.UpdateBuffer(buffers[2], 0, poison);
    }

    internal uint[] Read(int buffer)
    {
        using var staging = device.Factory.CreateBuffer(new(buffers[buffer].SizeInBytes, GpuBufferUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        commands.Begin(); commands.CopyBuffer(buffers[buffer], 0, staging, 0, staging.SizeInBytes); commands.End();
        device.Submit(commands); device.WaitForIdle();
        var result = new uint[staging.SizeInBytes / 4];
        unsafe
        {
            var map = device.Map(staging, GpuMapMode.Read);
            try { new ReadOnlySpan<uint>((void*)map.Data, result.Length).CopyTo(result); }
            finally { device.Unmap(staging); }
        }
        return result;
    }

    internal uint[] Results(uint[] words, int actor, int frame) => words.AsSpan(((frame % 3) * Count + actor) * 8, 8).ToArray();

    internal uint[] Surface(uint[] words, int actor) => words.AsSpan(actor * surfaceStride, surfaceStride).ToArray();
    internal uint[] Before(uint[] words, int actor) => words.AsSpan(actor * workStride + (int)header[4], surfaceStride).ToArray();

    public void Dispose()
    {
        foreach (var pipeline in pipelines) pipeline?.Dispose();
        foreach (var shader in shaders) shader?.Dispose();
        foreach (var set in sets) set?.Dispose();
        foreach (var layout in layouts) layout?.Dispose();
        foreach (var buffer in buffers) buffer.Dispose();
    }
}
