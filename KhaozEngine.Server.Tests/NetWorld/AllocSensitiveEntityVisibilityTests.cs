using System;
using System.Collections.Generic;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The per-viewer visibility filter both NetWorld serve loops run on every client's interest set every tick must not
/// allocate. Kept apart from <see cref="EntityVisibilityTests"/> so it joins the AllocSensitive collection.
/// </summary>
[Collection("AllocSensitive")]
public sealed class AllocSensitiveEntityVisibilityTests
{
    [Fact]
    public void TheFilterAllocatesNothingOnceWarm()
    {
        // Sized up front and refilled within capacity, so the measured passes never grow the set.
        var set = new HashSet<long>(64);
        Func<int, long, bool> visible = (slot, id) => id % 2 == 0;
        for (long i = 1; i <= 64; i++) set.Add(i);
        InterestVisibility.Filter(set, 0, 1, visible);

        AllocAssert.NoPerCallAllocation("a warm per-viewer interest filter", () =>
        {
            for (int tick = 0; tick < 100; tick++)
            {
                set.Clear();
                for (long i = 1; i <= 64; i++) set.Add(i);
                InterestVisibility.Filter(set, 0, 1, visible);
            }
        });

        Assert.Equal(33, set.Count);
        Assert.Contains(1L, set);
        Assert.Contains(2L, set);
        Assert.DoesNotContain(3L, set);
    }
}
