using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalReadFootprintScalingTests(ITestOutputHelper output)
{
    // #1692. Thousands of distinct instance windows and aligned uniform slices must not rescan the frame.
    [Fact]
    public void AThreeThousandDrawFootprintHasSubquadraticRecordingAndAccessWork()
    {
        var small = FrameWork(1500);
        var large = FrameWork(3000);
        output.WriteLine($"1500 draws: recording {small.Record}, publication {small.Publish}, writes/maps {small.Access} probe steps");
        output.WriteLine($"3000 draws: recording {large.Record}, publication {large.Publish}, writes/maps {large.Access} probe steps");
        Assert.InRange(large.Record, 1, 3000 * 512);
        Assert.InRange(large.Access, 1, 3000 * 128);
        Assert.InRange(large.Publish, 1, 32 * 128);
        Assert.True(large.Record < small.Record * 3, "Doubling draws must cost less than quadratic recording work.");
        Assert.True(large.Access < small.Access * 3, "Doubling draws must cost less than quadratic access work.");
    }

    static (long Record, long Publish, long Access) FrameWork(int draws)
    {
        var reads = new MetalRecordedReads();
        var uniform = new MetalCpuResource();
        var material = new MetalCpuResource();
        var frame = new MetalCpuResource();
        var vertices = new MetalCpuResource();
        var instances = new MetalCpuResource();
        var indices = new MetalCpuResource();
        for (int draw = 0; draw < draws; draw++)
        {
            reads.Add(uniform, (ulong)draw * 256, 64);
            reads.Add(material, (ulong)(draw % 8) * 256, 32);
            reads.Add(frame, 0, 64);
            // Indexed vertices protect every reachable attribute. Instances use distinct draw windows.
            reads.AddStrided(vertices, 0, 12, 32, 1024);
            reads.AddStrided(vertices, 16, 8, 32, 1024);
            reads.AddStrided(instances, (ulong)draw * 384, 16, 96, 4);
            reads.AddStrided(instances, (ulong)draw * 384 + 32, 16, 96, 4);
            reads.Add(indices, 0, 2048);
        }
        long record = reads.ProbeStepsForTests;
        var protection = new MetalCpuReadProtection(new(), () => 0, () => 0);
        object owner = new();
        for (int i = 0; i < 32; i++) protection.MapWritable(owner, uniform, (ulong)i * 256 + 64, 16);
        protection.Protect(0, reads);
        long publish = reads.ProbeStepsForTests - record;
        for (int i = 0; i < 32; i++) protection.Unmap(owner);
        for (int draw = 0; draw < draws; draw++)
        {
            protection.RequireWritable(uniform, (ulong)draw * 256 + 64, 192);
            protection.MapWritable(owner, instances, (ulong)draw * 384 + 16, 16);
            protection.Unmap(owner);
        }
        return (record, publish, reads.ProbeStepsForTests - record - publish);
    }
}
