using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>The restrained two-handed working pose: what it puts both arms at, that station work leans into
/// the bench and free-standing work does not, and that a zero weight leaves the pose alone.</summary>
public sealed class ProcessingSwingTests
{
    [Fact]
    public void NonStationWorkUsesARestrainedTwoHandedPose()
    {
        WalkPose result = ProcessingSwing.Compose(WalkPose.Rest, 0f, atStation: false, 1f);
        Assert.InRange(result.RightArm, 0.15f, 0.8f);
        Assert.InRange(result.RightElbow, 0.4f, 1.4f);
        Assert.InRange(result.RightArmYaw, -0.7f, 0.2f);
        Assert.InRange(result.RightWrist, 0.1f, 1.2f);
        Assert.InRange(result.LeftArm, 0.2f, 1.0f);
        Assert.InRange(result.LeftElbow, 0.8f, 2.2f);
        Assert.Equal(0f, result.TorsoLean);
    }

    [Fact]
    public void StationWorkLeansTowardTheTargetAndZeroWeightLeavesThePoseAlone()
    {
        var basePose = new WalkPose(0.1f, -0.1f, 0.2f, -0.2f);

        WalkPose bench = ProcessingSwing.Compose(basePose, 0.5f, atStation: true, 1f);
        Assert.True(bench.TorsoLean > 0f);
        Assert.NotEqual(basePose.RightArm, bench.RightArm);

        WalkPose untouched = ProcessingSwing.Compose(basePose, 0.5f, atStation: true, 0f);
        Assert.Equal(basePose, untouched);
    }
}
