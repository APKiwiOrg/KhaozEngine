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

    [Fact]
    public void Cloth_presentation_starts_at_fitted_fallback_and_keeps_small_geometry_far_from_origin()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("motion-cloth.json", ClothAsset(rig).Set);
        var surface = set.ClothSurface(ClothPartId)!;
        var motion = set.CreateMotion(null);
        var slot = new WornClothSlot();
        var selection = new WornClothSelection(slot, 0, ClothPartId);
        var model = Matrix4x4.CreateTranslation(4_194_304, 0, -4_194_304);

        motion.Step(rig.Skeleton, rig.Palette, model, 0, MotionLevel.Held, selection);
        Assert.Equal(WornClothFallback.Unprepared, motion.ClothStatus.Fallback);
        Assert.False(motion.TryGetClothPresentation(0, ClothPartId, out _));

        slot.Prepare(surface);
        motion.Step(rig.Skeleton, rig.Palette, model, 0, MotionLevel.Held, selection);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var presented));
        Assert.Same(surface, presented.Surface);
        Assert.Equal(model.Translation, presented.WorldOrigin);
        Assert.Equal(surface.FittedNodes.ToArray(), presented.Nodes.ToArray());
        Assert.Equal(0, motion.ClothStatus.Substeps);

        motion.Step(rig.Skeleton, rig.Palette, model, 1f / 60, MotionLevel.Held, selection);
        Assert.Equal(2, motion.ClothStatus.Substeps);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var advanced));
        Assert.True(advanced.DisplayRevision > presented.DisplayRevision);
    }

    [Fact]
    public void Cloth_switch_endpoints_and_reversal_keep_the_last_material_sample()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("handoff-cloth.json", ClothAsset(rig).Set);
        var motion = set.CreateMotion(null, new ChainSimulationOptions { HoldBlendSeconds = 1f / 12 });
        var slot = new WornClothSlot();
        var surface = set.ClothSurface(ClothPartId)!;
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, 0, ClothPartId);

        Step(0, null);
        Step(0, selection);
        AssertClose(surface.FittedNodes, Read());
        for (int i = 0; i < 12; i++) Step(1f / 120, selection);
        Assert.Equal(WornClothTier.Cloth, motion.ClothStatus.Tier);

        Vector3[] before = Read();
        Step(0, null);
        AssertClose(before, Read());
        Assert.Equal(0, motion.ClothStatus.Substeps);
        Vector3[] blendStart = before;
        for (int i = 1; i <= 4; i++)
        {
            Step(1f / 120, null);
            Vector3 expected = Vector3.Lerp(blendStart[2], surface.FittedNodes[2], i / 10f);
            Assert.InRange(Vector3.Distance(expected, Read()[2]), 0, .0005f);
        }
        before = Read();
        Step(0, selection);
        AssertClose(before, Read());

        Step(0, null);
        for (int i = 0; i < 9; i++) Step(1f / 120, null);
        Step(1f / 120 - .00001f, null);
        AssertClose(surface.FittedNodes, Read());
        Step(.00002f, null);
        Assert.False(motion.TryGetClothPresentation(0, ClothPartId, out _));
        Assert.Equal(WornClothTier.Held, motion.ClothStatus.Tier);

        void Step(float seconds, WornClothSelection? request) =>
            motion.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, seconds, MotionLevel.Held, request);

        Vector3[] Read()
        {
            Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var presentation));
            return presentation.Nodes.ToArray();
        }
    }

    [Fact]
    public void Cloth_lower_segments_are_sampled_with_an_empty_upper_chain_tier()
    {
        using var rig = new OutfitRig();
        var cloth = rig.Wear("upper-cloth.json", ClothAsset(rig).Set);
        var lowerGlb = SkinnedAttachmentGlbFixture.Chained();
        lowerGlb.Nodes[2]!["translation"]![2] = .125f;
        lowerGlb.SetInverseBind(0, Matrix4x4.CreateTranslation(-.75f, -1, -.125f));
        lowerGlb.SetInverseBind(2, Matrix4x4.CreateTranslation(-.25f, -1, -.125f));
        var lowerMotion = ChainSet(rig.Source.Mesh("lower.glb", lowerGlb.Build()));
        var lower = rig.Wear("lower.json", new AttachmentSetDefinition(lowerMotion.Parts, AttachmentLayer.Garment, []));
        var body = rig.BodyRegions(new RegionRun(0, 4));
        using var outfitted = rig.Cache.BindOutfit(body, [lower, cloth]);
        using var withoutCloth = rig.Cache.BindOutfit(body, [lower]);
        var local = outfitted.CreateMotion(null);
        var control = withoutCloth.CreateMotion(null);
        var slot = new WornClothSlot();
        var surface = outfitted.ClothSurface(1, ClothPartId)!;
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, 1, ClothPartId);
        var pose = (Matrix4x4[])rig.Palette.Clone();

        for (int i = 0; i < 12; i++)
        {
            pose[HostHipBone].Translation += new Vector3(.0001f, 0, 0);
            local.Step(rig.Skeleton, pose, Matrix4x4.Identity, 1f / 120, MotionLevel.Simulated, selection);
            control.Step(rig.Skeleton, pose, Matrix4x4.Identity, 1f / 120, MotionLevel.Simulated);
            Assert.True(control.JointsFromAnchor.SequenceEqual(local.JointsFromAnchor[..control.JointsFromAnchor.Length]));
        }
        Assert.Equal(2, local.ClothStatus.LowerSegments);
        Assert.Equal(1, local.ClothStatus.Substeps);
        Assert.True(outfitted.TryGetClothPresentation(1, local, ClothPartId, out var view));
        Assert.Same(surface, view.Surface);
        var descriptors = new AttachmentDraw[outfitted.DrawCount(1)];
        outfitted.WriteDraws(1, local, rig.Skeleton, pose, view.Model, rig.Key,
            new RigidInstanceDraw(default, Matrix4x4.Identity), descriptors);
        Assert.True(view.GarmentPalette.SequenceEqual(descriptors[0].BoneMatrices.Span));
        Assert.Equal(descriptors[0].Skinned.Model, view.Model);
        Assert.True(view.Surface.Materials[0].VisibleIndices.AsSpan().SequenceEqual(rig.Indices(descriptors[0].Skinned.Mesh)));
    }

    [Fact]
    public void Prewarmed_scale_keeps_pins_in_world_aligned_metres_and_refuses_incompatible_scale()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("scale-cloth.json", ClothAsset(rig).Set);
        var motion = set.CreateMotion(null, new ChainSimulationOptions { HoldBlendSeconds = 0 });
        var slot = new WornClothSlot();
        var surface = set.ClothSurface(ClothPartId)!;
        slot.Prepare(surface, scale: 2);
        var selection = new WornClothSelection(slot, 0, ClothPartId);
        var model = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationX(.25f) *
            Matrix4x4.CreateTranslation(4_194_304, 0, -4_194_304);
        motion.Step(rig.Skeleton, rig.Palette, model, 0, MotionLevel.Held, selection);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var view));
        Span<Vector3> expected = stackalloc Vector3[surface.FittedNodes.Length];
        for (int node = 0; node < expected.Length; node++)
            expected[node] = Vector3.Transform(surface.FittedNodes[node], view.RelativeModel);
        AssertClose(expected, view.Nodes);
        var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, view.RelativeModel));
        motion.Step(rig.Skeleton, rig.Palette, model, 1f / 30, MotionLevel.Held, selection);
        Assert.Equal(ClothStepStatus.Accepted, motion.ClothStatus.Solve.Status);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var underGravity));
        Assert.True(Vector3.Dot(underGravity.Nodes[2] - expected[2], normal) > .0001f);
        model.M11 = 3;
        motion.Step(rig.Skeleton, rig.Palette, model, 0, MotionLevel.Held, selection);
        Assert.Equal(WornClothFallback.UnsupportedTransform, motion.ClothStatus.Fallback);
        Assert.False(motion.TryGetClothPresentation(0, ClothPartId, out _));

        model.M11 = 2;
        motion.Step(rig.Skeleton, rig.Palette, model, 1f / 120, MotionLevel.Held, selection);
        Assert.Equal(0, motion.ClothStatus.Substeps);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var refitted));
        AssertClose(expected, refitted.Nodes);
    }

    [Fact]
    public void Cloth_keeps_a_bounded_stationary_hitch_then_discards_history_on_teleport_and_long_gap()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("reset-cloth.json", ClothAsset(rig).Set);
        var motion = set.CreateMotion(null);
        var surface = set.ClothSurface(ClothPartId)!;
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, 0, ClothPartId);

        motion.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        ulong history = motion.HistoryRevision;
        motion.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, .1f, MotionLevel.Held, selection);
        Assert.Equal(8, motion.ClothStatus.Substeps);
        Assert.Equal(history, motion.HistoryRevision);

        var teleport = Matrix4x4.CreateTranslation(4_194_304, 0, 0);
        motion.Step(rig.Skeleton, rig.Palette, teleport, 0, MotionLevel.Held, selection);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var reset));
        AssertClose(surface.FittedNodes, reset.Nodes);
        Assert.True(reset.HistoryRevision > history);
        history = reset.HistoryRevision;

        motion.Step(rig.Skeleton, rig.Palette, teleport, .55f, MotionLevel.Held, selection);
        Assert.Equal(0, motion.ClothStatus.Substeps);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var longGap));
        AssertClose(surface.FittedNodes, longGap.Nodes);
        Assert.True(longGap.HistoryRevision > history);
    }

    [Fact]
    public void Crossing_the_rebase_threshold_keeps_opening_pins_in_the_same_frame_as_the_solver()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("origin-crossing.json", ClothAsset(rig).Set);
        var surface = set.ClothSurface(ClothPartId)!;
        var options = new ChainSimulationOptions { HoldBlendSeconds = 0 };
        var far = set.CreateMotion(null, options);
        var near = set.CreateMotion(null, options);
        var farSlot = new WornClothSlot();
        var nearSlot = new WornClothSlot();
        farSlot.Prepare(surface);
        nearSlot.Prepare(surface);
        var farSelection = new WornClothSelection(farSlot, 0, ClothPartId);
        var nearSelection = new WornClothSelection(nearSlot, 0, ClothPartId);
        const float first = 31.99f, second = 32.01f;
        var nearEnd = Matrix4x4.CreateTranslation(second - first, 0, 0);
        far.Step(rig.Skeleton, rig.Palette, Matrix4x4.CreateTranslation(first, 0, 0), 0, MotionLevel.Held, farSelection);
        near.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, nearSelection);
        far.Step(rig.Skeleton, rig.Palette, Matrix4x4.CreateTranslation(second, 0, 0), 1f / 60, MotionLevel.Held, farSelection);
        near.Step(rig.Skeleton, rig.Palette, nearEnd, 1f / 60, MotionLevel.Held, nearSelection);

        Assert.Equal(WornClothFallback.None, far.ClothStatus.Fallback);
        Assert.Equal(2, far.ClothStatus.Substeps);
        Assert.True(far.TryGetClothPresentation(0, ClothPartId, out var rebased));
        Assert.True(near.TryGetClothPresentation(0, ClothPartId, out var control));
        Assert.Equal(second, rebased.WorldOrigin.X);
        for (int node = 0; node < rebased.Nodes.Length; node++)
            Assert.InRange(Vector3.Distance(control.Nodes[node] - nearEnd.Translation, rebased.Nodes[node]), 0, .0005f);
    }

    [Fact]
    public void A_refused_outfit_rebind_keeps_the_previous_surface_and_pose_identity()
    {
        using var rig = new OutfitRig();
        var cloth = rig.Wear("refused-rebind-cloth.json", ClothAsset(rig).Set);
        var corner = new Vector3(-.03f, .46f, -.03f);
        var hull = ClothSupportHull.Create([corner, corner + .12f * Vector3.UnitX, corner + .12f * Vector3.UnitY,
            corner + .12f * Vector3.UnitZ], [new(0, 2, 1), new(0, 1, 3), new(0, 3, 2), new(1, 2, 3)]);
        var rigid = OutfitRig.Rigid(9, rig.Source.Mesh("rebind-overlap.glb", AttachmentTestSource.Glb()), "alpha") with
        {
            Joint = "knee",
            ClothHulls = [new(1, hull, [0])],
        };
        var lower = rig.Wear("rebind-overlap.json", new AttachmentSetDefinition([rigid], AttachmentLayer.Garment, [],
            [new("knee", corner, corner + .12f * Vector3.UnitX, .025f)]));
        var body = OutfitLiftDrawTests.Body(rig);
        using var original = rig.Cache.BindOutfit(body, [cloth]);
        using var changed = rig.Cache.BindOutfit(body, [lower, cloth]);
        var old = original.CreateMotion(null);
        var next = changed.CreateMotion(null);
        var slot = new WornClothSlot();
        slot.Prepare(original.ClothSurface(0, ClothPartId)!);
        var request = new WornClothSelection(slot, 0, ClothPartId);
        old.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, request);
        for (int i = 0; i < 36; i++)
            old.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 1f / 120, MotionLevel.Held, request);
        Assert.True(old.TryGetClothPresentation(0, ClothPartId, out var before));
        ulong revision = before.DisplayRevision;
        var nodes = before.Nodes.ToArray();
        var palette = before.GarmentPalette.ToArray();

        Assert.True(slot.Rebind(next, 1, ClothPartId));
        next.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, new(slot, 1, ClothPartId));
        Assert.Equal(WornClothFallback.TransferRefused, next.ClothStatus.Fallback);
        Assert.Equal(ClothStepStatus.UnresolvedContact, next.ClothStatus.Projection.Status);
        Assert.False(next.TryGetClothPresentation(1, ClothPartId, out _));
        Assert.True(old.TryGetClothPresentation(0, ClothPartId, out var retained));
        Assert.Same(old, retained.Motion);
        Assert.Same(original.ClothSurface(0, ClothPartId), retained.Surface);
        Assert.Equal(revision, retained.DisplayRevision);
        Assert.Equal(Matrix4x4.Identity, retained.Model);
        Assert.Equal(nodes, retained.Nodes.ToArray());
        Assert.Equal(palette, retained.GarmentPalette.ToArray());
        Assert.False(retained.IsCurrentPose);

        var current = (Matrix4x4[])rig.Palette.Clone();
        current[0].M43 += .25f;
        ulong previousHistory = next.HistoryRevision;
        next.Step(rig.Skeleton, current, Matrix4x4.Identity, .55f, MotionLevel.Held, new(slot, 1, ClothPartId));
        Assert.True(next.TryGetClothPresentation(1, ClothPartId, out var afterGap));
        Assert.True(afterGap.HistoryRevision > previousHistory);
        Assert.False(old.TryGetClothPresentation(0, ClothPartId, out _));

        var independent = changed.CreateMotion(null);
        var independentSlot = new WornClothSlot();
        independentSlot.Prepare(changed.ClothSurface(1, ClothPartId)!);
        independent.Step(rig.Skeleton, current, Matrix4x4.Identity, .55f, MotionLevel.Held,
            new(independentSlot, 1, ClothPartId));
        Assert.True(independent.TryGetClothPresentation(1, ClothPartId, out var fitted));
        Assert.True(Vector3.Distance(nodes[2], fitted.Nodes[2]) > .001f);
        AssertClose(fitted.Nodes, afterGap.Nodes);
    }

    [Fact]
    public void Cloth_step_refusal_keeps_the_admissible_display_and_reports_bounded_work()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("refused-cloth.json", ClothAsset(rig).Set);
        var surface = set.ClothSurface(ClothPartId)!;
        var motion = set.CreateMotion(null, new ChainSimulationOptions
        {
            Gravity = new Vector3(50_000, 0, 0),
            HoldBlendSeconds = 0,
        });
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, 0, ClothPartId);
        motion.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        motion.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, .05f, MotionLevel.Held, selection);
        Assert.Equal(WornClothTier.Cloth, motion.ClothStatus.Tier);
        Assert.Equal(WornClothFallback.SolveRefused, motion.ClothStatus.Fallback);
        Assert.Equal(ClothStepStatus.NodeTravel, motion.ClothStatus.Solve.Status);
        Assert.Equal(1, motion.ClothStatus.Substeps);
        Assert.InRange(motion.ClothStatus.SolveWork.BroadPhaseVisits, 0, ClothWorkLimits.BroadPhaseVisitLimit);
        Assert.InRange(motion.ClothStatus.Projection.ContactChecks, 0, ClothWorkLimits.BroadPhaseVisitLimit);
        Assert.True(motion.TryGetClothPresentation(0, ClothPartId, out var safe));
        AssertClose(surface.FittedNodes, safe.Nodes);
    }

    [Fact]
    public void Compatible_outfit_rebind_keeps_free_material_and_replaces_pins_then_reset_uses_the_new_fit()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        var cloth = rig.Wear("rebind-cloth.json", fixture.Set);
        var body = OutfitLiftDrawTests.Body(rig);
        var standOff = OutfitLiftDrawTests.Garment(rig);
        using var oldOutfit = rig.Cache.BindOutfit(body, [cloth]);
        using var newOutfit = rig.Cache.BindOutfit(body, [standOff, cloth]);
        var oldWearer = oldOutfit.CreateMotion(null);
        var newWearer = newOutfit.CreateMotion(null);
        var oldSurface = oldOutfit.ClothSurface(0, ClothPartId)!;
        var newSurface = newOutfit.ClothSurface(1, ClothPartId)!;
        Assert.NotEqual(oldSurface.FittedNodes[2], newSurface.FittedNodes[2]);

        var slot = new WornClothSlot();
        slot.Prepare(oldSurface);
        oldWearer.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, new(slot, 0, ClothPartId));
        Assert.True(oldWearer.TryGetClothPresentation(0, ClothPartId, out var first));
        Vector3 previousFree = first.Nodes[2];

        Assert.True(slot.Rebind(newWearer, 1, ClothPartId));
        Assert.True(oldWearer.TryGetClothPresentation(0, ClothPartId, out var oldSample));
        Assert.False(oldSample.IsCurrentPose);
        Assert.False(newWearer.TryGetClothPresentation(1, ClothPartId, out _));
        newWearer.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, new(slot, 1, ClothPartId));
        Assert.True(newWearer.TryGetClothPresentation(1, ClothPartId, out var rebound));
        Assert.False(oldWearer.TryGetClothPresentation(0, ClothPartId, out _));
        Assert.Same(newSurface, rebound.Surface);
        Assert.Equal(previousFree, rebound.Nodes[2]);
        Assert.Equal(newSurface.FittedNodes[0], rebound.Nodes[0]);

        ulong history = rebound.HistoryRevision;
        newWearer.Reset();
        Assert.True(newWearer.TryGetClothPresentation(1, ClothPartId, out var reset));
        Assert.True(reset.HistoryRevision > history);
        AssertClose(newSurface.FittedNodes, reset.Nodes);

        var oldPart = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var oldRecipe = Assert.IsType<ClothMotionDefinition>(oldPart.Motion);
        ClothSurfaceFile file = ClothSurfaceJson.Parse(fixture.Bytes, oldRecipe.Surface.Key);
        string changedSha = rig.Source.Add("rebind-changed.kecs", ClothSurfaceJson.Write(file with
        {
            Material = file.Material with { DampingSeconds = .02f },
        }));
        var change = oldPart with { Motion = oldRecipe with { Surface = new("rebind-changed.kecs", changedSha) } };
        var incompatible = rig.Wear("rebind-changed.json", new AttachmentSetDefinition([change], AttachmentLayer.Armour, []));
        var changedWearer = incompatible.CreateMotion(null);
        Assert.False(slot.Rebind(changedWearer, 0, ClothPartId));
        changedWearer.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, new(slot, 0, ClothPartId));
        Assert.Equal(WornClothFallback.IncompatibleProfile, changedWearer.ClothStatus.Fallback);
        Assert.False(changedWearer.TryGetClothPresentation(0, ClothPartId, out _));
    }

    [Fact]
    public void Hidden_cloth_releases_the_slot_and_cannot_get_a_snapshot()
    {
        using var rig = new OutfitRig();
        var cloth = rig.Wear("hidden-cloth.json", ClothAsset(rig).Set);
        var cover = rig.Wear("hidden-cover.json", OutfitRig.Skinned(AttachmentLayer.Outerwear,
            ["beta"], rig.Garment("hidden-cover.glb"), new NamedRegionRun("beta", 4)));
        var body = OutfitLiftDrawTests.Body(rig);
        using var hidden = rig.Cache.BindOutfit(body, [cloth, cover]);
        var wearer = cloth.CreateMotion(null);
        var hiddenWearer = hidden.CreateMotion(null);
        var slot = new WornClothSlot();
        slot.Prepare(cloth.ClothSurface(ClothPartId)!);
        var selection = new WornClothSelection(slot, 0, ClothPartId);
        wearer.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        Assert.True(slot.IsOccupied);

        Assert.False(slot.Rebind(hiddenWearer, 0, ClothPartId));
        hiddenWearer.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        Assert.Equal(WornClothFallback.Hidden, hiddenWearer.ClothStatus.Fallback);
        Assert.False(slot.IsOccupied);
        Assert.False(wearer.TryGetClothPresentation(0, ClothPartId, out _));
        Assert.False(hiddenWearer.TryGetClothPresentation(0, ClothPartId, out _));
    }

    [Fact]
    public void Moving_bound_hull_cannot_cross_a_displayed_pinned_seam_during_demotion()
    {
        using var rig = new OutfitRig();
        var cloth = rig.Wear("swept-cloth.json", ClothAsset(rig).Set);
        var corner = new Vector3(.25f, .49f, .08f);
        var hull = ClothSupportHull.Create([corner, corner + .2f * Vector3.UnitX, corner + .2f * Vector3.UnitY,
            corner + .03f * Vector3.UnitZ], [new(0, 2, 1), new(0, 1, 3), new(0, 3, 2), new(1, 2, 3)]);
        var rigid = OutfitRig.Rigid(9, rig.Source.Mesh("swept-support.glb", AttachmentTestSource.Glb()), "alpha") with
        {
            Joint = "knee",
            ClothHulls = [new(1, hull, [0])],
        };
        var lower = rig.Wear("swept-support.json", new AttachmentSetDefinition([rigid], AttachmentLayer.Garment, [],
            [new("knee", corner, corner + .2f * Vector3.UnitX, .025f)]));
        using var outfit = rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [lower, cloth]);
        var motion = outfit.CreateMotion(null, new ChainSimulationOptions { HoldBlendSeconds = 1f / 12 });
        var slot = new WornClothSlot();
        slot.Prepare(outfit.ClothSurface(1, ClothPartId)!);
        var request = new WornClothSelection(slot, 1, ClothPartId);
        var pose = (Matrix4x4[])rig.Palette.Clone();
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, request);
        for (int frame = 0; frame < 12; frame++)
            motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 1f / 120, MotionLevel.Held, request);
        Assert.Equal(WornClothTier.Cloth, motion.ClothStatus.Tier);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held);
        Assert.True(motion.TryGetClothPresentation(1, ClothPartId, out var first));
        ulong displayRevision = first.DisplayRevision;

        // Both endpoint supports are clear. Only the sweep of the moving hull crosses the fixed seam.
        pose[0].M43 -= .25f;
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, .004f, MotionLevel.Held);
        Assert.Equal(1, motion.ClothStatus.Hulls);
        Assert.Equal(WornClothFallback.TransferRefused, motion.ClothStatus.Fallback);
        Assert.Equal(WornClothTier.Cloth, motion.ClothStatus.Tier);
        Assert.True(motion.TryGetClothPresentation(1, ClothPartId, out var retained));
        Assert.Equal(displayRevision, retained.DisplayRevision);
        Assert.False(retained.IsCurrentPose);
    }

    private static void AssertClose(ReadOnlySpan<Vector3> expected, ReadOnlySpan<Vector3> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Vector3.Distance(expected[i], actual[i]), 0, .0005f);
    }

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
