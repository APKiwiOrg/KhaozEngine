using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class MetalReadFootprintEquivalenceTests
{
    // #1696 round 2. Append runs and fallback reads must expose the same union to every CPU access path.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshRingSlicesAndInterleavedResourcesMatchTheByteOracle(bool outOfOrder)
    {
        var resources = new MetalCpuResource[] { new(), new(), new() };
        var reads = new MetalRecordedReads();
        var cursors = new MetalRecordedReads.Cursor[resources.Length];
        var oracle = new List<Read>();
        var random = new Random(0x1696);
        for (int draw = 0; draw < 3000; draw++)
        {
            // Two fresh aligned uniform slices and consecutive, padded instance windows.
            Add(new(0, (ulong)draw * 512, 64, 0, 1));
            Add(new(1, (ulong)draw * 512, 256, 0, 1));
            Add(new(2, (ulong)draw * 384, 32, 96, 4));
            if (outOfOrder && draw % 127 == 0)
            {
                // Reach back into a gap, then bridge append and fallback coverage on one resource.
                Add(new(0, (ulong)(draw / 2) * 512 + 128, 32, 0, 1));
                Add(new(0, (ulong)(draw / 2) * 512 + 48, 96, 0, 1));
                Add(new(2, (ulong)(draw / 2) * 384 + 48, 8, 96, 2));
            }
            var probe = new Read(random.Next(3), (ulong)random.Next(draw + 1) * 512
                + (ulong)random.Next(513), (ulong)random.Next(65), 0, 1);
            Assert.Equal(Overlaps(oracle, probe), reads.Overlaps(new(resources[probe.Resource], probe.Offset, probe.Bytes)));
        }
        Assert.True(oracle.Count >= 9000);
        ulong completed = 0;
        var protection = new MetalCpuReadProtection(new(), () => completed, () => 0);
        object owner = new();
        // An open gap lease allows publication. An actual read refuses it, atomically.
        protection.MapWritable(owner, resources[1], 256, 256);
        protection.MapWritable(owner, resources[0], 0, 1);
        Assert.Equal(MetalFrameAccessFailure.MappedPrefix,
            Assert.Throws<MetalFrameAccessException>(() => protection.Protect(0, reads)).Reason);
        protection.Unmap(owner);
        ulong frame = protection.Protect(0, reads);
        protection.Unmap(owner);
        int refused = 0, allowed = 0;
        for (int i = 0; i < 512; i++)
        {
            var access = new Read(i % 3, (ulong)random.Next(3000) * 512 + (ulong)random.Next(513),
                (ulong)random.Next(65), 0, 1);
            bool overlap = Overlaps(oracle, access);
            var write = Record.Exception(() => protection.RequireWritable(resources[access.Resource], access.Offset, access.Bytes));
            var map = Record.Exception(() => protection.MapWritable(owner, resources[access.Resource], access.Offset, access.Bytes));
            if (overlap)
            {
                Assert.Equal(MetalFrameAccessFailure.OverlappingWrite, Assert.IsType<MetalFrameAccessException>(write).Reason);
                Assert.Equal(MetalFrameAccessFailure.WritableMapping, Assert.IsType<MetalFrameAccessException>(map).Reason);
                refused++;
            }
            else { Assert.Null(write); Assert.Null(map); allowed++; }
            protection.Unmap(owner);
        }
        Assert.True(refused > 0 && allowed > 0);
        protection.Submitted(frame, 1);
        completed = 1;
        foreach (var resource in resources) protection.RequireWritable(resource, 0, 3000 * 512);
        reads.Clear();
        foreach (var resource in resources) Assert.False(reads.Overlaps(new(resource, 0, 3000 * 512)));

        void Add(Read read)
        {
            oracle.Add(read);
            if (read.Stride == 0) reads.Add(resources[read.Resource], read.Offset, read.Bytes, ref cursors[read.Resource]);
            else reads.AddStrided(resources[read.Resource], read.Offset, read.Bytes, read.Stride, read.Count, ref cursors[read.Resource]);
        }
    }
}
