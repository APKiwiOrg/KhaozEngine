using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalReadFootprintMultiPassTests(ITestOutputHelper output)
{
    // #1696 round 3. Passes revisit padded windows after mesh, instance-buffer and pipeline rebinds.
    [Fact]
    public void RevisitedMultiPassWindowsDoNoTreeWorkAndKeepCoverageChecksBounded()
    {
        using var f = new MetalReadFootprintMultiPassFixture();
        f.Draws.Begin();
        var list = f.Draws.List;
        uint sequence = 0;
        long repeatInserts = 0, repeatExtends = 0, repeatRotations = 0;
        long maxRepeatProbes = 0, maxRepeatWrites = 0, maxCoverage = 0;
        for (int pass = 0; pass < 11; pass++)
        {
            long probes = list.ReadProbeStepsForTests, writes = list.ReadNodeWritesForTests;
            long inserts = list.ReadInsertsForTests, extends = list.ReadExtendsForTests;
            long rotations = list.ReadRotationsForTests, coverage = list.ReadCoverageStepsForTests;
            for (uint draw = 0; draw < MetalReadFootprintMultiPassFixture.PassDraws(pass); draw++)
            {
                long p = list.ReadProbeStepsForTests, w = list.ReadNodeWritesForTests;
                long c = list.ReadCoverageStepsForTests;
                f.Draw(pass, draw, sequence++);
                if (pass > 0)
                {
                    maxRepeatProbes = System.Math.Max(maxRepeatProbes, list.ReadProbeStepsForTests - p);
                    maxRepeatWrites = System.Math.Max(maxRepeatWrites, list.ReadNodeWritesForTests - w);
                    maxCoverage = System.Math.Max(maxCoverage, list.ReadCoverageStepsForTests - c);
                }
            }
            if (pass > 0)
            {
                repeatInserts += list.ReadInsertsForTests - inserts;
                repeatExtends += list.ReadExtendsForTests - extends;
                repeatRotations += list.ReadRotationsForTests - rotations;
            }
            output.WriteLine($"Pass {pass}, {MetalReadFootprintMultiPassFixture.PassDraws(pass)} draws: probes {list.ReadProbeStepsForTests - probes}, inserts {list.ReadInsertsForTests - inserts}, extends {list.ReadExtendsForTests - extends}, rotations {list.ReadRotationsForTests - rotations}, node writes {list.ReadNodeWritesForTests - writes}, coverage steps {list.ReadCoverageStepsForTests - coverage}");
        }
        output.WriteLine($"Total {sequence} draws: probes {list.ReadProbeStepsForTests}, inserts {list.ReadInsertsForTests}, extends {list.ReadExtendsForTests}, rotations {list.ReadRotationsForTests}, node writes {list.ReadNodeWritesForTests}, coverage steps {list.ReadCoverageStepsForTests}");
        output.WriteLine($"Revisit maxima per draw: probes {maxRepeatProbes}, writes {maxRepeatWrites}, coverage {maxCoverage}. Revisit totals: inserts {repeatInserts}, extends {repeatExtends}, rotations {repeatRotations}");
        Assert.Equal(MetalReadFootprintMultiPassFixture.DrawCount, (int)sequence);
        // A revisited draw owes only its two fresh contiguous uniform slices. All vertex coverage is cached.
        Assert.InRange(list.ReadProbeStepsForTests, 1, MetalReadFootprintMultiPassFixture.DrawCount * 12);
        Assert.InRange(list.ReadNodeWritesForTests, 1, MetalReadFootprintMultiPassFixture.DrawCount * 12);
        // Adjacent instance attributes are one exact byte range, so a newly extended window owes at most
        // one extension per draw. Indexed attributes keep their separate end-of-buffer reachability.
        Assert.InRange(list.ReadExtendsForTests, 0, 450);
        Assert.Equal(0, repeatInserts);
        Assert.Equal(0, repeatExtends);
        Assert.Equal(0, repeatRotations);
        Assert.InRange(maxRepeatProbes, 1, 2);
        Assert.InRange(maxRepeatWrites, 1, 2);
        // Three stream preparations plus a bounded window lookup, independent of attributes or prior draws.
        Assert.InRange(maxCoverage, 1, 12);
        list.End();
    }
}
