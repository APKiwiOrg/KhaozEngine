using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TopologySpaceRefusalTests
{
    [Fact]
    public void Repeated_reference_keeps_its_owner_target_and_position()
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var opening = new MapRecordRef("opening", world.Patches[0].Key);
        TopologyRecordFixtures.Replace<MapVerticalLink>(world, "link", l => l with { Openings = new[] { opening, opening } });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, finding.Reason);
        Assert.Equal("link", finding.RecordId);
        Assert.Equal("opening", finding.RelatedRecordId);
        Assert.Equal(opening.Anchor, finding.Patch);
        Assert.Equal(1, finding.Index);
        TopologyRecordFixtures.Replace<MapVerticalLink>(world, "link", l => l with { Openings = new[] { opening } });
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Space_kind_keeps_its_declaring_space()
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Kind = (MapSpaceKind)255 });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, finding.Reason);
        Assert.Equal("a", finding.SpaceId);
        Assert.Equal("Kind", finding.FieldPath);
        Assert.Equal(typeof(MapSpaceKind), finding.EnumType);
        Assert.Equal(255L, finding.ActualInteger);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Kind = MapSpaceKind.Cave });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("alias", "AliasOf.AliasOf")]
    [InlineData("parent", "AliasOf.Parent")]
    [InlineData("kind", "AliasOf.Kind")]
    public void Alias_names_the_first_nonpeer_property(string fault, string field)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var patch = world.Patches[0];
        patch.Records.Add(Space("peer"));
        patch.Records.Add(Space("parent"));
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { AliasOf = new("b", patch.Key) });
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "b", s => s with
        {
            AliasOf = fault == "alias" ? new("peer", patch.Key) : null,
            Parent = fault == "parent" ? new("parent", patch.Key) : null,
            Kind = fault == "kind" ? MapSpaceKind.Exterior : MapSpaceKind.Cave,
        });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SpaceAliasPeerInvalid, finding.Reason);
        Assert.Equal("a", finding.SpaceId);
        Assert.Equal("b", finding.RelatedRecordId);
        Assert.Equal(field, finding.FieldPath);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "b", s => s with { AliasOf = null, Parent = null, Kind = MapSpaceKind.Cave });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.SchemaEnumUnsupported)]
    [InlineData(true, MapDocumentRefusal.IdentityDuplicate)]
    public void Boundary_side_distinguishes_undefined_and_repeated_sides(bool duplicate, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var wall = new MapBoundaryRef(new("wall", world.Patches[0].Key), MapSide.Front);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with
        { Walls = duplicate ? new[] { wall, wall } : new[] { wall with { Side = (MapSide)255 } } });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("a", finding.RecordId);
        Assert.Equal("wall", finding.RelatedRecordId);
        Assert.Equal(typeof(MapSide), finding.EnumType);
        Assert.Equal(duplicate ? (long)MapSide.Front : 255L, finding.ActualInteger);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Walls = new[] { wall } });
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Space_portal_membership_keeps_the_listing_space_and_endpoint()
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var boundary = new MapBoundaryRef(new("portal", world.Patches[0].Key), MapSide.Front);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Portals = [] });
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "b", s => s with { Portals = s.Portals.Concat(new[] { boundary }).ToArray() });
        // A misplaced side also leaves its real endpoint unlisted. The reason and owner distinguish the two rules.
        var finding = Assert.Single(Findings(world), f => f.Reason == MapDocumentRefusal.SpacePortalMembershipMismatch && f.RecordId == "b");
        Assert.Equal("b", finding.SpaceId);
        Assert.Equal("portal", finding.RelatedRecordId);
        Assert.Equal("a", finding.EntityId);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "b", s => s with { Portals = s.Portals.Where(p => p.Side != MapSide.Front).ToArray() });
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Portals = new[] { boundary } });
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Space_link_membership_rejects_an_unrelated_listing_space()
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var patch = world.Patches[0];
        patch.Records.Add(Space("other") with { Links = new[] { new MapRecordRef("link", patch.Key) } });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SpaceLinkMembershipMismatch, finding.Reason);
        Assert.Equal("other", finding.SpaceId);
        Assert.Equal("link", finding.RelatedRecordId);
        Assert.Equal("Links", finding.FieldPath);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "other", s => s with { Links = [] });
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Room_footprint_is_rejected_even_with_its_required_prism()
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var patch = world.Patches[0];
        var parent = new MapRecordRef("b", patch.Key);
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Kind = MapSpaceKind.Room, Parent = parent });
        patch.Records.Add(new MapPrismDomain("prism", 1, new("a", patch.Key), parent, "", "storey",
            new(world.Surfaces[0].Frame, new[] { new MapWaterRect(0, 0, 1, 1) }, []), 0, 1));
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SpaceRoomOccupancyInvalid, finding.Reason);
        Assert.Equal("fragment-a", finding.RecordId);
        Assert.Equal("a", finding.SpaceId);
        patch.Records.RemoveAll(r => r.Id == "fragment-a");
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Footprint_forwards_the_original_legacy_recipe_finding()
    {
        var world = TopologyRecordFixtures.LegacyExteriorWorld();
        var original = world.Surfaces[0];
        world.Surfaces[0] = original with { PresencePolicy = MapPresencePolicy.Native };
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SurfaceLegacyRecipeRequired, finding.Reason);
        Assert.Equal("world-cells", finding.RecordId);
        Assert.Equal("world", finding.SpaceId);
        Assert.Equal("plane-0", finding.SurfaceId);
        Assert.Equal(world.Patches[0].Key, finding.Patch);
        Assert.Equal("Lower.Surface.PresencePolicy", finding.FieldPath);
        world.Surfaces[0] = original;
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("lower", "Lower.Kind")]
    [InlineData("space", "Space.Kind")]
    [InlineData("surface", "Upper.SurfaceId")]
    [InlineData("opening", "Upper.Opening")]
    public void Open_top_bound_names_the_first_incompatible_field(string fault, string field)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Kind = MapSpaceKind.Exterior });
        var original = world.Patches[0].Records.OfType<MapSpaceFootprint>().Single(f => f.Id == "fragment-a");
        var open = new MapBoundRef(MapBoundKind.OpenTop, null, null);
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "fragment-a", f => f with
        {
            Lower = fault == "lower" ? open : f.Lower,
            Upper = fault == "lower" ? f.Upper : open with
            { SurfaceId = fault == "surface" ? "ceiling" : null, Opening = fault == "opening" ? new("opening", world.Patches[0].Key) : null },
        });
        if (fault == "space") TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Kind = MapSpaceKind.Cave });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SpaceOpenTopInvalid, finding.Reason);
        Assert.Equal("fragment-a", finding.RecordId);
        Assert.Equal("a", finding.SpaceId);
        Assert.Equal(field, finding.FieldPath);
        if (fault == "surface") Assert.Equal("ceiling", finding.SurfaceId);
        if (fault == "opening") Assert.Equal("opening", finding.RelatedRecordId);
        if (fault == "space") TopologyRecordFixtures.Replace<MapSpaceDoc>(world, "a", s => s with { Kind = MapSpaceKind.Exterior });
        else TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "fragment-a", f => f with
        { Lower = original.Lower, Upper = fault == "lower" ? original.Upper : open });
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Opening_bound_cannot_also_name_a_surface()
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var opening = new MapRecordRef("opening", world.Patches[0].Key);
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "fragment-a", f => f with
        { Upper = new(MapBoundKind.HorizontalOpening, "ceiling", opening) });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.AuthoredValueMismatch, finding.Reason);
        Assert.Equal("fragment-a", finding.RecordId);
        Assert.Equal("ceiling", finding.SurfaceId);
        Assert.Equal("Upper.SurfaceId", finding.FieldPath);
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "fragment-a", f => f with { Upper = f.Upper with { SurfaceId = null } });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("enum", MapDocumentRefusal.SchemaEnumUnsupported, "Lower.Kind")]
    [InlineData("kind", MapDocumentRefusal.SpaceBoundRoleMismatch, "Lower.Kind")]
    [InlineData("opening", MapDocumentRefusal.AuthoredValueMismatch, "Lower.Opening")]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired, "Lower.SurfaceId")]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing, "Lower.SurfaceId")]
    [InlineData("role", MapDocumentRefusal.SpaceBoundRoleMismatch, "Lower.Surface.Role")]
    public void Surface_bound_selects_the_actual_failed_branch(string fault, MapDocumentRefusal reason, string field)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var original = world.Patches[0].Records.OfType<MapSpaceFootprint>().Single(f => f.Id == "fragment-a");
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "fragment-a", f => f with
        {
            Lower = f.Lower with
            {
                Kind = fault == "enum" ? (MapBoundKind)255 : fault == "kind" ? MapBoundKind.Ceiling : f.Lower.Kind,
                Opening = fault == "opening" ? new("opening", world.Patches[0].Key) : null,
                SurfaceId = fault == "null" ? null : fault == "missing" ? "absent" : fault == "role" ? "ceiling" : "floor",
            }
        });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("fragment-a", finding.RecordId);
        Assert.Equal("a", finding.SpaceId);
        Assert.Equal(field, finding.FieldPath);
        if (fault == "enum")
        {
            Assert.Equal(typeof(MapBoundKind), finding.EnumType);
            Assert.Equal(255L, finding.ActualInteger);
        }
        if (fault == "role")
        {
            Assert.Equal(typeof(MapSurfaceRole), finding.EnumType);
            Assert.Equal((long)MapSurfaceRole.Ceiling, finding.ActualInteger);
        }
        if (fault == "missing") Assert.Equal("absent", finding.SurfaceId);
        if (fault == "opening") Assert.Equal("opening", finding.RelatedRecordId);
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "fragment-a", _ => original);
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4097, 4096)]
    public void Slot_cardinality_accepts_each_limit(int refused, int accepted)
    {
        var world = TopologyRecordFixtures.LegacyExteriorWorld();
        var patch = world.Patches[0];
        patch.Width = patch.Depth = 64;
        patch.Heights = new int[65 * 65];
        patch.Cells = Enumerable.Repeat(patch.Cells[0], 4096).ToArray();
        patch.Presence = Enumerable.Repeat(ulong.MaxValue, 64).ToArray();
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "world-cells", f => f with { SlotCells = Enumerable.Range(0, refused).ToArray() });
        var finding = Assert.Single(Findings(world), f => f.FieldPath == "SlotCells.Count");
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, finding.Reason);
        Assert.Equal("world-cells", finding.RecordId);
        Assert.Equal((long)refused, finding.ActualCount);
        Assert.Equal(1L, finding.MinimumInteger);
        Assert.Equal(4096L, finding.MaximumInteger);
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "world-cells", f => f with { SlotCells = Enumerable.Range(0, accepted).ToArray() });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData(true, MapDocumentRefusal.IdentityDuplicate)]
    [InlineData(false, MapDocumentRefusal.OrderInvalid)]
    public void Slot_order_distinguishes_a_repeat_from_a_backwards_step(bool duplicate, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.LegacyExteriorWorld();
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "world-cells", f => f with { SlotCells = duplicate ? new[] { 0, 0 } : new[] { 1, 0 } });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("world-cells", finding.RecordId);
        Assert.Equal(1, finding.Index);
        Assert.Equal(0L, finding.ActualInteger);
        Assert.Equal(duplicate ? 0L : 1L, finding.PreviousInteger);
        TopologyRecordFixtures.Replace<MapSpaceFootprint>(world, "world-cells", f => f with { SlotCells = new[] { 0, 1 } });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("range", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("patch", MapDocumentRefusal.SurfaceAddressOutOfPatch)]
    [InlineData("present", MapDocumentRefusal.SpaceOpeningCellsInvalid)]
    public void Opening_cells_keep_the_unavailable_or_refused_patch_subject(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var patch = world.Patches[0];
        var original = patch.Records.OfType<MapHorizontalOpening>().Single();
        TopologyRecordFixtures.Replace<MapHorizontalOpening>(world, "opening", o => o with
        { Patch = fault == "missing" ? new("floor", 1, 0) : o.Patch, SlotCells = new[] { fault == "range" ? 4096 : fault == "patch" ? 2 : fault == "present" ? 1 : 0 } });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("opening", finding.RecordId);
        Assert.Equal(fault == "missing" ? new MapPatchKey("floor", 1, 0) : patch.Key, finding.Patch);
        if (fault != "missing") Assert.Equal(0, finding.Index);
        if (fault == "range")
        {
            Assert.Equal(4096L, finding.ActualInteger);
            Assert.Equal(0L, finding.MinimumInteger);
            Assert.Equal(4095L, finding.MaximumInteger);
        }
        if (fault == "patch")
        {
            Assert.Equal(2, finding.CellX);
            Assert.Equal(0, finding.CellZ);
            Assert.Equal(2, finding.Width);
            Assert.Equal(1, finding.Depth);
        }
        TopologyRecordFixtures.Replace<MapHorizontalOpening>(world, "opening", _ => original);
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void First_negative_slot_does_not_invent_a_duplicate_predecessor()
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        TopologyRecordFixtures.Replace<MapHorizontalOpening>(world, "opening", o => o with { SlotCells = new[] { -1 } });
        var findings = Findings(world);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal(MapDocumentRefusal.ValueOutsideRange, f.Reason);
            Assert.Equal(-1L, f.ActualInteger);
            Assert.Null(f.PreviousInteger);
        });
        TopologyRecordFixtures.Replace<MapHorizontalOpening>(world, "opening", o => o with { SlotCells = new[] { 0 } });
        Assert.Empty(world.Validate());
    }

    static MapSpaceDoc Space(string id) => new(id, MapSpaceKind.Cave, null, null, [], [], [], []);

    static IReadOnlyList<MapDocumentFinding> Findings(TopologyWorld world)
    {
        var findings = MapTopologyReferenceValidator.ValidateFindings(world.Surfaces, world.Patches);
        Assert.NotEmpty(findings);
        Assert.Equal(world.Validate(), findings.Select(f => f.Message));
        return findings;
    }
}
