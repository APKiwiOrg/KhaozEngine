using System;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Separate known-input storage gives the original pose the production graphics precision policy.
internal sealed class CoverageOriginalPoseControl : IDisposable
{
    readonly IGpuBuffer current, staticData;
    readonly IGpuResourceSet cascade, point;
    readonly uint[] currentWords, staticWords;

    internal CoverageOriginalPoseControl(IGpuDevice device, CoverageGpuBatch batch,
        CoveragePullingTestScene.Actor[] actors, CoveragePullingBindingCapture bindings)
    {
        currentWords = new uint[batch.Current.SizeInBytes / 4];
        staticWords = new uint[batch.StaticData.SizeInBytes / 4];
        foreach (var actor in actors)
        {
            var r = actor.Frame.Preparation!.GpuReservation;
            currentWords[r.CurrentBase + 14] = 1;
            MemoryMarshal.Cast<ModelVertex, uint>(actor.Posed).CopyTo(currentWords.AsSpan((int)r.CurrentBase + 16));
            staticWords[r.StaticBase] = (uint)r.SourceCount;
            staticWords[r.StaticBase + 50] = 64;
            // Original triangles are contiguous in each material. Unused refined corners are degenerate.
            // This control does not reuse the production first-descendant fallback table.
            for (int slice = 0; slice < actor.Topology.MaterialRanges.Count; slice++)
            {
                var range = actor.Topology.MaterialRanges[slice];
                var originals = actor.Topology.Source.Triangles.Where(t => t.MaterialSlice == slice).ToArray();
                int start = checked((int)r.StaticBase + 64 + range.FirstIndex);
                Array.Fill(staticWords, (uint)originals[0].A, start, range.IndexCount);
                for (int t = 0; t < originals.Length; t++)
                {
                    staticWords[start + t * 3] = (uint)originals[t].A;
                    staticWords[start + t * 3 + 1] = (uint)originals[t].B;
                    staticWords[start + t * 3 + 2] = (uint)originals[t].C;
                }
            }
        }
        current = device.Factory.CreateBuffer(new(batch.Current.SizeInBytes, GpuBufferUsage.StructuredBufferReadOnly, 4));
        staticData = device.Factory.CreateBuffer(new(batch.StaticData.SizeInBytes, GpuBufferUsage.StructuredBufferReadOnly, 4));
        var tracker = (UniformBufferTrackingGpuDevice)device;
        cascade = Set("cascade"); point = Set("point");
        IGpuResourceSet Set(string pass)
        {
            var draw = bindings.Draws.First(d => d.Pass == pass);
            Assert.NotSame(batch.Current, current); Assert.NotSame(batch.StaticData, staticData);
            return device.Factory.CreateResourceSet(new(tracker.ResourcesOf(draw.Set).Layout,
                current, staticData, new GpuBufferRange(draw.Uniform, draw.RangeOffset, draw.RangeBytes), current));
        }
    }

    internal void Upload(IGpuCommandList commands)
    {
        commands.UpdateBuffer(current, 0, currentWords);
        commands.UpdateBuffer(staticData, 0, staticWords);
    }

    internal void Bind(IGpuCommandList commands, bool isCascade, uint offset)
        => commands.SetGraphicsResourceSet(isCascade ? 1u : 2u, isCascade ? cascade : point, offset);

    public void Dispose() { point.Dispose(); cascade.Dispose(); staticData.Dispose(); current.Dispose(); }
}
