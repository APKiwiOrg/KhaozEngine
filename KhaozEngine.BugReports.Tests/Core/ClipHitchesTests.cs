using System.Linq;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class ClipHitchesTests
{
    static ClipIndexFrame[] Frames(params double[] renderMilliseconds)
        => renderMilliseconds.Select((ms, i) => new ClipIndexFrame(i * 100, ms, 4, 4, BundleEntryNames.ClipFrame(i))).ToArray();

    [Fact]
    public void Hitches_are_strictly_over_twice_the_median()
    {
        (double median, var hitches) = ClipHitches.Find(Frames(10, 10, 10, 30, 10, 21, 20));
        Assert.Equal(10, median);
        Assert.Equal([3, 5], hitches);
    }

    [Fact]
    public void Even_count_median_is_the_mean_of_the_middle_two()
    {
        (double median, var hitches) = ClipHitches.Find(Frames(40, 10, 30, 20));
        Assert.Equal(25, median);
        Assert.Empty(hitches);
    }

    [Fact]
    public void Huge_render_times_give_a_finite_median()
    {
        (double median, var hitches) = ClipHitches.Find(Frames(1.7e308, 1.7e308));
        Assert.Equal(1.7e308, median);
        Assert.Empty(hitches);
    }

    [Fact]
    public void Empty_clip_has_no_hitches()
    {
        (double median, var hitches) = ClipHitches.Find([]);
        Assert.Equal(0, median);
        Assert.Empty(hitches);
    }
}
