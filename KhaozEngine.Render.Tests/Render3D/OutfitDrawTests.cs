using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class OutfitDrawTests
{
    // The body has four triangles. Its region map tags them alpha, alpha, beta, beta. Region gamma is declared and
    // tags none of them, and region delta is undeclared.
    static readonly RegionRun[] BodyRuns = [new(0, 2), new(1, 2)];

    [Fact]
    public void A_covered_body_region_is_not_drawn()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"],
            rig.Garment("garment.glb"), new NamedRegionRun("alpha", 4)));

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment]);

        Assert.Equal(1, outfit.BodyPartCount);
        Assert.True(outfit.TryBodyMesh(0, out SkinnedMeshHandle mesh));
        Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
        Assert.Equal([2u, 3, 4, 4, 3, 5], rig.Indices(mesh));
    }

    [Fact]
    public void An_uncovered_body_draws_its_source_handle()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["gamma"],
            rig.Garment("garment.glb"), new NamedRegionRun("gamma", 4)));
        int before = rig.Scene.LiveSkinnedMeshCount;

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment]);

        Assert.True(outfit.TryBodyMesh(0, out SkinnedMeshHandle mesh));
        Assert.Equal(rig.BodyHandle, mesh);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void A_fully_covered_body_part_is_skipped()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha", "beta"],
            rig.Garment("garment.glb"), new NamedRegionRun("alpha", 4)));
        int before = rig.Scene.LiveSkinnedMeshCount;

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment]);

        Assert.False(outfit.TryBodyMesh(0, out SkinnedMeshHandle mesh));
        Assert.Equal(default, mesh);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void A_lower_piece_loses_only_its_covered_triangles()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        // Eight garment triangles over two material handles of four each. The beta run spans the handle boundary, so
        // the first handle keeps its three alpha triangles and the second keeps its two gamma triangles.
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"],
            rig.Garment("garment.glb", materials: 2), new NamedRegionRun("alpha", 3), new NamedRegionRun("beta", 3), new NamedRegionRun("gamma", 2)));
        BoundAttachmentSet armour = rig.Wear("armour.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["beta"],
            rig.Garment("armour.glb", seed: .5f), new NamedRegionRun("beta", 4)));
        AttachmentDraw[] plainGarment = rig.Draws(garment), plainArmour = rig.Draws(armour);

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, armour]);

        AttachmentDraw[] garmentDraws = rig.Draws(outfit, 0);
        Assert.Equal(2, garmentDraws.Length);
        Assert.Equal([0, 1], garmentDraws.Select(draw => draw.MaterialIndex));
        Assert.All(garmentDraws, draw => Assert.True(rig.Scene.IsSkinnedMeshView(draw.Skinned.Mesh)));
        Assert.Equal([0u, 1, 2, 1, 2, 0, 2, 0, 1], rig.Indices(garmentDraws[0].Skinned.Mesh));
        Assert.Equal([2u, 0, 1, 0, 2, 1], rig.Indices(garmentDraws[1].Skinned.Mesh));
        for (int i = 0; i < 2; i++)
            Assert.Equal(plainGarment[i].Skinned with { Mesh = garmentDraws[i].Skinned.Mesh }, garmentDraws[i].Skinned);
        Assert.Equal(plainArmour, rig.Draws(outfit, 1));
        Assert.False(outfit.TryBodyMesh(0, out _));
    }

    [Fact]
    public void A_hidden_rigid_part_writes_no_draw_and_no_outline()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        AttachmentMeshRef hiddenMesh = rig.Source.Mesh("hidden.glb", AttachmentTestSource.Glb());
        AttachmentMeshRef shownMesh = rig.Source.Mesh("shown.glb", AttachmentTestSource.Glb(materials: 2));
        BoundAttachmentSet garment = rig.Wear("garment.json", new AttachmentSetDefinition(
            [OutfitRig.Rigid(1, hiddenMesh, "beta"), OutfitRig.Rigid(2, shownMesh, "gamma")], AttachmentLayer.Garment, []));
        BoundAttachmentSet armour = rig.Wear("armour.json", new AttachmentSetDefinition(
            [OutfitRig.Rigid(1, hiddenMesh, "beta")], AttachmentLayer.Armour, ["beta"]));
        AttachmentDraw[] plain = rig.Draws(garment);

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, armour]);

        Assert.Equal(2, outfit.DrawCount(0));
        Assert.Equal(plain.Where(draw => draw.PartId == 2), rig.Draws(outfit, 0));
        rig.Scene.Begin();
        MeshOutlineGroup group = rig.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);
        outfit.QueueOutline(0, rig.Skeleton, rig.Palette, rig.Model, group);
        Assert.Equal(plain.Where(draw => draw.PartId == 2).Select(draw => draw.Rigid.Mesh), rig.Recording.OutlinedMeshes);
        Assert.Empty(rig.Recording.OutlinedSkinned);
    }

    [Fact]
    public void Two_outfits_with_the_same_hidden_set_share_one_view()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        LoadedAttachmentSet loaded = rig.Load("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"],
            rig.Garment("garment.glb"), new NamedRegionRun("alpha", 4)));
        int before = rig.Scene.LiveSkinnedMeshCount;

        using BoundOutfit first = rig.Cache.BindOutfit(body, [rig.Bind(loaded)]);
        using BoundOutfit second = rig.Cache.BindOutfit(body, [rig.Bind(loaded)]);

        Assert.True(first.TryBodyMesh(0, out SkinnedMeshHandle firstView));
        Assert.True(second.TryBodyMesh(0, out SkinnedMeshHandle secondView));
        Assert.Equal(firstView, secondView);
        Assert.True(rig.Scene.IsSkinnedMeshView(firstView));
        Assert.Equal(before + 1, rig.Scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void A_shared_view_survives_until_its_last_outfit_is_disposed()
    {
        using var rig = new OutfitRig();
        (LoadedBodyRegions body, LoadedAttachmentSet loaded) = rig.CoveredAlpha();
        int before = rig.Scene.LiveSkinnedMeshCount;
        BoundOutfit first = rig.Cache.BindOutfit(body, [rig.Bind(loaded)]);
        BoundOutfit second = rig.Cache.BindOutfit(body, [rig.Bind(loaded)]);
        Assert.True(first.TryBodyMesh(0, out SkinnedMeshHandle view));

        first.Dispose();
        rig.Scene.Begin();

        Assert.True(second.TryBodyMesh(0, out SkinnedMeshHandle kept));
        Assert.Equal(view, kept);
        Assert.True(rig.Scene.IsSkinnedMeshView(view));
        Assert.Equal(before + 1, rig.Scene.LiveSkinnedMeshCount);

        second.Dispose();
        // The renderer keeps a view drawable until the frame ends, then frees it at the next frame boundary.
        Assert.True(rig.Scene.IsSkinnedMeshView(view));
        rig.Scene.Begin();
        Assert.False(rig.Scene.IsSkinnedMeshView(view));
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void A_view_is_released_once_and_never_while_another_outfit_uses_it()
    {
        using var rig = new OutfitRig();
        (LoadedBodyRegions body, LoadedAttachmentSet loaded) = rig.CoveredAlpha();
        BoundOutfit first = rig.Cache.BindOutfit(body, [rig.Bind(loaded)]);
        BoundOutfit second = rig.Cache.BindOutfit(body, [rig.Bind(loaded)]);
        Assert.True(second.TryBodyMesh(0, out SkinnedMeshHandle view));

        // The renderer refuses a second unload of a view in one frame, so a repeated release would throw here.
        first.Dispose();
        first.Dispose();
        rig.Scene.Begin();

        Assert.True(rig.Scene.IsSkinnedMeshView(view));
        var refused = Assert.Throws<SkinnedMeshViewArgumentException>(() => rig.Scene.UnloadSkinnedMesh(rig.BodyHandle));
        Assert.Equal(SkinnedMeshViewRefusal.SourceHasLiveViews, refused.Reason);
        Assert.Equal(1, refused.LiveViews);

        second.Dispose();
        rig.Scene.UnloadSkinnedMesh(rig.BodyHandle);
        Assert.False(rig.Scene.IsSkinnedMeshView(view));
    }

    [Fact]
    public void An_outfit_is_disposed_before_its_cache()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, [],
            rig.Garment("garment.glb"), new NamedRegionRun("alpha", 2), new NamedRegionRun("beta", 2)));
        BoundAttachmentSet armour = rig.Wear("armour.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["beta"],
            rig.Garment("armour.glb", seed: .5f), new NamedRegionRun("beta", 4)));
        BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, armour]);

        var refused = Assert.Throws<SkinnedMeshViewArgumentException>(() => rig.Cache.Dispose());

        Assert.Equal(SkinnedMeshViewRefusal.SourceHasLiveViews, refused.Reason);
        Assert.Single(rig.Draws(outfit, 0));
        outfit.Dispose();
        rig.Cache.Dispose();
    }

    [Fact]
    public void A_refused_view_releases_the_views_bound_before_it()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        // The garment's beta triangles are covered and bound first, then the body's partly covered part fails.
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, [],
            rig.Garment("garment.glb"), new NamedRegionRun("alpha", 2), new NamedRegionRun("beta", 2)));
        BoundAttachmentSet armour = rig.Wear("armour.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["beta"],
            rig.Garment("armour.glb", seed: .5f), new NamedRegionRun("beta", 4)));
        rig.Scene.UnloadSkinnedMesh(rig.BodyHandle);
        int before = rig.Scene.LiveSkinnedMeshCount;

        var refused = Assert.Throws<SkinnedMeshViewArgumentException>(() => rig.Cache.BindOutfit(body, [garment, armour]));

        Assert.Equal(SkinnedMeshViewRefusal.SourceNotLive, refused.Reason);
        rig.Scene.Begin();
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
        rig.Cache.Dispose();
    }

    [Fact]
    public void Binding_an_outfit_on_a_scene_without_views_is_refused()
    {
        using var plain = new OutfitRig(views: false);
        (LoadedBodyRegions plainBody, LoadedAttachmentSet plainSet) = plain.CoveredAlpha();
        int before = plain.Scene.LiveSkinnedMeshCount;

        var error = Assert.Throws<AttachmentBindingDataException>(() => plain.Cache.BindOutfit(plainBody, [plain.Bind(plainSet)]));

        Assert.Equal(AttachmentBindingRefusal.OutfitViewsUnsupported, error.Reason);
        Assert.Equal(before, plain.Scene.LiveSkinnedMeshCount);

        using var viewing = new OutfitRig();
        (LoadedBodyRegions body, LoadedAttachmentSet loaded) = viewing.CoveredAlpha();
        using BoundOutfit outfit = viewing.Cache.BindOutfit(body, [viewing.Bind(loaded)]);
        Assert.True(outfit.TryBodyMesh(0, out SkinnedMeshHandle view));
        Assert.True(viewing.Scene.IsSkinnedMeshView(view));
    }

    [Fact]
    public void Writing_draws_allocates_nothing_once_warm()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        BoundAttachmentSet garment = rig.Wear("garment.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"],
            rig.Garment("garment.glb", materials: 2), new NamedRegionRun("alpha", 3), new NamedRegionRun("beta", 3), new NamedRegionRun("gamma", 2)));
        BoundAttachmentSet armour = rig.Wear("armour.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["beta"],
            rig.Garment("armour.glb", seed: .5f), new NamedRegionRun("beta", 4)));
        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, armour]);
        var draws = new AttachmentDraw[4];
        RigidInstanceDraw template = new(default, Matrix4x4.Identity);
        void Frame()
        {
            outfit.WriteDraws(0, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws);
            outfit.WriteDraws(1, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws);
            outfit.TryBodyMesh(0, out _);
        }
        Frame();

        long allocated;
        using (AllocationRegion.Start("warm outfit draws"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) Frame();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_region_map_for_another_rig_is_refused()
    {
        using var rig = new OutfitRig();
        string sha = rig.BodyMap("body.json", OutfitRig.Rig, BodyRuns);
        string other = "rig-v1:" + new string('b', 64);

        var error = Assert.Throws<AttachmentBindingDataException>(() =>
            rig.Cache.GetBodyRegions("body.json", sha, other, [rig.BodyHandle], [rig.Body]));

        Assert.Equal(AttachmentBindingRefusal.BodyRegionMapMismatch, error.Reason);
        Assert.Equal("body.json", error.SourceName);
        Assert.Equal(other, error.ExpectedRigIdentity);
        Assert.Equal(OutfitRig.Rig, error.ActualRigIdentity);
        LoadedBodyRegions control = rig.Cache.GetBodyRegions("body.json", sha, OutfitRig.Rig, [rig.BodyHandle], [rig.Body]);
        Assert.Equal(OutfitRig.Rig, control.Map.RigIdentity);
    }

    [Fact]
    public void A_region_map_whose_parts_differ_from_the_body_is_refused()
    {
        using var rig = new OutfitRig();
        string shortSha = rig.BodyMap("short.json", OutfitRig.Rig, new RegionRun(0, 2), new RegionRun(1, 1));
        string extraSha = rig.BodyMap("extra.json", OutfitRig.Rig, [BodyRuns, [new RegionRun(0, 1)]]);
        string fitSha = rig.BodyMap("fit.json", OutfitRig.Rig, BodyRuns);

        var triangles = Assert.Throws<AttachmentBindingDataException>(() =>
            rig.Cache.GetBodyRegions("short.json", shortSha, OutfitRig.Rig, [rig.BodyHandle], [rig.Body]));
        var parts = Assert.Throws<AttachmentBindingDataException>(() =>
            rig.Cache.GetBodyRegions("extra.json", extraSha, OutfitRig.Rig, [rig.BodyHandle], [rig.Body]));

        Assert.Equal(AttachmentBindingRefusal.BodyRegionMapMismatch, triangles.Reason);
        Assert.Equal(0, triangles.BodyPartIndex);
        Assert.Equal(4, triangles.ExpectedTriangleCount);
        Assert.Equal(3, triangles.ActualTriangleCount);
        Assert.Equal(AttachmentBindingRefusal.BodyRegionMapMismatch, parts.Reason);
        Assert.Equal(1, parts.ExpectedPartCount);
        Assert.Equal(2, parts.ActualPartCount);
        LoadedBodyRegions control = rig.Cache.GetBodyRegions("fit.json", fitSha, OutfitRig.Rig, [rig.BodyHandle], [rig.Body]);
        Assert.Equal(4, control.Map.TriangleCount(0));
    }

    [Fact]
    public void A_piece_naming_a_region_the_body_lacks_is_refused()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        AttachmentMeshRef mesh = rig.Garment("garment.glb");
        BoundAttachmentSet first = rig.Wear("first.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"], mesh, new NamedRegionRun("alpha", 4)));
        BoundAttachmentSet known = rig.Wear("known.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["gamma"], mesh, new NamedRegionRun("beta", 4)));
        BoundAttachmentSet unknown = rig.Wear("unknown.json", OutfitRig.Skinned(AttachmentLayer.Armour, ["delta"], mesh, new NamedRegionRun("beta", 4)));
        int before = rig.Scene.LiveSkinnedMeshCount;

        var error = Assert.Throws<AttachmentBindingDataException>(() => rig.Cache.BindOutfit(body, [first, unknown]));

        Assert.Equal(AttachmentBindingRefusal.OutfitRegionUnknown, error.Reason);
        Assert.Equal("unknown.json", error.SourceName);
        Assert.Equal(1, error.WornIndex);
        Assert.Equal("delta", error.RegionName);
        Assert.Null(error.PartId);
        Assert.IsType<OutfitDataException>(error.InnerException);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
        using BoundOutfit control = rig.Cache.BindOutfit(body, [first, known]);
        Assert.Single(rig.Draws(control, 1));
    }

    [Fact]
    public void A_part_whose_region_runs_miss_its_triangles_is_refused()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        AttachmentMeshRef mesh = rig.Garment("garment.glb");
        BoundAttachmentSet broken = rig.Wear("broken.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"], mesh, new NamedRegionRun("alpha", 3)));
        BoundAttachmentSet whole = rig.Wear("whole.json", OutfitRig.Skinned(AttachmentLayer.Garment, ["alpha"], mesh, new NamedRegionRun("alpha", 4)));
        int before = rig.Scene.LiveSkinnedMeshCount;

        var error = Assert.Throws<AttachmentBindingDataException>(() => rig.Cache.BindOutfit(body, [broken]));

        Assert.Equal(AttachmentBindingRefusal.PartRegionTagsMismatch, error.Reason);
        Assert.Equal("broken.json", error.SourceName);
        Assert.Equal(1u, error.PartId);
        Assert.Equal(mesh.Key, error.MeshKey);
        Assert.Equal(4, error.ExpectedTriangleCount);
        Assert.Equal(3, error.ActualTriangleCount);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
        using BoundOutfit control = rig.Cache.BindOutfit(body, [whole]);
        Assert.Single(rig.Draws(control, 0));
    }

    [Fact]
    public void A_format_2_set_draws_exactly_as_it_does_today()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = rig.BodyRegions(BodyRuns);
        AttachmentMeshRef rigid = rig.Source.Mesh("rigid.glb", AttachmentTestSource.Glb());
        BoundAttachmentSet unlayered = rig.Wear("unlayered.json", new AttachmentSetDefinition(
            [new RigidAttachmentPartDefinition(1, rigid, "hip", Matrix4x4.Identity),
                new SkinnedAttachmentPartDefinition(2, rig.Garment("garment.glb", materials: 2))]));
        BoundAttachmentSet armour = rig.Wear("armour.json", OutfitRig.Skinned(AttachmentLayer.Outerwear, ["alpha", "beta", "gamma"],
            rig.Garment("armour.glb", seed: .5f), new NamedRegionRun("beta", 4)));
        AttachmentDraw[] plain = rig.Draws(unlayered);
        rig.Scene.Begin();
        MeshOutlineGroup group = rig.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);
        unlayered.QueueOutline(rig.Skeleton, rig.Palette, rig.Model, group);
        MeshHandle[] plainRigidOutlines = rig.Recording.OutlinedMeshes.ToArray();
        SkinnedMeshHandle[] plainSkinnedOutlines = rig.Recording.OutlinedSkinned.ToArray();
        rig.Recording.OutlinedMeshes.Clear();
        rig.Recording.OutlinedSkinned.Clear();

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [unlayered, armour]);

        Assert.Equal(unlayered.DrawCount, outfit.DrawCount(0));
        Assert.Equal(plain, rig.Draws(outfit, 0));
        outfit.QueueOutline(0, rig.Skeleton, rig.Palette, rig.Model, group);
        Assert.Equal(plainRigidOutlines, rig.Recording.OutlinedMeshes);
        Assert.Equal(plainSkinnedOutlines, rig.Recording.OutlinedSkinned);
    }
}

/// <summary>A synthetic two bone body of four triangles in one material part, its region map and the worn sets.</summary>
internal sealed class OutfitRig : IDisposable
{
    internal static readonly string Rig = "rig-v1:" + new string('a', 64);
    static readonly string[] Regions = ["alpha", "beta", "gamma"];
    static readonly Matrix4x4[] Binds = [Matrix4x4.CreateTranslation(0, -2, 0), Matrix4x4.CreateTranslation(0, -1, 0)];
    // Four distinct triangles over a garment's three vertices.
    static readonly ushort[] GarmentTriangles = [0, 1, 2, 1, 2, 0, 2, 0, 1, 0, 2, 1];

    readonly AttachmentAssetHarness _harness = new();
    internal Scene3D Scene => _harness.Scene;
    internal readonly AttachmentTestSource Source = new();
    internal readonly Skeleton Skeleton = RigidAttachmentTestRig.Create();
    internal readonly Matrix4x4[] Palette;
    internal readonly ContractJointMap Map;
    internal readonly SkinnedGltfMesh Body;
    internal readonly SkinnedMeshHandle BodyHandle;
    internal readonly OutfitRecordingScene Recording;
    internal readonly AttachmentSetCache Cache;
    internal readonly Matrix4x4 Model = Matrix4x4.CreateTranslation(10, 0, 0);
    internal readonly MotionKey Key = MotionKey.From(77);

    internal OutfitRig(bool views = true)
    {
        Palette = Skeleton.ComposeRestPose();
        Map = RigidAttachmentTestRig.Map(Skeleton);
        var vertices = new SkinnedVertex[6];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new SkinnedVertex
            {
                Position = new Vector3(i / 2 * .5f, i % 2 * .5f, 0),
                Normal = Vector3.UnitZ,
                Color = Vector4.One,
                BoneWeights = new Vector4(1, 0, 0, 0),
            };
        Body = new SkinnedGltfMesh(vertices, [0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5], Binds, Palette, Skeleton);
        BodyHandle = Scene.LoadSkinnedMesh(Body);
        Recording = new OutfitRecordingScene(Scene);
        Cache = new AttachmentSetCache(views ? Recording : new MixedAttachmentScene(new Scene3DAttachmentScene(Scene)), Source);
    }

    internal string BodyMap(string key, string rig, params RegionRun[] runs) => BodyMap(key, rig, [runs]);

    internal string BodyMap(string key, string rig, IEnumerable<RegionRun>[] parts) =>
        Source.Add(key, BodyRegionMapJson.Write(new BodyRegionMap(rig, Regions, parts)));

    internal LoadedBodyRegions BodyRegions(params RegionRun[] runs) =>
        Cache.GetBodyRegions("body.json", BodyMap("body.json", Rig, runs), Rig, [BodyHandle], [Body]);

    internal (LoadedBodyRegions Body, LoadedAttachmentSet Set) CoveredAlpha() => (BodyRegions(new RegionRun(0, 2), new RegionRun(1, 2)),
        Load("garment.json", Skinned(AttachmentLayer.Garment, ["alpha"], Garment("garment.glb"), new NamedRegionRun("alpha", 4))));

    internal LoadedAttachmentSet Load(string key, AttachmentSetDefinition definition) =>
        Cache.Get(key, Source.Add(key, AttachmentSetJson.Write(definition)));

    internal BoundAttachmentSet Bind(LoadedAttachmentSet loaded) => loaded.Bind(Map, Body);

    internal BoundAttachmentSet Wear(string key, AttachmentSetDefinition definition) => Bind(Load(key, definition));

    internal static AttachmentSetDefinition Skinned(AttachmentLayer layer, string[] covers, AttachmentMeshRef mesh,
        params NamedRegionRun[] runs) =>
        new([new SkinnedAttachmentPartDefinition(1, mesh) { Regions = runs }], layer, covers);

    internal static RigidAttachmentPartDefinition Rigid(uint id, AttachmentMeshRef mesh, string region) =>
        new(id, mesh, "hip", Matrix4x4.Identity) { Region = region };

    /// <summary>A skinned garment of four indexed triangles per material, bound to the body's two joints.</summary>
    internal AttachmentMeshRef Garment(string key, int materials = 1, float seed = 0)
    {
        var glb = new SkinnedAttachmentGlbFixture(2);
        glb.Nodes[0]!["name"] = "hip";
        glb.Nodes[1]!["name"] = "knee";
        glb.SetSkinJoints([0, 1]);
        glb.SetInverseBind(0, Binds[1]);
        glb.SetInverseBind(1, Binds[0]);
        glb.SetElement(0, 0, seed, 0, 0);
        byte[] indices = new byte[GarmentTriangles.Length * sizeof(ushort)];
        for (int i = 0; i < GarmentTriangles.Length; i++)
            BitConverter.TryWriteBytes(indices.AsSpan(i * sizeof(ushort)), GarmentTriangles[i]);
        glb.Accessors.Add(new JsonObject
        {
            ["bufferView"] = glb.AddView(indices),
            ["componentType"] = 5123,
            ["count"] = GarmentTriangles.Length,
            ["type"] = "SCALAR",
        });
        var materialList = new JsonArray();
        for (int i = 0; i < materials; i++) materialList.Add(new JsonObject());
        glb.Root["materials"] = materialList;
        JsonObject first = glb.Primitives[0]!.AsObject();
        first["indices"] = glb.Accessors.Count - 1;
        first["material"] = 0;
        for (int i = 1; i < materials; i++)
        {
            JsonObject next = first.DeepClone().AsObject();
            next["material"] = i;
            glb.Primitives.Add(next);
        }
        return Source.Mesh(key, glb.Build());
    }

    internal uint[] Indices(SkinnedMeshHandle mesh) => Scene.SkinnedSourceSnapshotForTests(mesh).Indices32;

    internal AttachmentDraw[] Draws(BoundAttachmentSet bound)
    {
        var draws = new AttachmentDraw[bound.DrawCount];
        bound.WriteDraws(Skeleton, Palette, Model, Key, new RigidInstanceDraw(default, Matrix4x4.Identity), draws);
        return draws;
    }

    internal AttachmentDraw[] Draws(BoundOutfit outfit, int worn)
    {
        var draws = new AttachmentDraw[outfit.DrawCount(worn)];
        outfit.WriteDraws(worn, Skeleton, Palette, Model, Key, new RigidInstanceDraw(default, Matrix4x4.Identity), draws);
        return draws;
    }

    public void Dispose() { Cache.Dispose(); _harness.Dispose(); }
}

/// <summary>Forwards to the shipped adapter and records which meshes each outline call names.</summary>
internal sealed class OutfitRecordingScene(Scene3D scene) : IAttachmentViewScene
{
    readonly Scene3DAttachmentScene _adapter = new(scene);
    internal List<MeshHandle> OutlinedMeshes { get; } = [];
    internal List<SkinnedMeshHandle> OutlinedSkinned { get; } = [];
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => _adapter.UploadAttachment(parts);
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts) =>
        _adapter.UploadSkinnedAttachment(parts);
    public SkinnedMeshHandle CreateSkinnedView(SkinnedMeshHandle source, ReadOnlySpan<uint> indices) =>
        _adapter.CreateSkinnedView(source, indices);
    public void ReleaseSkinnedView(SkinnedMeshHandle view) => _adapter.ReleaseSkinnedView(view);
    public void DrawMesh(in RigidInstanceDraw draw) => _adapter.DrawMesh(draw);
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> bones) => _adapter.DrawSkinned(draw, bones);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement)
    {
        OutlinedMeshes.Add(mesh);
        _adapter.DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    }
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> bones, Matrix4x4 world, float dissolve, bool complement)
    {
        OutlinedSkinned.Add(mesh);
        _adapter.DrawSkinnedOutlineDissolved(group, mesh, bones, world, dissolve, complement);
    }
}
