using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainCapsuleCullTests
{
    [Theory]
    [InlineData(-0.125f, true)]
    [InlineData(0, true)]
    [InlineData(0.00005f, true)]
    [InlineData(0.125f, false)]
    [InlineData(0, true, 80)]
    public void Chain_keeps_capsules_that_overlap_reach_or_can_stop_contact_velocity(float beyondReach, bool kept,
        int colliderCount = 1)
    {
        var cull = new ChainCapsuleCull(colliderCount);
        float[] lengths = [0.5f, 0.25f];
        const float chainRadius = 0.0625f, capsuleRadius = 0.125f;
        var root = new Vector3(2, 1, -1);
        float boundary = lengths[0] + lengths[1] + chainRadius + capsuleRadius;
        var axisCenter = root + Vector3.UnitX * (boundary + beyondReach);
        var capsules = new ColliderCapsule[colliderCount];
        var far = root + 8 * Vector3.UnitX;
        Array.Fill(capsules, new ColliderCapsule(far, far, capsuleRadius));
        capsules[^1] = new(axisCenter - Vector3.UnitY, axisCenter + Vector3.UnitY, capsuleRadius);

        var candidates = cull.Select(root, lengths, chainRadius, capsules);

        Assert.Equal(kept ? 1 : 0, candidates.Count);
        if (kept) Assert.Equal(colliderCount - 1, candidates.OriginalIndex(0));
    }

    [Theory]
    [InlineData(4, 4f, 30f, false)]
    [InlineData(4, 4f, 30f, true)]
    [InlineData(65, 0.5f, 0.25f, false)] // Every capsule is in reach of both chains in the crowded case.
    public void Cull_preserves_joint_frame_bits_across_linked_passes_and_moving_colliders(
        int colliderCount, float chainSpacing, float fillerX, bool bounded)
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Radius = 0.0625f, Stiffness = 4 },
            Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var rig = new ChainRig(2, [chain, chain with { Anchor = 1 }], [new(0, 1) { Stretch = bounded ? 0.025f : null }]);
        var culled = new ChainSimulation(rig, colliderCount);
        var all = new ChainSimulation(rig, colliderCount);
        var noContact = new ChainSimulation(rig, 0);
        var separation = chainSpacing * Vector3.UnitX;
        Matrix4x4[] anchors = new Matrix4x4[2];
        ColliderCapsule[] capsules = new ColliderCapsule[colliderCount];
        var filler = new Vector3(fillerX, 0, 0.5f);
        Array.Fill(capsules, new(filler, filler, 0.125f));
        noContact.Step([Matrix4x4.Identity, Matrix4x4.CreateTranslation(separation)], [], Matrix4x4.Identity, 0);

        for (int frame = 0; frame <= 48; frame++)
        {
            var wearer = Matrix4x4.CreateTranslation(0, 0, frame * 0.02f);
            anchors[0] = Matrix4x4.CreateRotationZ(frame * 0.01f) * wearer;
            anchors[1] = Matrix4x4.CreateRotationZ(-frame * 0.02f) * Matrix4x4.CreateTranslation(separation) * wearer;
            var p = new Vector3(-0.125f + 0.004f * frame, -1, wearer.Translation.Z + 0.125f);
            capsules[0] = new(p, p - Vector3.UnitY, 0.25f);
            capsules[^2] = new(p + separation, p + separation - Vector3.UnitY, 0.25f);
            var q = new Vector3(0, -1.875f, wearer.Translation.Z - 0.25f);
            capsules[^1] = new(q, q, 0.25f);

            float delta = frame == 0 ? 0 : 1f / 60;
            culled.Step(anchors, capsules, wearer, delta);
            StepWithAllCapsules(all, anchors, capsules, wearer, delta);
            AssertSameFrames(rig.JointCount, all, culled);
            if (frame == 1)
            {
                noContact.Step(anchors, [], wearer, delta);
                Assert.NotEqual(noContact.JointsFromAnchor[^1], all.JointsFromAnchor[^1]);
            }
        }
    }

    [Fact]
    public void Correction_outside_padded_reach_replays_with_the_culled_capsule()
    {
        // The unit tip starts 0.125 from the near center. A correction to (1.5, 0, 0)
        // puts it 0.125 from the far center, whose X alone is beyond the nominal reach of 1.25.
        var chain = Chain(Vector3.UnitX, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        var near = new Vector3(0.875f, 0, 0);
        var far = new Vector3(1.5f, 0.125f, 0);
        ColliderCapsule[] capsules = [new(near, near, 0.25f), new(far, far, 0.25f)];
        var kept = new ChainCapsuleCull(capsules.Length).Select(Vector3.Zero, [chain.Tip.Length()], chain.Settings.Radius, capsules);
        Assert.Equal(1, kept.Count);
        Assert.Equal(0, kept.OriginalIndex(0));

        Matrix4x4[] anchors = [Matrix4x4.Identity];
        ColliderCapsule[] nearOnly = [capsules[0]];
        var all = AtRest(capsules);
        var culled = AtRest(capsules);
        var withoutFarCapsule = AtRest(nearOnly);
        const float step = 1f / 120;
        ChainSegmentContact.SetFirstCorrectionDirectionForTests(new Vector3(1.5f, 0, 0));
        try
        {
            StepWithAllCapsules(all, anchors, capsules, Matrix4x4.Identity, step);
            culled.Step(anchors, capsules, Matrix4x4.Identity, step);
            withoutFarCapsule.Step(anchors, nearOnly, Matrix4x4.Identity, step);
            // Presentation shows the first simulated sample at the next grid boundary.
            StepWithAllCapsules(all, anchors, capsules, Matrix4x4.Identity, step);
            culled.Step(anchors, capsules, Matrix4x4.Identity, step);
            withoutFarCapsule.Step(anchors, nearOnly, Matrix4x4.Identity, step);
        }
        finally
        {
            ChainSegmentContact.SetFirstCorrectionDirectionForTests(null);
        }

        Assert.NotEqual(withoutFarCapsule.JointsFromAnchor[0], all.JointsFromAnchor[0]);
        AssertSameFrames(1, all, culled);

        ChainSimulation AtRest(ColliderCapsule[] colliders)
        {
            var simulation = Create(chain, colliders.Length);
            simulation.Step(anchors, colliders, Matrix4x4.Identity, 0);
            return simulation;
        }
    }

    [Fact]
    public void Cull_preserves_contacts_reached_by_rounded_world_positions()
    {
        // At 2^24 the float spacing is two. The first 1.125-unit segment rounds up to two
        // on addition to the root. The next tip can contact the sphere beyond the nominal reach.
        var anchor = Matrix4x4.CreateTranslation(16_777_216f, 0, 0);
        var chain = Chain(new(1.125f, 0, 0), Settings with { Gravity = 0 },
            Joint(Vector3.Zero), Joint(new(1.125f, 0, 0)));
        var all = Create(chain, 1);
        var culled = Create(chain, 1);
        var center = anchor.Translation + 4 * Vector3.UnitX;
        Matrix4x4[] anchors = [anchor];
        ColliderCapsule[] capsules = [new(center, center, 1)];
        all.Step(anchors, capsules, anchor, 0);
        culled.Step(anchors, capsules, anchor, 0);

        for (int step = 0; step < 2; step++)
        {
            culled.Step(anchors, capsules, anchor, 1f / 120);
            StepWithAllCapsules(all, anchors, capsules, anchor, 1f / 120);
        }

        Assert.NotEqual(Quaternion.Identity, Quaternion.CreateFromRotationMatrix(all.JointsFromAnchor[1]));
        AssertSameFrames(2, all, culled);
    }

    private static void StepWithAllCapsules(ChainSimulation simulation, Matrix4x4[] anchors,
        ColliderCapsule[] capsules, in Matrix4x4 wearer, float delta)
    {
        ChainCapsuleCull.DisableForTests = true;
        try
        {
            simulation.Step(anchors, capsules, wearer, delta);
        }
        finally
        {
            ChainCapsuleCull.DisableForTests = false;
        }
    }

    private static void AssertSameFrames(int count, ChainSimulation all, ChainSimulation culled)
    {
        Assert.Equal(count, all.JointsFromAnchor.Length);
        Assert.Equal(count, culled.JointsFromAnchor.Length);
        Assert.True(MemoryMarshal.AsBytes(all.JointsFromAnchor).SequenceEqual(MemoryMarshal.AsBytes(culled.JointsFromAnchor)));
    }
}
