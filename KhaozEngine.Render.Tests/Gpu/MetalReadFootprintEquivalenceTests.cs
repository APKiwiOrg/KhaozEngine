using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalReadFootprintEquivalenceTests
{
    // #1692. Indexing must preserve the old union of actual attribute bytes, never its bounding spans.
    [Fact]
    public void SeededMixedReadsWritesAndMappingsMatchABruteForceByteOracle()
    {
        var random = new Random(0x1692);
        var resources = new MetalCpuResource[] { new(), new(), new(), new() };
        int readCount = 0, accessCount = 0, prefixRefusals = 0;
        var reads = new MetalRecordedReads();
        for (int round = 0; round < 32; round++)
        {
            reads.Clear();
            var oracle = new List<Read>();
            for (int i = 0; i < 256; i++)
            {
                var read = new Read(random.Next(resources.Length), (ulong)random.Next(8192),
                    (ulong)random.Next(33), (ulong)random.Next(129), (ulong)random.Next(17));
                if (i % 3 == 0)
                {
                    read = read with { Stride = 0, Count = 1 };
                    reads.Add(resources[read.Resource], read.Offset, read.Bytes);
                }
                else reads.AddStrided(resources[read.Resource], read.Offset, read.Bytes, read.Stride, read.Count);
                oracle.Add(read);
                readCount++;
                // Query while the index is still growing, including zero-length accesses.
                var access = Access(random, resources.Length);
                Assert.Equal(Overlaps(oracle, access), reads.Overlaps(new(resources[access.Resource], access.Offset, access.Bytes)));
                accessCount++;
            }

            ulong completed = 0;
            var protection = new MetalCpuReadProtection(new(), () => completed, () => 0);
            var leases = new List<(object Owner, Read Access, bool Writable)>();
            for (int i = 0; i < 8; i++)
            {
                var access = Access(random, resources.Length);
                object owner = new();
                bool writable = i % 2 == 0;
                protection.Map(owner, resources[access.Resource], access.Offset, access.Bytes, writable);
                leases.Add((owner, access, writable));
            }
            bool mappedOverlap = leases.Exists(lease => lease.Writable && Overlaps(oracle, lease.Access));
            ulong frameId = 0;
            var prefixError = Record.Exception(() => frameId = protection.Protect(0, reads));
            if (mappedOverlap)
            {
                var refusal = Assert.IsType<MetalFrameAccessException>(prefixError);
                Assert.Equal(MetalFrameAccessFailure.MappedPrefix, refusal.Reason);
                Assert.Contains(leases, lease => lease.Writable && Overlaps(oracle, lease.Access)
                    && resources[lease.Access.Resource].Id == refusal.ResourceId
                    && lease.Access.Offset == refusal.OffsetBytes && lease.Access.Bytes == refusal.LengthBytes);
                prefixRefusals++;
            }
            else Assert.Null(prefixError);
            foreach (var lease in leases) protection.Unmap(lease.Owner);
            if (mappedOverlap) frameId = protection.Protect(0, reads);

            for (int i = 0; i < 512; i++)
            {
                var access = Access(random, resources.Length);
                bool expected = Overlaps(oracle, access);
                object owner = new();
                bool mapping = i % 2 == 0;
                var error = Record.Exception(() =>
                {
                    if (mapping) protection.MapWritable(owner, resources[access.Resource], access.Offset, access.Bytes);
                    else protection.RequireWritable(resources[access.Resource], access.Offset, access.Bytes);
                });
                if (expected)
                {
                    var refusal = Assert.IsType<MetalFrameAccessException>(error);
                    Assert.Equal(mapping ? MetalFrameAccessFailure.WritableMapping : MetalFrameAccessFailure.OverlappingWrite, refusal.Reason);
                    Assert.Equal(resources[access.Resource].Id, refusal.ResourceId);
                    Assert.Equal(access.Offset, refusal.OffsetBytes);
                    Assert.Equal(access.Bytes, refusal.LengthBytes);
                    Assert.Equal(frameId, refusal.FrameId);
                }
                else Assert.Null(error);
                protection.Unmap(owner);
                // Read maps remain legal even over protected bytes.
                protection.Map(owner, resources[access.Resource], access.Offset, access.Bytes, writable: false);
                protection.Unmap(owner);
                accessCount++;
            }
            protection.Submitted(frameId, 1);
            completed = 1;
            foreach (var resource in resources) protection.RequireWritable(resource, 0, 16384);
        }
        Assert.Equal(8192, readCount);
        Assert.Equal(24576, accessCount);
        Assert.InRange(prefixRefusals, 1, 32);
    }

    readonly record struct Read(int Resource, ulong Offset, ulong Bytes, ulong Stride, ulong Count);

    static Read Access(Random random, int resources)
        => new(random.Next(resources), (ulong)random.Next(12288), (ulong)random.Next(65), 0, 1);

    static bool Overlaps(List<Read> reads, Read access)
    {
        if (access.Bytes == 0) return false;
        foreach (var read in reads)
        {
            if (read.Resource != access.Resource || read.Bytes == 0 || read.Count == 0) continue;
            // Deliberately enumerate elements rather than using MetalCpuRead's stride arithmetic.
            ulong count = read.Stride == 0 ? 1 : read.Count;
            for (ulong element = 0; element < count; element++)
            {
                ulong start = read.Offset + element * read.Stride;
                if (start < access.Offset + access.Bytes && access.Offset < start + read.Bytes) return true;
            }
        }
        return false;
    }
}
