using System;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportProposalAdmissionTests : IDisposable
{
    readonly SupportProposalAdmission.ControlRead? _reader = SupportProposalAdmission.ControlReaderForTests;
    readonly bool _bypass = SupportProposalAdmission.BypassForTests;

    public SupportProposalAdmissionTests()
    {
        SupportProposalAdmission.ResetForTests();
        SupportProposalAdmission.ControlReaderForTests = Read;
        SupportProposalAdmission.BypassForTests = false;
    }

    [Fact]
    public void TheSingleSlotStaysWithinItsPayloadBudget() =>
        Assert.InRange(SupportProposalAdmission.PayloadBytesForTests, 1, 127);

    [Fact]
    public void WarmIdentityStillChecksEveryLivePositionComponentAtBothClosedBounds()
    {
        using var capture = new SupportProposalAdmissionCapture();
        Assert.True(Check(Pose.Identity));
        foreach (float value in new[] { -2048f, 2048f, MathF.BitDecrement(-2048f), MathF.BitIncrement(2048f),
                     float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 position = axis == 0 ? new(value, 0, 0) : axis == 1 ? new(0, value, 0) : new(0, 0, value);
                Assert.Equal(float.IsFinite(value) && MathF.Abs(value) <= 2048, Check(Pose.At(position)));
            }
        Assert.Equal(1, capture.Misses);
        Assert.Equal(6, capture.Hits);
    }

    [Fact]
    public void OnlyCanonicalQuaternionBitsCacheWhileAllSignedZerosMatchTheOriginalPredicate()
    {
        float negative = BitConverter.Int32BitsToSingle(int.MinValue);
        for (int rotation = 0; rotation < 8; rotation++)
            foreach (float w in new[] { 1f, -1f })
                for (int translation = 0; translation < 8; translation++)
                {
                    var q = new Quaternion((rotation & 1) == 0 ? 0 : negative,
                        (rotation & 2) == 0 ? 0 : negative, (rotation & 4) == 0 ? 0 : negative, w);
                    var p = new Vector3((translation & 1) == 0 ? 0 : negative,
                        (translation & 2) == 0 ? 0 : negative, (translation & 4) == 0 ? 0 : negative);
                    using var capture = new SupportProposalAdmissionCapture();
                    Assert.True(Check(new(p, q)));
                    Assert.Equal(rotation == 0 && w == 1 ? 0 : 1, capture.Bypasses);
                }
    }

    [Fact]
    public void InvalidAndUnprovenRotationsKeepTheOriginalPath()
    {
        Quaternion[] rotations = [default, new(0, 0, 0, 2), new(0, 0, 0, -1),
            new(0, 1, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f), new(0, 0.70710677f, 0, 0.70710677f)];
        using var capture = new SupportProposalAdmissionCapture();
        foreach (var q in rotations) Check(new(new(2, -4, 8), q));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int component = 0; component < 4; component++)
            {
                var q = Quaternion.Identity;
                if (component == 0) q.X = value;
                else if (component == 1) q.Y = value;
                else if (component == 2) q.Z = value;
                else q.W = value;
                Assert.False(Check(new(Vector3.Zero, q)));
            }
        Assert.Equal(rotations.Length + 12, capture.Bypasses);
        Assert.Equal(0, capture.Hits + capture.Misses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceAndUnavailableReaderAlwaysRecompute(bool reference)
    {
        Assert.True(Check(Pose.Identity));
        using var capture = new SupportProposalAdmissionCapture();
        using var scope = reference ? SupportReferenceEvaluation.Begin() : default;
        if (!reference) SupportProposalAdmission.ControlReaderForTests = Unavailable;
        Assert.True(Check(Pose.At(new(1, 2, 3))));
        Assert.True(Check(Pose.Identity));
        Assert.Equal(2, capture.Bypasses);
        Assert.Equal(0, capture.Hits + capture.Misses);
    }

    internal static bool Check(Pose pose)
    {
        bool expected = InstalledPoseOperator.TryCreateSupport(new RigidPose(pose.Position, pose.Orientation), out _);
        bool actual = SupportProposalAdmission.Admits(pose);
        Assert.Equal(expected, actual);
        return actual;
    }

    static bool Read(out ulong controls) { controls = 0; return true; }
    static bool Unavailable(out ulong controls) { controls = 0; return false; }

    public void Dispose()
    {
        SupportProposalAdmission.ControlReaderForTests = _reader;
        SupportProposalAdmission.BypassForTests = _bypass;
        SupportProposalAdmission.ResetForTests();
    }
}
