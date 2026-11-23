using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class OutfitLiftFootprintTests
{
    const uint SkinnedId = 2;
    const float Clearance = 0.001f;
    const string Rig = "rig-v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    static readonly BodyRegionMap Body = new(Rig, ["a", "b"], [[new(0, 1)], [new(1, 1)]]);
    static readonly int PanelCell = new BodyCell(0, 0).Key;
    static readonly int HemCell = new BodyCell(0, 1).Key;
    static readonly int BackCell = new BodyCell(1, 0).Key;

    [Fact]
    public void A_strap_clears_the_hem_only_over_its_cells()
    {
        // #1891: a hem must not push the strap off the panel in the same body triangle.
        // Review T4 finding 4: every corner of the spanning triangle must clear the last cell as well.
        const float clearance = 0.015625f;
        const float hemHeight = 0.0625f;
        const float panelHeight = 0.03125f;
        AttachmentSetDefinition armour = new([AttachmentFixtures.TaggedSkinned(new NamedRegionRun("a", 2))
            with { Lift = new(clearance) }], AttachmentLayer.Armour, []);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body,
            [Garment(new NamedRegionRun("a", 2)), armour]);
        LiftFootprint shirt = Footprint(SkinnedId, Fixed(HemCell, hemHeight), Fixed(PanelCell, panelHeight));
        LiftFootprint strap = Footprint(SkinnedId, new(false, [PanelCell, HemCell], [0.046875, 0.046875]),
            Fixed(PanelCell, 0.0078125));
        LiftMaterialMesh spanning = Triangle(new(0, 0, 0.015625f), 0.015625f) with
        {
            Positions = new Vector3[] { new(0, 0, 0.015625f), new(1, 0, 0.03125f), new(0, 1, 0.046875f) },
            Bearings = new[] { 0.015625f, 0.03125f, 0.046875f },
        };
        LiftMaterialMesh panel = Triangle(new(2, 0, 0.0078125f), 0.0078125f);
        OutfitLift lift = Resolve(coverage, [new(1, SkinnedId, [spanning, panel])], [shirt, strap]);

        Assert.True(lift.FootprintApplied(0));
        Assert.True(lift.FootprintApplied(1));
        AssertHeight(hemHeight + clearance, RestHeight(lift, 1, 0, 0));
        AssertHeight(hemHeight + clearance, RestHeight(lift, 1, 0, 1));
        AssertHeight(hemHeight + clearance, RestHeight(lift, 1, 0, 2));
        AssertHeight(panelHeight + clearance, RestHeight(lift, 1, 1));
    }

    [Theory]
    [InlineData(0.6f, 0.8f, true, 0.078125f, 0.078125f)]
    [InlineData(0f, 1f, true, 0.0625f, 0.078125f)]
    [InlineData(0.96f, 0.28f, true, 0.125f, 0.050625f)]
    [InlineData(0.6f, 0.8f, false, 0.0625f, 0.065625f)]
    public void A_measured_section_lifts_in_the_body_face_height_frame(float x, float z, bool hasNormals,
        float expectedDistance, float expectedHeight)
    {
        // Review T4 finding 2 (#1891): an oblique section used to rise by less than the clearance required in the cell.
        const float restHeight = 0.015625f;
        AttachmentSetDefinition armour = new([AttachmentFixtures.TaggedSkinned(new NamedRegionRun("a", 1))
            with { Lift = new(restHeight) }], AttachmentLayer.Armour, []);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [Garment(new NamedRegionRun("a", 1)), armour]);
        LiftFootprint shirt = Footprint(SkinnedId, Fixed(HemCell, 0.0625));
        LiftFootprintPart strapPart = new(SkinnedId, [Fixed(HemCell, restHeight)]);
        LiftFootprint strap = hasNormals
            ? new(Body.Sha256, 2, [strapPart], [new(0, Vector3.UnitZ)])
            : new(Body.Sha256, 2, [strapPart]);
        Vector3 direction = new(x, 0, z);
        Vector3 origin = new(0, 0, restHeight);
        LiftMaterialMesh mesh = Triangle(origin, restHeight) with { Directions = new[] { direction, direction, direction } };

        OutfitLift lift = Resolve(coverage, [new(1, SkinnedId, [mesh])], [shirt, strap]);

        IReadOnlyList<ReadOnlyMemory<Vector3>>? positions = lift.LiftedPositions(1, SkinnedId);
        Assert.NotNull(positions);
        Vector3 lifted = Assert.Single(positions).Span[0];
        AssertHeight(expectedDistance, Vector3.Distance(origin, lifted));
        AssertHeight(expectedHeight, lifted.Z);
        AssertHeight((float)strapPart.Triangles[0].HeightMetres(0) + expectedDistance, lift.EffectiveStandoff(1, 0)!.Value);
    }

    [Fact]
    public void The_largest_face_frame_requirement_lifts_every_weld_in_a_section()
    {
        const float restHeight = 0.015625f;
        AttachmentSetDefinition armour = new([AttachmentFixtures.TaggedSkinned(new NamedRegionRun("a", 2))
            with { Lift = new(restHeight) }], AttachmentLayer.Armour, []);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [Garment(new NamedRegionRun("a", 2)), armour]);
        LiftFootprint shirt = Footprint(SkinnedId, Fixed(HemCell, 0.09375), Fixed(BackCell, 0.0625));
        LiftFootprint strap = new(Body.Sha256, 2,
            [new(SkinnedId, [Fixed(HemCell, restHeight), Fixed(BackCell, restHeight)])],
            [new(0, Vector3.UnitZ), new(1, Vector3.UnitX)]);
        LiftMaterialMesh front = Triangle(new(0, 0, restHeight), restHeight);
        LiftMaterialMesh back = Triangle(new(2, 0, restHeight), restHeight);

        OutfitLift lift = Resolve(coverage, [new(1, SkinnedId, [front, back])], [shirt, strap]);

        // The lower back cell needs twice its height difference. The two triangles share one section basis.
        AssertHeight(0.140625f, RestHeight(lift, 1, 0));
        AssertHeight(0.140625f, RestHeight(lift, 1, 1));
    }

    [Fact]
    public void Outerwear_clears_the_lifted_strap_over_its_cells_and_the_panel_beside_it()
    {
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body,
            [Lifting(AttachmentLayer.Outerwear, new NamedRegionRun("a", 2)),
                Lifting(AttachmentLayer.Armour, new NamedRegionRun("a", 1)), Garment(new NamedRegionRun("a", 2))]);
        LiftFootprint cloak = Footprint(SkinnedId, Fixed(HemCell, 0.005), Fixed(PanelCell, 0.006));
        LiftFootprint strap = Footprint(SkinnedId, Fixed(HemCell, 0.005));
        LiftFootprint shirt = Footprint(SkinnedId, Fixed(HemCell, 0.012), Fixed(PanelCell, 0.008));
        LiftMaterialMesh strapMesh = Triangle(new(0, 0, 0.002f), 0.002f) with
        {
            Bearings = new[] { 0.014f, 0.002f, 0.002f },
        };

        OutfitLift lift = Resolve(coverage, [TwoSections(0), new(1, SkinnedId, [strapMesh])], [cloak, strap, shirt]);

        AssertHeight(0.016f, lift.EffectiveStandoff(1, 0)!.Value);
        AssertHeight(0.016f, lift.HeightBelow(0, 0)!.Value);
        AssertHeight(0.017f, RestHeight(lift, 0, 0));
        AssertHeight(0.009f, RestHeight(lift, 0, 1));
    }

    [Fact]
    public void Measured_sets_on_the_same_layer_do_not_lift_each_other()
    {
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, new NamedRegionRun("a", 1));
        AttachmentSetDefinition cloak = Lifting(AttachmentLayer.Outerwear, new NamedRegionRun("a", 1));
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [armour, armour, cloak]);
        LiftFootprint higher = Footprint(SkinnedId, Fixed(HemCell, 0.012));
        LiftFootprint lower = Footprint(SkinnedId, Fixed(HemCell, 0.008));
        LiftMaterialMesh mesh = Triangle(new(0, 0, 0.002f), 0.002f);

        OutfitLift lift = Resolve(coverage, [new(0, SkinnedId, [mesh]), new(1, SkinnedId, [mesh]), new(2, SkinnedId, [mesh])],
            [higher, lower, lower]);

        Assert.Null(lift.LiftedPositions(0, SkinnedId));
        Assert.Null(lift.LiftedPositions(1, SkinnedId));
        AssertHeight(0.013f, RestHeight(lift, 2, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rigid_plate_only_pushes_outerwear_over_the_cells_where_it_is_drawn(bool hidePlate)
    {
        // https://github.com/APKiwiOrg/Grimhollow/issues/847: a partial plate must not raise the rest of the chest.
        AttachmentSetDefinition plate = new([AttachmentFixtures.TaggedRigid("b")], AttachmentLayer.Armour, []);
        AttachmentSetDefinition cloak = new([AttachmentFixtures.TaggedSkinned(new NamedRegionRun("a", 2))
            with { Lift = new(Clearance) }], AttachmentLayer.Outerwear, hidePlate ? ["b"] : []);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [plate, cloak]);
        LiftFootprint plateFootprint = Footprint(1, Fixed(HemCell, 0.04));
        LiftFootprint cloakFootprint = Footprint(SkinnedId, Fixed(HemCell, 0.005), Fixed(PanelCell, 0.006));

        OutfitLift lift = Resolve(coverage, [TwoSections(1)], [plateFootprint, cloakFootprint]);

        if (hidePlate)
        {
            Assert.Null(lift.LiftedPositions(1, SkinnedId));
            Assert.Null(lift.EffectiveStandoff(0, 0));
        }
        else
        {
            AssertHeight(0.041f, RestHeight(lift, 1, 0));
            AssertHeight(0.003f, RestHeight(lift, 1, 1));
        }
    }

    [Fact]
    public void Only_drawn_triangles_contribute_over_a_shared_cell()
    {
        AttachmentSetDefinition armour = new(
            [AttachmentFixtures.TaggedSkinned(new NamedRegionRun("b", 1)) with { Lift = new(Clearance) }],
            AttachmentLayer.Armour, ["b"]);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [Garment(new("b", 1), new("a", 1)), armour]);
        LiftFootprint shirt = Footprint(SkinnedId, Fixed(BackCell, 0.021), Fixed(BackCell, 0.008));
        var mesh = new LiftingPartMesh(1, SkinnedId, [Triangle(new(0, 0, 0.002f), 0.002f)]);

        // The armour has no footprint, so it reads the maximum over the cells of its tagged region.
        OutfitLift lift = Resolve(coverage, [mesh], [shirt, null]);

        Assert.Null(lift.EffectiveStandoff(0, 0));
        AssertHeight(0.008f, lift.EffectiveStandoff(0, 1)!.Value);
        AssertHeight(0.008f, lift.HeightBelow(1, 1)!.Value);
        AssertHeight(0.009f, RestHeight(lift, 1, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_footprint_for_another_body_falls_back_to_regional_standoff(bool wrongHash)
    {
        AttachmentSetDefinition garment = new([AttachmentFixtures.TaggedRigid()], AttachmentLayer.Garment,
            [], [], [new("a", 0.008f)]);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body,
            [garment, Lifting(AttachmentLayer.Armour, new NamedRegionRun("a", 1))]);
        LiftFootprintPart part = new(1, [Fixed(HemCell, 0.012)]);
        var otherFootprint = new LiftFootprint(wrongHash ? new string('0', 64) : Body.Sha256, wrongHash ? 2 : 3, [part]);
        LiftingPartMesh[] meshes = [new(1, SkinnedId, [Triangle(new(0, 0, 0.002f), 0.002f)])];
        LiftFootprint strap = Footprint(SkinnedId, Fixed(HemCell, 0.005));

        OutfitLift control = Resolve(coverage, meshes, [new(Body.Sha256, 2, [part]), strap]);
        OutfitLift lift = Resolve(coverage, meshes, [otherFootprint, strap]);

        Assert.True(control.FootprintApplied(0));
        AssertHeight(0.013f, RestHeight(control, 1, 0));
        Assert.False(lift.FootprintApplied(0));
        Assert.True(lift.FootprintApplied(1));
        AssertHeight(0.009f, RestHeight(lift, 1, 0));
    }

    [Fact]
    public void A_section_with_different_region_tags_moves_whole_and_keeps_its_thickness()
    {
        // #1887: the two sides of the section must not separate when only one touches the higher region.
        AttachmentSetDefinition garment = new([AttachmentFixtures.TaggedRigid()], AttachmentLayer.Garment,
            [], [], [new("a", 0.012f), new("b", 0.008f)]);
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, new("a", 1), new("b", 1));
        LiftMaterialMesh inner = Triangle(new(0, 0, 0.002f), 0.002f);
        LiftMaterialMesh outer = Triangle(new(0, 0, 0.005f), 0.002f) with
        {
            Directions = new Vector3[] { new(-0f, 0, 1), new(-0f, 0, 1), new(-0f, 0, 1) },
        };

        OutfitLift lift = OutfitLift.Resolve(OutfitCoverage.Resolve(Body, [garment, armour]),
            [new(1, SkinnedId, [inner, outer])]);

        AssertHeight(0.013f, RestHeight(lift, 1, 0));
        AssertHeight(0.003f, RestHeight(lift, 1, 1) - RestHeight(lift, 1, 0));
    }

    [Fact]
    public void A_swinging_triangle_lifts_but_holds_no_height()
    {
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body,
            [Garment(new NamedRegionRun("a", 1)), Lifting(AttachmentLayer.Armour, new NamedRegionRun("a", 1)),
                Lifting(AttachmentLayer.Outerwear, new NamedRegionRun("a", 1))]);
        LiftFootprint shirt = Footprint(SkinnedId, Fixed(HemCell, 0.008));
        LiftFootprint swinging = Footprint(SkinnedId, new LiftFootprintTriangle(true, [HemCell], []));
        LiftFootprint cloak = Footprint(SkinnedId, Fixed(HemCell, 0.005));
        LiftMaterialMesh mesh = Triangle(new(0, 0, 0.002f), 0.002f);

        OutfitLift lift = Resolve(coverage, [new(1, SkinnedId, [mesh]), new(2, SkinnedId, [mesh])], [shirt, swinging, cloak]);

        AssertHeight(0.009f, RestHeight(lift, 1, 0));
        Assert.Null(lift.EffectiveStandoff(1, 0));
        AssertHeight(0.009f, RestHeight(lift, 2, 0));
    }

    [Fact]
    public void A_set_without_a_layer_ignores_even_a_footprint_for_this_body()
    {
        AttachmentSetDefinition plain = new([AttachmentFixtures.TaggedRigid() with { Region = null }]);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [plain]);

        OutfitLift lift = Resolve(coverage, [], [Footprint(1, Fixed(HemCell, 0.012))]);

        Assert.False(lift.FootprintApplied(0));
        Assert.Null(lift.EffectiveStandoff(0, 0));
    }

    [Theory]
    [InlineData("list count")]
    [InlineData("missing rigid part")]
    [InlineData("extra part")]
    [InlineData("different skinned part")]
    [InlineData("skinned triangle count")]
    public void A_matching_footprint_must_match_the_worn_set_shape(string fault)
    {
        AttachmentSetDefinition garment = new(
            [AttachmentFixtures.TaggedRigid(), AttachmentFixtures.TaggedSkinned(new NamedRegionRun("a", 1))],
            AttachmentLayer.Garment, []);
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, [garment]);
        LiftFootprintPart rigid = new(1, [Fixed(HemCell, 0.008)]);
        LiftFootprintPart skinned = new(SkinnedId, [Fixed(PanelCell, 0.008)]);
        Assert.True(Resolve(coverage, [], [new(Body.Sha256, 2, [rigid, skinned])]).FootprintApplied(0));

        LiftFootprint?[] malformed = fault switch
        {
            "list count" => [],
            "missing rigid part" => [new(Body.Sha256, 2, [skinned, new(3, rigid.Triangles)])],
            "extra part" => [new(Body.Sha256, 2, [rigid, skinned, new(3, skinned.Triangles)])],
            "different skinned part" => [new(Body.Sha256, 2, [rigid, new(3, skinned.Triangles)])],
            "skinned triangle count" => [new(Body.Sha256, 2, [rigid, new(SkinnedId, [Fixed(PanelCell, 0.008), default])])],
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        Assert.Throws<ArgumentException>("footprints", () => Resolve(coverage, [], malformed));
    }

    static OutfitLift Resolve(OutfitCoverage coverage, LiftingPartMesh[] meshes, LiftFootprint?[] footprints) =>
        OutfitLift.Resolve(coverage, meshes, footprints);

    static LiftFootprint Footprint(uint partId, params LiftFootprintTriangle[] triangles) =>
        new(Body.Sha256, 2, [new(partId, triangles)]);

    static LiftFootprintTriangle Fixed(int cell, double height) => new(false, [cell], [height]);

    static AttachmentSetDefinition Garment(params NamedRegionRun[] runs) =>
        new([AttachmentFixtures.TaggedSkinned(runs)], AttachmentLayer.Garment, []);

    static LiftingPartMesh TwoSections(int wornIndex) =>
        new(wornIndex, SkinnedId, [Triangle(new(0, 0, 0.002f), 0.002f), Triangle(new(2, 0, 0.003f), 0.003f)]);

    static AttachmentSetDefinition Lifting(AttachmentLayer layer, params NamedRegionRun[] runs) =>
        new([AttachmentFixtures.TaggedSkinned(runs) with { Lift = new(Clearance) }], layer, []);

    static LiftMaterialMesh Triangle(Vector3 origin, float bearing) =>
        new(new[] { origin, origin + Vector3.UnitX, origin + Vector3.UnitY }, new uint[] { 0, 1, 2 },
            new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ }, new[] { bearing, bearing, bearing });

    static float RestHeight(OutfitLift lift, int wornIndex, int materialIndex, int vertexIndex = 0)
    {
        IReadOnlyList<ReadOnlyMemory<Vector3>>? positions = lift.LiftedPositions(wornIndex, SkinnedId);
        Assert.NotNull(positions);
        return positions[materialIndex].Span[vertexIndex].Z;
    }

    static void AssertHeight(float expected, float actual) => Assert.Equal(expected, actual, precision: 6);
}
