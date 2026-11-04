using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class MetalReadFootprintEquivalenceTests
{
    // #1696. Shader use and physical segments invalidate reads, encoder invalidation alone does not.
    [Fact]
    public void BindingCoverageTracksShaderUseDynamicOffsetsAndRecordingIdentity()
    {
        using var f = new MetalReadFootprintDrawFixture(4);
        var table = f.Pipeline.Table;
        var layouts = new GpuResourceLayoutDescription[table.Layouts.Count];
        for (int i = 0; i < layouts.Length; i++) layouts[i] = table.Layouts[i];
        var frameOnly = MetalShaderIndexTable.Build(layouts,
            [new(MetalShaderStage.Vertex, [new(0, 0)])], "read coverage");
        var binds = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
        var reads = new MetalRecordedReads();
        var set = MetalBindProgram.Set(new MetalBoundResource(MetalIndexSpace.Buffer, f.Uniform, 0, 64, true),
            new(MetalIndexSpace.Buffer, f.Material, 0, 32, false));
        binds.SetIndexTable(frameOnly);
        binds.Record(0, set, 0);
        binds.RecordReads(reads, 0);
        Assert.True(reads.Overlaps(new(f.Uniform.CpuResource, 0, 64)));
        Assert.False(reads.Overlaps(new(f.Material.CpuResource, 0, 32)));
        binds.SetIndexTable(table);
        binds.RecordReads(reads, 0);
        Assert.True(reads.Overlaps(new(f.Material.CpuResource, 0, 32)));
        binds.Record(0, set, 256);
        binds.RecordReads(reads, 0);
        Assert.True(reads.Overlaps(new(f.Uniform.CpuResource, 256, 64)));
        Assert.False(reads.Overlaps(new(f.Uniform.CpuResource, 64, 192)));
        Assert.False(reads.Overlaps(new(f.Material.CpuResource, 256, 32)));
        long probes = reads.ProbeStepsForTests;
        binds.InvalidateAll();
        binds.RecordReads(reads, 0);
        Assert.Equal(probes, reads.ProbeStepsForTests);
        binds.RecordReads(reads, 1);
        ulong frameBase = f.Uniform.Ring!.SegmentBaseBytes(1);
        Assert.True(reads.Overlaps(new(f.Uniform.CpuResource, frameBase + 256, 64)));
        reads.Clear();
        binds.RecordReads(reads, 1);
        Assert.True(reads.Overlaps(new(f.Uniform.CpuResource, frameBase + 256, 64)));
        Assert.False(reads.Overlaps(new(f.Uniform.CpuResource, 0, 64)));
        var other = new MetalRecordedReads();
        binds.RecordReads(other, 0);
        Assert.True(other.Overlaps(new(f.Material.CpuResource, 0, 32)));
    }

    // #1696. Layout, buffer and divisor changes must invalidate the corresponding coverage.
    [Fact]
    public void LayoutBindingAndStepRateChangesKeepExactVertexCoverage()
    {
        using var h = new MetalRingHarness();
        var a = h.NewBuffer(256, GpuBufferUsage.VertexBuffer);
        var b = h.NewBuffer(256, GpuBufferUsage.VertexBuffer);
        var resources = new[] { a.CpuResource, b.CpuResource };
        var reads = new MetalRecordedReads();
        var vertices = new MetalVertexReadRecords();
        var layout = new MetalVertexReadLayout(
            [new(30, 32, MTLVertexStepFunction.PerVertex, 1), new(29, 32, MTLVertexStepFunction.PerInstance, 2)],
            [new(0, 30, GpuVertexElementFormat.Float2, 0), new(1, 29, GpuVertexElementFormat.Float1, 4)]);
        var changed = new MetalVertexReadLayout(
            [new(30, 24, MTLVertexStepFunction.PerVertex, 1), new(29, 32, MTLVertexStepFunction.PerInstance, 3)],
            [new(0, 30, GpuVertexElementFormat.Float3, 4), new(1, 29, GpuVertexElementFormat.Float1, 4)]);
        var oracle = new List<Read>();
        void Check()
        {
            Assert.NotEmpty(oracle);
            for (int resource = 0; resource < resources.Length; resource++)
                for (ulong offset = 0; offset < 272; offset++)
                {
                    var access = new Read(resource, offset, 5, 0, 1);
                    Assert.Equal(Overlaps(oracle, access), reads.Overlaps(new(resources[resource], offset, 5)));
                }
        }
        vertices.Record(0, a, 8);
        vertices.Record(1, a, 0);
        vertices.RecordReads(reads, layout, 2, 2, 1, 3, indexed: false);
        oracle.Add(new(0, 72, 8, 32, 2));
        oracle.Add(new(0, 4, 4, 32, 2));
        Check();
        vertices.RecordReads(reads, layout, 2, 3, 3, 4, indexed: false);
        oracle.Add(new(0, 72, 8, 32, 3));
        oracle.Add(new(0, 36, 4, 32, 3));
        Check();
        vertices.Record(0, b, 16);
        vertices.RecordReads(reads, changed, 0, 3, 6, 3, indexed: true);
        oracle.Add(new(1, 20, 12, 24, 10));
        oracle.Add(new(0, 68, 4, 32, 1));
        Check();
        vertices.Record(0, b, 0);
        vertices.RecordReads(reads, layout, 0, 3, 0, 1, indexed: true);
        oracle.Add(new(1, 0, 8, 32, 8));
        Check();
        // Reclaiming the same footprint must invalidate coverage even when bindings and layout stay put.
        reads.Clear();
        oracle.Clear();
        vertices.RecordReads(reads, layout, 0, 3, 0, 1, indexed: false);
        oracle.Add(new(1, 0, 8, 32, 3));
        oracle.Add(new(0, 4, 4, 32, 1));
        Check();
    }

    // #1696. Cache hits, joined windows and changed bindings must preserve the same byte union at a split.
    [Fact]
    public void RepeatedAndChangedDrawStateMatchesTheByteOracleAcrossAMidPassPrefix()
    {
        using var f = new MetalReadFootprintDrawFixture(16);
        f.List.EarlySubmission = new(new FakeMetalEarlyFrameApi(), f.Harness.SubmitLock);
        f.Begin();
        var resources = new[] { f.Vertices.CpuResource, f.Instances.CpuResource, f.Uniform.CpuResource,
            f.Material.CpuResource, f.Indices.CpuResource };
        ulong uniformBase = f.Uniform.Ring!.SegmentBaseBytes(f.List.RingSegment);
        ulong materialBase = f.Material.Ring!.SegmentBaseBytes(f.List.RingSegment);
        var oracle = new List<Read>();
        var random = new Random(0x1696);
        uint meshOffset = 0;
        var set = f.Set;
        // Empty draws must not claim coverage that a later nonempty draw needs to record.
        f.List.DrawIndexed(1024, 0, 0, 0, 0);
        Assert.False(f.List.ReadOverlapsForTests(new(resources[0], 0, 1)));
        for (uint draw = 0; draw < 8; draw++)
        {
            if (draw == 4) { meshOffset = 24; f.List.SetVertexBuffer(0, f.Vertices, meshOffset); }
            if (draw == 5)
                set = MetalBindProgram.Set(new MetalBoundResource(MetalIndexSpace.Buffer, f.Uniform, 128, 64, true),
                    new(MetalIndexSpace.Buffer, f.Material, 64, 32, false));
            uint firstInstance = draw < 2 ? 0 : draw * 4;
            uint dynamicOffset = draw < 2 ? 0 : draw * 256;
            f.List.GraphicsBinds.Record(0, set, dynamicOffset);
            f.List.DrawIndexed(1024, 4, 0, 0, firstInstance);
            for (uint attribute = 0; attribute < 6; attribute++)
            {
                ulong offset = meshOffset + attribute * 12;
                oracle.Add(new(0, offset, 12, 96, (32768 - offset - 12) / 96 + 1));
            }
            oracle.Add(new(1, firstInstance * 96, 32, 96, 4));
            oracle.Add(new(2, uniformBase + dynamicOffset + (draw >= 5 ? 128ul : 0), 64, 0, 1));
            oracle.Add(new(3, materialBase + (draw >= 5 ? 64ul : dynamicOffset), 32, 0, 1));
            oracle.Add(new(4, 0, 2048, 0, 1));
            for (int access = 0; access < 128; access++)
            {
                var probe = Access(random, resources.Length);
                Assert.Equal(Overlaps(oracle, probe),
                    f.List.ReadOverlapsForTests(new(resources[probe.Resource], probe.Offset, probe.Bytes)));
            }
        }

        object owner = new();
        f.Harness.Rings.CpuReads.MapWritable(owner, resources[1], 28 * 96, 16);
        Assert.Equal(MetalFrameAccessFailure.MappedPrefix,
            Assert.Throws<MetalFrameAccessException>(() => f.List.ProtectPrefix()).Reason);
        f.Harness.Rings.CpuReads.Unmap(owner);
        MetalSwapchainFramebuffer? drawable = null;
        drawable = new(GpuPixelFormat.R8G8B8A8UNorm, new(16, 16),
            () => drawable!.Adopt(new(new(0x777), GpuPixelFormat.R8G8B8A8UNorm), new(16, 16)));
        f.List.SetFramebuffer(drawable);
        long probes = f.List.ReadProbeStepsForTests, writes = f.List.ReadNodeWritesForTests;
        f.Draw(12);
        Assert.Equal(probes, f.List.ReadProbeStepsForTests);
        Assert.Equal(writes, f.List.ReadNodeWritesForTests);
        int refused = 0, allowed = 0;
        for (int i = 0; i < 512; i++)
        {
            var access = Access(random, resources.Length);
            bool overlap = Overlaps(oracle, access);
            var error = Record.Exception(() => f.Harness.Rings.CpuReads.RequireWritable(
                resources[access.Resource], access.Offset, access.Bytes));
            if (overlap) { Assert.IsType<MetalFrameAccessException>(error); refused++; }
            else { Assert.Null(error); allowed++; }
            var mapError = Record.Exception(() => f.Harness.Rings.CpuReads.MapWritable(
                owner, resources[access.Resource], access.Offset, access.Bytes));
            if (overlap) Assert.Equal(MetalFrameAccessFailure.WritableMapping,
                Assert.IsType<MetalFrameAccessException>(mapError).Reason);
            else Assert.Null(mapError);
            f.Harness.Rings.CpuReads.Unmap(owner);
        }
        Assert.True(refused > 0 && allowed > 0);
        f.List.End();
        ulong final = f.Harness.Submit(f.List);
        f.Harness.Event.Completed = final;
        foreach (var resource in resources) f.Harness.Rings.CpuReads.RequireWritable(resource, 0, 65536);
    }
}
