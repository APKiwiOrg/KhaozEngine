using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WornMotionTests
{
    const float Frame = 1f / 60;
    static readonly ColliderDefinition DistantCapsule = new("hip", Vector3.UnitZ * 4, Vector3.UnitZ * 5, .125f);

    [Fact]
    public void Body_colliders_are_verified_cached_by_reference_and_checked_against_the_requested_rig()
    {
        using var rig = new OutfitRig();
        var definition = new BodyColliderSet(OutfitRig.Rig, [DistantCapsule]);
        string key = "colliders.json";
        string rev = rig.Source.Add(key, BodyCollidersJson.Write(definition));
        LoadedBodyColliders loaded = rig.Cache.GetBodyColliders(key, rev, OutfitRig.Rig);
        Assert.Equal(DistantCapsule, Assert.Single(loaded.Set.Colliders));
        rig.Source.Bytes.Remove(key);
        Assert.Same(loaded, rig.Cache.GetBodyColliders(key, rev, OutfitRig.Rig));

        string otherRig = "rig-v1:" + new string('b', 64);
        var error = Assert.Throws<AttachmentBindingDataException>(() => rig.Cache.GetBodyColliders(key, rev, otherRig));
        Assert.Equal(AttachmentBindingRefusal.BodyCollidersMismatch, error.Reason);
        Assert.Equal(key, error.SourceName);
        Assert.Equal(otherRig, error.ExpectedRigIdentity);
        Assert.Equal(OutfitRig.Rig, error.ActualRigIdentity);

        rig.Cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Set);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Motion_refuses_an_unknown_collider_joint_with_source_and_index(bool fromBody)
    {
        using var rig = new OutfitRig();
        var mesh = rig.Source.Mesh("chain.glb", SkinnedAttachmentGlbFixture.Chained().Build());
        BoundAttachmentSet control = rig.Wear("control.json", ChainSet(mesh, colliders: fromBody ? [] : [DistantCapsule]));
        control.CreateMotion(fromBody ? BodyColliders(rig, DistantCapsule) : null);
        ColliderDefinition unknown = DistantCapsule with { Joint = "missing" };
        BoundAttachmentSet set = rig.Wear("candidate.json", ChainSet(mesh, colliders: fromBody ? [] : [unknown]));
        LoadedBodyColliders? bodyColliders = fromBody ? BodyColliders(rig, unknown) : null;

        var error = Assert.Throws<AttachmentBindingDataException>(() => set.CreateMotion(bodyColliders));

        Assert.Equal(AttachmentBindingRefusal.ColliderJointUnresolved, error.Reason);
        Assert.Equal(fromBody ? "body-colliders.json" : "candidate.json", error.SourceName);
        Assert.Equal(0, error.ColliderIndex);
        Assert.Equal("missing", error.JointName);
    }

    [Fact]
    public void A_map_only_set_needs_an_explicit_host_skin_when_it_creates_motion_with_colliders()
    {
        using var rig = new OutfitRig();
        AttachmentMeshRef mesh = rig.Source.Mesh("rigid.glb", AttachmentTestSource.Glb());
        LoadedAttachmentSet loaded = rig.Load("rigid.json",
            new AttachmentSetDefinition([OutfitRig.Rigid(1, mesh, "alpha")], AttachmentLayer.Garment, [], [DistantCapsule]));
        Step(rig, rig.Bind(loaded).CreateMotion(null));
        BoundAttachmentSet mapOnly = loaded.Bind(rig.Map);

        var error = Assert.Throws<AttachmentBindingDataException>(() => mapOnly.CreateMotion(null));

        Assert.Equal(AttachmentBindingRefusal.ColliderHostMeshRequired, error.Reason);
        Assert.Equal("rigid.json", error.SourceName);
    }

    [Fact]
    public void An_outfit_checks_colliders_against_its_body_rig()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(new RegionRun(0, 4));
        using BoundOutfit outfit = rig.Cache.BindOutfit(body, []);
        Step(rig, outfit.CreateMotion(BodyColliders(rig, DistantCapsule)));
        string otherRig = "rig-v1:" + new string('b', 64);
        string key = "other-colliders.json";
        string hash = rig.Source.Add(key, BodyCollidersJson.Write(new BodyColliderSet(otherRig, [DistantCapsule])));
        LoadedBodyColliders colliders = rig.Cache.GetBodyColliders(key, hash, otherRig);

        var error = Assert.Throws<AttachmentBindingDataException>(() => outfit.CreateMotion(colliders));

        Assert.Equal(AttachmentBindingRefusal.BodyCollidersMismatch, error.Reason);
        Assert.Equal(key, error.SourceName);
        Assert.Equal(OutfitRig.Rig, error.ExpectedRigIdentity);
        Assert.Equal(otherRig, error.ActualRigIdentity);
    }

    [Fact]
    public void Wearer_collider_limit_counts_the_body_and_all_worn_sets()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(new RegionRun(0, 4));
        LoadedBodyColliders bodyColliders = BodyColliders(rig, Enumerable.Repeat(DistantCapsule, 32).ToArray());
        AttachmentMeshRef mesh = rig.Source.Mesh("chain.glb", SkinnedAttachmentGlbFixture.Chained().Build());
        BoundAttachmentSet eight = rig.Wear("eight.json", ChainSet(mesh, colliders: Enumerable.Repeat(DistantCapsule, 8).ToArray()));
        BoundAttachmentSet one = rig.Wear("one.json", ChainSet(mesh, colliders: [DistantCapsule]));
        using BoundOutfit atLimit = rig.Cache.BindOutfit(body, [eight, eight, eight, eight]);
        using BoundOutfit overLimit = rig.Cache.BindOutfit(body, [eight, eight, eight, eight, one]);
        Step(rig, atLimit.CreateMotion(bodyColliders));

        var error = Assert.Throws<AttachmentBindingDataException>(() => overLimit.CreateMotion(bodyColliders));

        Assert.Equal(AttachmentBindingRefusal.WearerCollidersTooMany, error.Reason);
        Assert.Equal(65L, error.ColliderCount);
        Assert.Equal(64, error.MaximumColliderCount);
    }

    [Fact]
    public void One_wearer_falls_under_gravity_and_reset_restores_the_hang()
    {
        using var rig = new OutfitRig();
        BoundAttachmentSet set = WearChain(rig);
        WornMotion first = set.CreateMotion(null);
        WornMotion second = set.CreateMotion(null);
        var rest = Assert.Single(rig.Draws(set)).BoneMatrices.ToArray();
        Assert.Equal(4, rest.Length);
        Matrix4x4[] swing = (Matrix4x4[])rest.Clone();

        Matrix4x4 model = Matrix4x4.CreateRotationX(MathF.PI / 2) * rig.Model;
        for (int frame = 0; frame < 24; frame++) first.Step(rig.Skeleton, rig.Palette, model, Frame);
        first.CopyChainPalette(0, ChainPartId, rig.Palette, swing);

        // The wearer is rotated about X, so world down points along its positive Z axis.
        Assert.True(swing[0].Translation.Z > rest[0].Translation.Z + .05f);
        for (int joint = 0; joint < swing.Length; joint++)
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    Assert.True(float.IsFinite(swing[joint][row, col]));
        Assert.Equal(rest[1], swing[1]);
        Assert.Equal(rest[3], swing[3]);

        var other = (Matrix4x4[])rest.Clone();
        second.CopyChainPalette(0, ChainPartId, rig.Palette, other);
        Assert.Equal(rest, other);
        first.Reset();
        first.CopyChainPalette(0, ChainPartId, rig.Palette, swing);
        Assert.Equal(rest, swing);
        first.Step(rig.Skeleton, rig.Palette, model, Frame);
        first.CopyChainPalette(0, ChainPartId, rig.Palette, swing);
        Assert.Equal(rest, swing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Motion_poses_body_or_worn_capsules_through_the_current_joint_frame(bool fromBody)
    {
        using var rig = new OutfitRig();
        ColliderDefinition capsule = new("knee", new(.75f, 2, .25f), new(.75f, 2, .25f), .125f);
        BoundAttachmentSet set = WearChain(rig, fromBody ? [] : [capsule]);
        var options = new ChainSimulationOptions { Gravity = Vector3.Zero };
        WornMotion motion = set.CreateMotion(fromBody ? BodyColliders(rig, capsule) : null, options);
        rig.Palette[0] = Matrix4x4.CreateScale(1, 1, 2) * Matrix4x4.CreateTranslation(0, 2, -.31f);
        var pose = Assert.Single(rig.Draws(set)).BoneMatrices.ToArray();

        for (int frame = 0; frame < 4; frame++) Step(rig, motion);
        motion.CopyChainPalette(0, ChainPartId, rig.Palette, pose);

        // The knee's inverse bind subtracts 2 from Y, then the pose restores Y and scales the radius by 2.
        Vector3 center = new(.75f, 2, .19f);
        Assert.True(Vector3.Distance(pose[0].Translation, center) >= .25f);
    }

    [Fact]
    public void Motion_tuning_uses_worn_position_and_excludes_a_fully_hidden_chain()
    {
        using var rig = new OutfitRig();
        BoundAttachmentSet visible = WearChain(rig);
        var cover = rig.Wear("cover.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["alpha"],
            rig.Garment("cover.glb"), new NamedRegionRun("alpha", 4)));
        LoadedBodyRegions body = rig.BodyRegions(new RegionRun(0, 4));
        using BoundOutfit bothVisible = rig.Cache.BindOutfit(body, [visible, visible]);
        WornMotion motion = bothVisible.CreateMotion(null);
        var rest = Assert.Single(rig.Draws(visible)).BoneMatrices.ToArray();
        var first = (Matrix4x4[])rest.Clone();
        var second = (Matrix4x4[])rest.Clone();
        motion.Tune(1, ChainPartId, 0, FallSettings with { Gravity = 0 });
        for (int frame = 0; frame < 24; frame++) Step(rig, motion);
        motion.CopyChainPalette(0, ChainPartId, rig.Palette, first);
        motion.CopyChainPalette(1, ChainPartId, rig.Palette, second);
        Assert.True(first[0].Translation.Y < rest[0].Translation.Y - .05f);
        Assert.Equal(rest, second);

        using BoundOutfit hidden = rig.Cache.BindOutfit(body, [visible, cover]);
        Assert.Equal(0, hidden.DrawCount(0));
        WornMotion noChain = hidden.CreateMotion(null);
        Step(rig, noChain);
        Assert.Throws<ArgumentOutOfRangeException>(() => noChain.Tune(0, ChainPartId, 0, FallSettings));
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.Tune(2, ChainPartId, 0, FallSettings));
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.Tune(1, ChainPartId, 1, FallSettings));
    }

    [Fact]
    public void Motion_step_allocates_nothing_after_warmup_with_colliders()
    {
        using var rig = new OutfitRig();
        WornMotion motion = WearChain(rig, [DistantCapsule]).CreateMotion(BodyColliders(rig, DistantCapsule));
        Step(rig, motion);
        AllocAssert.NoPerCallAllocation("a wearer's motion step", () => Step(rig, motion));
    }

    [Theory]
    [InlineData(AttachmentBindingRefusal.SkeletonMismatch)]
    [InlineData(AttachmentBindingRefusal.HostPaletteLengthMismatch)]
    [InlineData(AttachmentBindingRefusal.ModelTransformInvalid)]
    [InlineData(AttachmentBindingRefusal.HostJointPoseInvalid)]
    public void Motion_step_validates_the_frame_like_binding(AttachmentBindingRefusal reason)
    {
        using var rig = new OutfitRig();
        WornMotion motion = WearChain(rig).CreateMotion(null);
        Step(rig, motion);
        Skeleton skeleton = reason == AttachmentBindingRefusal.SkeletonMismatch ? RigidAttachmentTestRig.Create() : rig.Skeleton;
        Matrix4x4[] palette = reason == AttachmentBindingRefusal.HostPaletteLengthMismatch ? [Matrix4x4.Identity] : rig.Palette;
        Matrix4x4 model = reason == AttachmentBindingRefusal.ModelTransformInvalid ? default : rig.Model;
        if (reason == AttachmentBindingRefusal.HostJointPoseInvalid) palette[1] = default;

        var error = Assert.Throws<AttachmentBindingArgumentException>(() => motion.Step(skeleton, palette, model, Frame));

        Assert.Equal(reason, error.Reason);
        if (reason == AttachmentBindingRefusal.HostJointPoseInvalid) Assert.Equal(1, error.BoneIndex);
    }

    static BoundAttachmentSet WearChain(OutfitRig rig, ColliderDefinition[]? colliders = null) =>
        rig.Wear("chain.json", ChainSet(rig.Source.Mesh("chain.glb", SkinnedAttachmentGlbFixture.Chained().Build()), colliders: colliders));

    static LoadedBodyColliders BodyColliders(OutfitRig rig, params ColliderDefinition[] colliders)
    {
        const string key = "body-colliders.json";
        var definition = new BodyColliderSet(OutfitRig.Rig, colliders);
        return rig.Cache.GetBodyColliders(key, rig.Source.Add(key, BodyCollidersJson.Write(definition)), OutfitRig.Rig);
    }

    static void Step(OutfitRig rig, WornMotion motion) => motion.Step(rig.Skeleton, rig.Palette, rig.Model, Frame);
}
