using System;
using System.Linq;
using KhaozEngine.Skills;
using Xunit;

namespace KhaozEngine.Tests.Foundation.Skills;

public class HarvestYieldRangeTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(45, 9)]
    [InlineData(46, 10)]
    [InlineData(50, 10)]
    [InlineData(99, 10)]
    public void Ceiling_distributes_every_yield_across_the_inclusive_level_range(int level, int expected)
    {
        var range = new HarvestYieldRange(1, 50, 1, 10);

        Assert.Equal(expected, range.CeilingAt(level));
    }

    [Fact]
    public void A_later_unlock_starts_its_first_band_at_the_minimum()
    {
        var range = new HarvestYieldRange(20, 29, 3, 6);

        Assert.Equal(3, range.CeilingAt(20));
        Assert.Equal(3, range.CeilingAt(21));
        Assert.Equal(3, range.CeilingAt(22));
        Assert.Equal(4, range.CeilingAt(23));
        Assert.Equal(6, range.CeilingAt(29));
        Assert.Equal(6, range.CeilingAt(100));
    }

    [Fact]
    public void Non_divisible_level_intervals_reach_every_ceiling_without_rounding_up()
    {
        var range = new HarvestYieldRange(1, 7, 2, 4);

        Assert.Equal([2, 2, 2, 3, 3, 4, 4],
            Enumerable.Range(1, 7).Select(range.CeilingAt).ToArray());
    }

    [Fact]
    public void Improvised_ceiling_halves_the_normal_ceiling_without_crossing_the_minimum()
    {
        var range = new HarvestYieldRange(1, 50, 1, 10);

        Assert.Equal(1, range.ImprovisedCeilingAt(1));
        Assert.Equal(1, range.ImprovisedCeilingAt(11));
        Assert.Equal(4, range.ImprovisedCeilingAt(45));
        Assert.Equal(5, range.ImprovisedCeilingAt(46));

        var elevatedMinimum = new HarvestYieldRange(1, 10, 4, 8);
        Assert.Equal(4, elevatedMinimum.ImprovisedCeilingAt(1));
        Assert.Equal(4, elevatedMinimum.ImprovisedCeilingAt(10));
    }

    [Fact]
    public void A_level_below_unlock_is_refused()
    {
        var range = new HarvestYieldRange(10, 20, 1, 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => range.CeilingAt(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => range.ImprovisedCeilingAt(9));
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(-1, 1, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, -1, 1)]
    [InlineData(1, 1, 2, 1)]
    public void Invalid_ranges_are_refused(int unlockLevel, int capLevel, int minimum, int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HarvestYieldRange(unlockLevel, capLevel, minimum, maximum));
    }

    [Fact]
    public void Wide_integer_arithmetic_stays_finite_at_int_limits()
    {
        var range = new HarvestYieldRange(1, int.MaxValue, 1, int.MaxValue);

        Assert.Equal(1, range.CeilingAt(1));
        Assert.Equal(int.MaxValue, range.CeilingAt(int.MaxValue));
    }

    [Fact]
    public void The_default_struct_is_not_a_usable_yield_range()
    {
        HarvestYieldRange range = default;

        Assert.Throws<InvalidOperationException>(() => range.CeilingAt(1));
        Assert.Throws<InvalidOperationException>(() => range.ImprovisedCeilingAt(1));
    }
}
