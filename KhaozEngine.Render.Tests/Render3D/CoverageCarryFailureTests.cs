using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancelling_generated_direction_keeps_covered_positions_from_poisoned_storage(bool tangent)
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.CancellingTransport(tangent: tangent);
        fixture.Publish();
        fixture.AssertCoveredDirections();
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0x7fc00000u, false)]
    [InlineData(0x7f800000u, false)]
    [InlineData(0u, true)]
    [InlineData(0x7fc00000u, true)]
    [InlineData(0x7f800000u, true)]
    public void Late_direction_failure_keeps_covered_positions_and_other_vertices_unchanged(uint word, bool tangent)
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.FailingGather(fail: true, tangent: tangent);
        fixture.Work[CoverageCarryFailureFixture.Basis + 46 + (tangent ? 22 : 27)] = word;
        fixture.Publish();
        fixture.AssertCoveredDirections();
    }

    [Fact]
    public void Invalid_position_still_publishes_the_whole_uncovered_pose_from_poisoned_storage()
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.FailingGather(fail: false);
        fixture.Work[CoverageCarryFailureFixture.After + 18] = CoverageCarryFailureFixture.Poison;
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
        uint[] expected = new float[]
        {
            0, 0, -.25f, 1, 0, 0, .25f, .5f, .75f, 1, 0, 0, 0, 0, 0, 0,
            2, 0, -.25f, 1, 0, 0, .75f, .5f, .25f, 1, 1, 0, 0, 0, 0, 0,
        }.Select(BitConverter.SingleToUInt32Bits).ToArray();
        Assert.Equal(expected, fixture.Current);
    }
}
