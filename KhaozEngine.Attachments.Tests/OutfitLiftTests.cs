using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class OutfitLiftTests
{
    const uint LiftingPartId = 2;
    const float Clearance = 0.001f;
    const float Bearing = 0.002f;
    const int Front = 0;
    const int Back = 1;

    static readonly BodyRegionMap Body = new(
        AttachmentGlb.ComputeRigIdentity(new SkinnedAttachmentGlbFixture().Build(), "body.glb"),
        ["a", "b"], [[new RegionRun(Front, 1), new RegionRun(Back, 1)]]);

    [Fact]
    public void A_part_clears_the_layer_below_in_its_authored_direction()
    {
        LiftMaterialMesh material = Triangle(Vector3.Zero, direction: -Vector3.UnitX);
        OutfitLift lift = Resolve([Garment(new RegionStandoff("a", 0.008f)), Lifting(AttachmentLayer.Armour)],
            new LiftingPartMesh(1, LiftingPartId, [material]));

        Assert.Equal(0.008f, lift.HeightBelow(1, Front));
        Assert.True(lift.HasLift);
        ReadOnlyMemory<Vector3> positions = Assert.Single(Positions(lift, 1));
        Assert.Equal(3, positions.Length);
        for (int vertex = 0; vertex < positions.Length; vertex++)
            AssertPosition(material.Positions.Span[vertex] - new Vector3(0.007f, 0, 0), positions.Span[vertex]);
    }

    [Fact]
    public void Lift_uses_the_set_data_captured_when_coverage_was_resolved()
    {
        AttachmentSetDefinition[] worn = [Garment(new RegionStandoff("a", 0.008f)), Lifting(AttachmentLayer.Armour)];
        OutfitCoverage coverage = OutfitCoverage.Resolve(Body, worn);
        worn[0] = Garment(new RegionStandoff("a", 0.016f));
        worn[1] = Lifting(AttachmentLayer.Armour, clearance: 0.004f);

        OutfitLift lift = OutfitLift.Resolve(coverage, [new LiftingPartMesh(1, LiftingPartId, [Triangle(Vector3.Zero)])]);

        Assert.Equal(0.008f, lift.HeightBelow(1, Front));
        AssertPosition(new(0, 0, 0.007f), Assert.Single(Positions(lift, 1)).Span[0]);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(1f, false)]
    public void A_part_reports_lift_only_when_a_rest_position_changes(float restHeight, bool moves)
    {
        const float heightBelow = 0.125f;
        // The lift is 2^-27, which is representable at zero and rounds away when added to one.
        const float distance = 1f / (1 << 27);
        LiftMaterialMesh material = Triangle(new(0, 0, restHeight), bearing: MathF.BitDecrement(heightBelow));
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, standoff: [new("a", 0)], clearance: 0);

        OutfitLift lift = Resolve([Garment(new RegionStandoff("a", heightBelow)), armour],
            new LiftingPartMesh(1, LiftingPartId, [material]));

        Assert.Equal(heightBelow, lift.HeightBelow(1, Front));
        Assert.Equal(moves, lift.HasLift);
        Assert.Equal(moves ? distance : 0f, lift.EffectiveStandoff(1, Front));
        if (moves)
            Assert.Equal(new Vector3(0, 0, distance), Assert.Single(Positions(lift, 1)).Span[0]);
        else
            Assert.Null(lift.LiftedPositions(1, LiftingPartId));
    }

    [Fact]
    public void A_bridging_span_keeps_its_positions_when_its_bearing_already_clears_the_garment()
    {
        LiftMaterialMesh root = Triangle(Vector3.Zero);
        LiftMaterialMesh span = Triangle(new(2, 0, 0.014f), bearing: 0.014f);
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, runs: [new("a", 2)]);

        OutfitLift lift = Resolve([Garment(new RegionStandoff("a", 0.008f)), armour],
            new LiftingPartMesh(1, LiftingPartId, [root, span]));

        IReadOnlyList<ReadOnlyMemory<Vector3>> positions = Positions(lift, 1);
        Assert.Equal(2, positions.Count);
        AssertPosition(new(0, 0, 0.007f), positions[0].Span[0]);
        Assert.Equal(span.Positions.ToArray(), positions[1].ToArray());
    }

    [Fact]
    public void A_cross_section_with_a_shared_bearing_keeps_its_thickness()
    {
        LiftMaterialMesh inner = Triangle(new(0, 0, 0.002f));
        LiftMaterialMesh outer = Triangle(new(0, 0, 0.005f));
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, runs: [new("a", 2)]);

        OutfitLift lift = Resolve([Garment(new RegionStandoff("a", 0.008f)), armour],
            new LiftingPartMesh(1, LiftingPartId, [inner, outer]));

        IReadOnlyList<ReadOnlyMemory<Vector3>> positions = Positions(lift, 1);
        Assert.Equal(2, positions.Count);
        AssertPosition(new(0, 0, 0.009f), positions[0].Span[0]);
        AssertPosition(new(0, 0, 0.003f), positions[1].Span[0] - positions[0].Span[0]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("b", null)]
    [InlineData("a", 0f)]
    public void Only_a_declared_standoff_in_the_touched_region_enables_lift(string? lowerRegion, float? expectedHeight)
    {
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour);
        AttachmentSetDefinition[] worn = lowerRegion is null ? [armour] : [armour, Garment(new RegionStandoff(lowerRegion, 0))];
        OutfitLift lift = Resolve(worn, new LiftingPartMesh(0, LiftingPartId, [Triangle(Vector3.Zero, bearing: 0)]));

        Assert.Equal(expectedHeight, lift.HeightBelow(0, Front));
        Assert.Equal(expectedHeight.HasValue, lift.HasLift);
        if (expectedHeight.HasValue)
            AssertPosition(new(0, 0, Clearance), Assert.Single(Positions(lift, 0)).Span[0]);
        else
            Assert.Null(lift.LiftedPositions(0, LiftingPartId));
    }

    [Fact]
    public void A_lower_set_does_not_contribute_in_a_region_hidden_by_a_higher_layer()
    {
        AttachmentSetDefinition garment = Garment(new RegionStandoff("a", 0.008f));
        LiftingPartMesh mesh = new(1, LiftingPartId, [Triangle(Vector3.Zero)]);
        OutfitLift control = Resolve([garment, Lifting(AttachmentLayer.Armour)], mesh);
        OutfitLift lift = Resolve([garment, Lifting(AttachmentLayer.Armour, covers: ["a"])], mesh);

        Assert.Equal(0.008f, control.EffectiveStandoff(0, Front));
        Assert.True(control.HasLift);
        Assert.Null(lift.EffectiveStandoff(0, Front));
        Assert.Null(lift.HeightBelow(1, Front));
        Assert.False(lift.HasLift);
        Assert.Null(lift.LiftedPositions(1, LiftingPartId));
    }

    [Theory]
    [InlineData(true, 0.012f, 0.011f)]
    [InlineData(false, 0.005f, 0.004f)]
    public void Outerwear_clears_the_armour_at_its_effective_standoff(bool withGarment, float armourStandoff, float outerLift)
    {
        AttachmentSetDefinition outerwear = Lifting(AttachmentLayer.Outerwear);
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, standoff: [new("a", 0.005f)]);
        AttachmentSetDefinition[] worn = withGarment ? [outerwear, armour, Garment(new RegionStandoff("a", 0.008f))] : [outerwear, armour];
        LiftMaterialMesh material = Triangle(new(0, 0, Bearing));

        OutfitLift lift = Resolve(worn, new LiftingPartMesh(0, LiftingPartId, [material]), new LiftingPartMesh(1, LiftingPartId, [material]));

        Assert.Equal(armourStandoff, lift.EffectiveStandoff(1, Front)!.Value, precision: 6);
        Assert.Equal(armourStandoff, lift.HeightBelow(0, Front)!.Value, precision: 6);
        AssertPosition(new(0, 0, Bearing + outerLift), Assert.Single(Positions(lift, 0)).Span[0]);
        if (withGarment)
            AssertPosition(new(0, 0, 0.009f), Assert.Single(Positions(lift, 1)).Span[0]);
        else
            Assert.Null(lift.LiftedPositions(1, LiftingPartId));
    }

    [Fact]
    public void Sets_on_one_layer_do_not_lift_each_other_and_leave_the_largest_height_for_the_next_layer()
    {
        AttachmentSetDefinition high = Lifting(AttachmentLayer.Armour, standoff: [new("a", 0.008f)]);
        AttachmentSetDefinition low = Lifting(AttachmentLayer.Armour, standoff: [new("a", 0.005f)]);
        LiftMaterialMesh material = Triangle(Vector3.Zero);

        OutfitLift lift = Resolve([high, low, Lifting(AttachmentLayer.Outerwear)],
            new LiftingPartMesh(2, LiftingPartId, [material]), new LiftingPartMesh(0, LiftingPartId, [material]),
            new LiftingPartMesh(1, LiftingPartId, [material]));

        Assert.Null(lift.HeightBelow(0, Front));
        Assert.Null(lift.HeightBelow(1, Front));
        Assert.Null(lift.LiftedPositions(0, LiftingPartId));
        Assert.Null(lift.LiftedPositions(1, LiftingPartId));
        Assert.Equal(0.008f, lift.HeightBelow(2, Front));
        AssertPosition(new(0, 0, 0.007f), Assert.Single(Positions(lift, 2)).Span[0]);
    }

    [Theory]
    [InlineData("same", null, 0.007f, 0.012f)]
    [InlineData("negative zero", null, 0.007f, 0.012f)]
    [InlineData("apart", null, 0.007f, 0.005f)]
    [InlineData("same", 0.012f, 0.011f, 0.016f)]
    public void A_hem_ramps_one_triangle_across_material_parts_at_coincident_positions(
        string seamKind, float? backHeight, float seamLift, float backStandoff)
    {
        LiftMaterialMesh inside = Triangle(Vector3.Zero);
        // Negative zero is the same point as zero and must weld with it, or the seam would crack.
        bool coincident = seamKind != "apart";
        Vector3 seam = seamKind switch
        {
            "negative zero" => new(BitConverter.Int32BitsToSingle(int.MinValue), 0, 0),
            "apart" => new(1e-6f, 0, 0),
            _ => Vector3.Zero,
        };
        // The two baked bearings at the seam can differ within the GLB policy's tolerance.
        LiftMaterialMesh outside = new(new Vector3[] { seam, new(1, 1, 0), new(2, 0, 0), new(2, 1, 0) },
            new uint[] { 0, 1, 2, 1, 3, 2 }, new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            new[] { 0.00205f, 0.014f, 0.014f, 0.014f });
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour, runs: [new("a", 1), new("b", 2)],
            standoff: [new("b", 0.005f)]);
        RegionStandoff[] lowerStandoff = backHeight is { } height ? [new("a", 0.008f), new("b", height)] : [new("a", 0.008f)];

        OutfitLift lift = Resolve([Garment(lowerStandoff), armour],
            new LiftingPartMesh(1, LiftingPartId, [inside, outside]));

        IReadOnlyList<ReadOnlyMemory<Vector3>> positions = Positions(lift, 1);
        Assert.Equal(2, positions.Count);
        Assert.Equal(backHeight, lift.HeightBelow(1, Back));
        AssertPosition(new(0, 0, seamLift), positions[0].Span[0]);
        AssertPosition(new(1, 0, 0.007f), positions[0].Span[1]);
        Assert.Equal(coincident ? positions[0].Span[0] : seam, positions[1].Span[0]);
        Assert.Equal(outside.Positions[1..].ToArray(), positions[1][1..].ToArray());
        Assert.Equal(backStandoff, lift.EffectiveStandoff(1, Back)!.Value, precision: 6);
    }

    [Fact]
    public void A_set_without_a_layer_neither_lifts_nor_contributes()
    {
        AttachmentSetDefinition plain = new([AttachmentFixtures.TaggedSkinned() with { Regions = [] }]);
        AttachmentSetDefinition garment = Garment(new RegionStandoff("a", 0.008f));
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour);

        OutfitLift lift = Resolve([garment, plain, armour], new LiftingPartMesh(2, LiftingPartId, [Triangle(Vector3.Zero)]));

        Assert.Null(lift.HeightBelow(1, Front));
        Assert.Null(lift.EffectiveStandoff(1, Front));
        Assert.Null(lift.LiftedPositions(1, LiftingPartId));
        Assert.Null(lift.LiftedPositions(0, 1));
        Assert.Equal(0.008f, lift.HeightBelow(2, Front));
        AssertPosition(new(0, 0, 0.007f), Assert.Single(Positions(lift, 2)).Span[0]);
    }

    [Fact]
    public void An_unknown_standoff_region_refuses_with_the_worn_index_and_region_name()
    {
        AttachmentSetDefinition armour = Lifting(AttachmentLayer.Armour);
        var mesh = new LiftingPartMesh(0, LiftingPartId, [Triangle(Vector3.Zero)]);
        OutfitLift control = Resolve([armour, Garment(new RegionStandoff("b", 0.008f))], mesh);

        var error = Assert.Throws<OutfitDataException>(() => OutfitCoverage.Resolve(Body, [armour, Garment(new RegionStandoff("unknown", 0.008f))]));
        Assert.Equal(OutfitRefusal.RegionUnknown, error.Reason);
        Assert.Equal(1, error.WornIndex);
        Assert.Equal("unknown", error.RegionName);
        Assert.Null(error.PartId);
        Assert.Null(error.RunIndex);
        Assert.Equal(0.008f, control.EffectiveStandoff(1, Back));
    }

    [Theory]
    [InlineData("missing part")]
    [InlineData("extra part")]
    [InlineData("duplicate part")]
    [InlineData("directions count")]
    [InlineData("bearings count")]
    [InlineData("partial triangle")]
    [InlineData("index out of range")]
    [InlineData("fewer triangles than runs")]
    [InlineData("more triangles than runs")]
    public void Malformed_lifting_mesh_shapes_refuse_with_an_argument_exception(string fault)
    {
        AttachmentSetDefinition[] worn = [Lifting(AttachmentLayer.Armour, runs: [new("a", 2)])];
        LiftMaterialMesh material = Triangle(Vector3.Zero) with { Indices = new uint[] { 0, 1, 2, 0, 2, 1 } };
        var mesh = new LiftingPartMesh(0, LiftingPartId, [material]);
        Assert.False(Resolve(worn, mesh).HasLift);

        LiftingPartMesh[] malformed = fault switch
        {
            "missing part" => [],
            "extra part" => [mesh, mesh with { PartId = 99 }],
            "duplicate part" => [mesh, mesh],
            "directions count" => [mesh with { Materials = [material with { Directions = default }] }],
            "bearings count" => [mesh with { Materials = [material with { Bearings = default }] }],
            "partial triangle" => [mesh with { Materials = [material with { Indices = material.Indices[..^1] }] }],
            "index out of range" => [mesh with { Materials = [material with { Indices = new uint[] { 0, 1, 2, 0, 2, 3 } }] }],
            "fewer triangles than runs" => [mesh with { Materials = [Triangle(Vector3.Zero)] }],
            "more triangles than runs" => [mesh with { Materials = [material, Triangle(Vector3.Zero)] }],
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        Assert.Throws<ArgumentException>(() => Resolve(worn, malformed));
    }

    static OutfitLift Resolve(AttachmentSetDefinition[] worn, params LiftingPartMesh[] meshes) =>
        OutfitLift.Resolve(OutfitCoverage.Resolve(Body, worn), meshes);

    static AttachmentSetDefinition Garment(params RegionStandoff[] standoff) =>
        new([AttachmentFixtures.TaggedRigid()], AttachmentLayer.Garment, [], [], standoff);

    static AttachmentSetDefinition Lifting(AttachmentLayer layer, NamedRegionRun[]? runs = null,
        string[]? covers = null, RegionStandoff[]? standoff = null, float clearance = Clearance) =>
        new([AttachmentFixtures.TaggedSkinned(runs ?? [new("a", 1)]) with { Lift = new(clearance) }],
            layer, covers ?? [], [], standoff ?? []);

    static LiftMaterialMesh Triangle(Vector3 origin, float bearing = Bearing, Vector3? direction = null)
    {
        Vector3 normal = direction ?? Vector3.UnitZ;
        return new(new[] { origin, origin + Vector3.UnitX, origin + Vector3.UnitY }, new uint[] { 0, 1, 2 },
            new[] { normal, normal, normal }, new[] { bearing, bearing, bearing });
    }

    static IReadOnlyList<ReadOnlyMemory<Vector3>> Positions(OutfitLift lift, int wornIndex)
    {
        IReadOnlyList<ReadOnlyMemory<Vector3>>? positions = lift.LiftedPositions(wornIndex, LiftingPartId);
        Assert.NotNull(positions);
        return positions;
    }

    static void AssertPosition(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.000001f);
}
