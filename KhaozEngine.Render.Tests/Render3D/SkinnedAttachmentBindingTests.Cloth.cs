using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class SkinnedAttachmentBindingTests
{
    internal const uint ClothPartId = uint.MaxValue;
    const string ClothKey = "coat.kecs";
    const float SkinOffset = .015625f;

    [Fact]
    public void Cloth_binding_keeps_mesh_ordinals_and_skin_offsets_across_materials_and_outfits()
    {
        using var rig = new OutfitRig(customLeases: true);
        var fixture = ClothAsset(rig);
        BoundAttachmentSet set = rig.Wear("coat.json", fixture.Set);
        BoundClothSurface bound = Assert.IsType<BoundClothSurface>(set.ClothSurface(ClothPartId));
        Assert.Same(bound, set.ClothSurface(ClothPartId));
        Assert.Equal(2, rig.Draws(set).Length);
        Assert.Equal([3, 4, 5], bound.Materials[0].Embeddings.Select(e => e.VertexIndex));
        Assert.Equal([0, 1, 2, 3, 4, 5], bound.Materials[1].Embeddings.Select(e => e.VertexIndex));
        Assert.Equal(-SkinOffset, bound.Materials[0].Embeddings[0].SignedOffset);
        Assert.Equal(SkinOffset, bound.Materials[1].Embeddings[0].SignedOffset);
        Assert.Equal(-SkinOffset, bound.Materials[1].Embeddings[5].FittedOffset.Z);
        var part = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var motion = Assert.IsType<ClothMotionDefinition>(part.Motion);
        Assert.Equal(new ClothRoundTrip(0, 0), ClothSurfaceBinding.Check(rig.Source.Bytes[part.Mesh.Key],
            ClothSurfaceJson.Parse(fixture.Bytes, ClothKey), motion.Fallback, ClothKey, ClothPartId));
        Assert.Equal(2 * Vector3.UnitY, bound.FittedNodes[2]);

        Matrix4x4[] posed = [Matrix4x4.CreateTranslation(0, 2, 2), Matrix4x4.CreateTranslation(0, 1, 4)];
        var pins = new Vector3[2];
        bound.CopyPins(posed, rig.Model, pins);
        Assert.Equal(new Vector3(10, 0, 3.5f), pins[0]);
        Assert.Equal(new Vector3(11, 0, 3.5f), pins[1]);

        LoadedBodyRegions body = OutfitLiftDrawTests.Body(rig);
        using BoundOutfit bare = rig.Cache.BindOutfit(body, [set]);
        BoundAttachmentSet flat = OutfitLiftDrawTests.Garment(rig, standoff: null);
        using BoundOutfit overFlat = rig.Cache.BindOutfit(body, [flat, set]);
        var hull = ClothSupportHull.Create([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ],
            [new(0, 2, 1), new(0, 1, 3), new(0, 3, 2), new(1, 2, 3)]);
        RigidAttachmentPartDefinition support = OutfitRig.Rigid(ClothPartId,
            rig.Source.Mesh("support.glb", AttachmentTestSource.Glb()), "alpha") with
        {
            Local = Matrix4x4.CreateScale(2, .75f, 1) * Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(.25f, -.5f, .125f),
            ClothHulls = [new(5, hull, [0])],
        };
        BoundAttachmentSet raised = rig.Wear("raised.json", new AttachmentSetDefinition([support], AttachmentLayer.Garment, [],
            [new("hip", Vector3.Zero, Vector3.UnitX, .1f)], [new("alpha", .125f), new("beta", .125f)]));
        using BoundOutfit overRaised = rig.Cache.BindOutfit(body, [raised, set]);
        var bareCloth = Assert.IsType<BoundClothSurface>(bare.ClothSurface(0, ClothPartId));
        var flatCloth = Assert.IsType<BoundClothSurface>(overFlat.ClothSurface(1, ClothPartId));
        var raisedCloth = Assert.IsType<BoundClothSurface>(overRaised.ClothSurface(1, ClothPartId));
        Assert.Equal(bareCloth.FittedNodes.ToArray(), flatCloth.FittedNodes.ToArray());
        Assert.Equal(new Vector3(0, 2, .125f), raisedCloth.FittedNodes[2]);
        raisedCloth.CopyPins(rig.Palette, Matrix4x4.Identity, pins);
        Assert.Equal(new Vector3(0, .5f, .125f), pins[0]);
        Assert.Equal(6, raisedCloth.Materials[0].VisibleIndices.Length);
        BoundClothHull boundHull = Assert.Single(overRaised.ClothHulls(0));
        Assert.Equal(new ClothSupportIdentity(5, ClothPartId, 5, AttachmentLayer.Garment), boundHull.Identity(5));
        Assert.Equal(0, Assert.Single(boundHull.CapsuleIndices));
        Matrix4x4 restPlacement = boundHull.Compose(rig.Palette[boundHull.HostBone], rig.Model);
        Assert.Equal(rig.Model, restPlacement);
        Vector3 restPoint = Vector3.Transform(boundHull.Hull.Vertices[1], restPlacement);
        Assert.InRange(Vector3.Distance(new Vector3(11, 0, 0), restPoint), 0, 1e-5f);
        Matrix4x4 animated = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(3, 4, 0);
        Vector3 moved = Vector3.Transform(boundHull.Hull.Vertices[1], boundHull.Compose(animated, rig.Model));
        Assert.InRange(Vector3.Distance(new Vector3(15, 5, 0), moved), 0, 1e-5f);
        Assert.True(Matrix4x4.Invert(rig.Draws(raised)[0].Rigid.World, out Matrix4x4 restDrawInverse));
        Vector3 partLocalPoint = Vector3.Transform(restPoint, restDrawInverse);
        var animatedDraw = new AttachmentDraw[1];
        Matrix4x4[] animatedPalette = [rig.Palette[0], animated];
        raised.WriteDraws(rig.Skeleton, animatedPalette, rig.Model, rig.Key,
            new RigidInstanceDraw(default, Matrix4x4.Identity), animatedDraw);
        Assert.InRange(Vector3.Distance(moved, Vector3.Transform(partLocalPoint, animatedDraw[0].Rigid.World)), 0, 1e-5f);
        for (int material = 0; material < raisedCloth.Materials.Length; material++)
        {
            AttachmentDraw draw = rig.Draws(overRaised, 1)[material];
            var fittedMesh = rig.Scene.SkinnedSourceSnapshotForTests(draw.Skinned.Mesh);
            Assert.Equal(fittedMesh.Vertices.Select(v => v.Position), raisedCloth.Materials[material].Vertices.Select(v => v.Position));
        }

        BoundAttachmentSet cover = rig.Wear("cover-cloth.json", OutfitRig.Skinned(AttachmentLayer.Outerwear,
            ["beta"], rig.Garment("cover-cloth.glb"), new NamedRegionRun("beta", 4)));
        using BoundOutfit hidden = rig.Cache.BindOutfit(body, [set, cover]);
        Assert.Null(hidden.ClothSurface(0, ClothPartId));
        Assert.Single(rig.Draws(hidden, 0));
        hidden.Dispose();
        Assert.Throws<ObjectDisposedException>(() => hidden.ClothSurface(0, ClothPartId));
    }

    [Theory]
    [InlineData("asset digest", ClothSurfaceRefusal.AssetDigest, null)]
    [InlineData("source point", ClothSurfaceRefusal.SourcePoint, 0)]
    [InlineData("mesh digest", ClothSurfaceRefusal.MeshDigest, null)]
    [InlineData("source count", ClothSurfaceRefusal.Count, null)]
    public void Refused_cloth_load_has_typed_context_and_leaves_uploads_retryable(string fault,
        ClothSurfaceRefusal reason, int? index)
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig, wrongMesh: fault == "source point");
        const string setKey = "coat.json";
        AttachmentSetDefinition candidate = fixture.Set;
        if (fault is "mesh digest" or "source count")
        {
            ClothSurfaceFile sidecar = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey);
            sidecar = fault == "mesh digest" ? sidecar with { MeshSha256 = new string('a', 64) }
                : sidecar with { SourceVertexCount = 13 };
            string hash = rig.Source.Add(ClothKey, ClothSurfaceJson.Write(sidecar));
            var part = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(candidate.Parts));
            var motion = Assert.IsType<ClothMotionDefinition>(part.Motion);
            candidate = new AttachmentSetDefinition([part with { Motion = motion with { Surface = new(ClothKey, hash) } }],
                AttachmentLayer.Armour, []);
        }
        string setHash = rig.Source.Add(setKey, AttachmentSetJson.Write(candidate));
        if (fault == "asset digest") rig.Source.Bytes[ClothKey] = [.. fixture.Bytes, (byte)' '];
        int live = rig.Scene.LiveSkinnedMeshCount;
        var error = Assert.Throws<ClothSurfaceDataException>(() => rig.Cache.Get(setKey, setHash));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(ClothPartId, error.PartId);
        Assert.Equal(ClothKey, error.SourceName);
        Assert.Equal(index, error.ElementIndex);
        Assert.Equal(live, rig.Scene.LiveSkinnedMeshCount);

        if (fault == "asset digest") rig.Source.Bytes[ClothKey] = fixture.Bytes;
        else
        {
            fixture = ClothAsset(rig);
            setHash = rig.Source.Add(setKey, AttachmentSetJson.Write(fixture.Set));
        }
        Assert.Equal(2, rig.Bind(rig.Cache.Get(setKey, setHash)).DrawCount);
    }

    [Fact]
    public void Cloth_binding_can_retry_against_a_valid_host_after_a_bind_refusal()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        LoadedAttachmentSet loaded = rig.Load("retry.json", fixture.Set);
        Matrix4x4[] binds = (Matrix4x4[])rig.Body.InverseBind.Clone();
        binds[1].M42 -= .25f;
        var incompatible = new SkinnedGltfMesh(rig.Body.Vertices, rig.Body.Indices32, binds, rig.Palette, rig.Skeleton);
        int live = rig.Scene.LiveSkinnedMeshCount;
        var refusal = Assert.Throws<AttachmentBindingDataException>(() => loaded.Bind(rig.Map, incompatible));
        Assert.Equal(ClothPartId, refusal.PartId);
        Assert.Equal(AttachmentBindingRefusal.ChainBindMismatch, refusal.Reason);
        Assert.Equal(live, rig.Scene.LiveSkinnedMeshCount);
        Assert.NotNull(rig.Bind(loaded).ClothSurface(ClothPartId));
    }

    [Fact]
    public void Cloth_load_refuses_fallback_weight_on_an_unembedded_vertex()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        var parsed = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey);
        byte[] sidecar = ClothSurfaceJson.Write(parsed with { Render = parsed.Render.RemoveAt(0) });
        string hash = rig.Source.Add(ClothKey, sidecar);
        var part = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var motion = Assert.IsType<ClothMotionDefinition>(part.Motion);
        var invalid = new AttachmentSetDefinition([part with { Motion = motion with { Surface = new(ClothKey, hash) } }],
            AttachmentLayer.Armour, []);

        var error = Assert.Throws<ClothSurfaceDataException>(() => rig.Load("unembedded.json", invalid));
        Assert.Equal(ClothSurfaceRefusal.UnembeddedFallbackVertex, error.Reason);
        Assert.Equal(ClothPartId, error.PartId);
        Assert.Equal(3, error.ElementIndex);
    }

    [Theory]
    [InlineData("model", ClothSurfaceRefusal.ModelPose)]
    [InlineData("host bone", ClothSurfaceRefusal.HostPose)]
    [InlineData("garment bone", ClothSurfaceRefusal.GarmentPose)]
    [InlineData("collapsed fallback face", ClothSurfaceRefusal.FittedTriangle)]
    public void Refused_cloth_pose_leaves_the_entire_sampling_output_unchanged(string fault, ClothSurfaceRefusal reason)
    {
        using var rig = new OutfitRig();
        BoundAttachmentSet set = rig.Wear("animated-coat.json", ClothAsset(rig, collapsible: true).Set);
        BoundClothSurface bound = Assert.IsType<BoundClothSurface>(set.ClothSurface(ClothPartId));
        var sentinel = new Vector3(99, 99, 99);
        Vector3[] output = [sentinel, sentinel, sentinel];
        Matrix4x4[] garment = rig.Draws(set)[0].BoneMatrices.ToArray();
        var model = Matrix4x4.Identity;
        Matrix4x4[] host = (Matrix4x4[])rig.Palette.Clone();
        if (fault == "model") model.M41 = float.NaN;
        else if (fault == "host bone") host[1].M41 = float.NaN;
        else if (fault == "garment bone") garment[0].M41 = float.NaN;
        else garment[1] = Matrix4x4.CreateTranslation(0, 2, 0);
        if (fault == "collapsed fallback face")
            garment[0] = Matrix4x4.CreateTranslation(.75f, 2, 0);

        var error = Assert.Throws<ClothSurfaceDataException>(() =>
        {
            if (fault == "model" || fault == "host bone") bound.CopyPins(host, model, output);
            else bound.CopyFallbackNodes(garment, model, output);
        });
        Assert.Equal(ClothKey, error.SourceName);
        Assert.Equal(ClothPartId, error.PartId);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fault switch { "model" => null, "host bone" => 1, "garment bone" => 0, _ => (int?)2 }, error.ElementIndex);
        Assert.Equal([sentinel, sentinel, sentinel], output);
    }

    [Theory]
    [InlineData(true, "$.pins[1]", 1)]
    [InlineData(false, "$.fallback[2]", 2)]
    public void A_zero_offset_cloth_triangle_sample_refuses_late_collapse_atomically(bool pin, string path, int index)
    {
        using var rig = new OutfitRig();
        BoundAttachmentSet set = rig.Wear("zero-offset.json", ClothAsset(rig, zeroOffsetSamples: true).Set);
        var surface = Assert.IsType<BoundClothSurface>(set.ClothSurface(ClothPartId));
        var sentinel = new Vector3(99, 98, 97);
        Vector3[] output = [sentinel, sentinel, sentinel];
        Matrix4x4 hip = Matrix4x4.CreateTranslation(0, 1, 0);
        Matrix4x4[] host = [hip, hip];
        Matrix4x4[] garment = rig.Draws(set)[0].BoneMatrices.ToArray();
        garment[1] = garment[3] = hip;

        var error = Assert.Throws<ClothSurfaceDataException>(() =>
        {
            if (pin) surface.CopyPins(host, Matrix4x4.Identity, output);
            else surface.CopyFallbackNodes(garment, Matrix4x4.Identity, output);
        });
        Assert.Equal(ClothSurfaceRefusal.FittedTriangle, error.Reason);
        Assert.Equal(ClothKey, error.SourceName);
        Assert.Equal(ClothPartId, error.PartId);
        Assert.Equal(index, error.ElementIndex);
        Assert.Equal(path, error.JsonPath);
        Assert.Equal([sentinel, sentinel, sentinel], output);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void A_fallback_joint_cannot_anchor_another_chain_and_the_refused_mesh_can_be_retried(bool childFirst, int chainIndex)
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        AttachmentSetDefinition invalid = SeparateFallbackChains(fixture.Set, childFirst);
        int live = rig.Scene.LiveSkinnedMeshCount;
        var error = Assert.Throws<ClothSurfaceDataException>(() => rig.Load("anchor.json", invalid));
        Assert.Equal(ClothSurfaceRefusal.FallbackHierarchy, error.Reason);
        Assert.Equal(ClothPartId, error.PartId);
        Assert.Equal(ClothKey, error.SourceName);
        Assert.Equal(chainIndex, error.ElementIndex);
        Assert.Equal(live, rig.Scene.LiveSkinnedMeshCount);
        Assert.Empty(rig.Cache.MeshSnapshotHashesForTests);

        Assert.NotNull(rig.Wear("anchor.json", fixture.Set).ClothSurface(ClothPartId));
    }

    [Fact]
    public void Separate_fallback_chains_can_share_one_fixed_host_anchor()
    {
        using var rig = new OutfitRig();
        AttachmentSetDefinition set = SeparateFallbackChains(ClothAsset(rig, sharedAnchor: true).Set);
        var bound = rig.Wear("shared-anchor.json", set);
        Assert.NotNull(bound.ClothSurface(ClothPartId));
        Matrix4x4[] palette = rig.Draws(bound)[0].BoneMatrices.ToArray();
        Assert.Equal(new Vector3(.25f, 2, 0), palette[2].Translation);
        Assert.Equal(new Vector3(.75f, 2, 0), palette[0].Translation);
    }

    static AttachmentSetDefinition SeparateFallbackChains(AttachmentSetDefinition set, bool childFirst = false)
    {
        var part = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(set.Parts));
        var motion = Assert.IsType<ClothMotionDefinition>(part.Motion);
        ChainDefinition chain = Assert.Single(motion.Fallback.Chains);
        ChainDefinition parent = chain with { Joints = ["left0"] };
        ChainDefinition child = chain with { Joints = ["left1"] };
        ChainMotionDefinition fallback = motion.Fallback with { Chains = childFirst ? [child, parent] : [parent, child] };
        part = part with { Motion = motion with { Fallback = fallback } };
        return new AttachmentSetDefinition([part], AttachmentLayer.Armour, []);
    }

    internal static (AttachmentSetDefinition Set, byte[] Bytes) ClothAsset(OutfitRig rig, bool wrongMesh = false, bool collapsible = false, bool zeroOffsetSamples = false, bool sharedAnchor = false, bool hipOnlyPins = false)
    {
        SkinnedAttachmentGlbFixture glb = SkinnedAttachmentGlbFixture.Chained();
        glb.SetIndexedMaterialParts([0, 1, 2], 3);
        glb.Primitives[2]!["material"] = 0;
        glb.Primitives.Add(glb.Primitives[1]!.DeepClone());
        Vector3[] sheet = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        for (int primitive = 0; primitive < 4; primitive++)
        {
            float offset = primitive == 0 ? 0 : primitive == 1 ? SkinOffset : -SkinOffset;
            Vector3[] positions = primitive == 3
                ? [Vector3.UnitZ * SkinOffset, Vector3.UnitX + Vector3.UnitZ * SkinOffset, Vector3.UnitX - Vector3.UnitZ * SkinOffset]
                : sheet.Select(p => p + Vector3.UnitZ * offset).ToArray();
            glb.SetPositions(primitive, positions);
            glb.SetLiftAttributes(primitive, [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [0, 0, 0]);
        }
        glb.SetUniformSkinning(0, 3, new Vector4(3, 1, 0, 0), hipOnlyPins ? Vector4.UnitX : new Vector4(.75f, .25f, 0, 0));
        for (int primitive = 1; primitive < 4; primitive++)
            glb.SetUniformSkinning(primitive, 3, Vector4.Zero, Vector4.UnitX);

        if (sharedAnchor)
        {
            glb.Nodes[0]!["children"]!.AsArray().Add(3);
            glb.Nodes[2]!.AsObject().Remove("children");
            glb.Nodes[3]!["translation"]![0] = .75f;
        }
        if (zeroOffsetSamples)
        {
            glb.SetUniformSkinning(0, 3, new Vector4(3), Vector4.UnitX);
            glb.SetElement(glb.Primitives[0]!["attributes"]!["JOINTS_0"]!.GetValue<int>(), 2, 1, 0, 0, 0);
        }
        if (collapsible)
            glb.SetElement(glb.Primitives[1]!["attributes"]!["JOINTS_0"]!.GetValue<int>(), 2, 1, 0, 0, 0);

        AttachmentMeshRef mesh = rig.Source.Mesh("coat.glb", glb.Build());
        var sidecar = new ClothSurfaceFile(mesh.Sha256, 12, 4, new(1, 3, 4, 5, 1, 0, .01f, 0, 0),
            [.. sheet], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [new(0, 1, 2)], [],
            [new(0, new(wrongMesh ? 1 : 0, 0, 0, Vector3.UnitX)), new(1, new(0, 0, 1))],
            [new(new(0, 1, 0), 0, Vector3.UnitX, SkinOffset), new(new(0, 1, 1), 0, Vector3.UnitY, SkinOffset),
             new(new(0, 1, 2), 0, Vector3.UnitZ, SkinOffset), new(new(0, 2, 0), 0, Vector3.UnitX, -SkinOffset),
             new(new(0, 2, 1), 0, Vector3.UnitY, -SkinOffset), new(new(0, 2, 2), 0, Vector3.UnitZ, -SkinOffset),
             new(new(0, 3, 0), 0, Vector3.UnitX, SkinOffset), new(new(0, 3, 1), 0, Vector3.UnitY, SkinOffset),
             new(new(0, 3, 2), 0, Vector3.UnitY, -SkinOffset)],
            [new(0, 0, 0), new(0, 0, 1), new(0, 1, 0, Vector3.UnitZ, -SkinOffset)]);
        if (zeroOffsetSamples)
            sidecar = sidecar with
            {
                Pins = [new(0, new(0, 0, 0)), new(1, new(0, 0, 0, Vector3.UnitY))],
                Fallback = sidecar.Fallback.SetItem(2, new(0, 0, 0, Vector3.UnitZ)),
            };
        byte[] bytes = ClothSurfaceJson.Write(sidecar);
        string hash = rig.Source.Add(ClothKey, bytes);
        var part = (SkinnedAttachmentPartDefinition)ChainSet(mesh).Parts[0];
        var motion = new ClothMotionDefinition(new(ClothKey, hash), (ChainMotionDefinition)part.Motion!);
        part = part with { Id = ClothPartId, Motion = motion, Regions = [new("alpha", 1), new("beta", 3)], Lift = new(0) };
        return (new AttachmentSetDefinition([part], AttachmentLayer.Armour, []), bytes);
    }
}
