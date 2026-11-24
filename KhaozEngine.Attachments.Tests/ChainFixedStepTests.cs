using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainFixedStepTests(ITestOutputHelper output)
{
    private const int TicksPerStep = 16;
    private const float StepSeconds = 1f / 64;
    private const float TickSeconds = StepSeconds / TicksPerStep;
    private const float BreathAmplitude = 0.0075f;

    [Fact]
    public void Breathing_chain_follows_the_same_path_under_uneven_pacing()
    {
        // #1883: keep the shown path stable when render frames arrive at uneven intervals.
        var segment = new Vector3(0, -0.3f, 0);
        var chain = Chain(segment, new ChainSettings(12, 5, 0, 0.65f, MathF.PI, 0),
            Joint(Vector3.Zero), Joint(segment), Joint(segment));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds };
        var fixedRun = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);
        var uneven = new ChainSimulation(fixedRun.Rig, 0, options);
        const int settledStep = 3 * 64;
        const int finalStep = 8 * 64;
        var reference = new Vector3[finalStep + 1];

        At(fixedRun, 0, 0);
        At(uneven, 0, 0);
        for (int step = 1; step <= finalStep; step++)
        {
            int ticks = step * TicksPerStep;
            At(fixedRun, ticks, StepSeconds);
            reference[step] = Tip(fixedRun);
            if (step <= settledStep)
                At(uneven, ticks, StepSeconds);
        }

        float peakDeviation = 0;
        var random = new Random(1883);
        int elapsed = settledStep * TicksPerStep;
        Assert.True(Vector3.Distance(reference[settledStep], reference[settledStep + 64]) > 0.0001f);
        for (int frame = 1; elapsed < finalStep * TicksPerStep; frame++)
        {
            int ticks = Math.Min(UnevenTicks(random, frame), finalStep * TicksPerStep - elapsed);
            elapsed += ticks;
            At(uneven, elapsed, ticks * TickSeconds);
            int lower = elapsed / TicksPerStep;
            int upper = Math.Min(lower + 1, finalStep);
            float fraction = (elapsed % TicksPerStep) / (float)TicksPerStep;
            var expected = Vector3.Lerp(reference[lower], reference[upper], fraction);
            peakDeviation = MathF.Max(peakDeviation, Vector3.Distance(expected, Tip(uneven)));
        }

        output.WriteLine($"Peak uneven pacing deviation: {peakDeviation:F9} m");
        // The interpolated inputs approximate the same sinusoid from different sample times.
        Assert.InRange(peakDeviation, 0, 0.01f * BreathAmplitude);

        static void At(ChainSimulation simulation, int ticks, float elapsed)
        {
            float phase = MathF.Tau * 0.35f * (ticks * TickSeconds);
            var wearer = Matrix4x4.CreateTranslation(BreathAmplitude * MathF.Sin(phase), 0, 0);
            simulation.Step([wearer], [], wearer, elapsed);
        }
    }

    [Fact]
    public void Frozen_pose_in_contact_stops_moving_under_uneven_pacing()
    {
        // #1883: the sphere pushes the settled tip beyond its bend limit.
        var segment = new Vector3(0.25f, 0, 0);
        var chain = Chain(segment, new ChainSettings(14, 10, 1, 1, 0.12f, 0.02f), Joint(Vector3.Zero));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds };
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 1, options);
        var center = new Vector3(0.23f, -0.04f, 0.04f);
        ColliderCapsule sphere = new(center, center, 0.12f);

        At(0);
        for (int step = 0; step < 4 * 64; step++)
            At(StepSeconds);

        var previous = Tip(simulation);
        float fixedPeak = 0;
        for (int step = 0; step < 32; step++)
        {
            At(StepSeconds);
            var current = Tip(simulation);
            fixedPeak = MathF.Max(fixedPeak, Vector3.Distance(previous, current));
            previous = current;
        }
        float clearance = sphere.Radius + chain.Settings.Radius;
        float distance = ChainSegmentContactTests.DistanceToSegment(center, Vector3.Zero, previous);
        Assert.InRange(MathF.Abs(distance - clearance), 0, 0.00002f);
        Assert.InRange(fixedPeak, 0, 0.000003f);
        float peakTravel = 0;
        var random = new Random(1883);
        for (int frame = 1; frame <= 130; frame++)
        {
            At(UnevenTicks(random, frame) * TickSeconds);
            var current = Tip(simulation);
            peakTravel = MathF.Max(peakTravel, Vector3.Distance(previous, current));
            previous = current;
        }

        output.WriteLine($"Peak frozen contact travel: fixed {fixedPeak:F9} m, uneven {peakTravel:F9} m");
        Assert.InRange(peakTravel, 0, 0.000003f);

        void At(float elapsed) => simulation.Step([Matrix4x4.Identity], [sphere], Matrix4x4.Identity, elapsed);
    }

    [Fact]
    public void Substep_frames_show_between_grid_samples_on_the_current_anchor()
    {
        var root = new Vector3(0.375f, 0.125f, 0);
        var chain = Chain(Vector3.UnitX, Settings with { Trail = 0, Damping = 0 },
            Joint(root), Joint(Vector3.UnitX));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds };
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);
        var worlds = new Matrix4x4[2];

        At(0, 0);
        for (int frame = 1; frame <= 4; frame++)
            At(frame, StepSeconds / 2);
        var earlier = simulation.JointsFromAnchor[1];
        At(5, StepSeconds / 2);
        var midway = simulation.JointsFromAnchor[1];
        var midwayParent = simulation.JointsFromAnchor[0];
        At(6, StepSeconds / 2);
        var later = simulation.JointsFromAnchor[1];

        Assert.True(Vector3.Distance(earlier.Translation, later.Translation) > 0.0001f);
        var expectedTranslation = Vector3.Transform(Vector3.UnitX, midwayParent);
        var expectedRotation = Quaternion.Slerp(Quaternion.CreateFromRotationMatrix(earlier),
            Quaternion.CreateFromRotationMatrix(later), 0.5f);
        Assert.InRange(Vector3.Distance(expectedTranslation, midway.Translation), 0, 0.00001f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, expectedRotation),
            Vector3.TransformNormal(Vector3.UnitX, midway)), 0, 0.00001f);

        var previous = simulation.JointsFromAnchor.ToArray();
        At(7, 0);
        Assert.True(simulation.JointsFromAnchor.SequenceEqual(previous));

        void At(int frame, float elapsed)
        {
            var pose = Matrix4x4.CreateTranslation(frame * 0.25f, frame * 0.0625f, 0);
            simulation.Step([pose], [], pose, elapsed);
            simulation.ComposeJointWorlds([pose], worlds);
            Assert.InRange(Vector3.Distance(Vector3.Transform(root, pose), worlds[0].Translation), 0, 0.00001f);
        }
    }

    [Fact]
    public void Hold_between_steps_starts_from_the_interpolated_shape()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds };
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);
        var pose = Matrix4x4.CreateTranslation(1, 0, 0);

        Step(simulation, 0);
        simulation.Step([pose], [], pose, StepSeconds);
        simulation.Step([pose], [], pose, StepSeconds / 2);
        var before = Tip(simulation);
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(before, -Vector3.UnitY), -1, 1));
        Assert.InRange(angle, 0.1f, MathF.PI / 2);

        const float heldSeconds = StepSeconds / 4;
        simulation.Step([pose], [], pose, heldSeconds, MotionLevel.Held);
        float maximumTravel = 2 * MathF.Sin(angle * (heldSeconds / options.HoldBlendSeconds) / 2);
        Assert.InRange(Vector3.Distance(before, Tip(simulation)), 0.0001f, maximumTravel + 0.00001f);
    }

    [Fact]
    public void Holding_and_resuming_a_swung_chain_preserve_every_segment_length()
    {
        // #1883: blending positions independently shortens segments during a large swing.
        var segment = -Vector3.UnitY;
        var chain = Chain(segment, Settings with { Gravity = 0 },
            Joint(Vector3.Zero), Joint(segment), Joint(segment));
        var options = new ChainSimulationOptions { MaxStepSeconds = StepSeconds, HoldBlendSeconds = 8 * StepSeconds };
        var simulation = new ChainSimulation(new ChainRig(1, [chain], []), 0, options);
        var turned = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        var worlds = new Matrix4x4[3];

        Step(simulation, 0);
        At(turned, MotionLevel.Simulated);
        At(turned, MotionLevel.Simulated);
        Assert.True(Vector3.Distance(3 * segment, Tip(simulation)) > 2);
        Assert.InRange(LargestLengthError(turned), 0, 0.00001f);

        float heldError = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            At(turned, MotionLevel.Held);
            heldError = MathF.Max(heldError, LargestLengthError(turned));
        }
        var held = Tip(simulation);

        float resumedError = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            At(Matrix4x4.Identity, MotionLevel.Simulated);
            resumedError = MathF.Max(resumedError, LargestLengthError(Matrix4x4.Identity));
        }

        Assert.True(Vector3.Distance(held, Tip(simulation)) > 0.1f);
        output.WriteLine($"Largest segment length error: held {heldError:F9} m, resumed {resumedError:F9} m");
        Assert.InRange(heldError, 0, 0.00001f);
        Assert.InRange(resumedError, 0, 0.00001f);

        void At(Matrix4x4 anchor, MotionLevel level) => simulation.Step([anchor], [], Matrix4x4.Identity, StepSeconds, level);

        float LargestLengthError(Matrix4x4 anchor)
        {
            simulation.ComposeJointWorlds([anchor], worlds);
            float maximum = 0;
            for (int joint = 0; joint < worlds.Length; joint++)
            {
                var end = joint + 1 < worlds.Length ? worlds[joint + 1].Translation : Vector3.Transform(chain.Tip, worlds[joint]);
                maximum = MathF.Max(maximum, MathF.Abs(Vector3.Distance(worlds[joint].Translation, end) - segment.Length()));
            }
            return maximum;
        }
    }

    private static int UnevenTicks(Random random, int frame) =>
        frame % 23 == 0 ? 69 : random.Next(5, 28);

    private static Vector3 Tip(ChainSimulation simulation) =>
        Vector3.Transform(simulation.Rig.Chains[0].Tip, simulation.JointsFromAnchor[^1]);
}
