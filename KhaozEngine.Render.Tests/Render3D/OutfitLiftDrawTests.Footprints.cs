using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class OutfitLiftDrawTests
{
    const float MeasuredHeight = .0625f;

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void Footprint_lift_uses_the_body_it_was_measured_on(bool matchingHash, bool matchingBodyCount, bool measuredArmour)
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        LoadedBodyRegions measuredBody = matchingHash ? body : rig.BodyRegions(new RegionRun(0, 1), new RegionRun(1, 3));
        (AttachmentSetDefinition definition, LiftFootprint measured) = MeasureGarment(rig, measuredBody);
        bool matchingBody = matchingHash && matchingBodyCount;
        if (!matchingBody)
        {
            // An inapplicable file can also describe a different version of the garment mesh.
            LiftFootprintPart part = Assert.Single(measured.Parts);
            LiftFootprintTriangle triangle = Assert.Single(part.Triangles);
            measured = new LiftFootprint(measured.BodyRegionMapSha256, measured.BodyTriangleCount + (matchingBodyCount ? 0 : 1),
                [new LiftFootprintPart(part.PartId, [triangle, triangle])]);
        }
        BoundAttachmentSet garment = rig.Wear("garment.json", rig.WithFootprint("liner.khlf", definition, measured));
        // A measured lifting part must reach the resolver through binding too, not only lower sets.
        BoundAttachmentSet armour = measuredArmour ? MeasuredArmour(rig, measuredBody) : Armour(rig);

        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [garment, armour]);

        var mesh = ArmourDraw(rig, outfit);
        Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
        Assert.Equal((matchingBody ? MeasuredHeight : Standoff) + Clearance - Bearing, Positions(rig, mesh)[0].Z, 5);
        Assert.Equal(matchingBody, outfit.FootprintApplied(0));
        Assert.Equal(measuredArmour && matchingHash, outfit.FootprintApplied(1));
    }

    [Fact]
    public void A_footprint_with_different_part_ids_leaves_the_set_cache_unchanged()
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        (AttachmentSetDefinition definition, LiftFootprint measured) = MeasureGarment(rig, body);
        LiftFootprintPart part = Assert.Single(measured.Parts);
        var wrong = new LiftFootprint(measured.BodyRegionMapSha256, measured.BodyTriangleCount,
            [new LiftFootprintPart(part.PartId + 1, part.Triangles)]);
        AttachmentSetDefinition refused = rig.WithFootprint("wrong.khlf", definition, wrong);
        int before = rig.Scene.LiveSkinnedMeshCount;
        var definitionsBefore = rig.Cache.DefinitionSnapshotHashesForTests;
        var meshesBefore = rig.Cache.MeshSnapshotHashesForTests;

        var error = Assert.Throws<LiftFootprintDataException>(() => rig.Load("garment.json", refused));

        Assert.Equal(LiftFootprintRefusal.PartIdsMismatch, error.Reason);
        Assert.Equal("wrong.khlf", error.SourceName);
        Assert.Equal(part.PartId, error.PartId);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
        Assert.Equal(definitionsBefore, rig.Cache.DefinitionSnapshotHashesForTests);
        Assert.Equal(meshesBefore, rig.Cache.MeshSnapshotHashesForTests);
        BoundAttachmentSet control = rig.Wear("garment.json", rig.WithFootprint("matching.khlf", definition, measured));
        using BoundOutfit accepted = rig.Cache.BindOutfit(body, [control]);
        Assert.True(accepted.FootprintApplied(0));
        Assert.Single(rig.Draws(accepted, 0));
    }

    [Fact]
    public void Footprint_bytes_must_match_the_declared_digest_before_the_set_loads()
    {
        const string setKey = "garment.json", footprintKey = "liner.khlf";
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        (AttachmentSetDefinition definition, LiftFootprint measured) = MeasureGarment(rig, body);
        AttachmentSetDefinition declared = rig.WithFootprint(footprintKey, definition, measured);
        string setHash = rig.Source.Add(setKey, AttachmentSetJson.Write(declared));
        var substituted = new LiftFootprint(measured.BodyRegionMapSha256, measured.BodyTriangleCount + 1, measured.Parts);
        rig.Source.Add(footprintKey, LiftFootprintFile.Write(substituted));

        Assert.Throws<InvalidDataException>(() => rig.Cache.Get(setKey, setHash));
        Assert.Empty(rig.Cache.DefinitionSnapshotHashesForTests);

        rig.Source.Add(footprintKey, LiftFootprintFile.Write(measured));
        using BoundOutfit outfit = rig.Cache.BindOutfit(body, [rig.Bind(rig.Cache.Get(setKey, setHash))]);
        Assert.True(outfit.FootprintApplied(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_matching_body_footprint_with_the_wrong_triangle_count_refuses_before_creating_views(bool rigid)
    {
        using var rig = new OutfitRig();
        LoadedBodyRegions body = Body(rig);
        (AttachmentSetDefinition definition, LiftFootprint measured) = MeasureGarment(rig, body, rigid);
        LiftFootprintPart part = Assert.Single(measured.Parts);
        LiftFootprintTriangle triangle = Assert.Single(part.Triangles);
        var wrong = new LiftFootprint(measured.BodyRegionMapSha256, measured.BodyTriangleCount,
            [new LiftFootprintPart(part.PartId, [triangle, triangle])]);
        BoundAttachmentSet garment = rig.Wear("garment.json", rig.WithFootprint("wrong.khlf", definition, wrong));
        BoundAttachmentSet control = rig.Wear("control.json", rig.WithFootprint("matching.khlf", definition, measured));
        BoundAttachmentSet armour = Armour(rig);
        BoundAttachmentSet cover = Cover(rig);
        int before = rig.Scene.LiveSkinnedMeshCount;

        var error = Assert.Throws<AttachmentBindingDataException>(() =>
        {
            using BoundOutfit unexpected = rig.Cache.BindOutfit(body, [garment, armour, cover]);
        });

        Assert.Equal(AttachmentBindingRefusal.FootprintTriangleCountMismatch, error.Reason);
        Assert.Equal("garment.json", error.SourceName);
        Assert.Equal(0, error.WornIndex);
        Assert.Equal(part.PartId, error.PartId);
        Assert.Equal(1, error.ExpectedTriangleCount);
        Assert.Equal(2, error.ActualTriangleCount);
        Assert.Equal(before, rig.Scene.LiveSkinnedMeshCount);
        using BoundOutfit accepted = rig.Cache.BindOutfit(body, [control, armour, cover]);
        Assert.True(accepted.TryBodyMesh(0, out var mesh));
        Assert.True(rig.Scene.IsSkinnedMeshView(mesh));
    }

    internal static BoundAttachmentSet FootprintGarment(OutfitRig rig, LoadedBodyRegions body)
    {
        (AttachmentSetDefinition definition, LiftFootprint footprint) = MeasureGarment(rig, body);
        return rig.Wear("footprint-liner.json", rig.WithFootprint("liner.khlf", definition, footprint));
    }

    static BoundAttachmentSet MeasuredArmour(OutfitRig rig, LoadedBodyRegions measuredBody)
    {
        AttachmentSetDefinition definition = ArmourDefinition(rig);
        // Upload order puts both primitives in one material group: the chest triangle, then the back triangle.
        Vector3[] positions = [.. Chest, .. Back];
        var claim = new CoverageClaimMesh(positions, new uint[] { 0, 1, 2, 3, 4, 5 }) { PartId = ArmourPartId };
        LiftFootprint footprint = rig.MeasureFootprint(measuredBody, definition, claim);
        return rig.Wear("armour.json", rig.WithFootprint("shell.khlf", definition, footprint));
    }

    static (AttachmentSetDefinition, LiftFootprint) MeasureGarment(OutfitRig rig, LoadedBodyRegions body, bool rigid = false)
    {
        const uint id = 1;
        const float side = .015625f;
        // The body fixture winds toward -Z. The liner covers a small patch of its alpha region.
        var origin = new Vector3(.0625f, .0625f, -MeasuredHeight);
        Vector3[] positions = [origin, origin + Vector3.UnitX * side, origin + Vector3.UnitY * side];
        AttachmentPartDefinition part;
        if (rigid)
        {
            part = OutfitRig.Rigid(id, rig.Source.Mesh("rigid-liner.glb", AttachmentTestSource.Glb()), "alpha")
                with { Local = Matrix4x4.CreateScale(side) * Matrix4x4.CreateTranslation(origin) };
        }
        else
        {
            var glb = OutfitRig.GarmentFixture();
            glb.SetPositions(0, positions);
            part = new SkinnedAttachmentPartDefinition(id, rig.Source.Mesh("measured-liner.glb", glb.Build()))
            { Regions = [new("alpha", 1)] };
        }
        var definition = new AttachmentSetDefinition([part], AttachmentLayer.Garment, [], [], [new("alpha", Standoff)]);
        return (definition, rig.MeasureFootprint(body, definition, new CoverageClaimMesh(positions, new uint[] { 0, 1, 2 })
        {
            PartId = id,
        }));
    }
}
