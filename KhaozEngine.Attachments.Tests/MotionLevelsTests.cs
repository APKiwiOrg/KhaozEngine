using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class MotionLevelsTests
{
    [Theory]
    [InlineData(2, 2, 2, -1)]
    [InlineData(4, 3, 2, 3)]
    [InlineData(0, 0, -1, -1)]
    public void Assign_selects_the_nearest_within_range_with_stable_ties_without_allocating(
        int budget, int count, int second, int third)
    {
        Vector3[] wearers = [new(8, 0, 0), new(0, 2, 0), new(0, -1, 0), new(2, 0, 0)];
        var levels = new MotionLevel[wearers.Length];
        var scratch = new int[wearers.Length];
        MotionLevels.Assign(wearers, Vector3.Zero, 2, budget, scratch, levels);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int simulated = 0;
        for (var call = 0; call < 100; call++)
            simulated = MotionLevels.Assign(wearers, Vector3.Zero, 2, budget, scratch, levels);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(count, simulated);
        for (var index = 0; index < levels.Length; index++)
        {
            bool expected = (count > 0 && index == 1) || index == second || index == third;
            Assert.Equal(expected ? MotionLevel.Simulated : MotionLevel.Held, levels[index]);
        }
    }
}
