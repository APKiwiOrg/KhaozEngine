using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainPresentationTransitionTests
{
    [Theory]
    [InlineData(MotionLevel.Held)]
    [InlineData(MotionLevel.Simulated)]
    public void Blend_keeps_its_arc_and_lengths_across_stretch_changes_at_a_half_turn(MotionLevel level)
    {
        var segment = -Vector3.UnitY;
        var chain = Chain(segment, Settings with { Gravity = 0, Damping = 0 },
            Joint(Vector3.Zero), Joint(segment));
        var rig = new ChainRig(1, [chain], []);
        const float step = 1f / 64;
        const float frame = step / 8;
        const float duration = step * 8;
        var options = new ChainSimulationOptions { MaxStepSeconds = step, HoldBlendSeconds = duration };
        var shown = new ChainSimulation(rig, 0, options);
        var live = new ChainSimulation(rig, 0, options with { HoldBlendSeconds = 0 });
        var worlds = new Matrix4x4[2];
        var rigid = Matrix4x4.CreateRotationZ(179 * (MathF.PI / 180));

        if (level == MotionLevel.Held)
        {
            Step(shown, 0);
            shown.Step([rigid], [], Matrix4x4.Identity, step);
            shown.Step([rigid], [], Matrix4x4.Identity, step / 2);
        }
        else
        {
            shown.Step([Matrix4x4.Identity], [], Matrix4x4.Identity, 0, MotionLevel.Held);
            live.Step([Matrix4x4.Identity], [], Matrix4x4.Identity, 0, MotionLevel.Held);
            shown.Step([rigid], [], Matrix4x4.Identity, step * 4);
            live.Step([rigid], [], Matrix4x4.Identity, step * 4);
        }

        float remaining = level == MotionLevel.Held ? duration : duration - step * 4;
        float[] scales = [1, 0.75f, 0.75f, 1, 1];
        for (int index = 0; index < scales.Length; index++)
        {
            var anchor = Matrix4x4.CreateScale(1, scales[index], 1) *
                Matrix4x4.CreateRotationZ((179 + index) * (MathF.PI / 180));
            var start = shown.JointsFromAnchor[0] * anchor;
            var target = anchor;
            if (level == MotionLevel.Simulated)
            {
                live.Step([anchor], [], Matrix4x4.Identity, frame);
                target = live.JointsFromAnchor[0] * anchor;
            }

            float sweep = MathF.IEEERemainder(Heading(target) - Heading(start), MathF.Tau);
            float angle = Heading(start) + sweep * (frame / remaining);
            var expectedDirection = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
            shown.Step([anchor], [], Matrix4x4.Identity, frame, level);
            shown.ComposeJointWorlds([anchor], worlds);

            var actualDirection = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, worlds[0]));
            Assert.InRange(Vector3.Distance(expectedDirection, actualDirection), 0, 0.00001f);
            Assert.InRange(Vector3.Distance(worlds[1].Translation, worlds[0].Translation), scales[index] - 0.00001f,
                scales[index] + 0.00001f);
            Assert.InRange(Vector3.TransformNormal(segment, worlds[1]).Length(), scales[index] - 0.00001f,
                scales[index] + 0.00001f);
            remaining -= frame;
        }

        static float Heading(Matrix4x4 matrix) => MathF.Atan2(matrix.M12, matrix.M11);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_near_singular_anchor_preserves_legacy_display_when_reanchored(bool stretch)
    {
        // The second row differs from the first by 2^-26. Turning both 45 degrees rounds out that separation.
        var sampledAnchor = Matrix4x4.Identity;
        sampledAnchor.M21 = 1;
        sampledAnchor.M22 = 1f / (1 << 26);
        var shownAnchor = stretch ? sampledAnchor * Matrix4x4.CreateScale(1, 1.000002f, 1) : sampledAnchor;
        var segment = Vector3.UnitX;
        var chain = Chain(segment, Settings with { Gravity = 1, Damping = 0 },
            Joint(Vector3.Zero), Joint(segment));
        var rig = new ChainRig(1, [chain], []);
        const float step = 1f / 64;
        // The first step adds exactly one unit in Y to a one-unit X segment at rest.
        var options = new ChainSimulationOptions { MaxStepSeconds = step, Gravity = new Vector3(0, 8192, 0) };
        var grid = new ChainSimulation(rig, 0, options);
        var display = new ChainSimulation(rig, 0, options);
        grid.Step([sampledAnchor], [], Matrix4x4.Identity, 0);
        grid.Step([sampledAnchor], [], Matrix4x4.Identity, step);
        grid.Step([sampledAnchor], [], Matrix4x4.Identity, step);

        display.Step([sampledAnchor], [], Matrix4x4.Identity, 0);
        display.Step([sampledAnchor], [], Matrix4x4.Identity, step);
        display.Step([shownAnchor], [], Matrix4x4.Identity, step / 2);

        var expected = LegacyFrames(rig, rig.RestFromAnchor, grid.JointsFromAnchor, shownAnchor);
        Assert.Equal(expected, display.JointsFromAnchor.ToArray());
    }

    [Fact]
    public void A_near_singular_rest_basis_preserves_legacy_display_on_a_rigid_anchor()
    {
        var parent = Joint(Vector3.Zero) with { Scale = new Vector3(1, 1f / (1 << 26), 1) };
        var child = Joint(Vector3.UnitX) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4) };
        var rig = new ChainRig(1, [Chain(Vector3.UnitX, Settings, parent, child)], []);
        var from = Sample(rig, [Quaternion.Identity, Quaternion.Identity]);
        var to = Sample(rig, [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4)]);
        var expected = LegacyFrames(rig, from.Frames, to.Frames, Matrix4x4.Identity);
        var output = new ChainPresentationFrame(rig);

        new ChainPresentationHierarchy(rig).Interpolate(from, to, output, [new ChainPose(Matrix4x4.Identity)],
            [new ChainPresentationAnchor(Matrix4x4.Identity, Quaternion.Identity, 1)], .5f);

        Assert.Equal(expected, output.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_endpoint_swing_at_the_arc_tie_preserves_every_legacy_joint_frame(bool stretch)
    {
        var segment = -Vector3.UnitX;
        var rig = new ChainRig(1, [Chain(segment, Settings, Joint(Vector3.Zero), Joint(segment), Joint(segment))], []);
        var from = Sample(rig, [Quaternion.Identity, Quaternion.Identity, Quaternion.Identity]);
        // The same half-turn matrix can be read back with the opposite quaternion sign.
        // At a zero dot product that sign chooses which way the child carries its descendants.
        var to = Sample(rig, [Quaternion.Identity, new Quaternion(0, 0, -1, 0), Quaternion.Identity]);
        var shownAnchor = stretch ? Matrix4x4.CreateScale(1, 1.000002f, 1) : Matrix4x4.Identity;
        var expected = LegacyFrames(rig, from.Frames, to.Frames, shownAnchor);
        Assert.True(ChainMath.TryInvert(shownAnchor, out var inverse));

        var pose = new ChainPose(shownAnchor);
        new ChainPresentationHierarchy(rig).Interpolate(from, to, from, [pose],
            [new ChainPresentationAnchor(shownAnchor, inverse, pose.Rotation)], .5f);

        Assert.Equal(expected, from.Frames);
    }

    private static ChainPresentationFrame Sample(ChainRig rig, ReadOnlySpan<Quaternion> swings)
    {
        var sample = new ChainPresentationFrame(rig);
        sample.SetRest([new ChainPresentationAnchor(Matrix4x4.Identity, Quaternion.Identity, 1)]);
        swings.CopyTo(sample.Swings);
        var parent = Matrix4x4.Identity;
        for (int joint = 0; joint < rig.JointCount; joint++)
        {
            var unswung = rig.RestLocal(joint) * parent;
            parent = unswung * Matrix4x4.CreateFromQuaternion(swings[joint]);
            parent.Translation = unswung.Translation;
            sample.Frames[joint] = parent;
        }
        return sample;
    }

    private static Matrix4x4[] LegacyFrames(ChainRig rig, ReadOnlySpan<Matrix4x4> from,
        ReadOnlySpan<Matrix4x4> to, Matrix4x4 anchor)
    {
        Assert.True(ChainMath.TryInvert(anchor, out var inverse));
        var expected = new Matrix4x4[rig.JointCount];
        var parent = anchor;
        for (int joint = 0; joint < expected.Length; joint++)
        {
            var local = rig.RestLocal(joint);
            var fromSwing = LegacySwing(from, joint, local, anchor);
            var toSwing = LegacySwing(to, joint, local, anchor);
            var unswung = local * parent;
            parent = unswung * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.Slerp(fromSwing, toSwing, .5f)));
            parent.Translation = unswung.Translation;
            expected[joint] = parent * inverse;
        }
        expected[0].Translation = rig.RestFromAnchor[0].Translation;
        return expected;
    }

    private static Quaternion LegacySwing(ReadOnlySpan<Matrix4x4> frames, int joint, Matrix4x4 local, Matrix4x4 anchor)
    {
        var parent = joint == 0 ? anchor : frames[joint - 1] * anchor;
        var unswung = ChainPose.ReadRotation(local * parent);
        return ChainPose.ReadRotation(frames[joint] * anchor) * Quaternion.Conjugate(unswung);
    }
}
