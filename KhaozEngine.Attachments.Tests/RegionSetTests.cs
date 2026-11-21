using System;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class RegionSetTests
{
    [Fact]
    public void A_region_set_holds_region_127_and_refuses_128()
    {
        Assert.True(RegionSet.Empty.IsEmpty);
        Assert.Equal(0, RegionSet.Empty.Count);

        RegionSet last = RegionSet.Empty.With(RegionSet.MaxRegions - 1);
        Assert.True(last.Contains(127));
        Assert.False(last.IsEmpty);
        Assert.Equal(1, last.Count);

        Assert.Throws<ArgumentOutOfRangeException>(() => RegionSet.Empty.With(RegionSet.MaxRegions));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegionSet.Empty.With(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => last.Contains(RegionSet.MaxRegions));
    }

    [Fact]
    public void Union_and_contains_agree_for_regions_in_both_words()
    {
        RegionSet low = RegionSet.Empty.With(3).With(64);
        RegionSet high = RegionSet.Empty.With(63).With(127);
        RegionSet union = low.Union(high);

        foreach (int region in new[] { 3, 63, 64, 127 })
            Assert.True(union.Contains(region), $"region {region}");
        foreach (int region in new[] { 0, 2, 4, 62, 65, 126 })
            Assert.False(union.Contains(region), $"region {region}");
        Assert.Equal(4, union.Count);
        Assert.Equal(union, high.Union(low));
        Assert.Equal(union, RegionSet.Empty.With(127).With(64).With(63).With(3).With(3));
        Assert.True(union == low.With(63).With(127));
        Assert.NotEqual(union, low);
        Assert.Equal(low, low.Union(RegionSet.Empty));
    }
}
