using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class OutfitLiftDrawTests
{
    const uint ArmourPartId = 3;
    const float Standoff = .125f, Clearance = .03125f, Bearing = .0625f;
    static readonly Vector3[] Chest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
    static readonly Vector3[] Back = [new(2, 0, 0), new(3, 0, 0), new(2, 1, 0)];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_part_clears_the_garment_only_where_it_stands_off(bool customLeases, bool splitMaterials)
    {
        using var rig = new OutfitRig(customLeases: customLeases);
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet garment = Garment(rig);
        BoundAttachmentSet armour = Armour(rig, splitMaterials);
        AttachmentDraw[] sourceDraws = rig.Draws(armour);

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, armour]);

        AttachmentDraw[] draws = rig.Draws(outfit, 1);
        Assert.Equal(splitMaterials ? 2 : 1, draws.Length);
        float distance = Standoff + Clearance - Bearing;
        Vector3[] chest = [Chest[0] + Vector3.UnitZ * distance, Chest[1], Chest[2] + Vector3.UnitX * distance];
        Vector3[][] expected = splitMaterials ? [chest, Back] : [[.. chest, .. Back]];
        for (int material = 0; material < draws.Length; material++)
        {
            SkinnedMeshHandle mesh = draws[material].Skinned.Mesh;
            Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
            Assert.Equal(sourceDraws[material].Skinned with { Mesh = mesh }, draws[material].Skinned);
            Assert.Equal(rig.Indices(sourceDraws[material].Skinned.Mesh), rig.Indices(mesh));
            Assert.Equal(expected[material], Positions(rig, mesh));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identical_lifts_share_a_view_until_the_last_outfit_releases_it(bool withFootprint)
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet garment = withFootprint ? FootprintGarment(rig, body) : Garment(rig);
        BoundAttachmentSet armour = Armour(rig);
        int before = rig.Scene.LiveSkinnedMeshCount;
        using BoundOutfit first = rig.Cache.BindOutfit(body, [garment, armour]);
        using BoundOutfit second = rig.Cache.BindOutfit(body, [garment, armour]);
        SkinnedMeshHandle mesh = ArmourDraw(rig, first);

        Assert.Equal(withFootprint, first.FootprintApplied(0));
        Assert.Equal(withFootprint, second.FootprintApplied(0));
        Assert.Equal(mesh, ArmourDraw(rig, second));
        Assert.True(first.SharesDisplacedViewPositionsForTests(second, 0));
        Assert.Equal(before + 1, rig.Scene.LiveSkinnedMeshCount);

        first.Dispose();
        rig.Scene.Begin();
        Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
        second.Dispose();
        rig.Scene.Begin();
        Assert.False(rig.Scene.IsSkinnedMeshView(mesh));
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_custom_lease_decodes_a_shared_mesh_only_once_even_when_one_use_needs_vertices(bool withLift)
    {
        using var rig = new OutfitRig(customLeases: true);
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet garment = Garment(rig);
        BoundAttachmentSet armour = Armour(rig);
        var part = (SkinnedAttachmentPartDefinition)Assert.Single(armour.Definition.Parts);
        BoundAttachmentSet plain = rig.Wear("plain.json", new AttachmentSetDefinition(
            [part with { Lift = null }], AttachmentLayer.Armour, []));
        int decodesBefore = GltfDecodeAdmission.ModelDecodeEntries;

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, plain, withLift ? armour : plain]);

        // The garment and the shared armour each have one mesh identity.
        Assert.Equal(2, GltfDecodeAdmission.ModelDecodeEntries - decodesBefore);
        Assert.Equal(rig.Draws(plain), rig.Draws(outfit, 1));
        SkinnedMeshHandle lastDraw = Assert.Single(rig.Draws(outfit, 2)).Skinned.Mesh;
        Assert.Equal(withLift, rig.Scene.IsSkinnedMeshView(lastDraw));
    }

    [Fact]
    public void Armour_without_a_garment_draws_its_source()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet armour = Armour(rig);
        int before = rig.Scene.LiveSkinnedMeshCount;

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [armour]);

        Assert.Equal(rig.Draws(armour), rig.Draws(outfit, 0));
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_displaced_view_draws_only_triangles_the_higher_layer_leaves_visible(bool splitMaterials)
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet armour = Armour(rig, splitMaterials);
        BoundAttachmentSet cover = Cover(rig);

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [Garment(rig), armour, cover]);

        SkinnedMeshHandle mesh = ArmourDraw(rig, outfit);
        Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
        Assert.Equal([0u, 1, 2], rig.Indices(mesh));
        Assert.Equal(Chest[0] + Vector3.UnitZ * (Standoff + Clearance - Bearing), Positions(rig, mesh)[0]);
    }

    [Fact]
    public void Matching_source_and_triangles_with_different_positions_use_distinct_views()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet armour = Armour(rig);
        BoundAttachmentSet cover = Cover(rig);
        using BoundOutfit plain = rig.Cache.BindOutfit(body, [Garment(rig, null), armour, cover]);
        using BoundOutfit low = rig.Cache.BindOutfit(body, [Garment(rig), armour, cover]);
        using BoundOutfit high = rig.Cache.BindOutfit(body, [Garment(rig, .25f), armour, cover]);

        SkinnedMeshHandle plainView = ArmourDraw(rig, plain);
        SkinnedMeshHandle lowView = ArmourDraw(rig, low);
        SkinnedMeshHandle highView = ArmourDraw(rig, high);
        Assert.NotEqual(plainView, lowView);
        Assert.NotEqual(lowView, highView);
        Assert.NotEqual(plainView, highView);
        Assert.All(new[] { plainView, lowView, highView }, mesh =>
        {
            Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
            Assert.Equal([0u, 1, 2], rig.Indices(mesh));
        });
        Assert.Equal(Chest[0], Positions(rig, plainView)[0]);
        Assert.Equal(Vector3.UnitZ * (Standoff + Clearance - Bearing), Positions(rig, lowView)[0]);
        Assert.Equal(Vector3.UnitZ * (.25f + Clearance - Bearing), Positions(rig, highView)[0]);
    }

    [Fact]
    public void A_scene_without_displaced_views_refuses_lift_before_creating_any_view()
    {
        using var rig = new OutfitRig(displacedViews: false);
        LoadedBodyRegions body = Body(rig);
        BoundAttachmentSet garment = Garment(rig);
        BoundAttachmentSet bareGarment = Garment(rig, null);
        BoundAttachmentSet armour = Armour(rig);
        BoundAttachmentSet cover = Cover(rig);
        int before = rig.Scene.LiveSkinnedMeshCount;

        AttachmentBindingDataException refusal = Assert.Throws<AttachmentBindingDataException>(() =>
        {
            using BoundOutfit unexpected = rig.Cache.BindOutfit(body, [garment, armour, cover]);
        });

        Assert.Equal(AttachmentBindingRefusal.OutfitLiftUnsupported, refusal.Reason);
        Assert.Equal("armour.json", refusal.SourceName);
        Assert.Equal(1, refusal.WornIndex);
        Assert.Equal(ArmourPartId, refusal.PartId);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
        using BoundOutfit control = rig.Cache.BindOutfit(body, [bareGarment, armour, cover]);
        Assert.True(rig.Scene.IsSkinnedMeshView(ArmourDraw(rig, control)));
        Assert.True(rig.Scene.IsSkinnedMeshView(Assert.Single(rig.Draws(control, 0)).Skinned.Mesh));
    }

    [Fact]
    public void A_lifted_chain_steps_and_keeps_its_displaced_view_for_draws_and_outlines()
    {
        using var rig = new OutfitRig();
        using BoundOutfit outfit = rig.Cache.BindOutfit(Body(rig), [Garment(rig), ChainedArmour(rig)]);
        SkinnedMeshHandle mesh = ArmourDraw(rig, outfit);
        Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
        Assert.Equal(Vector3.UnitZ * (Standoff + Clearance - Bearing), Positions(rig, mesh)[0]);
        Matrix4x4 restTip = Assert.Single(rig.Draws(outfit, 1)).BoneMatrices.Span[0]; // left1 comes first in the skin.
        WornMotion motion = outfit.CreateMotion(null);
        for (int frame = 0; frame < 8; frame++)
            motion.Step(rig.Skeleton, rig.Palette, rig.Model, 1f / 60f);
        var draws = new AttachmentDraw[1];
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);

        Assert.Equal(1, outfit.WriteDraws(1, motion, rig.Skeleton, rig.Palette, rig.Model, rig.Key, template, draws));
        Assert.Equal(mesh, draws[0].Skinned.Mesh);
        Assert.NotEqual(restTip, draws[0].BoneMatrices.Span[0]);
        rig.Scene.Begin();
        MeshOutlineGroup group = rig.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);
        outfit.QueueOutline(1, motion, rig.Skeleton, rig.Palette, rig.Model, group);
        Assert.Equal(mesh, Assert.Single(rig.Recording.OutlinedSkinned));
    }

    static SkinnedMeshHandle ArmourDraw(OutfitRig rig, BoundOutfit outfit) =>
        Assert.Single(rig.Draws(outfit, 1)).Skinned.Mesh;

    static Vector3[] Positions(OutfitRig rig, SkinnedMeshHandle mesh) =>
        rig.Scene.SkinnedSourceSnapshotForTests(mesh).Vertices.Select(vertex => vertex.Position).ToArray();

    internal static LoadedBodyRegions Body(OutfitRig rig) => rig.BodyRegions(new RegionRun(0, 2), new RegionRun(1, 2));

    internal static BoundAttachmentSet Garment(OutfitRig rig, float? standoff = Standoff)
    {
        AttachmentMeshRef mesh = rig.Garment("liner.glb", seed: .25f);
        return rig.Wear(FormattableString.Invariant($"liner-{standoff}.json"), new AttachmentSetDefinition(
            [new SkinnedAttachmentPartDefinition(1, mesh) { Regions = [new("alpha", 2), new("beta", 2)] }],
            AttachmentLayer.Garment, [], [], standoff is float height ? [new("alpha", height)] : []));
    }

    internal static BoundAttachmentSet Armour(OutfitRig rig, bool splitMaterials = false) =>
        rig.Wear("armour.json", ArmourDefinition(rig, splitMaterials));

    internal static AttachmentSetDefinition ArmourDefinition(OutfitRig rig, bool splitMaterials = false)
    {
        SkinnedAttachmentGlbFixture glb = OutfitRig.GarmentFixture();
        glb.SetIndexedMaterialParts([0, 1, 2], 2);
        glb.SetPositions(1, Back);
        glb.Primitives[0]!["material"] = splitMaterials ? 1 : 0;
        glb.Primitives[1]!["material"] = 0;
        glb.SetLiftAttributes(0, [Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX], [Bearing, .25f, Bearing]);
        // Vertices with equal lift data form one section, so give the back its own data.
        glb.SetLiftAttributes(1, [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Bearing, Bearing, Bearing]);
        var part = new SkinnedAttachmentPartDefinition(ArmourPartId, rig.Source.Mesh("shell.glb", glb.Build()))
        {
            Regions = [new("alpha", 1), new("beta", 1)],
            Lift = new(Clearance),
        };
        return new AttachmentSetDefinition([part], AttachmentLayer.Armour, []);
    }

    internal static BoundAttachmentSet ChainedArmour(OutfitRig rig)
    {
        SkinnedAttachmentGlbFixture glb = SkinnedAttachmentGlbFixture.Chained();
        glb.SetLiftAttributes(0, [Vector3.UnitZ, Vector3.UnitY, Vector3.UnitZ], [Bearing, .25f, Bearing]);
        var part = (SkinnedAttachmentPartDefinition)ChainSet(rig.Source.Mesh("chain.glb", glb.Build())).Parts[0];
        return rig.Wear("chain.json", new AttachmentSetDefinition([part with { Lift = new(Clearance) }], AttachmentLayer.Armour, []));
    }

    static BoundAttachmentSet Cover(OutfitRig rig) =>
        rig.Wear("cover.json", OutfitRig.Skinned(AttachmentLayer.Outerwear, ["beta"],
            rig.Garment("cover.glb", seed: .5f), new NamedRegionRun("beta", 4)));
}
