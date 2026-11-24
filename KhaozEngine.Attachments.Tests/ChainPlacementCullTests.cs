using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainPlacementCullTests(ITestOutputHelper output)
{
    private const int StepsPerSecond = 120;
    private const int Steps = 3 * StepsPerSecond;
    private const float StepSeconds = 1f / StepsPerSecond;

    private static readonly ColliderCapsule[] RestCapsules =
    [
        new(new(-0.22f, -0.09f, -0.14f), new(-0.22f, -0.47f, -0.15f), 0.098f),
        new(new(0.22f, -0.09f, -0.14f), new(0.22f, -0.47f, -0.15f), 0.098f),
        new(new(-0.08f, -0.1f, -0.09f), new(-0.08f, -0.46f, -0.12f), 0.103f),
        new(new(0.08f, -0.1f, -0.09f), new(0.08f, -0.46f, -0.12f), 0.103f),
        new(new(-0.27f, -0.11f, -0.05f), new(-0.34f, -0.3f, 0), 0.072f),
        new(new(0.27f, -0.11f, -0.05f), new(0.34f, -0.3f, 0), 0.072f),
        new(new(-0.21f, -0.48f, -0.12f), new(-0.055f, -0.55f, -0.11f), 0.102f),
        new(new(0.21f, -0.48f, -0.12f), new(0.055f, -0.55f, -0.11f), 0.102f),
        new(new(-0.19f, -0.1f, -0.22f), new(0.19f, -0.1f, -0.23f), 0.045f),
        new(new(-0.22f, -0.34f, -0.2f), new(0.22f, -0.36f, -0.19f), 0.057f),
        new(new(-0.18f, -0.56f, -0.19f), new(0.17f, -0.6f, -0.17f), 0.05f),
        new(new(-0.27f, -0.28f, -0.18f), new(-0.39f, -0.44f, -0.22f), 0.045f),
        new(new(0.27f, -0.25f, -0.2f), new(0.39f, -0.44f, -0.21f), 0.045f),
        new(new(-0.178f, -0.58f, -0.04f), new(-0.14f, -0.94f, 0.035f), 0.09f),
        new(new(0.178f, -0.58f, -0.04f), new(0.14f, -0.94f, 0.035f), 0.09f),
        new(new(-0.14f, -0.94f, 0.035f), new(-0.14f, -1.31f, 0), 0.068f),
        new(new(0.14f, -0.94f, 0.035f), new(0.14f, -1.31f, 0), 0.068f),
        new(new(-0.14f, -1.32f, -0.03f), new(-0.14f, -1.34f, 0.15f), 0.068f),
        new(new(0.14f, -1.32f, -0.03f), new(0.14f, -1.34f, 0.15f), 0.068f),
        new(new(-0.07f, -0.26f, 0.11f), new(0.07f, -0.45f, 0.18f), 0.055f)
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Placement_cull_preserves_joint_frame_bits(bool longAxis)
    {
        var rig = CreateRig();
        var culled = new ChainSimulation(rig, RestCapsules.Length);
        var unfiltered = new ChainSimulation(rig, RestCapsules.Length);
        var noContact = new ChainSimulation(rig, 0);
        var capsules = new ColliderCapsule[RestCapsules.Length];
        Assert.Equal(42, rig.JointCount);
        bool madeContact = false;

        for (int step = 0; step <= Steps; step++)
        {
            var (anchor, wearer) = Pose(step, capsules);
            if (longAxis)
                capsules[^1] = new(new(0.27f, 2e20f, wearer.Translation.Z - 0.21f),
                    new(0.27f, -2e20f, wearer.Translation.Z - 0.21f), 0.04f);
            float elapsed = step == 0 ? 0 : StepSeconds;
            culled.Step([anchor], capsules, wearer, elapsed);
            ChainPlacementCull.DisableForTests = true;
            try
            {
                unfiltered.Step([anchor], capsules, wearer, elapsed);
            }
            finally
            {
                ChainPlacementCull.DisableForTests = false;
            }

            noContact.Step([anchor], [], wearer, elapsed);
            var expected = MemoryMarshal.AsBytes(unfiltered.JointsFromAnchor);
            var actual = MemoryMarshal.AsBytes(culled.JointsFromAnchor);
            Assert.True(expected.SequenceEqual(actual), $"Joint frame bits diverged at step {step}.");
            madeContact |= !actual.SequenceEqual(MemoryMarshal.AsBytes(noContact.JointsFromAnchor));
        }

        Assert.True(madeContact, "The moving capsules must change the solved pose.");
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void Counts_segment_axis_queries_per_wearer_step()
    {
        output.WriteLine($"Segment-axis queries per wearer step over {Steps} steps");
        output.WriteLine($"Placement filter enabled: {Count(disable: false) / (double)Steps:F2}");
        output.WriteLine($"Placement filter disabled: {Count(disable: true) / (double)Steps:F2}");
    }

    private static long Count(bool disable)
    {
        var simulation = new ChainSimulation(CreateRig(), RestCapsules.Length);
        var capsules = new ColliderCapsule[RestCapsules.Length];
        var (openingAnchor, openingWearer) = Pose(0, capsules);
        simulation.Step([openingAnchor], capsules, openingWearer, 0);
        ChainPlacementCull.DisableForTests = disable;
        ChainSegmentContact.ClosestPointEvaluationsForTests = 0;
        try
        {
            for (int step = 1; step <= Steps; step++)
            {
                var (anchor, wearer) = Pose(step, capsules);
                simulation.Step([anchor], capsules, wearer, StepSeconds);
            }

            return ChainSegmentContact.ClosestPointEvaluationsForTests.GetValueOrDefault();
        }
        finally
        {
            ChainPlacementCull.DisableForTests = false;
            ChainSegmentContact.ClosestPointEvaluationsForTests = null;
        }
    }

    private static ChainRig CreateRig() => ChainSwingFixture.CreateRig(stretch: 0.08f, capeJoints: 6);

    private static (Matrix4x4 Anchor, Matrix4x4 Wearer) Pose(int step, Span<ColliderCapsule> capsules)
    {
        float seconds = step * StepSeconds;
        var (anchor, wearer) = ChainSwingFixture.Pose(seconds);
        var stride = Matrix4x4.CreateRotationX(0.13f * MathF.Sin(MathF.Tau * 1.7f * seconds)) * wearer;
        for (int index = 0; index < capsules.Length; index++)
        {
            var capsule = RestCapsules[index];
            var pose = index is >= 13 and <= 18 ? stride : anchor;
            capsules[index] = new ColliderCapsule(Vector3.Transform(capsule.Start, pose),
                Vector3.Transform(capsule.End, pose), capsule.Radius);
        }

        return (anchor, wearer);
    }
}
