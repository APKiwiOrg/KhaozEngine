using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalReadFootprintPerChangeTests(ITestOutputHelper output)
{
    // #1696. Repeated mesh/bind state must not make index work grow with earlier instance windows.
    [Fact]
    public void ASteadyDrawRecordsOnlyChangedUniformSlicesAndInstanceWindows()
    {
        using var f = new MetalReadFootprintDrawFixture();
        f.Begin();
        f.Draw(0);
        long before = f.List.ReadProbeStepsForTests;
        long writesBefore = f.List.ReadNodeWritesForTests;
        for (uint draw = 1; draw < 3000; draw++) f.Draw(draw);
        long steps = f.List.ReadProbeStepsForTests - before;
        long writes = f.List.ReadNodeWritesForTests - writesBefore;
        output.WriteLine($"2999 steady draws: {steps} index probes, {steps / 2999.0:F2} per draw, {writes} node mutations");
        Assert.InRange(steps, 1, 2999 * 32);
        Assert.InRange(writes, 1, 2999 * 48);
        f.List.End();
    }

    // #1696. A duplicate deep in the tree is a lookup, with no ancestor rewrites or rebalancing.
    [Fact]
    public void DuplicateStridedReadsDoNotWriteAnyIndexNode()
    {
        var index = new MetalReadIntervalIndex();
        var resource = new MetalCpuResource();
        for (ulong i = 0; i < 127; i++) index.Add(new(resource, i * 128, 8, 32, 3));
        long before = index.NodeWritesForTests;
        for (ulong i = 0; i < 127; i++) index.Add(new(resource, i * 128, 8, 32, 3));
        Assert.Equal(before, index.NodeWritesForTests);
        Assert.True(index.Overlaps(new(resource, 126 * 128 + 64, 8)));
        Assert.False(index.Overlaps(new(resource, 126 * 128 + 72, 8)));
    }

    // #1696. Repeating a draw without changing its read state must do no index work.
    [Fact]
    public void ADrawWithUnchangedStateDoesNoIndexWork()
    {
        using var f = new MetalReadFootprintDrawFixture();
        f.Begin();
        f.Draw(0);
        long probes = f.List.ReadProbeStepsForTests, writes = f.List.ReadNodeWritesForTests;
        f.Draw(0);
        Assert.Equal(probes, f.List.ReadProbeStepsForTests);
        Assert.Equal(writes, f.List.ReadNodeWritesForTests);
        f.List.End();
    }

    // #1696. Extending a read must never replace earlier coverage with an overflowing bounding span.
    [Fact]
    public void AnOverflowingStridedWindowCannotEraseEarlierReads()
    {
        var reads = new MetalRecordedReads();
        var resource = new MetalCpuResource();
        reads.AddStrided(resource, ulong.MaxValue - 255, 8, 96, 2);
        reads.AddStrided(resource, ulong.MaxValue - 255, 8, 96, 4);
        Assert.True(reads.Overlaps(new(resource, ulong.MaxValue - 159, 8)));
    }
}
