using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

[Collection(AttachmentAllocationCollection.Name)]
public sealed class ChainSheetContactTests
{
    private const float StepSeconds = 1f / 120;
    private const float Tolerance = 0.0002f;
    private const float SheetRadius = 0.01f;
    private const float ObstacleRadius = 0.03f;
    private const float Clearance = SheetRadius + ObstacleRadius;
    private const float SweepStart = -0.16f;
    private const float SweepEnd = 0.12f;
    private static readonly Matrix4x4 Identity = Matrix4x4.Identity;

    [Theory]
    [InlineData(true, 112)]
    [InlineData(false, 112)]
    [InlineData(true, 1)]
    public void A_capsule_sweeping_between_chains_clears_only_a_bounded_sheet(bool bounded, int sweepSteps)
    {
        var rig = CreateSheet(bounded, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        var previous = Capsule(SweepStart);
        simulation.Step([Identity], [previous], Identity, 0);
        Assert.Equal(rig.JointCount, simulation.JointsFromAnchor.Length);
        float minimum = float.PositiveInfinity;

        for (int frame = 1; frame <= sweepSteps + 1; frame++)
        {
            var capsule = Capsule(SweepPosition(frame, sweepSteps));
            simulation.Step([Identity], [capsule], Identity, StepSeconds);

            // At the grid boundary the displayed sample is one solved step behind the input.
            float clearance = MeasureSheet(simulation, left, right, previous);
            if (bounded)
                Assert.True(clearance >= Clearance - Tolerance, $"Clearance at step {frame - 1}: {clearance} m.");
            minimum = MathF.Min(minimum, clearance);
            previous = capsule;
        }

        if (bounded)
        {
            if (sweepSteps > 1)
                Assert.InRange(minimum, Clearance - Tolerance, Clearance + Tolerance);

            var a = simulation.JointsFromAnchor[1].Translation;
            var b = simulation.JointsFromAnchor[left.Joints.Count + 1].Translation;
            var c = Tip(simulation, left, 0);
            var d = Tip(simulation, right, left.Joints.Count);
            var center = (previous.Start + previous.End) / 2;
            // The orientation of both band faces points along +Z before the sweep.
            Assert.True(Vector3.Dot(center - a, Vector3.Cross(c - a, d - a)) < 0);
            Assert.True(Vector3.Dot(center - a, Vector3.Cross(d - a, b - a)) < 0);
        }
        else
        {
            Assert.InRange(minimum, 0, Tolerance);
        }
    }

    [Theory]
    [InlineData(true, 112)]
    [InlineData(false, 112)]
    [InlineData(true, 1)]
    public void A_lower_chain_sweeping_between_unequal_guides_stays_on_the_opening_side_of_a_bounded_sheet(
        bool bounded, int sweepSteps)
    {
        // Follow-up to #1894: the tail clears both guide tubes but crosses their cloth.
        var rig = CreateSheet(bounded, out var left, out var right, layered: true);
        var simulation = new ChainSimulation(rig, 0);
        var lower = rig.Chains[2];
        var lowerAlone = Create(lower with { Anchor = 0 });
        var anchor = LowerAnchor(SweepStart);
        simulation.Step([Identity, anchor], [], Identity, 0);
        lowerAlone.Step([anchor], [], Identity, 0);
        Assert.Equal(rig.JointCount, simulation.JointsFromAnchor.Length);
        Span<Matrix4x4> worlds = stackalloc Matrix4x4[rig.JointCount];
        float minimum = float.PositiveInfinity;
        ColliderCapsule obstacle = default;

        for (int frame = 1; frame <= sweepSteps + 1; frame++)
        {
            var next = LowerAnchor(SweepPosition(frame, sweepSteps));
            simulation.Step([Identity, next], [], Identity, StepSeconds);
            lowerAlone.Step([next], [], Identity, StepSeconds);
            Assert.True(MemoryMarshal.AsBytes(lowerAlone.JointsFromAnchor)
                .SequenceEqual(MemoryMarshal.AsBytes(simulation.JointsFromAnchor[^1..])));
            simulation.ComposeJointWorlds([Identity, anchor], worlds);
            obstacle = new ColliderCapsule(worlds[^1].Translation, Vector3.Transform(lower.Tip, worlds[^1]), ObstacleRadius);
            float clearance = MeasureSheet(simulation, left, right, obstacle);
            if (frame == 1 || bounded)
                Assert.True(clearance >= Clearance - Tolerance, $"Clearance at step {frame - 1}: {clearance} m.");
            minimum = MathF.Min(minimum, clearance);
            anchor = next;
        }

        if (!bounded)
        {
            Assert.InRange(minimum, 0, Tolerance);
            return;
        }

        Assert.InRange(minimum, Clearance - Tolerance, Clearance + Tolerance);
        var upperLeft = worlds[1].Translation;
        var upperRight = worlds[left.Joints.Count + 1].Translation;
        var tipLeft = Tip(simulation, left, 0);
        var tipRight = Tip(simulation, right, left.Joints.Count);
        var center = (obstacle.Start + obstacle.End) / 2;
        Assert.True(Vector3.Dot(center - upperLeft, Vector3.Cross(tipLeft - upperLeft, tipRight - upperLeft)) < 0);
        Assert.True(Vector3.Dot(center - upperLeft, Vector3.Cross(tipRight - upperLeft, upperRight - upperLeft)) < 0);
    }

    [Fact]
    public void A_capsule_already_touching_the_sheet_stays_on_its_opening_side()
    {
        var rig = CreateSheet(bounded: true, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        var touching = Capsule(-Clearance + Tolerance / 4);
        var across = Capsule(SweepEnd);
        simulation.Step([Identity], [touching], Identity, 0);
        simulation.Step([Identity], [touching], Identity, StepSeconds);
        simulation.Step([Identity], [across], Identity, StepSeconds);
        simulation.Step([Identity], [across], Identity, StepSeconds);

        AssertHemOpeningSide(simulation, left, right, across);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.03f)]
    public void A_capsule_crossing_late_in_an_oblique_sweep_keeps_the_opening_side(float halfAxis)
    {
        var rig = CreateSheet(bounded: true, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        var before = DiagonalCapsule(-5.88f);
        var after = DiagonalCapsule(0.12f);
        simulation.Step([Identity], [before], Identity, 0);
        simulation.Step([Identity], [after], Identity, StepSeconds);
        simulation.Step([Identity], [after], Identity, StepSeconds);

        AssertHemOpeningSide(simulation, left, right, after);

        ColliderCapsule DiagonalCapsule(float coordinate) =>
            new(new(coordinate - halfAxis, -0.9f, coordinate), new(coordinate + halfAxis, -0.9f, coordinate), ObstacleRadius);
    }

    [Fact]
    public void A_capsule_that_leaves_the_sheet_reach_does_not_drag_the_hem_after_it()
    {
        var rig = CreateSheet(bounded: true, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        var before = Capsule(SweepStart);
        var after = Capsule(5 * left.Tip.Length());
        simulation.Step([Identity], [before], Identity, 0);
        var initial = simulation.JointsFromAnchor.ToArray();
        simulation.Step([Identity], [after], Identity, StepSeconds);
        simulation.Step([Identity], [after], Identity, StepSeconds);

        Assert.True(MeasureSheet(simulation, left, right, after) > Clearance);
        Assert.True(MemoryMarshal.AsBytes(initial.AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(simulation.JointsFromAnchor)));
    }

    [Fact]
    public void A_capsule_can_change_sides_beyond_the_outer_guide()
    {
        var rig = CreateSheet(bounded: true, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        var before = Capsule(SweepStart);
        var shift = new Vector3(1.9f, 0, SweepEnd - SweepStart);
        var after = new ColliderCapsule(before.Start + shift, before.End + shift, before.Radius);
        simulation.Step([Identity], [before], Identity, 0);
        var initial = simulation.JointsFromAnchor.ToArray();
        simulation.Step([Identity], [after], Identity, StepSeconds);
        simulation.Step([Identity], [after], Identity, StepSeconds);

        // The side changes, while the path is already beyond the free edge.
        Assert.True(MeasureSheet(simulation, left, right, after) > Clearance);
        Assert.True(MemoryMarshal.AsBytes(initial.AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(simulation.JointsFromAnchor)));
    }

    [Fact]
    public void A_capsule_striking_the_middle_of_a_band_does_not_throw_the_corners()
    {
        var rig = CreateSheet(bounded: true, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        simulation.Step([Identity], [Capsule(SweepStart)], Identity, 0);
        var previousLeft = Tip(simulation, left, 0);
        var previousRight = Tip(simulation, right, left.Joints.Count);
        const int sweepSteps = 112;
        float surfaceSpeed = (SweepEnd - SweepStart) / (sweepSteps * StepSeconds);
        float peakAfterStop = 0;

        for (int frame = 1; frame <= sweepSteps + 25; frame++)
        {
            simulation.Step([Identity], [Capsule(SweepPosition(frame, sweepSteps))], Identity, StepSeconds);
            var nextLeft = Tip(simulation, left, 0);
            var nextRight = Tip(simulation, right, left.Joints.Count);
            if (frame > sweepSteps + 1)
            {
                float distance = MathF.Max(Vector3.Distance(nextLeft, previousLeft), Vector3.Distance(nextRight, previousRight));
                peakAfterStop = MathF.Max(peakAfterStop, distance / StepSeconds);
            }

            previousLeft = nextLeft;
            previousRight = nextRight;
        }

        Assert.True(MathF.Min(previousLeft.Z, previousRight.Z) > SweepEnd);
        Assert.InRange(peakAfterStop, 0, surfaceSpeed + 0.005f);
    }

    [Fact]
    public void An_embedded_sheet_resets_to_one_side_with_nonmonotonic_chain_indices()
    {
        var settings = Settings with { Gravity = 0, Stiffness = 0 };
        var tip = -0.5f * Vector3.UnitY;
        var left = Chain(tip, settings, Joint(-0.5f * Vector3.UnitX));
        var right = Chain(tip, settings, Joint(0.5f * Vector3.UnitX));
        var middle = Chain(tip, settings, Joint(Vector3.Zero));
        var forward = Solve(new(0, 2) { Stretch = 0.14f }, new(1, 2) { Stretch = 0.14f });
        var reversed = Solve(new(2, 0) { Stretch = 0.14f }, new(2, 1) { Stretch = 0.14f });

        Assert.Equal(3, forward.JointsFromAnchor.Length);
        Assert.True(MemoryMarshal.AsBytes(forward.JointsFromAnchor)
            .SequenceEqual(MemoryMarshal.AsBytes(reversed.JointsFromAnchor)));
        float side = Vector3.Transform(tip, forward.JointsFromAnchor[0]).Z;
        Assert.True(MathF.Abs(side) > 0.01f);
        Assert.True(side * Vector3.Transform(tip, forward.JointsFromAnchor[1]).Z > 0);
        Assert.True(side * Vector3.Transform(tip, forward.JointsFromAnchor[2]).Z > 0);

        ChainSimulation Solve(ChainLink first, ChainLink second)
        {
            var simulation = new ChainSimulation(new ChainRig(1, [left, right, middle], [first, second]), 2);
            var a = new Vector3(-0.25f, -0.375f, 0);
            var b = new Vector3(0.25f, -0.375f, 0);
            ColliderCapsule[] capsules = [new(a, a, 0.05f), new(b, b, 0.05f)];
            simulation.Step([Identity], capsules, Identity, 0);
            simulation.Step([Identity], capsules, Identity, StepSeconds);
            simulation.Step([Identity], capsules, Identity, StepSeconds);
            return simulation;
        }
    }

    [Fact]
    public void A_piercing_axis_stops_approach_when_the_bend_limit_keeps_the_sheet_in_contact()
    {
        var settings = Settings with { Gravity = 0, Stiffness = 0, Radius = SheetRadius, AngleLimit = 0.1f };
        var tip = -0.5f * Vector3.UnitY;
        var left = Chain(tip, settings, Joint(-0.65f * Vector3.UnitX));
        var right = Chain(tip, settings, Joint(0.65f * Vector3.UnitX));
        var rig = new ChainRig(1, [left, right], [new(0, 1) { Stretch = 0.14f }]);
        var previous = new ColliderCapsule(new(0, -0.35f, -0.6f), new(0, -0.35f, -0.2f), ObstacleRadius);
        var current = new ColliderCapsule(new(0, -0.35f, -0.4f), new(0, -0.35f, 0.1f), ObstacleRadius);
        var particles = new ChainParticles(rig, colliderCount: 1, placementRows: 2);
        particles.SetRest([Identity], reset: true);
        particles.SnapshotOpening();
        particles.SetCapsuleGeometry(0, current, previous);
        for (int chain = 0; chain < rig.Chains.Count; chain++)
            particles.Predict(chain, settings, Vector3.Zero, StepSeconds, Vector3.Zero, Vector3.Zero, Quaternion.Identity);

        new ChainLinks(rig, rig.Links, 1).FinishStep(particles, [Identity], [current], [previous],
            [settings, settings], StepSeconds);

        var positions = particles.Positions;
        float clearance = MathF.Min(AxisDistance(current, positions[0], positions[1], positions[3]),
            AxisDistance(current, positions[0], positions[3], positions[2]));
        Assert.InRange(clearance, 0, 0.00005f);
        Assert.InRange(positions[1].Z, 0, 0.051f);
        Assert.InRange(positions[3].Z, 0, 0.051f);
        float peak = MathF.Max(particles.Velocities[1].Z, particles.Velocities[3].Z);
        Assert.InRange(peak, 0.01f, 0.3f / StepSeconds + 0.0001f);
    }

    [Fact]
    public void A_thick_capsule_keeps_the_band_thickness_clear_through_a_sweep()
    {
        const float bandRadius = 0.03f;
        const float obstacleRadius = 0.16f;
        var settings = Settings with { Gravity = 0, Radius = bandRadius };
        var tip = -0.5f * Vector3.UnitY;
        var left = Chain(tip, settings, Joint(-0.04f * Vector3.UnitX));
        var right = Chain(tip, settings, Joint(0.04f * Vector3.UnitX));
        var rig = new ChainRig(1, [left, right], [new(0, 1) { Stretch = 0.14f }]);
        var simulation = new ChainSimulation(rig, 1);
        const int sweepSteps = 60;
        ColliderCapsule[] capsules = [CapsuleAt(0)];
        simulation.Step([Identity], capsules, Identity, 0);
        Assert.Equal(2, simulation.JointsFromAnchor.Length);

        float minimum = float.PositiveInfinity;
        for (int frame = 1; frame <= sweepSteps + 1; frame++)
        {
            var previous = capsules[0];
            capsules[0] = CapsuleAt(frame);
            simulation.Step([Identity], capsules, Identity, StepSeconds);
            float clearance = MeasureSheet(simulation, left, right, previous);
            Assert.True(clearance >= obstacleRadius + bandRadius - Tolerance,
                $"Clearance at step {frame - 1}: {clearance} m.");
            minimum = MathF.Min(minimum, clearance);
        }

        Assert.InRange(minimum, obstacleRadius + bandRadius - Tolerance, obstacleRadius + bandRadius + Tolerance);

        static ColliderCapsule CapsuleAt(int step)
        {
            float z = float.Lerp(-0.28f, -0.12f, Math.Min(step, sweepSteps) / (float)sweepSteps);
            return new ColliderCapsule(new(0, -0.37f, z), new(0, -0.35f, z), obstacleRadius);
        }
    }

    [Fact]
    public void Warm_sheet_contact_steps_allocate_nothing()
    {
        var rig = CreateSheet(bounded: true, out var left, out var right);
        var simulation = new ChainSimulation(rig, 1);
        Matrix4x4[] anchors = [Identity];
        ColliderCapsule[] capsules = [Capsule(SweepStart)];
        simulation.Step(anchors, capsules, Identity, 0);

        const int sweepSteps = 112;
        WarmSweep();
        float clearance = MeasureSheet(simulation, left, right, capsules[0]);
        Assert.InRange(clearance, Clearance - Tolerance, Clearance + Tolerance);

        AllocationMeasurement.NoPerCallAllocation("warm sheet contact", WarmSweep);

        void WarmSweep()
        {
            capsules[0] = Capsule(SweepStart);
            simulation.Step(anchors, capsules, Identity, 0);
            for (int frame = 1; frame <= sweepSteps + 1; frame++)
            {
                capsules[0] = Capsule(SweepPosition(frame, sweepSteps));
                simulation.Step(anchors, capsules, Identity, StepSeconds);
            }
            simulation.Reset();
        }
    }

    internal static ChainRig CreateSheet(bool bounded, out ChainRigChain left, out ChainRigChain right, bool layered = false)
    {
        // The capsule fits between both chains without reaching either chain's length sphere.
        var settings = Settings with { Gravity = 0, Damping = 0, Radius = SheetRadius };
        var segment = -0.5f * Vector3.UnitY;
        left = Chain(segment, settings with { Radius = SheetRadius / 2 }, Joint(-0.65f * Vector3.UnitX), Joint(segment));
        right = Chain(segment, settings, Joint(0.65f * Vector3.UnitX), Joint(segment));
        ChainLink[] links = [new(0, 1) { Stretch = bounded ? 0.14f : null }];
        if (!layered)
            return new ChainRig(1, [left, right], links);

        left = left with { Layer = AttachmentLayer.Outerwear };
        right = Chain(segment / 2, settings, Joint(0.65f * Vector3.UnitX), Joint(segment), Joint(segment / 2))
            with { Layer = AttachmentLayer.Outerwear };
        var lower = Chain(0.06f * Vector3.UnitX, settings with { Radius = ObstacleRadius, Trail = 0 },
            Joint(-0.03f * Vector3.UnitX)) with { Anchor = 1, Layer = AttachmentLayer.Armour };
        return new ChainRig(2, [left, right, lower], links);
    }

    private static Matrix4x4 LowerAnchor(float z) => Matrix4x4.CreateTranslation(0, -0.82f, z);

    private static ColliderCapsule Capsule(float z) => new(new(-0.03f, -0.82f, z), new(0.03f, -0.82f, z), ObstacleRadius);

    private static float SweepPosition(int frame, int steps) =>
        float.Lerp(SweepStart, SweepEnd, Math.Min(frame, steps) / (float)steps);

    private static Vector3 Tip(ChainSimulation simulation, ChainRigChain chain, int first) =>
        Vector3.Transform(chain.Tip, simulation.JointsFromAnchor[first + chain.Joints.Count - 1]);

    private static void AssertHemOpeningSide(ChainSimulation simulation, ChainRigChain left, ChainRigChain right,
        ColliderCapsule across)
    {
        float clearance = MeasureSheet(simulation, left, right, across);
        Assert.True(clearance >= Clearance - Tolerance, $"Clearance after the crossing: {clearance} m.");
        var joints = simulation.JointsFromAnchor;
        var upperLeft = joints[1].Translation;
        var upperRight = joints[left.Joints.Count + 1].Translation;
        var lowerLeft = Tip(simulation, left, 0);
        var lowerRight = Tip(simulation, right, left.Joints.Count);
        float first = AxisDistance(across, upperLeft, lowerLeft, lowerRight);
        float second = AxisDistance(across, upperLeft, lowerRight, upperRight);
        var normal = first <= second
            ? Vector3.Cross(lowerLeft - upperLeft, lowerRight - upperLeft)
            : Vector3.Cross(lowerRight - upperLeft, upperRight - upperLeft);
        var center = (across.Start + across.End) / 2;
        float side = Vector3.Dot(center - upperLeft, normal);
        Assert.True(side < 0, $"Opening-side witness {side}, triangle clearances {first} and {second}.");
    }

    internal static float MeasureSheet(ChainSimulation simulation, ChainRigChain left, ChainRigChain right,
        ColliderCapsule capsule, int first = 0, int? rightStart = null)
    {
        var joints = simulation.JointsFromAnchor;
        int second = rightStart ?? first + left.Joints.Count;
        int linkedRows = Math.Min(left.Joints.Count, right.Joints.Count);
        float minimum = float.PositiveInfinity;
        for (int row = 1; row <= linkedRows; row++)
        {
            var a = joints[first + row - 1].Translation;
            var b = joints[second + row - 1].Translation;
            var c = row == linkedRows ? Tip(simulation, left, first) : joints[first + row].Translation;
            var d = row == linkedRows ? Tip(simulation, right, second) : joints[second + row].Translation;
            minimum = MathF.Min(minimum, AxisDistance(capsule, a, c, d));
            minimum = MathF.Min(minimum, AxisDistance(capsule, a, d, b));
        }
        return minimum;
    }

    internal static float AxisDistance(ColliderCapsule capsule, Vector3 a, Vector3 b, Vector3 c)
    {
        // Distance to a convex triangle is convex along the axis. Minimize the point distance
        // independently of the solver's feature queries. The remaining interval is below 4 um here.
        float low = 0, high = 1;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            float left = (2 * low + high) / 3;
            float right = (low + 2 * high) / 3;
            if (DistanceAt(left) < DistanceAt(right))
                high = right;
            else
                low = left;
        }
        return DistanceAt((low + high) / 2);

        float DistanceAt(float fraction)
        {
            var p = Vector3.Lerp(capsule.Start, capsule.End, fraction);
            var normal = Vector3.Cross(b - a, c - a);
            if (normal != Vector3.Zero && Vector3.Dot(normal, Vector3.Cross(b - a, p - a)) >= 0 &&
                Vector3.Dot(normal, Vector3.Cross(c - b, p - b)) >= 0 &&
                Vector3.Dot(normal, Vector3.Cross(a - c, p - c)) >= 0)
                return MathF.Abs(Vector3.Dot(p - a, normal)) / normal.Length();

            return MathF.Min(ChainSegmentContactTests.DistanceToSegment(p, a, b),
                MathF.Min(ChainSegmentContactTests.DistanceToSegment(p, b, c),
                    ChainSegmentContactTests.DistanceToSegment(p, c, a)));
        }
    }
}
