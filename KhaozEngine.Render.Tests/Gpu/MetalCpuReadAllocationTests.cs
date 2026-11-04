using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class MetalCpuReadAllocationTests(ITestOutputHelper output)
{
    // #1696. Per-change state and continuation hints must retain their capacity across warmed recordings.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyBindingAndWindowChangesAllocateNothing(bool freshSlices)
    {
        using var f = new MetalReadFootprintDrawFixture(freshSlices: freshSlices);
        var reads = new MetalRecordedReads();
        var binds = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
        var vertices = new MetalVertexReadRecords();
        void Frame()
        {
            reads.Clear();
            binds.Reset();
            binds.SetIndexTable(f.Pipeline.Table);
            vertices.Reset();
            vertices.Record(0, f.Vertices, 0);
            vertices.Record(1, f.Instances, 0);
            for (uint draw = 0; draw < 3000; draw++)
            {
                if (freshSlices)
                {
                    vertices.Record(0, f.Meshes[draw / 750 % (uint)f.Meshes.Length], 0);
                    binds.Record(1, f.StaticSets[draw / 500 % (uint)f.StaticSets.Length], 0);
                }
                binds.Record(0, f.Set, draw * (freshSlices ? 512u : 256u));
                binds.RecordReads(reads, 0);
                vertices.RecordReads(reads, f.Pipeline.Plan.ReadLayout, 0, 1024, draw * 4, 4, indexed: true);
            }
        }
        Frame();
        Frame();
        long bytes = AllocationMeasurement.Measure("3000 per-change draw reads", Frame);
        output.WriteLine($"3000 per-change draw reads: {bytes} bytes");
        Assert.Equal(0, bytes);
        Assert.True(reads.Overlaps(new(f.Instances.CpuResource, 11999 * 96, 32)));
        Assert.False(reads.Overlaps(new(f.Instances.CpuResource, 11999 * 96 + 32, 64)));
    }

    // #1692. Both interval indexes must reuse large, irregular recordings, not only tiny prefixes.
    [Fact]
    public void ASteadyThreeThousandDrawFootprintReusesItsIndexes()
    {
        var reads = new MetalRecordedReads();
        var resource = new MetalCpuResource();
        ulong completed = 0;
        var protection = new MetalCpuReadProtection(new(), () => completed, () => completed);
        object owner = new();
        void Frame()
        {
            protection.RequireSlotAvailable(0);
            reads.Clear();
            for (uint draw = 0; draw < 3000; draw++)
            {
                // A permutation exercises insertion and rotations away from the append path.
                ulong offset = draw * 1999 % 3000 * 256;
                reads.Add(resource, offset, 32);
                reads.AddStrided(resource, offset + 64, 8, 32, 4);
            }
            // A late contiguous transfer joins many nodes and recycles their storage next frame.
            reads.Add(resource, 0, 1024);
            ulong frame = protection.Protect(0, reads);
            protection.MapWritable(owner, resource, 3000 * 256, 64);
            protection.Unmap(owner);
            protection.Submitted(frame, ++completed);
        }
        Frame();
        Frame();
        long bytes = AllocationMeasurement.Measure("one steady 3000-draw footprint", Frame);
        output.WriteLine($"One steady 3000-draw footprint: {bytes} bytes");
        Assert.Equal(0, bytes);
        Assert.True(reads.Overlaps(new(resource, 2999 * 256, 1)));
        Assert.False(reads.Overlaps(new(resource, 2999 * 256 + 32, 32)));
    }

    // #1685. Alternating main and capture footprints must not shuffle differently sized range lists.
    [Fact]
    public void AlternatingSteadyFootprintsReuseTheirRangeStorage()
    {
        var reads = new MetalRecordedReads();
        var resources = new MetalCpuResource[21];
        for (int i = 0; i < resources.Length; i++) resources[i] = new();
        void Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
                for (int capture = 0; capture < 2; capture++)
                {
                    reads.Clear();
                    for (int i = 0; i < resources.Length - capture * 2; i++)
                        for (int range = 0; range < (i == 7 ? 12 : i == 10 ? 6 : 1); range++)
                            reads.AddStrided(resources[i], (ulong)range * 8, 4, 128, 2);
                }
        }
        Frames(8);
        long bytes = AllocationMeasurement.Measure("16 alternating main and capture footprints", () => Frames(16));
        output.WriteLine($"16 alternating main and capture footprints: {bytes} bytes");
        Assert.Equal(0, bytes);
        Assert.True(reads.Overlaps(new(resources[7], 88, 4)));
        Assert.False(reads.Overlaps(new(resources[7], 92, 4)));
    }

    // #1685. Final completion can lag by the whole ring, rather than retiring each prefix immediately.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SteadyPrefixesReuseProtectionStorageUntilFinalCompletion(int depth)
    {
        ulong completed = 0, submitted = 0;
        var protection = new MetalCpuReadProtection(new(), () => completed, () => submitted);
        var recordings = new MetalRecordingReads(protection, depth);
        var resource = new MetalCpuResource();
        void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (submitted >= (ulong)depth) completed = submitted - (ulong)depth + 1;
                int segment = (int)(submitted % (ulong)depth);
                protection.RequireSlotAvailable(segment);
                recordings.Begin(segment);
                var reads = recordings.Current;
                reads.Add(resource, (ulong)segment * 64, 16);
                ulong id = protection.Protect(segment, reads);
                protection.Committed(id);
                protection.RequireWritable(resource, (ulong)depth * 64, 16);
                protection.Submitted(id, ++submitted);
            }
        }
        Frames(8);
        long bytes = AllocationMeasurement.Measure("16 steady protected prefixes", () => Frames(16));
        output.WriteLine($"16 steady protected prefixes at depth {depth}: {bytes} bytes");
        Assert.Equal(0, bytes);
        Assert.Throws<KhaozEngine.Gpu.Metal.MetalFrameAccessException>(
            () => protection.RequireWritable(resource, (submitted - 1) % (ulong)depth * 64, 16));
        completed = submitted;
        protection.RequireWritable(resource, 0, (ulong)depth * 64);
    }

    // #1685. Readback and writable map leases are returned on Unmap, including nested maps.
    [Fact]
    public void RepeatedMappingsReuseTheirLeaseStorage()
    {
        var protection = new MetalCpuReadProtection(new(), () => 0, () => 0);
        var resource = new MetalCpuResource();
        object owner = new();
        void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                protection.MapWritable(owner, resource, 0, 16);
                protection.Map(owner, resource, 0, 16, writable: false);
                protection.Unmap(owner);
                protection.Unmap(owner);
            }
        }
        Frames(2);
        long bytes = AllocationMeasurement.Measure("16 repeated nested mappings", () => Frames(16));
        output.WriteLine($"16 repeated nested mappings: {bytes} bytes");
        Assert.Equal(0, bytes);
        var reads = new MetalRecordedReads();
        reads.Add(resource, 0, 16);
        protection.Protect(0, reads);
        Assert.Throws<KhaozEngine.Gpu.Metal.MetalFrameAccessException>(
            () => protection.MapWritable(owner, resource, 0, 16));
    }
}
