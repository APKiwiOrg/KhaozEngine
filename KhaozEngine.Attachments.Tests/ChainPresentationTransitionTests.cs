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
}
