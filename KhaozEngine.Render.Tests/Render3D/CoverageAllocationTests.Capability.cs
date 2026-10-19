using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_capability_failure_allocates_zero_after_first_report(bool buildFailure)
    {
        using var h = new CoverageSceneFixture(supportsCompute: buildFailure, failCoverageBuild: buildFailure);
        using var commands = new NullGpuCommandList();
        var poses = h.CommandPoses;
        RigidInstanceDraw[] input = [new(h.RigidHandle, Matrix4x4.Identity)];
        var frozen = new RigidInstanceDraw[1];
        SkinnedCoverageFrame frame = default;
        bool equipment = false, host = false;
        CoverageFailure equipmentFailure = default, hostFailure = default;
        void Encode()
        {
            h.Begin();
            equipment = h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support, input, frozen, out equipmentFailure);
            host = h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, poses, MotionKey.From(10), CoverageFallback.UncoveredCurrentPose,
                out frame, out hostFailure);
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette); h.Scene.Draw(frozen[0]);
            h.RenderUncaptured(commands);
        }
        Encode(); Encode();
        long allocated = AllocationMeasurement.Measure("coverage capability fallback encoding", Encode);
        output.WriteLine($"buildFailure={buildFailure} warmed capability fallback bytes={allocated}");
        Assert.True(equipment, equipmentFailure.ToString()); Assert.True(host, hostFailure.ToString());
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, h.Scene.GetCoverageOutcome(frame).Failure.Code);
        Assert.Equal(buildFailure ? 1 : 0, h.Capture.CoverageBuildAttempts);
        Assert.Equal(0, allocated);
    }
}
