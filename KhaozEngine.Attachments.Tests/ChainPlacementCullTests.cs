using System;
using System.Numerics;
using System.Linq;
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
    private const float MotionStepScale = 0.5f;

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

    [Theory]
    [InlineData((int)ChainContactOptimization.SegmentCull)]
    [InlineData((int)ChainContactOptimization.OpeningStamp)]
    [InlineData((int)ChainContactOptimization.AxisCull)]
    [InlineData((int)ChainContactOptimization.RoundingMargin)]
    [InlineData((int)ChainContactOptimization.RoundingReuse)]
    [InlineData((int)ChainContactOptimization.FirstPassCandidates)]
    [InlineData((int)ChainContactOptimization.BendCosine)]
    [InlineData((int)ChainContactOptimization.WidthNearSolids)]
    [InlineData((int)ChainContactOptimization.SheetOpening)]
    [InlineData((int)ChainContactOptimization.All)]
    public void Contact_reuse_preserves_every_joint_frame_bit(int disabled)
    {
        var flags = (ChainContactOptimization)disabled;
        CompareProbe(flags);
        CompareLayeredSweep(flags);
        if (flags is ChainContactOptimization.FirstPassCandidates or ChainContactOptimization.BendCosine or ChainContactOptimization.All)
            CompareProbe(flags, bounded: false);
        if (flags == ChainContactOptimization.WidthNearSolids)
        {
            // Keep the independent float cull off so the count exposes the near-solid cut.
            const ChainContactOptimization control = ChainContactOptimization.AxisCull;
            long reduced = Count(disable: false, control)[ChainContactQuery.WidthSafety];
            long full = Count(disable: false, control | flags)[ChainContactQuery.WidthSafety];
            Assert.True(reduced < full, $"Expected fewer exact width queries with reuse enabled. Got {reduced} versus {full}.");
        }
        if (flags == ChainContactOptimization.SheetOpening)
        {
            long reduced = Count(disable: false)[ChainContactQuery.SheetOpening];
            long full = Count(disable: false, flags)[ChainContactQuery.SheetOpening];
            Assert.True(reduced < full, $"Expected fewer exact opening queries. Got {reduced} versus {full}.");
        }
    }

    [Fact]
    public void Settling_tolerance_keeps_the_pose_close_to_exact_settling()
    {
        // Compare full histories in the reachable sweep. The embedded dense
        // workload compares a single step in Settling_tolerance_keeps_a_dense_step_close_from_the_same_state.
        CompareLayeredSweep(ChainContactOptimization.PassTolerance);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(146)]
    [InlineData(201)]
    [InlineData(221)]
    public void Settling_tolerance_keeps_a_dense_step_close_from_the_same_state(int target)
    {
        var rig = CreateRig(layered: true);
        var first = new ChainSimulation(rig, RestCapsules.Length);
        var exact = new ChainSimulation(rig, RestCapsules.Length);
        var capsules = new ColliderCapsule[RestCapsules.Length];
        for (int frame = 0; frame < target; frame++)
        {
            var (anchor, wearer) = Pose(frame, capsules);
            float seconds = frame == 0 ? 0 : StepSeconds;
            first.Step([anchor], capsules, wearer, seconds);
            exact.Step([anchor], capsules, wearer, seconds);
        }

        var (endAnchor, endWearer) = Pose(target, capsules);
        first.Step([endAnchor], capsules, endWearer, StepSeconds);
        ChainContactOptimizations.DisabledForTests = ChainContactOptimization.PassTolerance;
        try
        {
            exact.Step([endAnchor], capsules, endWearer, StepSeconds);
        }
        finally
        {
            ChainContactOptimizations.DisabledForTests = default;
        }

        // Show the new solution without stepping a second time from different states.
        first.Step([endAnchor], capsules, endWearer, StepSeconds / 2);
        exact.Step([endAnchor], capsules, endWearer, StepSeconds / 2);
        AssertSettledPose(first, exact, target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_shown_chains_move_with_their_drivers_from_a_clear_reachable_rest(bool layeredSweep)
    {
        var rig = layeredSweep ? CreateLayeredSegmentRig() :
            CreateMotionRig();
        var capsules = new ColliderCapsule[layeredSweep ? 1 : RestCapsules.Length];
        var simulation = new ChainSimulation(rig, capsules.Length);
        var motion = new ChainShownMotionCheck(simulation);
        int samples = layeredSweep ? 114 : (int)(Steps / MotionStepScale);
        Assert.Equal(layeredSweep ? 6 : 42, rig.JointCount);
        var anchors = new Matrix4x4[rig.AnchorCount];

        for (int frame = 0; frame <= samples; frame++)
        {
            Matrix4x4 wearer;
            if (layeredSweep)
            {
                float z = float.Lerp(-0.16f, 0.12f, Math.Min(frame, 112) / 112f);
                wearer = anchors[0] = Matrix4x4.Identity;
                anchors[1] = SweepLower(z);
                capsules[0] = SweepSolid(z);
            }
            else
            {
                (anchors[0], wearer) = Pose(frame * MotionStepScale, capsules);
            }

            simulation.Step(anchors, capsules, wearer, frame == 0 ? 0 : StepSeconds);
            if (frame == 0)
                ChainShownMotionCheck.AssertInitialClearance(simulation, anchors, capsules);
            motion.Check(frame, anchors, capsules);
        }
    }

    private static ChainRig CreateMotionRig()
    {
        var original = CreateRig(layered: true);
        var chains = original.Chains.Select((chain, index) =>
        {
            if (index >= ChainSwingFixture.CapeChains)
                return chain;

            var joints = chain.Joints.ToArray();
            // Leave a full driven step of clearance behind the torso at the authored hang.
            joints[0] = joints[0] with { Translation = joints[0].Translation - 0.14f * Vector3.UnitZ };
            return chain with { Joints = joints };
        }).ToArray();
        return new ChainRig(original.AnchorCount, chains, original.Links);
    }

    private static void CompareProbe(ChainContactOptimization disabled, bool bounded = true)
    {
        var rig = bounded ? CreateRig(layered: true) : ChainSwingFixture.CreateRig(capeJoints: 6, layered: true);
        var optimized = new ChainSimulation(rig, RestCapsules.Length);
        var control = new ChainSimulation(rig, RestCapsules.Length);
        var capsules = new ColliderCapsule[RestCapsules.Length];
        Assert.Equal(42, rig.JointCount);

        for (int step = 0; step <= Steps; step++)
        {
            var (anchor, wearer) = Pose(step, capsules);
            float elapsed = step == 0 ? 0 : StepSeconds;
            Compare(optimized, control, [anchor], capsules, wearer, elapsed, disabled, step);
        }
    }

    private static void CompareLayeredSweep(ChainContactOptimization disabled)
    {
        var rig = CreateLayeredSegmentRig();
        var optimized = new ChainSimulation(rig, 1);
        var control = new ChainSimulation(rig, 1);
        var lowerNeutral = rig.Chains[2] with { Layer = null };
        var withoutLowerContact = new ChainSimulation(new ChainRig(rig.AnchorCount,
            [rig.Chains[0], rig.Chains[1], lowerNeutral], rig.Links), 1);
        const int sweepFrames = 112;
        Assert.Equal(6, rig.JointCount);
        bool lowerContactChangedChain = false;

        for (int frame = 0; frame <= sweepFrames + 2; frame++)
        {
            float z = float.Lerp(-0.16f, 0.12f, Math.Min(frame, sweepFrames) / (float)sweepFrames);
            var lower = SweepLower(z);
            ColliderCapsule capsule = SweepSolid(z);
            float elapsed = frame == 0 ? 0 : (frame % 2 == 0 ? 1.5f : 0.5f) * StepSeconds;
            Compare(optimized, control, [Matrix4x4.Identity, lower], [capsule], Matrix4x4.Identity,
                elapsed, disabled, frame);
            withoutLowerContact.Step([Matrix4x4.Identity, lower], [capsule], Matrix4x4.Identity, elapsed);
            var tip = rig.Chains[0].Tip;
            lowerContactChangedChain |= Vector3.DistanceSquared(Vector3.Transform(tip, optimized.JointsFromAnchor[1]),
                Vector3.Transform(tip, withoutLowerContact.JointsFromAnchor[1])) > 0.000001f;
        }

        Assert.True(optimized.JointsFromAnchor[3].Translation.Z > 0.02f);
        Assert.True(lowerContactChangedChain, "The lower chain must change the upper chain during the replay.");
    }

    private static ChainRig CreateLayeredSegmentRig()
    {
        var settings = ChainSimulationTests.Settings with { Gravity = 0, Radius = 0.01f };
        var segment = -0.5f * Vector3.UnitY;
        var left = ChainSimulationTests.Chain(segment, settings,
            ChainSimulationTests.Joint(-0.09f * Vector3.UnitX), ChainSimulationTests.Joint(segment))
            with { Layer = AttachmentLayer.Outerwear };
        var right = ChainSimulationTests.Chain(segment / 2, settings,
            ChainSimulationTests.Joint(0.09f * Vector3.UnitX), ChainSimulationTests.Joint(segment),
            ChainSimulationTests.Joint(segment / 2)) with { Layer = AttachmentLayer.Outerwear };
        var lower = ChainSimulationTests.Chain(0.06f * Vector3.UnitX, settings with { Radius = 0.03f, Trail = 0 },
            ChainSimulationTests.Joint(-0.03f * Vector3.UnitX)) with { Anchor = 1, Layer = AttachmentLayer.Armour };
        return new ChainRig(2, [left, right, lower], [new(0, 1) { Stretch = 0.14f }]);
    }

    // The authored capsule strikes the right chain and the lower segment strikes the left.
    private static ColliderCapsule SweepSolid(float z) => new(new(0.06f, -0.32f, z), new(0.12f, -0.32f, z), 0.03f);

    private static Matrix4x4 SweepLower(float z) => Matrix4x4.CreateTranslation(-0.09f, -0.82f, z);

    private static void Compare(ChainSimulation optimized, ChainSimulation control,
        ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> capsules, Matrix4x4 wearer,
        float elapsed, ChainContactOptimization disabled, int frame)
    {
        optimized.Step(anchors, capsules, wearer, elapsed);
        ChainContactOptimizations.DisabledForTests = disabled;
        try
        {
            control.Step(anchors, capsules, wearer, elapsed);
        }
        finally
        {
            ChainContactOptimizations.DisabledForTests = default;
        }

        if (disabled == ChainContactOptimization.PassTolerance)
            AssertSettledPose(optimized, control, frame);
        else
            Assert.True(MemoryMarshal.AsBytes(optimized.JointsFromAnchor)
                .SequenceEqual(MemoryMarshal.AsBytes(control.JointsFromAnchor)),
                $"Joint frame bits diverged at frame {frame} with {disabled} disabled.");
    }

    private static void AssertSettledPose(ChainSimulation optimized, ChainSimulation control, int frame)
    {
        Assert.Equal(optimized.Rig.JointCount, control.JointsFromAnchor.Length);
        // Each depth has at most eight passes. Compare both the joint and the
        // drawn tip because a tip can move while its last joint stays fixed.
        for (int chain = 0; chain < optimized.Rig.Chains.Count; chain++)
        {
            var definition = optimized.Rig.Chains[chain];
            int first = optimized.Rig.FirstJoint(chain);
            int count = definition.Joints.Count;
            double bound = ChainContactPass.Tolerance * ChainSegmentContact.ContactPasses * count;
            for (int joint = first; joint < first + count; joint++)
                AssertDistance(optimized.JointsFromAnchor[joint].Translation, control.JointsFromAnchor[joint].Translation, bound);
            AssertDistance(Vector3.Transform(definition.Tip, optimized.JointsFromAnchor[first + count - 1]),
                Vector3.Transform(definition.Tip, control.JointsFromAnchor[first + count - 1]), bound);
        }

        void AssertDistance(Vector3 actual, Vector3 reference, double bound)
        {
            float distance = Vector3.Distance(actual, reference);
            Assert.True(distance <= bound, $"Frame {frame}: deviation {distance} m exceeds {bound} m.");
        }
    }

    [Fact]
    public void A_swept_axis_keeps_contact_between_separated_endpoint_poses()
    {
        var opening = new ColliderCapsule(new(-0.5f, -0.375f, 0), new(0.5f, -0.375f, 0), 0.03125f);
        var current = new ColliderCapsule(new(-0.5f, 0.375f, 0), new(0.5f, 0.375f, 0), 0.03125f);
        const float reach = 0.03125f;

        Assert.False(new ChainFloatAxis(opening.Start, opening.End, opening.Radius).Reaches(Vector3.Zero, reach));
        Assert.False(new ChainFloatAxis(current.Start, current.End, current.Radius).Reaches(Vector3.Zero, reach));
        Assert.True(ChainFloatAxis.Sweep(current, opening).Reaches(Vector3.Zero, reach));
    }

    [Theory]
    [InlineData(65_536, 32_768, MotionLevel.Held)]
    [InlineData(512, 96, MotionLevel.Simulated)]
    public void Independent_standalone_chains_share_placement_scratch(int chainCount, int capsuleCount, MotionLevel level)
    {
        var settings = new ChainSettings(0, 0, 0, 1, MathF.PI, 0);
        var definition = ChainSimulationTests.Chain(-Vector3.UnitY, settings, ChainSimulationTests.Joint(Vector3.Zero));
        var chains = new ChainRigChain[chainCount];
        Array.Fill(chains, definition);
        var rig = new ChainRig(1, chains, []);
        var simulation = new ChainSimulation(rig, capsuleCount);
        var capsules = new ColliderCapsule[capsuleCount];
        var distant = new Vector3(4, 0, 0);
        Array.Fill(capsules, new ColliderCapsule(distant, distant, 0.125f));
        var center = new Vector3(0.04f, -0.5f, 0);
        capsules[0] = new ColliderCapsule(center, center, 0.125f);
        var anchor = Matrix4x4.Identity;
        simulation.Step([anchor], capsules, anchor, 0, level);

        // Run held frames at the overflow boundary to validate the large public inputs in linear
        // work. The smaller rig runs its grid steps and exercises the shared row during contact.
        simulation.Step([anchor], capsules, anchor, StepSeconds, level);
        simulation.Step([anchor], capsules, anchor, StepSeconds, level);
        Assert.Equal(chainCount, simulation.JointsFromAnchor.Length);
        var tip = Vector3.Transform(definition.Tip, simulation.JointsFromAnchor[^1]);
        if (level == MotionLevel.Held)
            Assert.Equal(definition.Tip, tip);
        else
            Assert.True(ChainSegmentContactTests.DistanceToSegment(center, Vector3.Zero, tip) >= capsules[0].Radius);
    }

    [Fact]
    public void Unrepresentable_placement_scratch_keeps_the_original_candidates()
    {
        var filter = new ChainPlacementCull(int.MaxValue, 2);
        var center = 10 * Vector3.UnitX;
        ColliderCapsule[] colliders = [new(center, center, 0.125f), new(-center, -center, 0.125f)];
        for (int index = 0; index < colliders.Length; index++)
            filter.SetBounds(index, colliders[index], colliders[index]);

        var selected = filter.Select(0, Vector3.Zero, 1, 0, new ChainCapsuleCandidates(colliders));
        Assert.Equal(colliders.Length, selected.Count);
        Assert.Equal(colliders[0], selected[0]);
        Assert.Equal(colliders[1], selected[1]);
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void Counts_contact_queries_per_wearer_step()
    {
        output.WriteLine($"Closest-point queries per layered wearer step over {Steps} steps");
        WriteCounts("Placement filter enabled", Count(disable: false));
        WriteCounts("Linked reuse disabled", Count(disable: false, ChainContactOptimization.FirstPassCandidates |
            ChainContactOptimization.BendCosine));
        WriteCounts("Placement filter disabled", Count(disable: true));
        WriteCounts("Exact settling", Count(disable: false, ChainContactOptimization.PassTolerance));
        WriteCounts("Exact cuts disabled", Count(disable: false,
            ChainContactOptimization.All | ChainContactOptimization.PassTolerance));
    }

    private void WriteCounts(string label, ChainContactCounts counts)
    {
        output.WriteLine($"{label}: {counts.SegmentAxis / (double)Steps:F2} segment-axis");
        foreach (var kind in Enum.GetValues<ChainContactQuery>())
            output.WriteLine($"{label} {kind}: {counts[kind] / (double)Steps:F2}");
        output.WriteLine($"{label} Segment passes 1-{ChainSegmentContact.ContactPasses}: {string.Join(",", counts.SegmentPasses[1..].ToArray())}");
    }

    private static ChainContactCounts Count(bool disable, ChainContactOptimization disabledOptimizations = default)
    {
        var simulation = new ChainSimulation(CreateRig(layered: true), RestCapsules.Length);
        var capsules = new ColliderCapsule[RestCapsules.Length];
        var (openingAnchor, openingWearer) = Pose(0, capsules);
        simulation.Step([openingAnchor], capsules, openingWearer, 0);
        var counts = new ChainContactCounts();
        ChainPlacementCull.DisableForTests = disable;
        ChainContactOptimizations.DisabledForTests = disabledOptimizations;
        ChainContactCounts.Active = counts;
        try
        {
            for (int step = 1; step <= Steps; step++)
            {
                var (anchor, wearer) = Pose(step, capsules);
                simulation.Step([anchor], capsules, wearer, StepSeconds);
            }

            return counts;
        }
        finally
        {
            ChainPlacementCull.DisableForTests = false;
            ChainContactOptimizations.DisabledForTests = default;
            ChainContactCounts.Active = null;
        }
    }

    private static ChainRig CreateRig(bool layered = false) =>
        ChainSwingFixture.CreateRig(stretch: 0.08f, capeJoints: 6, layered: layered);

    private static (Matrix4x4 Anchor, Matrix4x4 Wearer) Pose(float step, Span<ColliderCapsule> capsules)
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
