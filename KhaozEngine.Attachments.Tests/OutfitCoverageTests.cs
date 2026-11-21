using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class OutfitCoverageTests
{
    const string Rig = "rig-v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Five regions, one triangle each, so region indices are a=0 to e=4.
    static readonly BodyRegionMap Body = new(Rig, ["a", "b", "c", "d", "e"],
        [[new RegionRun(0, 1), new RegionRun(1, 1), new RegionRun(2, 1), new RegionRun(3, 1), new RegionRun(4, 1)]]);

    [Fact]
    public void Skin_hides_under_any_layered_piece()
    {
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body, [Piece(AttachmentLayer.Garment, "a"), Piece(AttachmentLayer.Armour, "b")]);

        Assert.Equal(Regions("a", "b"), outfit.BodyHidden);
        Assert.Equal(2, outfit.WornCount);
    }

    [Fact]
    public void A_piece_hides_only_under_a_higher_layer()
    {
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body,
            [Piece(AttachmentLayer.Garment, "a", "b"), Piece(AttachmentLayer.Armour, "b", "c"), Piece(AttachmentLayer.Outerwear, "c")]);

        Assert.Equal(Regions("b", "c"), outfit.HiddenFor(0));
        Assert.Equal(Regions("c"), outfit.HiddenFor(1));
        Assert.True(outfit.HiddenFor(2).IsEmpty);
        Assert.Equal(Regions("a", "b", "c"), outfit.BodyHidden);
    }

    [Fact]
    public void Two_pieces_on_one_layer_do_not_hide_each_other()
    {
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body,
            [Piece(AttachmentLayer.Garment, "a", "b"), Piece(AttachmentLayer.Garment, "b", "c"), Piece(AttachmentLayer.Armour, "d")]);

        Assert.Equal(Regions("d"), outfit.HiddenFor(0));
        Assert.Equal(Regions("d"), outfit.HiddenFor(1));
        Assert.True(outfit.HiddenFor(2).IsEmpty);
    }

    [Fact]
    public void A_body_look_piece_hides_under_every_layer_that_covers_it()
    {
        AttachmentSetDefinition look = new([Rigid(1, "a")], AttachmentLayer.Body, ["a"]);
        OutfitCoverage bare = OutfitCoverage.Resolve(Body, [look]);
        OutfitCoverage dressed = OutfitCoverage.Resolve(Body, [look, Piece(AttachmentLayer.Garment, "a")]);
        OutfitCoverage cloaked = OutfitCoverage.Resolve(Body, [look, Piece(AttachmentLayer.Outerwear, "a")]);

        Assert.Equal(Regions("a"), bare.BodyHidden);
        Assert.False(bare.RigidPartHidden(0, 1));
        Assert.Equal(Regions("a"), dressed.HiddenFor(0));
        Assert.True(dressed.RigidPartHidden(0, 1));
        Assert.True(cloaked.RigidPartHidden(0, 1));
    }

    [Fact]
    public void A_rigid_part_hides_whole_when_a_higher_layer_covers_its_region()
    {
        AttachmentSetDefinition garment = new([Rigid(1, "d"), Rigid(2, "e")], AttachmentLayer.Garment, ["b"]);
        OutfitCoverage elsewhere = OutfitCoverage.Resolve(Body, [garment, Piece(AttachmentLayer.Outerwear, "a")]);
        OutfitCoverage over = OutfitCoverage.Resolve(Body, [garment, Piece(AttachmentLayer.Outerwear, "d")]);

        Assert.False(elsewhere.RigidPartHidden(0, 1));
        Assert.True(over.RigidPartHidden(0, 1));
        Assert.False(over.RigidPartHidden(0, 2));
        Assert.False(over.RigidPartHidden(1, 1));
    }

    [Fact]
    public void A_skinned_part_reports_its_runs_by_body_region()
    {
        var skinned = new SkinnedAttachmentPartDefinition(3, new("parts/skinned.glb", Hash))
        { Regions = [new("c", 2), new("a", 1), new("c", 4)] };
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body,
            [new([skinned], AttachmentLayer.Garment, ["a", "c"]), Piece(AttachmentLayer.Armour, "c")]);

        Assert.Equal(new[] { new RegionRun(2, 2), new RegionRun(0, 1), new RegionRun(2, 4) }, outfit.SkinnedPartRegions(0, 3));
        Assert.True(outfit.HiddenFor(0).Contains(outfit.SkinnedPartRegions(0, 3)[0].Region));
        Assert.False(outfit.HiddenFor(0).Contains(outfit.SkinnedPartRegions(0, 3)[1].Region));
        Assert.Throws<ArgumentException>(() => outfit.SkinnedPartRegions(0, 9));
    }

    [Fact]
    public void A_set_without_a_layer_neither_hides_nor_is_hidden()
    {
        var untagged = new SkinnedAttachmentPartDefinition(4, new("parts/skinned.glb", Hash));
        AttachmentSetDefinition plain = new([Rigid(1, null), untagged]);
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body,
            [Piece(AttachmentLayer.Garment, "a"), plain, Piece(AttachmentLayer.Outerwear, "a", "b")]);

        Assert.Equal(Regions("a", "b"), outfit.BodyHidden);
        Assert.Equal(Regions("a", "b"), outfit.HiddenFor(0));
        Assert.True(outfit.HiddenFor(1).IsEmpty);
        Assert.False(outfit.RigidPartHidden(1, 1));
        Assert.Empty(outfit.SkinnedPartRegions(1, 4));
        Assert.True(outfit.HiddenFor(2).IsEmpty);
    }

    [Fact]
    public void One_set_worn_twice_resolves_each_entry_on_its_own()
    {
        AttachmentSetDefinition shirt = new([Rigid(1, "a"), Skinned(2, "a", "b")], AttachmentLayer.Garment, ["a", "b"]);
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body, [shirt, Piece(AttachmentLayer.Armour, "b"), shirt]);

        Assert.Equal(3, outfit.WornCount);
        Assert.Equal(Regions("b"), outfit.HiddenFor(0));
        Assert.Equal(Regions("b"), outfit.HiddenFor(2));
        Assert.True(outfit.HiddenFor(1).IsEmpty);
        Assert.False(outfit.RigidPartHidden(0, 1));
        Assert.False(outfit.RigidPartHidden(2, 1));
        Assert.Equal(outfit.SkinnedPartRegions(0, 2), outfit.SkinnedPartRegions(2, 2));
        Assert.Equal(Regions("a", "b"), outfit.BodyHidden);
    }

    [Fact]
    public void Regions_past_index_63_hide_through_the_high_word()
    {
        var names = new string[70];
        var runs = new RegionRun[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            names[index] = $"r{index}";
            runs[index] = new RegionRun(index, 1);
        }
        var body = new BodyRegionMap(Rig, names, [runs]);
        var low = new RigidAttachmentPartDefinition(1, new("parts/rigid.glb", Hash), "root", Matrix4x4.Identity) { Region = "r2" };
        var high = new RigidAttachmentPartDefinition(2, new("parts/rigid.glb", Hash), "root", Matrix4x4.Identity) { Region = "r66" };
        var skinned = new SkinnedAttachmentPartDefinition(3, new("parts/skinned.glb", Hash)) { Regions = [new("r2", 1), new("r66", 1)] };
        AttachmentSetDefinition shirt = new([low, high, skinned], AttachmentLayer.Garment, ["r1", "r66"]);
        AttachmentSetDefinition plate = new([high with { Id = 1 }], AttachmentLayer.Armour, ["r66", "r68"]);

        OutfitCoverage outfit = OutfitCoverage.Resolve(body, [shirt, plate]);

        // Region 66 sits in the high word at bit 2, so a high word read as the low word would hide region 2 instead.
        Assert.Equal(RegionSet.Empty.With(66).With(68), outfit.HiddenFor(0));
        Assert.Equal(RegionSet.Empty.With(1).With(66).With(68), outfit.BodyHidden);
        Assert.False(outfit.RigidPartHidden(0, 1));
        Assert.True(outfit.RigidPartHidden(0, 2));
        Assert.Equal(new[] { new RegionRun(2, 1), new RegionRun(66, 1) }, outfit.SkinnedPartRegions(0, 3));
    }

    [Theory]
    [InlineData("a covered region", null, null)]
    [InlineData("a rigid part's region", 1u, null)]
    [InlineData("a skinned part's second run", 2u, 1)]
    public void An_unknown_region_is_refused_naming_the_piece_and_the_region(string where, uint? partId, int? runIndex)
    {
        AttachmentSetDefinition WithRegion(string name) => where switch
        {
            "a covered region" => new([Rigid(1, "a"), Skinned(2, "a", "b")], AttachmentLayer.Garment, ["a", name]),
            "a rigid part's region" => new([Rigid(1, name), Skinned(2, "a", "b")], AttachmentLayer.Garment, ["a"]),
            _ => new([Rigid(1, "a"), Skinned(2, "a", name)], AttachmentLayer.Garment, ["a"]),
        };

        OutfitCoverage control = OutfitCoverage.Resolve(Body, [Piece(AttachmentLayer.Armour, "a"), WithRegion("e")]);
        Assert.Equal(Regions("a"), control.HiddenFor(1));

        var error = Assert.Throws<OutfitDataException>(() => OutfitCoverage.Resolve(Body, [Piece(AttachmentLayer.Armour, "a"), WithRegion("zeta")]));
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(OutfitRefusal.RegionUnknown, error.Reason);
        Assert.Equal(1, error.WornIndex);
        Assert.Equal(partId, error.PartId);
        Assert.Equal(runIndex, error.RunIndex);
        Assert.Equal("zeta", error.RegionName);
    }

    [Fact]
    public void An_empty_outfit_hides_nothing()
    {
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body, []);

        Assert.True(outfit.BodyHidden.IsEmpty);
        Assert.Equal(0, outfit.WornCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => outfit.HiddenFor(0));
    }

    [Fact]
    public void Resolving_keeps_no_reference_to_the_worn_list()
    {
        var worn = new List<AttachmentSetDefinition> { Piece(AttachmentLayer.Garment, "a"), Piece(AttachmentLayer.Armour, "b") };
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body, worn);

        worn[1] = Piece(AttachmentLayer.Garment, "e");
        worn.Add(Piece(AttachmentLayer.Outerwear, "a"));

        Assert.Equal(2, outfit.WornCount);
        Assert.Equal(Regions("a", "b"), outfit.BodyHidden);
        Assert.Equal(Regions("b"), outfit.HiddenFor(0));
        Assert.True(outfit.HiddenFor(1).IsEmpty);
    }

    [Fact]
    public void Queries_refuse_a_worn_index_or_part_the_outfit_lacks()
    {
        AttachmentSetDefinition garment = new([Rigid(1, "a"), Skinned(2, "a")], AttachmentLayer.Garment, ["a"]);
        OutfitCoverage outfit = OutfitCoverage.Resolve(Body, [garment]);

        Assert.Throws<ArgumentOutOfRangeException>(() => outfit.HiddenFor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => outfit.RigidPartHidden(1, 1));
        Assert.Throws<ArgumentException>(() => outfit.RigidPartHidden(0, 2));
        Assert.Throws<ArgumentException>(() => outfit.SkinnedPartRegions(0, 1));
        Assert.Throws<ArgumentNullException>(() => OutfitCoverage.Resolve(Body, [garment, null!]));
    }

    static AttachmentSetDefinition Piece(AttachmentLayer layer, params string[] covers) => new([Rigid(1, covers[0])], layer, covers);

    static RigidAttachmentPartDefinition Rigid(uint id, string? region) =>
        new(id, new("parts/rigid.glb", Hash), "root", Matrix4x4.Identity) { Region = region };

    static SkinnedAttachmentPartDefinition Skinned(uint id, params string[] regions)
    {
        var runs = new NamedRegionRun[regions.Length];
        for (int index = 0; index < regions.Length; index++)
            runs[index] = new(regions[index], 1);
        return new(id, new("parts/skinned.glb", Hash)) { Regions = runs };
    }

    static RegionSet Regions(params string[] names)
    {
        RegionSet set = RegionSet.Empty;
        foreach (string name in names)
            set = set.With(Body.TryRegion(name, out int index) ? index : throw new ArgumentException(name));
        return set;
    }
}
