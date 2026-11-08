using System;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryFailureTests
{
    [Fact]
    public void Cancelling_generated_direction_publishes_complete_uncovered_records_from_poisoned_storage()
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.CancellingTransport();
        fixture.Publish();
        fixture.AssertUncovered();
    }

    [Fact]
    public void Late_gather_failure_selects_uncovered_records_for_the_entire_actor()
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.FailingGather(fail: true);
        fixture.Publish();
        fixture.AssertUncovered();
    }

    [Fact]
    public void Successful_gather_publishes_complete_moved_records_from_poisoned_storage()
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.FailingGather(fail: false);
        fixture.Publish();
        Assert.Equal(0u, fixture.Errors[6]);
        Assert.DoesNotContain(CoverageCarryFailureFixture.Poison, fixture.Current);
        Assert.NotEqual(fixture.Expected, fixture.Current);
        Assert.Equal(BitConverter.SingleToUInt32Bits(-.25f), fixture.Current[2]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(-.25f), fixture.Current[18]);
    }
}
