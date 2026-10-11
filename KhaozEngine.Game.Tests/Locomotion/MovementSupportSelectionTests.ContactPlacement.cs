using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class MovementSupportSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicSelectUsesTheHigherFootprintWithinTheLegsBandInEitherCandidateOrder(bool reverse)
    {
        using var scene = new Scene();
        scene.Floor(0);
        scene.Floor(0.25f);
        var low = Candidate(Low, 0);
        var high = Candidate(High, 0.25f);
        scene.Candidates = reverse ? [high, low] : [low, high];
        var result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(High, result.Candidate!.Value.Owner);
        Assert.InRange(result.Centre.Y, 1.0009f, 1.0011f);
    }

    [Fact]
    public void PublicSelectSeatsSlopedLandWithTheFixedVerticalSkin()
    {
        using var scene = new Scene();
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        Vector3 normal = Vector3.Transform(Vector3.UnitY, rotation);
        scene.World.AddStatic(new BoxShape(new(3, 0.05f, 3)), new Pose(-normal * 0.05f, rotation));
        scene.Candidates = [new(Low, Room, Vector3.Zero, normal, null)];
        var result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(Low, result.Candidate!.Value.Owner);
        Assert.Equal(Vector3.Zero, result.Candidate.Value.Feet);
        // Axis support 0 + full half-height 0.75 + vertical skin 0.001, independent of normal.Y.
        Assert.InRange(result.Centre.Y, 0.7509f, 0.7511f);
    }

    [Fact]
    public void PublicSelectLeavesBelowKneeOverlapsToTheSharedFootprint()
    {
        using var scene = new Scene();
        scene.Floor(0);
        scene.World.AddStatic(new BoxShape(new(0.05f, 0.1f, 2)), Pose.At(new(0.25f, 0.1f, 0)));
        scene.Candidates = [Candidate(Low, 0)];
        var result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(Low, result.Candidate!.Value.Owner);
        Assert.InRange(result.Centre.Y, 0.7509f, 0.7511f);
    }
}
