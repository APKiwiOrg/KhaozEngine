using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;

namespace KhaozEngine.Tests.Gpu;

// Seeds typed continuations at the actual dispatch boundary. No shader result or certificate is forged.
internal sealed class CoverageFilteredQueueFrame : IDisposable
{
    readonly IGpuDevice device;
    readonly CoverageGpuTestSession session;
    readonly IGpuBuffer observed;
    readonly List<IGpuBuffer> inputs = [];
    readonly uint[][] headers;
    internal readonly int[][] Active;

    internal CoverageFilteredQueueFrame(IGpuDevice device, CoverageGpuTestSession session, CoverageReferenceCase[] cases, bool failed)
    {
        this.device = device; this.session = session;
        Active = cases.Select(CoverageFilteredQueueFixture.Faces).ToArray();
        headers = cases.Select(c => CoverageGpuLayout.WorkHeader(CoverageGpuUpload.Host(c.Topology, 2) with
        { FilterSupportCapacity = session.Batch.MaximumFilterSupportCount == 0 ? 0 : 4 })).ToArray();
        observed = device.Factory.CreateBuffer(new(session.Batch.Work.SizeInBytes, GpuBufferUsage.Staging));
        var beforeFaces = new List<(IGpuBuffer Input, IGpuBuffer Target, uint Offset, uint Bytes)>();
        var copies = new List<(IGpuBuffer Input, IGpuBuffer Target, uint Offset, uint Bytes)>();
        for (int actor = 0; actor < cases.Length; actor++)
        {
            // A point at each region's sheet origin makes every height query dyadic and certifiable.
            // These are stage inputs. The overlay below gets separate nondegenerate typed triangles.
            var vertices = new uint[cases[actor].Topology.VertexCount * 16];
            for (int v = 0; v < vertices.Length / 16; v++)
            {
                vertices[v * 16] = cases[actor].Topology.Vertices[v].RegionIndex == 1 ? 0x40800000u : 0u;
                vertices[v * 16 + 2] = vertices[v * 16 + 5] = vertices[v * 16 + 15] = 0x3f800000u;
            }
            Add(vertices, session.Batch.Work, (session.Batch.Actors[actor].WorkBase + headers[actor][6]) * 4, beforeFaces);
            for (int slot = 0; slot < cases[actor].Commands.Length; slot++)
                Add(CoverageGpuUpload.Support(CoverageFilteredQueueFixture.FlatSupport()).Words,
                    session.Batch.StaticData, session.SupportOffset(actor, slot) * 4, beforeFaces);
            uint[] faces = new uint[cases[actor].Topology.TriangleCount * 12]; Array.Fill(faces, 0x7fc00001u);
            for (int t = 0; t < faces.Length / 12; t++) faces[t * 12 + 11] = 0;
            foreach (int t in Active[actor])
            {
                var host = CoverageFilteredQueueFixture.Host;
                for (int k = 0; k < 3; k++)
                {
                    faces[t * 12 + k * 3] = BitConverter.SingleToUInt32Bits(host[k].X);
                    faces[t * 12 + k * 3 + 1] = BitConverter.SingleToUInt32Bits(host[k].Y);
                    faces[t * 12 + k * 3 + 2] = BitConverter.SingleToUInt32Bits(host[k].Z);
                }
                faces[t * 12 + 9] = 0; faces[t * 12 + 10] = 0; faces[t * 12 + 11] = 1;
            }
            Add(faces, session.Batch.Work, (session.Batch.Actors[actor].WorkBase + headers[actor][36]) * 4);
            for (int slot = 0; slot < cases[actor].Commands.Length; slot++)
                Add(CoverageGpuUpload.Support(CoverageFilteredQueueFixture.Support(failed)).Words, session.Batch.StaticData, session.SupportOffset(actor, slot) * 4);
        }
        session.Batch.DispatchObserverForTests = (commands, phase, entering) =>
        {
            if (phase == CoverageGpuPhase.CorrectFaces && entering)
                foreach (var copy in beforeFaces) commands.CopyBuffer(copy.Input, 0, copy.Target, copy.Offset, copy.Bytes);
            if (phase != CoverageGpuPhase.CorrectFacesOverlay) return;
            if (entering)
                foreach (var copy in copies) commands.CopyBuffer(copy.Input, 0, copy.Target, copy.Offset, copy.Bytes);
            else commands.CopyBuffer(session.Batch.Work, 0, observed, 0, observed.SizeInBytes);
        };
        void Add(uint[] words, IGpuBuffer target, uint offset, List<(IGpuBuffer Input, IGpuBuffer Target, uint Offset, uint Bytes)>? destination = null)
        {
            var buffer = device.Factory.CreateBuffer(new((uint)words.Length * 4, GpuBufferUsage.StructuredBufferReadOnly, 4));
            inputs.Add(buffer); device.UpdateBuffer(buffer, 0, words); (destination ?? copies).Add((buffer, target, offset, (uint)words.Length * 4));
        }
    }

    internal unsafe uint[] Read()
    {
        var map = device.Map(observed, GpuMapMode.Read);
        try { return new ReadOnlySpan<uint>((void*)map.Data, (int)observed.SizeInBytes / 4).ToArray(); }
        finally { device.Unmap(observed); }
    }

    internal uint[] Row(uint[] words, int actor, int row, int stride) => words.AsSpan(
        (int)(session.Batch.Actors[actor].WorkBase + headers[actor][row]), (int)headers[actor][row + 1] * stride).ToArray();

    internal uint[] Header(uint[] words, int actor) => words.AsSpan((int)session.Batch.Actors[actor].WorkBase, 64).ToArray();

    public void Dispose()
    {
        session.Batch.DispatchObserverForTests = null;
        foreach (var buffer in inputs) buffer.Dispose();
        observed.Dispose();
    }
}
