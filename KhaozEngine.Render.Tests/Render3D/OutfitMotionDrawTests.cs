using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class OutfitMotionDrawTests
{
    const int ChainedWornIndex = 1;
    const int HostHipBone = 1;
    // Chained() declares its garment skin as left1, knee, left0, hip.
    const int ChainRootSlot = 2, LastChainJointSlot = 0;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outfit_draws_keep_a_close_chain_outside_equipment_at_bind_and_immediate_reset(bool reset)
    {
        // #1904: use the actual submitted palette for the same chain with and without a lower set.
        using var rig = new OutfitRig();
        var bindPose = (Matrix4x4[])rig.Palette.Clone();
        // The mesh can supply a bind palette distinct from the skeleton's pose.
        bindPose[0].Translation += Vector3.UnitY;
        bindPose[HostHipBone].Translation -= Vector3.UnitY;
        var host = new SkinnedGltfMesh(rig.Body.Vertices, rig.Body.Indices32, rig.Body.InverseBind, bindPose, rig.Skeleton);
        var chainSet = ChainSet(rig.Source.Mesh("chain.glb", SkinnedAttachmentGlbFixture.Chained().Build()));
        var chain = rig.Load("chain.json", new AttachmentSetDefinition(chainSet.Parts, AttachmentLayer.Outerwear, []))
            .Bind(rig.Map, host);
        var center = new Vector3(.55f, 1, -.04f);
        var obstacle = new ColliderDefinition("knee", center, center, .125f);
        var hardware = OutfitRig.Rigid(1, rig.Source.Mesh("hardware.glb", AttachmentTestSource.Glb()), "alpha");
        var lower = rig.Load("hardware.json", new AttachmentSetDefinition([hardware], AttachmentLayer.Armour, [], [obstacle]))
            .Bind(rig.Map, host);
        var body = rig.BodyRegions(new RegionRun(0, 4));
        using var bare = rig.Cache.BindOutfit(body, [chain]);
        using var equipped = rig.Cache.BindOutfit(body, [chain, lower]);
        var bareMotion = bare.CreateMotion(null);
        var equippedMotion = equipped.CreateMotion(null);
        var pose = (Matrix4x4[])bindPose.Clone();
        if (reset)
        {
            // Move only the lower joint. Reset is visible immediately, before the next Step.
            var displacement = new Vector3(0, 0, -.04f);
            pose[0].Translation += displacement;
            center += displacement;
            equippedMotion.Step(rig.Skeleton, pose, rig.Model, 0);
            equippedMotion.Reset();
        }

        var barePalette = ReadPalette(bare, bareMotion);
        var wornPalette = ReadPalette(equipped, equippedMotion);
        Assert.Equal(barePalette[ChainRootSlot].Translation, wornPalette[ChainRootSlot].Translation);
        float radius = obstacle.Radius + FallSettings.Radius;
        Assert.True(DistanceToFirstSegment(barePalette) < radius);
        Assert.True(DistanceToFirstSegment(wornPalette) >= radius - .0002f);
        Assert.True(wornPalette[LastChainJointSlot].Translation.Z > barePalette[LastChainJointSlot].Translation.Z);

        Matrix4x4[] ReadPalette(BoundOutfit outfit, WornMotion motion)
        {
            var draws = new AttachmentDraw[outfit.DrawCount(0)];
            Assert.Equal(1, outfit.WriteDraws(0, motion, rig.Skeleton, pose, rig.Model, rig.Key,
                new RigidInstanceDraw(default, Matrix4x4.Identity), draws));
            return Assert.Single(draws).BoneMatrices.ToArray();
        }

        float DistanceToFirstSegment(Matrix4x4[] palette)
        {
            var root = palette[ChainRootSlot].Translation;
            var segment = palette[LastChainJointSlot].Translation - root;
            float along = Math.Clamp(Vector3.Dot(center - root, segment) / segment.LengthSquared(), 0, 1);
            return Vector3.Distance(center, root + along * segment);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Motion_draws_use_current_anchors_and_preserve_submission_fields(bool asOutfit)
    {
        using var rig = new OutfitRig();
        BoundAttachmentSet set = WearDrawSet(rig);
        using BoundOutfit? outfit = asOutfit ? BindOutfit(rig, set) : null;
        WornMotion motion = outfit?.CreateMotion(null) ?? set.CreateMotion(null);
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity)
        {
            Tint = new Color(.25f, .5f, .75f, 1f), Dissolve = .4f, DissolveEdgeWidth = .2f,
            DissolveEdgeColor = Color.White, CastsShadows = true, ShadowOnly = true,
            InvertShadowDissolve = true, DissolveComplement = 1f,
        };
        Matrix4x4 model = Matrix4x4.CreateRotationY(.4f) * rig.Model;
        int drawCount = outfit?.DrawCount(ChainedWornIndex) ?? set.DrawCount;
        Assert.Equal(3, drawCount);
        var baseline = new AttachmentDraw[drawCount];
        Assert.Equal(drawCount, outfit?.WriteDraws(ChainedWornIndex, rig.Skeleton, rig.Palette, model, rig.Key, template, baseline)
            ?? set.WriteDraws(rig.Skeleton, rig.Palette, model, rig.Key, template, baseline));
        Assert.False(baseline[0].IsSkinned);
        Assert.Equal(asOutfit, rig.Scene.IsSkinnedMeshView(baseline[1].Skinned.Mesh));
        Assert.False(rig.Scene.IsSkinnedMeshView(baseline[2].Skinned.Mesh));
        Matrix4x4[] rest = baseline[1].BoneMatrices.ToArray();

        // Replay the fixture's solver inputs without the wearer's binding or palette code.
        var chain = new ChainRigChain(0,
            [new(Vector3.UnitX * .25f, Quaternion.Identity, Vector3.One),
             new(Vector3.UnitX * .5f, Quaternion.Identity, Vector3.One)], Vector3.UnitX * .5f, FallSettings);
        var solver = new ChainSimulation(new ChainRig(1, [chain], []), 0);
        Matrix4x4 anchorWorld = rig.Palette[HostHipBone] * model;
        for (int frame = 0; frame < 8; frame++)
        {
            motion.Step(rig.Skeleton, rig.Palette, model, 1f / 60f);
            solver.Step([anchorWorld], [], model, 1f / 60f);
        }

        Assert.True(Matrix4x4.Invert(model, out Matrix4x4 inverseModel));
        ReadOnlySpan<Matrix4x4> frames = solver.JointsFromAnchor;
        Assert.Equal(2, frames.Length);
        Matrix4x4 rootJoint = frames[0] * anchorWorld * inverseModel;
        Matrix4x4 lastJoint = frames[1] * anchorWorld * inverseModel;
        Assert.NotEqual(rest[LastChainJointSlot], lastJoint);
        var actual = new AttachmentDraw[drawCount];
        Assert.Equal(drawCount, Write(rig.Palette));
        Assert.Equal(baseline[0], actual[0]);
        for (int i = 1; i < drawCount; i++)
        {
            Assert.Equal(baseline[i].PartId, actual[i].PartId);
            Assert.Equal(baseline[i].MaterialIndex, actual[i].MaterialIndex);
            Assert.Equal(baseline[i].Skinned, actual[i].Skinned);
            ReadOnlySpan<Matrix4x4> palette = actual[i].BoneMatrices.Span;
            Assert.Equal(4, palette.Length);
            AssertMatrix(rootJoint, palette[ChainRootSlot]);
            AssertMatrix(lastJoint, palette[LastChainJointSlot]);
            Assert.Equal(rig.Palette[0], palette[1]); // knee
            Assert.Equal(rig.Palette[HostHipBone], palette[3]); // hip
        }

        // Move and turn the host anchor without stepping. Only the anchor and its chain move.
        Matrix4x4[] moved = (Matrix4x4[])rig.Palette.Clone();
        Matrix4x4 movement = Matrix4x4.CreateRotationZ(.5f) * Matrix4x4.CreateTranslation(2, 3, 4);
        moved[HostHipBone] *= movement;
        Assert.Equal(drawCount, Write(moved));
        Assert.Equal(4, actual[1].BoneMatrices.Length);
        ReadOnlySpan<Matrix4x4> carried = actual[1].BoneMatrices.Span;
        AssertMatrix(rootJoint * movement, carried[ChainRootSlot]);
        AssertMatrix(lastJoint * movement, carried[LastChainJointSlot]);
        Assert.Equal(moved[0], carried[1]);
        Assert.Equal(moved[HostHipBone], carried[3]);

        int Write(Matrix4x4[] palette) => outfit?.WriteDraws(ChainedWornIndex, motion, rig.Skeleton, palette, model, rig.Key, template, actual)
            ?? set.WriteDraws(motion, rig.Skeleton, palette, model, rig.Key, template, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Motion_outline_uses_the_drawn_meshes_and_palette(bool asOutfit)
    {
        using var rig = new OutfitRig();
        BoundAttachmentSet set = WearDrawSet(rig);
        using BoundOutfit? outfit = asOutfit ? BindOutfit(rig, set) : null;
        WornMotion motion = outfit?.CreateMotion(null) ?? set.CreateMotion(null);
        Step(rig, motion);
        var draws = new AttachmentDraw[3];
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        Assert.Equal(draws.Length, outfit?.WriteDraws(ChainedWornIndex, motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws)
            ?? set.WriteDraws(motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws));
        Matrix4x4[] jointPose = draws[1].BoneMatrices.ToArray();
        Assert.NotEqual(rig.Draws(set)[1].BoneMatrices.ToArray()[0], jointPose[0]);
        rig.Scene.Begin();
        MeshOutlineGroup group = rig.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);

        QueueOutline(rig, set, outfit, motion, group);

        Assert.Equal(draws[0].Rigid.Mesh, Assert.Single(rig.Recording.OutlinedMeshes));
        Assert.Equal(new[] { draws[1].Skinned.Mesh, draws[2].Skinned.Mesh }, rig.Recording.OutlinedSkinned);
        Assert.Equal(jointPose.Length * 2, rig.Scene.OutlinePoseMatrixCount);
        for (int i = 0; i < 2; i++)
        {
            Matrix4x4[] inverseBind = rig.Scene.SkinnedSourceSnapshotForTests(draws[i + 1].Skinned.Mesh).InverseBind;
            for (int bone = 0; bone < jointPose.Length; bone++)
                Assert.Equal(inverseBind[bone] * jointPose[bone], rig.Scene.OutlinePoseMatrixAt(i * jointPose.Length + bone));
        }
    }

    [Theory]
    [InlineData("set write")]
    [InlineData("set queue")]
    [InlineData("set templated queue")]
    [InlineData("set outline")]
    [InlineData("member set write")]
    [InlineData("outfit write")]
    [InlineData("outfit outline")]
    public void Motion_from_a_different_binding_refuses_before_submission(string entry)
    {
        using var rig = new OutfitRig();
        LoadedAttachmentSet loaded = rig.Load("chain.json", ChainSet(rig.Source.Mesh("chain.glb", SkinnedAttachmentGlbFixture.Chained().Build())));
        BoundAttachmentSet set = rig.Bind(loaded);
        BoundAttachmentSet otherSet = rig.Bind(loaded);
        LoadedBodyRegions body = rig.BodyRegions(new RegionRun(0, 4));
        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [set]);
        using BoundOutfit otherOutfit = rig.Cache.BindOutfit(body, [set]);
        bool asOutfit = entry.StartsWith("outfit", StringComparison.Ordinal);
        WornMotion match = asOutfit ? outfit.CreateMotion(null) : set.CreateMotion(null);
        WornMotion mismatch = entry switch
        {
            "member set write" => outfit.CreateMotion(null),
            _ when asOutfit => otherOutfit.CreateMotion(null),
            _ => otherSet.CreateMotion(null),
        };
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        var draws = new AttachmentDraw[1];
        rig.Scene.Begin();
        MeshOutlineGroup group = rig.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);
        var error = Assert.Throws<AttachmentSubmissionArgumentException>(() => Submit(mismatch));
        Assert.Equal(AttachmentSubmissionRefusal.MotionMismatch, error.Reason);
        Assert.Equal("motion", error.ParamName);
        Assert.Equal(default, draws[0]);
        Assert.Equal(0, rig.Scene.OutlinePoseMatrixCount);
        Assert.Empty(rig.Scene.QueuedSkinnedInstancesForTests);

        Submit(match);

        switch (entry)
        {
            case "set write":
            case "member set write":
            case "outfit write":
                Assert.True(draws[0].IsSkinned);
                break;
            case "set outline":
            case "outfit outline":
                Assert.Equal(4, rig.Scene.OutlinePoseMatrixCount);
                break;
            default:
                Assert.Single(rig.Scene.QueuedSkinnedInstancesForTests);
                break;
        }

        void Submit(WornMotion motion)
        {
            switch (entry)
            {
                case "set write":
                case "member set write": set.WriteDraws(motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws); break;
                case "set queue": set.Queue(motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key); break;
                case "set templated queue": set.Queue(motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template); break;
                case "set outline": QueueOutline(rig, set, null, motion, group); break;
                case "outfit write": outfit.WriteDraws(0, motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws); break;
                default: outfit.QueueOutline(0, motion, rig.Skeleton, rig.Palette, rig.Model, group); break;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Motion_draw_calls_allocate_nothing_once_warm(bool lifted)
    {
        using var rig = new OutfitRig(recordOutlines: false);
        BoundAttachmentSet set = lifted ? OutfitLiftDrawTests.ChainedArmour(rig) : WearDrawSet(rig);
        using BoundOutfit outfit = lifted
            ? rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [OutfitLiftDrawTests.Garment(rig), set])
            : BindOutfit(rig, set);
        if (lifted) Assert.True(rig.Scene.IsSkinnedMeshView(Assert.Single(rig.Draws(outfit, ChainedWornIndex)).Skinned.Mesh));
        WornMotion setMotion = set.CreateMotion(null), outfitMotion = outfit.CreateMotion(null);
        Step(rig, setMotion);
        Step(rig, outfitMotion);
        var draws = new AttachmentDraw[set.DrawCount];
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        for (int i = 0; i < 3; i++) Frame();
        long allocated = 0;
        using (AllocationRegion.Start("warm motion draw calls"))
            for (int i = 0; i < 100; i++) allocated += Frame();
        Assert.Equal(0, allocated);

        long Frame()
        {
            rig.Scene.Begin();
            MeshOutlineGroup group = rig.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);
            long start = GC.GetAllocatedBytesForCurrentThread();
            outfitMotion.Step(rig.Skeleton, rig.Palette, rig.Model, 1f / 60f);
            set.WriteDraws(setMotion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws);
            set.Queue(setMotion, rig.Skeleton, rig.Palette, rig.Model, rig.Key);
            set.Queue(setMotion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template);
            QueueOutline(rig, set, null, setMotion, group);
            outfit.WriteDraws(ChainedWornIndex, outfitMotion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws);
            QueueOutline(rig, set, outfit, outfitMotion, group);
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }

    static BoundAttachmentSet WearDrawSet(OutfitRig rig)
    {
        SkinnedAttachmentGlbFixture glb = SkinnedAttachmentGlbFixture.Chained();
        glb.SetIndexedMaterialParts([0, 1, 2, 0, 2, 1], 2);
        var skin = (SkinnedAttachmentPartDefinition)ChainSet(rig.Source.Mesh("chain.glb", glb.Build())).Parts[0];
        skin = skin with { Regions = [new NamedRegionRun("alpha", 1), new NamedRegionRun("beta", 1), new NamedRegionRun("gamma", 2)] };
        AttachmentMeshRef rigid = rig.Source.Mesh("rigid.glb", AttachmentTestSource.Glb());
        return rig.Wear("chain.json", new AttachmentSetDefinition([OutfitRig.Rigid(1, rigid, "alpha"), skin], AttachmentLayer.Garment, []));
    }

    static BoundOutfit BindOutfit(OutfitRig rig, BoundAttachmentSet set)
    {
        BoundAttachmentSet cover = rig.Wear("cover.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["beta"],
            rig.Garment("cover.glb"), new NamedRegionRun("beta", 4)));
        return rig.Cache.BindOutfit(rig.BodyRegions(new RegionRun(0, 2), new RegionRun(1, 2)), [cover, set]);
    }

    static void Step(OutfitRig rig, WornMotion motion)
    {
        for (int i = 0; i < 8; i++) motion.Step(rig.Skeleton, rig.Palette, rig.Model, 1f / 60f);
    }

    static void QueueOutline(OutfitRig rig, BoundAttachmentSet set, BoundOutfit? outfit, WornMotion motion, MeshOutlineGroup group)
    {
        if (outfit is null) set.QueueOutline(motion, rig.Skeleton, rig.Palette, rig.Model, group);
        else outfit.QueueOutline(ChainedWornIndex, motion, rig.Skeleton, rig.Palette, rig.Model, group);
    }

    static void AssertMatrix(in Matrix4x4 expected, in Matrix4x4 actual)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                Assert.InRange(MathF.Abs(expected[row, column] - actual[row, column]), 0f, 1e-5f);
    }
}
