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


    [Fact]
    public void Material_pin_uses_the_oblique_fitted_normal_in_the_current_host_pose()
    {
        Vector3[] rest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        var rig = ClothRig.Create(rest, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], [0], [],
            ClothMaterial.HeavyWoven);
        Matrix4x4 tilt = Matrix4x4.CreateRotationX(.37f);
        Vector3[] fitted = [Vector3.Zero, Vector3.UnitX, Vector3.Transform(Vector3.UnitY, tilt)];
        SkinnedVertex[] source =
        [
            new() { Position = Vector3.Zero, BoneWeights = Vector4.UnitX },
            new() { Position = Vector3.UnitX, BoneWeights = Vector4.UnitX },
            new() { Position = Vector3.UnitY, BoneWeights = Vector4.UnitX },
        ];
        ResolvedClothPoint[] pins = [new(0, 1, 2, new(.5f, .25f, .25f), 0, true)];
        var frames = new ClothPinnedDirections(rig, source, pins, fitted, [Matrix4x4.Identity]);
        Matrix4x4 host = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationY(.42f) *
            Matrix4x4.CreateTranslation(4, -7, 2);
        Span<Vector3> liveNormals = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        frames.Pose(rig, source, pins, [host], liveNormals);

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
}
