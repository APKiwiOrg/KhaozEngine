using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class SkinnedAttachmentBindingTests
{
    [Fact]
    public void Detailed_material_vertices_keep_signed_detail_and_the_host_skinned_fixed_vertices()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        ClothSurfaceFile file = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey);
        var source = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(source.Motion);
        ClothRenderEmbedding fitted = file.Render[0];
        ClothRenderEmbedding intrinsic = fitted with { Barycentric = new(.25f, .25f, .5f), SignedOffset = -2 * SkinOffset };
        var set = BindMaterialCloth(rig, fixture.Set, source, recipe, file with { MaterialRender = [intrinsic] });
        var surface = set.ClothSurface(ClothPartId)!;
        var draws = rig.Draws(set);
        var output = new ClothDetailedReconstruction(surface);
        var points = new[] { Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitY };
        Assert.True(output.Material(surface, points, Matrix4x4.Identity, draws[1].BoneMatrices.Span));
        ReadOnlySpan<ModelVertex> vertices = output.Vertices(1);
        var view = surface.Materials[1];
        var expected = .25f * Vector3.UnitX + Vector3.UnitY - 2 * SkinOffset * Vector3.UnitZ;
        ModelVertex materialPoint = vertices[view.MaterialEmbeddings[0].VertexIndex];
        Assert.InRange(Vector3.Distance(expected, materialPoint.Position), 0, 1e-6f);
        Assert.Equal(view.Vertices[view.MaterialEmbeddings[0].VertexIndex].Uv, materialPoint.Uv);
        Assert.Equal(view.Vertices[view.MaterialEmbeddings[0].VertexIndex].Tangent.W, materialPoint.Tangent.W);
        int fixedVertex = 0;
        ModelVertex fixedExpected = CheapVertex(0, fixedVertex);
        Assert.Equal(fixedExpected.Position, output.Vertices(0)[fixedVertex].Position);
        Assert.Equal(fixedExpected.Normal, output.Vertices(0)[fixedVertex].Normal);
        Assert.True(output.Fitted(surface, surface.FittedNodes, Matrix4x4.Identity, draws[1].BoneMatrices.Span));
        int moving = view.Embeddings[0].VertexIndex;
        Assert.InRange(Vector3.Distance(CheapVertex(1, moving).Position, output.Vertices(1)[moving].Position),
            0, ClothSurfaceFile.RoundTripTolerance);

        ModelVertex CheapVertex(int material, int vertex)
        {
            AttachmentDraw draw = draws[material];
            SkinnedGltfMesh mesh = rig.Scene.SkinnedSourceSnapshotForTests(draw.Skinned.Mesh);
            Span<Matrix4x4> skin = stackalloc Matrix4x4[mesh.InverseBind.Length];
            for (int i = 0; i < skin.Length; i++)
                skin[i] = SkinningMath.Compose(draw.BoneMatrices.Span[i], mesh.InverseBind[i]);
            return SkinningMath.SkinVertex(mesh.Vertices[vertex], skin);
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Material_pin_uses_the_oblique_fitted_normal_in_the_current_host_pose(bool differentTranslations)
    {
        Vector3[] rest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        var rig = ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], [0], [],
            ClothMaterial.HeavyWoven);
        Matrix4x4 tilt = Matrix4x4.CreateRotationX(.37f);
        Vector3[] fitted = [Vector3.Zero, Vector3.UnitX, Vector3.Transform(Vector3.UnitY, tilt)];
        SkinnedVertex[] source =
        [
            new() { Position = Vector3.Zero, BoneWeights = Vector4.UnitX },
            new() { Position = Vector3.UnitX, BoneIndices = Vector4.One, BoneWeights = Vector4.UnitX },
            new() { Position = Vector3.UnitY, BoneIndices = 2 * Vector4.One, BoneWeights = Vector4.UnitX },
        ];
        ResolvedClothPoint[] pins = [new(0, 1, 2, new(.5f, .25f, .25f), 0, true)];
        var frames = new ClothPinnedDirections(rig, source, pins, fitted, rest,
            [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity]);
        Matrix4x4 host = differentTranslations ? Matrix4x4.CreateRotationX(-.28f) :
            Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationY(.42f) * Matrix4x4.CreateTranslation(4, -7, 2);
        Matrix4x4[] poses = differentTranslations
            ? [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.CreateTranslation(Vector3.Transform(Vector3.UnitY, host) - Vector3.UnitY)]
            : [host, host, host];
        Span<Vector3> liveNormals = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        frames.Pose(rig, source, pins, poses, liveNormals);

        Vector3 expected = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, tilt * host));
        Assert.InRange(Vector3.Distance(expected, Vector3.Normalize(liveNormals[0])), 0, 1e-6f);
        Assert.Equal(Vector3.UnitY, liveNormals[1]);
        Assert.Equal(Vector3.UnitZ, liveNormals[2]);
    }

    [Fact]
    public void Material_shading_transports_authored_crease_directions_and_tangent_handedness()
    {
        Vector3 normal = Vector3.Normalize(new Vector3(0, 1, 1));
        var source = new SkinnedVertex
        {
            Normal = normal, Tangent = new Vector4(Vector3.UnitX, -1), Color = new Vector4(.4f), Uv = new Vector2(.25f, .75f),
        };
        var rest = new ClothFittedFrame(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var authored = new ModelVertex(Vector3.Zero, source.Normal, source.Color, source.Uv, source.Tangent);
        var shading = new ClothShading(authored, rest);
        var quarterTurn = Matrix4x4.CreateRotationX(MathF.PI / 2);
        var live = new ClothFittedFrame(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY);
        ModelVertex vertex = shading.Pose(Vector3.One, source, live);
        Assert.InRange(Vector3.Distance(Vector3.TransformNormal(normal, quarterTurn), vertex.Normal), 0, 1e-6f);
        Assert.Equal(source.Tangent, vertex.Tangent);
        Assert.Equal(source.Uv, vertex.Uv);
        Assert.Equal(source.Color, vertex.Color);
    }

    [Fact]
    public void Detailed_handoff_charges_support_visits_that_miss_the_visible_cloth()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        var source = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(source.Motion);
        var file = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey) with { MaterialRender = [] };
        var set = BindMaterialCloth(rig, fixture.Set, source, recipe, file);
        var surface = set.ClothSurface(ClothPartId)!;
        var output = new ClothDetailedReconstruction(surface);
        output.Cheap(surface, rig.Draws(set)[0].BoneMatrices.Span);
        var limits = ClothWorkLimits.LayeredContact with { MaxContactChecks = surface.Materials.Length };
        var sweep = new ClothDetailedSweep(surface, limits);
        var capsule = new ColliderCapsule(Vector3.Zero, Vector3.UnitX, .1f);
        ClothSupport[] far =
        [
            ClothSupport.Capsule(capsule, Quaternion.Identity, 20 * Vector3.UnitZ),
            ClothSupport.Capsule(capsule, Quaternion.Identity, -20 * Vector3.UnitZ),
        ];

        Assert.Equal(ClothStepStatus.Accepted, Verify(far.AsSpan(0, 1)).Status);
        ClothStepResult result = Verify(far);
        Assert.Equal(ClothStepStatus.ContactWork, result.Status);
        Assert.Equal(limits.MaxContactChecks, result.ContactChecks);
        Assert.Equal(0, result.ContactCandidates);

        ClothStepResult Verify(ReadOnlySpan<ClothSupport> supports) =>
            sweep.Verify(surface, output.Storage, Matrix4x4.Identity, output.Storage, Matrix4x4.Identity,
                new ClothSubstep([], supports, supports, ClothWorkLimits.LayeredStepSeconds, Vector3.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Support_can_refuse_signed_detail_without_publishing_a_partial_pose(bool compatibleRebind)
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig, hipOnlyPins: true);
        var source = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(source.Motion);
        ClothSurfaceFile file = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey);
        ClothRenderEmbedding protrusion = file.Render[2] with { SignedOffset = .12f };
        var center = new Vector3(0, 2, .4f);
        const float radius = .012f;
        var withSupport = new AttachmentSetDefinition(fixture.Set.Parts, fixture.Set.Layer!.Value, [],
            [new ColliderDefinition("knee", center, center, radius)]);
        var set = BindMaterialCloth(rig, withSupport, source, recipe, file with { MaterialRender = [protrusion] });
        BoundClothSurface surface = set.ClothSurface(ClothPartId)!;
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, 0, ClothPartId);
        var pose = (Matrix4x4[])rig.Palette.Clone();
        var options = new ChainSimulationOptions { Gravity = Vector3.Zero, HoldBlendSeconds = 0 };
        var motion = set.CreateMotion(null, options);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, ClothWorkLimits.LayeredStepSeconds, MotionLevel.Held, selection);
        Assert.Equal(WornClothTier.Cloth, motion.ClothStatus.Tier);
        Assert.Equal(WornClothFallback.None, motion.ClothStatus.Fallback);
        Assert.True(set.TryGetClothPresentation(motion, ClothPartId, out var live));
        Assert.True(live.IsCurrentPose);
        int peak = surface.Materials[1].MaterialEmbeddings[2].VertexIndex;
        Assert.Contains((uint)peak, surface.Materials[1].VisibleIndices);
        Vector3 point = live.DetailedVertices(1).Span[peak].Position;
        var collider = Assert.Single(motion.FindClothBinding(0, ClothPartId)!.Capsules);
        Vector3 posedCenter = collider.Pose(pose, Matrix4x4.Identity).WorldCapsule.Start;
        int knee = rig.Map.Bone("knee");
        pose[knee].Translation += point - posedCenter + 2 * radius * Vector3.UnitZ;
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        Assert.Equal(WornClothFallback.None, motion.ClothStatus.Fallback);
        Assert.True(set.TryGetClothPresentation(motion, ClothPartId, out var clear));
        Assert.True(clear.IsCurrentPose);
        Vector3 previousPoint = clear.DetailedVertices(1).Span[peak].Position;

        if (compatibleRebind)
        {
            BoundAttachmentSet lower = OutfitLiftDrawTests.Garment(rig, standoff: .002f);
            int hip = rig.Map.Bone("hip");
            Assert.True(Matrix4x4.Invert(rig.Body.InverseBind[hip] * pose[hip], out Matrix4x4 toSupportRest));
            Vector3 addedCenter = Vector3.Transform(previousPoint, toSupportRest);
            var changed = new AttachmentSetDefinition(lower.Definition.Parts, lower.Definition.Layer,
                lower.Definition.Covers, [new ColliderDefinition("hip", addedCenter, addedCenter, radius)], lower.Definition.Standoff);
            var newLower = rig.Wear("material-rebind-support.json", changed);
            using BoundOutfit rebound = rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [newLower, set]);
            var changedSurface = rebound.ClothSurface(1, ClothPartId)!;
            Assert.True(changedSurface.CompatibleMaterial(surface));
            Assert.NotEqual(surface.Materials[1].Vertices[peak].Position, changedSurface.Materials[1].Vertices[peak].Position);
            var newMotion = rebound.CreateMotion(null, options);
            Assert.Contains(newMotion.FindClothBinding(1, ClothPartId)!.Capsules, candidate =>
                Vector3.Distance(candidate.Pose(pose, Matrix4x4.Identity).WorldCapsule.Start, previousPoint) < radius * .5f);
            Assert.True(slot.Rebind(newMotion, 1, ClothPartId));
            newMotion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, new(slot, 1, ClothPartId));
            Assert.Equal(ClothStepStatus.Accepted, newMotion.ClothStatus.Projection.Status);
            Assert.Equal(ClothStepStatus.UnresolvedContact, newMotion.ClothStatus.DetailedProjection.Status);
            Assert.Equal(WornClothFallback.TransferRefused, newMotion.ClothStatus.Fallback);
            Assert.False(rebound.TryGetClothPresentation(1, newMotion, ClothPartId, out _));
            Assert.True(set.TryGetClothPresentation(motion, ClothPartId, out var retained));
            Assert.Equal(clear.DisplayRevision, retained.DisplayRevision);
            Assert.Equal(previousPoint, retained.DetailedVertices(1).Span[peak].Position);
            return;
        }

        pose[knee].Translation -= 2 * radius * Vector3.UnitZ;
        Vector3 penetratingCenter = collider.Pose(pose, Matrix4x4.Identity).WorldCapsule.Start;
        Assert.InRange(Vector3.Distance(penetratingCenter, previousPoint), 0, radius * .5f);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        Assert.Equal(ClothStepStatus.Accepted, motion.ClothStatus.Projection.Status);
        Assert.Equal(ClothStepStatus.UnresolvedContact, motion.ClothStatus.DetailedProjection.Status);
        Assert.Equal(WornClothFallback.TransferRefused, motion.ClothStatus.Fallback);
        Assert.True(set.TryGetClothPresentation(motion, ClothPartId, out var refused));
        Assert.False(refused.IsCurrentPose);
        Assert.Equal(clear.DisplayRevision, refused.DisplayRevision);
        Assert.Equal(previousPoint, refused.DetailedVertices(1).Span[peak].Position);
    }

    [Fact]
    public void A_detail_without_a_carried_fit_residual_moves_by_the_full_accepted_proxy_displacement()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        var source = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(source.Motion);
        var file = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey) with { MaterialRender = [] };
        var set = BindMaterialCloth(rig, fixture.Set, source, recipe, file);
        var surface = set.ClothSurface(ClothPartId)!;
        var detail = new WornClothDetail(surface);
        var palette = rig.Draws(set)[0].BoneMatrices;
        var pose = new ClothSubstep([], [], [], ClothWorkLimits.LayeredStepSeconds, Vector3.Zero);
        Assert.Equal(ClothStepStatus.Accepted, detail.Stage(surface, surface.FittedNodes, [], Matrix4x4.Identity, Vector3.Zero,
            palette.Span, [], liveTarget: true, amount: 1, sameMaterialPose: false, pose, default).Status);
        detail.Publish(Matrix4x4.Identity, Vector3.Zero);
        int moving = surface.Materials[1].MaterialEmbeddings[0].VertexIndex;
        Vector3 before = detail.Shown[1][moving].Position;

        Vector3 acceptedProxyTravel = .005f * Vector3.UnitX;
        Vector3[] shownNodes = surface.FittedNodes.ToArray();
        for (int node = 0; node < shownNodes.Length; node++) shownNodes[node] += acceptedProxyTravel;
        Assert.Equal(ClothStepStatus.Accepted, detail.Stage(surface, shownNodes, [], Matrix4x4.Identity, Vector3.Zero,
            palette.Span, [], liveTarget: true, amount: .5f, sameMaterialPose: false, pose, default).Status);
        detail.Publish(Matrix4x4.Identity, Vector3.Zero);
        Assert.InRange(Vector3.Distance(acceptedProxyTravel, detail.Shown[1][moving].Position - before), 0, 1e-6f);
    }

    [Fact]
    public void Dense_and_proxy_visits_share_one_allowance_without_replacing_the_shown_mesh()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        var source = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(source.Motion);
        var file = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey) with { MaterialRender = [] };
        var set = BindMaterialCloth(rig, fixture.Set, source, recipe, file);
        var surface = set.ClothSurface(ClothPartId)!;
        var palette = rig.Draws(set)[0].BoneMatrices;
        ClothSupport[] supports = [ClothSupport.Capsule(new(Vector3.Zero, Vector3.UnitX, .1f),
            Quaternion.Identity, 20 * Vector3.UnitZ)];
        var pins = new Vector3[surface.Rig.Pins.Length];
        surface.CopyPins(rig.Palette, Matrix4x4.Identity, pins);
        var pose = new ClothSubstep(pins, supports, supports, ClothWorkLimits.LayeredStepSeconds, Vector3.Zero);
        var solver = new ClothSimulation(surface.Rig, ClothWorkLimits.LayeredContact);
        var nodes = new Vector3[surface.FittedNodes.Length];
        ClothStepResult proxy = solver.ProjectDisplay(surface.FittedNodes, nodes, pose);
        Assert.Equal(ClothStepStatus.Accepted, proxy.Status);
        Assert.True(proxy.ContactChecks > 0);

        var unboundedByProxy = new WornClothDetail(surface);
        ClothStepResult detailed = unboundedByProxy.Stage(surface, nodes, [], Matrix4x4.Identity, Vector3.Zero,
            palette.Span, [], liveTarget: true, amount: 1, sameMaterialPose: false, pose, default);
        Assert.Equal(ClothStepStatus.Accepted, detailed.Status);
        Assert.True(detailed.ContactChecks > 0);
        int budget = Math.Max(proxy.ContactChecks, detailed.ContactChecks);
        var limited = new WornClothDetail(surface, ClothWorkLimits.LayeredContact with { MaxContactChecks = budget });
        Assert.Equal(ClothStepStatus.Accepted, limited.Stage(surface, nodes, [], Matrix4x4.Identity, Vector3.Zero,
            palette.Span, [], liveTarget: true, amount: 1, sameMaterialPose: false, pose, default).Status);
        limited.Publish(Matrix4x4.Identity, Vector3.Zero);
        int moving = surface.Materials[1].MaterialEmbeddings[0].VertexIndex;
        Vector3 before = limited.Shown[1][moving].Position;
        for (int node = 0; node < nodes.Length; node++) nodes[node] += .001f * Vector3.UnitX;
        ClothStepResult refused = limited.Stage(surface, nodes, [], Matrix4x4.Identity, Vector3.Zero,
            palette.Span, [], liveTarget: true, amount: 1, sameMaterialPose: false, pose, proxy);
        Assert.Equal(ClothStepStatus.ContactWork, refused.Status);
        Assert.Equal(budget, proxy.ContactChecks + refused.ContactChecks);
        Assert.Equal(before, limited.Shown[1][moving].Position);
    }
}
