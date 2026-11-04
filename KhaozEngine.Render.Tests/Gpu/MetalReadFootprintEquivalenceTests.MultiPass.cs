using System;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class MetalReadFootprintEquivalenceTests
{
    // #1696 round 3. A byte bitmap independently tracks multi-pass reads, padding and skipped elements.
    [Fact]
    public void SeededMultiPassDrawsPreserveExactWritesMappingsAndAtomicPrefixRefusal()
    {
        using var fixture = new MetalReadFootprintMultiPassFixture();
        var f = fixture.Draws;
        f.Begin();
        MetalCpuResource[] resources = [f.Meshes[0].CpuResource, f.Meshes[1].CpuResource,
            f.Meshes[2].CpuResource, f.Meshes[3].CpuResource, f.InstanceStreams[0].CpuResource,
            f.InstanceStreams[1].CpuResource, f.InstanceStreams[2].CpuResource,
            f.Uniform.CpuResource, f.Material.CpuResource, f.Indices.CpuResource];
        int uniformBase = (int)f.Uniform.Ring!.SegmentBaseBytes(f.List.RingSegment);
        int materialBase = (int)f.Material.Ring!.SegmentBaseBytes(f.List.RingSegment);
        var bytes = new bool[resources.Length][];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = new bool[i < 4 ? 32768 : i < 7 ? 262144
                : i == 7 ? uniformBase + (int)f.Uniform.SizeInBytes
                : i == 8 ? materialBase + (int)f.Material.SizeInBytes : 2048];
        var meshRecorded = new bool[4];
        var random = new Random(0x169603);
        uint sequence = 0;
        int checks = 0;
        for (int pass = 0; pass < 11; pass++)
        {
            for (uint draw = 0; draw < MetalReadFootprintMultiPassFixture.PassDraws(pass); draw++)
            {
                var (family, window, mesh) = MetalReadFootprintMultiPassFixture.Window(pass, draw);
                fixture.Draw(pass, draw, sequence);
                if (!meshRecorded[mesh])
                {
                    for (int attribute = 0; attribute < 6; attribute++)
                        MarkStrided((int)mesh, attribute * 12, 12, 96, 32768);
                    meshRecorded[mesh] = true;
                }
                // The previous-position stream aliases mesh 3 but has a distinct stride and attribute shape.
                if (sequence == 0) MarkStrided(3, 0, 12, 32, 32768);
                for (int element = 0; element < 4; element++) Mark((int)family + 4, (int)(window + element) * 96, 32);
                Mark(7, uniformBase + (int)sequence * 512, 64);
                Mark(8, materialBase + (int)sequence * 512, 256);
                Mark(9, 0, 2048);
                sequence++;
            }
            // Check the growing footprint, so stale cache hits cannot be hidden by a later pass.
            for (int query = 0; query < 128; query++)
            {
                var (resource, offset, length) = Access();
                Assert.Equal(Expected(resource, offset, length),
                    f.List.ReadOverlapsForTests(new(resources[resource], (ulong)offset, (ulong)length)));
                checks++;
            }
        }
        Assert.Equal(MetalReadFootprintMultiPassFixture.DrawCount, (int)sequence);
        Assert.Equal(1408, checks);
        Assert.All(meshRecorded, Assert.True);
        // This buffer ends 32 bytes into a vertex. Only its first two complete 12-byte attributes fit.
        Assert.True(f.List.ReadOverlapsForTests(new(resources[0], 32748, 12)));
        Assert.False(f.List.ReadOverlapsForTests(new(resources[0], 32760, 8)));

        object owner = new();
        var protection = f.Harness.Rings.CpuReads;
        // Both attribute padding and entire skipped instance elements remain writable.
        protection.MapWritable(owner, resources[4], 32, 64);
        protection.MapWritable(owner, resources[4], 40 * 96, 192);
        protection.MapWritable(owner, resources[4], 0, 1);
        Assert.Equal(MetalFrameAccessFailure.MappedPrefix,
            Assert.Throws<MetalFrameAccessException>(() => f.List.ProtectPrefix()).Reason);
        protection.RequireWritable(resources[4], 0, 1); // Failed publication installed no protection.
        protection.Unmap(owner);
        f.List.ProtectPrefix();
        protection.Unmap(owner);
        protection.Unmap(owner);
        int allowed = 0, refused = 0;
        for (int query = 0; query < 1024; query++)
        {
            var (resource, offset, length) = Access();
            bool overlap = Expected(resource, offset, length);
            var write = Record.Exception(() => protection.RequireWritable(resources[resource], (ulong)offset, (ulong)length));
            var map = Record.Exception(() => protection.MapWritable(owner, resources[resource], (ulong)offset, (ulong)length));
            if (overlap)
            {
                Assert.Equal(MetalFrameAccessFailure.OverlappingWrite, Assert.IsType<MetalFrameAccessException>(write).Reason);
                Assert.Equal(MetalFrameAccessFailure.WritableMapping, Assert.IsType<MetalFrameAccessException>(map).Reason);
                refused++;
            }
            else { Assert.Null(write); Assert.Null(map); allowed++; }
            protection.Unmap(owner);
            protection.Map(owner, resources[resource], (ulong)offset, (ulong)length, writable: false);
            protection.Unmap(owner);
        }
        Assert.True(allowed > 0 && refused > 0);
        f.List.End();
        ulong final = f.Harness.Submit(f.List);
        f.Harness.Event.Completed = final;
        for (int i = 0; i < resources.Length; i++) protection.RequireWritable(resources[i], 0, (ulong)bytes[i].Length);

        void Mark(int resource, int offset, int length) => Array.Fill(bytes[resource], true, offset, length);
        void MarkStrided(int resource, int offset, int length, int stride, int limit)
        {
            for (int start = offset; start + length <= limit; start += stride) Mark(resource, start, length);
        }
        bool Expected(int resource, int offset, int length)
        {
            for (int i = offset; i < offset + length && i < bytes[resource].Length; i++)
                if (bytes[resource][i]) return true;
            return false;
        }
        (int Resource, int Offset, int Length) Access()
        {
            int resource = random.Next(resources.Length);
            int offset = resource < 4 ? random.Next(32769) : resource < 7 ? random.Next(900 * 96)
                : resource < 9 ? (resource == 7 ? uniformBase : materialBase)
                    + random.Next((int)sequence) * 512 + random.Next(513) : random.Next(2049);
            return (resource, offset, random.Next(65));
        }
    }
}
