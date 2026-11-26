using System;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

public sealed class WornClothHeldSupportTests
{
    [Theory]
    [InlineData(false, 0, 90)]
    [InlineData(true, 45, 135)]
    [InlineData(true, 5, 120)]
    public void A_rotating_held_segment_reaches_the_pinned_seam_along_its_arc(bool subTick, int startsAt, int endsAt)
    {
        using var rig = new OutfitRig();
        var cloth = rig.Wear("arm-cloth.json", ClothAsset(rig, hipOnlyPins: true).Set);
        var glb = SkinnedAttachmentGlbFixture.Chained();
        glb.Nodes[0]!["children"] = new JsonArray(1);
        glb.Nodes[1]!["children"] = new JsonArray(2);
        glb.Nodes[2]!["translation"] = new JsonArray(0, 0, 0);
        glb.Nodes[3]!["translation"] = new JsonArray(.1f, 0, 0);
        glb.SetInverseBind(0, Matrix4x4.CreateTranslation(-.1f, -2, 0));
        glb.SetInverseBind(2, Matrix4x4.CreateTranslation(0, -2, 0));
        const uint armId = 124;
        var arm = new SkinnedAttachmentPartDefinition(armId, rig.Source.Mesh("rotating-arm.glb", glb.Build()))
        {
            Motion = new ChainMotionDefinition([new ChainDefinition(["left0", "left1"], .1f * Vector3.UnitX, FallSettings)], []),
            Regions = [new("alpha", 1)],
        };
        var lower = rig.Wear("rotating-arm.json", new AttachmentSetDefinition([arm], AttachmentLayer.Garment, []));
        using var outfit = rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [lower, cloth]);
        var motion = outfit.CreateMotion(null, new ChainSimulationOptions { HoldBlendSeconds = 0 });
        var surface = outfit.ClothSurface(1, ClothPartId)!;
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var select = new WornClothSelection(slot, 1, ClothPartId);
        var pose = (Matrix4x4[])rig.Palette.Clone();
        var pin = surface.FittedNodes[1];
        float midReach = .2f * MathF.Sqrt(.5f);
        var anchor = subTick ? pin + .2f * Vector3.UnitZ : pin + new Vector3(-midReach, 0, midReach);

        float startAngle = startsAt * (MathF.PI / 180);
        float endAngle = endsAt * (MathF.PI / 180);
        float endpointChordGap = subTick ? .2f * (1 - MathF.Max(MathF.Sin(startAngle), MathF.Sin(endAngle))) : midReach - .1f;
        float contactReach = FallSettings.Radius + surface.Rig.Material.HalfThickness;
        Assert.True(endpointChordGap > contactReach);
        if (startsAt == 5)
            Assert.True(.2f * (1 - MathF.Sin(.5f * (startAngle + endAngle))) > contactReach);
        pose[0] = Matrix4x4.CreateRotationY(startAngle) * Matrix4x4.CreateTranslation(anchor);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, select);
        Assert.True(motion.TryGetClothPresentation(1, ClothPartId, out var opening));
        ulong revision = opening.DisplayRevision;
        pose[0] = Matrix4x4.CreateRotationY(endAngle) * Matrix4x4.CreateTranslation(anchor);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, subTick ? .004f : 1f / 60, MotionLevel.Held, select);
        Assert.Equal(subTick ? 0 : 1, motion.ClothStatus.Substeps);
        ClothStepResult contact = subTick ? motion.ClothStatus.Projection : motion.ClothStatus.Solve;
        if (startsAt == 5)
            Assert.True(contact.Status is ClothStepStatus.UnresolvedContact or ClothStepStatus.SweepWork);
        else
            Assert.Equal(ClothStepStatus.UnresolvedContact, contact.Status);
        Assert.Equal(armId, contact.PartId);
        if (subTick)
        {
            Assert.Equal(WornClothFallback.TransferRefused, motion.ClothStatus.Fallback);
            Assert.True(motion.TryGetClothPresentation(1, ClothPartId, out var retained));
            Assert.Equal(revision, retained.DisplayRevision);
        }

    }
    [Fact]
    public void Fitted_held_joint_shapes_preserve_segment_length_at_the_shared_tick()
    {
        var joint = new ChainJointRest(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var arm = new ChainRigChain(0, [joint, joint with { Translation = .2f * Vector3.UnitX }],
            .2f * Vector3.UnitX, FallSettings);
        var rig = new ChainRig(1, [arm], []);
        var simulation = new ChainSimulation(rig, 1, new ChainSimulationOptions { HoldBlendSeconds = 0 }, fitRestContacts: true);
        Matrix4x4[] anchors = [Matrix4x4.Identity];
        ColliderCapsule[] rest = [new(new(.15f, .01f, .5f), new(.15f, .01f, .5f), .06f)];
        ColliderCapsule[] fitted = [new(new(.15f, .01f, 0), new(.15f, .01f, 0), .06f)];
        simulation.Step(anchors, rest, Matrix4x4.Identity, 0, MotionLevel.Held);
        var segment = new ChainSegment(rig, 0, 0);
        var old = simulation.ShownSegment(segment);
        var probe = new HeldLengthProbe(segment);
        simulation.PrepareStep(anchors, fitted, Matrix4x4.Identity, 1f / 60, MotionLevel.Held);
        simulation.StepPrepared(anchors, fitted, Matrix4x4.Identity, 1f / 60, MotionLevel.Held, probe);
        var current = simulation.ShownSegment(segment);
        Assert.True(Vector3.Distance(old.End, current.End) > .02f);
        Assert.InRange(probe.MiddleLength, .199999f, .200001f);
    }

    [Fact]
    public void An_independently_turning_parent_contributes_to_the_child_capsule_sweep_bound()
    {
        var anchor = new ChainPose(Matrix4x4.Identity);
        var firstRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4);
        var lastRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 3 * MathF.PI / 4);
        var parentAxis = .2f * Vector3.UnitX;
        Vector3 firstParent = Vector3.Transform(parentAxis, firstRotation);
        Vector3 lastParent = Vector3.Transform(parentAxis, lastRotation);
        var first = new HeldChainSegment(new(firstParent, firstParent + .1f * Vector3.UnitY, .01f),
            Quaternion.Identity, anchor, Vector3.Zero);
        var last = new HeldChainSegment(new(lastParent, lastParent + .1f * Vector3.UnitY, .01f),
            Quaternion.Identity, anchor, Vector3.Zero);
        var id = new ClothSupportIdentity(1, 1, 0, AttachmentLayer.Garment);
        var before = ClothSupport.HeldLowerSegment(first, ClothHeldEdge.From(first), id, 0, 1);
        var after = ClothSupport.HeldLowerSegment(last, ClothHeldEdge.From(last), id, 0, 1);
        var paths = new ClothHeldPaths([new(firstRotation, parentAxis)], [new(lastRotation, parentAxis)]);

        var mid = before.CapsuleAt(after, .5f, paths);
        Assert.InRange(Vector3.Distance(mid.Start, -.2f * Vector3.UnitZ), 0, .000001f);
        Assert.InRange(Vector3.Distance(mid.Start, mid.End), .099999f, .100001f);
        Assert.True(.125f * before.CurvedCapsuleReach(after, 0, paths) >= mid.Start.Length() - MathF.Abs(firstParent.Z));
    }

    private sealed class HeldLengthProbe(ChainSegment segment) : IChainExternalStep
    {
        public ChainExclusion Excluded => default;
        internal float MiddleLength { get; private set; }
        public void OnDroppedTime() { }

        public void OnSubstep(ChainSimulation simulation, ChainParticles? particles, float fraction)
        {
            if (fraction >= .75f) return;
            var sample = simulation.HeldSegment(segment, fraction);
            MiddleLength = Vector3.Distance(sample.Start, sample.End);
        }
    }

}
