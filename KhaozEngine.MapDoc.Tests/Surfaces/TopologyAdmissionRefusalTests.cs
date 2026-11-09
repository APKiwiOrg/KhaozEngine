using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TopologyAdmissionRefusalTests
{
    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired, "surface")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "Id")]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate, "surfaces")]
    [InlineData("role", MapDocumentRefusal.SchemaEnumUnsupported, "Role")]
    [InlineData("presence", MapDocumentRefusal.SchemaEnumUnsupported, "PresencePolicy")]
    [InlineData("frame", MapDocumentRefusal.SchemaValueRequired, "Frame")]
    [InlineData("frame-enum", MapDocumentRefusal.SchemaEnumUnsupported, "RowDirection")]
    [InlineData("frame-unit", MapDocumentRefusal.ValueOutsideRange, "numerator")]
    public void Surface_admission_retains_the_first_failed_rule(string fault, MapDocumentRefusal reason, string field)
    {
        var world = World();
        world.Patches.Clear();
        MapSurfaceRef original = world.Surfaces[0];
        world.Surfaces[0] = fault switch
        {
            "null" => null!,
            "id" => original with { Id = " " },
            "role" => original with { Role = (MapSurfaceRole)255 },
            "presence" => original with { PresencePolicy = (MapPresencePolicy)255 },
            "frame" => original with { Frame = null! },
            "frame-enum" => original with { Frame = original.Frame with { RowDirection = (MapRowDirection)255 } },
            "frame-unit" => original with { Frame = original.Frame with { CellUnitMetres = default } },
            _ => original,
        };
        if (fault == "duplicate") world.Surfaces.Add(original);
        var finding = Assert.Single(Findings(world), f => f.Reason == reason && f.FieldPath == field);
        if (fault is not ("null" or "id")) Assert.Equal("r", finding.SurfaceId);
        if (fault is "role" or "presence" or "frame-enum")
        {
            Assert.Equal(255L, finding.ActualInteger);
            Assert.Equal(fault == "role" ? typeof(MapSurfaceRole) : fault == "presence" ? typeof(MapPresencePolicy) : typeof(MapRowDirection), finding.EnumType);
        }
        if (fault == "frame-unit")
        {
            Assert.Equal(0L, finding.ActualInteger);
            Assert.Equal(1L, finding.MinimumInteger);
        }
        Assert.Empty(world.Patches);
        world.Surfaces.Clear();
        world.Surfaces.Add(original);
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("surface", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("patch", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("record-null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("record-id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("record-duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("local", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("kind", MapDocumentRefusal.OperationKindUnsupported)]
    public void Patch_and_record_admission_keeps_its_typed_subject(string fault, MapDocumentRefusal reason)
    {
        var world = World();
        MapSurfacePatch patch = world.Patches[0];
        if (fault == "surface") world.Surfaces.Clear();
        if (fault == "patch") world.Patches.Add(patch.Clone());
        if (fault == "record-null") patch.Records.Add(null!);
        if (fault == "record-id") patch.Records.Add(Space(" "));
        if (fault == "record-duplicate") patch.Records.AddRange(new[] { Space("same"), Space("same") });
        if (fault == "local") patch.Width = 0;
        if (fault == "kind") patch.Records.Add(new UnsupportedRecord("unknown"));
        var finding = Assert.Single(Findings(world), f => f.Reason == reason);
        Assert.Equal(patch.Key, finding.Patch);
        if (fault == "surface") Assert.Equal("r", finding.SurfaceId);
        if (fault == "record-duplicate") Assert.Equal("same", finding.RecordId);
        if (fault == "record-null")
        {
            var error = Assert.Throws<MapDocumentException>(() => patch.Clone());
            Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
            Assert.Equal("Records", error.FieldPath);
            Assert.Equal(patch.Key, error.Patch);
        }
        if (fault == "kind")
        {
            Assert.Equal(typeof(UnsupportedRecord), finding.RecordType);
            var error = Assert.Throws<MapDocumentException>(() => patch.Clone());
            Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
            Assert.Equal("unknown", error.RecordId);
            Assert.Equal(typeof(UnsupportedRecord), error.RecordType);
            Assert.Equal(patch.Key, error.Patch);
        }
        Assert.Equal(new MapPatchKey("r", 0, 0), patch.Key);
        world.Surfaces.Clear();
        world.Surfaces.Add(Surface());
        world.Patches.RemoveRange(1, world.Patches.Count - 1);
        patch.Width = 1;
        patch.Records.Clear();
        Assert.NotNull(patch.Clone());
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Strip_facing_keeps_the_record_owner()
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        MapSurfacePatch patch = world.Patches[0];
        int index = patch.Records.FindIndex(r => r is MapWallStrip);
        var original = (MapWallStrip)patch.Records[index];
        patch.Records[index] = original with { Facing = (MapStripFacing)255 };
        var facing = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, facing.Reason);
        Assert.Equal("wall", facing.RecordId);
        Assert.Equal("Facing", facing.FieldPath);
        Assert.Equal(typeof(MapStripFacing), facing.EnumType);
        Assert.Equal(255L, facing.ActualInteger);
        patch.Records[index] = original;
        Assert.Empty(world.Validate());

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_record_reference_keeps_the_owner_and_required_field(bool blank)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        MapSurfacePatch patch = world.Patches[0];
        int index = patch.Records.FindIndex(r => r is MapWallStrip);
        var original = (MapWallStrip)patch.Records[index];
        patch.Records[index] = original with { LowerChain = blank ? new(" ", patch.Key) : null! };
        var reference = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, reference.Reason);
        Assert.Equal("wall", reference.RecordId);
        Assert.Equal(blank ? "reference.Id" : "reference", reference.FieldPath);
        if (blank) Assert.Equal(" ", reference.ActualText);
        patch.Records[index] = original;
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("parent", MapDocumentRefusal.SpacePrismParentMismatch)]
    public void Prism_admission_distinguishes_version_room_kind_and_parent(string fault, MapDocumentRefusal reason)
    {
        var world = World();
        MapSurfacePatch patch = world.Patches[0];
        var parent = new MapRecordRef("parent", patch.Key);
        var room = Space("room") with { Kind = MapSpaceKind.Room, Parent = parent };
        var prism = new MapPrismDomain("prism", 1, new("room", patch.Key), parent, "", "storey",
            new(Surface().Frame, new[] { new MapWaterRect(0, 0, 1, 1) }, []), 0, 1);
        patch.Records.AddRange(new MapTopologyRecord[] { Space("parent"), Space("other"), room, prism });
        if (fault == "version") patch.Records[3] = prism with { PayloadVersion = 2 };
        if (fault == "missing") patch.Records.RemoveAt(2);
        if (fault == "kind") patch.Records[2] = room with { Kind = MapSpaceKind.Cave };
        if (fault == "parent") patch.Records[2] = room with { Parent = new("other", patch.Key) };
        string field = fault == "version" ? "PayloadVersion" : fault == "missing" ? "Space" : fault == "kind" ? "Space.Kind" : "Space.Parent";
        var finding = Assert.Single(Findings(world), f => f.Reason == reason && f.RecordId == "prism" && f.FieldPath == field);
        Assert.Equal("prism", finding.RecordId);
        Assert.Equal("room", finding.SpaceId);
        if (fault == "version")
        {
            Assert.Equal(2, finding.ActualVersion);
            Assert.Equal(new[] { 1 }, finding.SupportedVersions);
        }
        Assert.Equal(field, finding.FieldPath);
        Assert.Equal("parent", finding.RelatedRecordId);
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapSpaceKind), finding.EnumType);
            Assert.Equal((long)MapSpaceKind.Cave, finding.ActualInteger);
        }
        Assert.Equal(fault == "missing" ? 3 : 4, patch.Records.Count);
        if (fault == "missing") patch.Records.Insert(2, room);
        patch.Records[2] = room;
        patch.Records[3] = prism;
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Room_occupancy_requires_exactly_one_prism(int count)
    {
        var world = World();
        MapSurfacePatch patch = world.Patches[0];
        var parent = new MapRecordRef("parent", patch.Key);
        patch.Records.Add(Space("parent"));
        patch.Records.Add(Space("room") with { Kind = MapSpaceKind.Room, Parent = parent });
        var prism = new MapPrismDomain("prism", 1, new("room", patch.Key), parent, "", "storey",
            new(Surface().Frame, new[] { new MapWaterRect(0, 0, 1, 1) }, []), 0, 1);
        for (int i = 0; i < count; i++) patch.Records.Add(prism with { Id = "prism" + i });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SpaceRoomOccupancyInvalid, finding.Reason);
        Assert.Equal("room", finding.SpaceId);
        Assert.Equal((long)count, finding.ActualCount);
        Assert.Equal(1L, finding.ExpectedCount);
        Assert.Equal(count + 2, patch.Records.Count);
        patch.Records.RemoveRange(2, count);
        patch.Records.Add(prism);
        Assert.Empty(world.Validate());
    }

    [Fact]
    public void Parent_cycle_names_the_cycle_space()
    {
        var world = World();
        MapSurfacePatch patch = world.Patches[0];
        patch.Records.Add(Space("a") with { Parent = new("b", patch.Key) });
        patch.Records.Add(Space("b") with { Parent = new("a", patch.Key) });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.SpaceParentCycle, finding.Reason);
        Assert.Equal("a", finding.SpaceId);
        Assert.Equal(2, patch.Records.Count);
        patch.Records[1] = Space("b");
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("space", MapDocumentRefusal.LegacyExteriorSpaceRequired)]
    [InlineData("upper", MapDocumentRefusal.LegacyExteriorOpenTopRequired)]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("role", MapDocumentRefusal.SurfaceLegacyRecipeRequired)]
    [InlineData("presence", MapDocumentRefusal.SurfaceLegacyRecipeRequired)]
    [InlineData("frame", MapDocumentRefusal.SurfaceLegacyRecipeRequired)]
    [InlineData("lattice", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void Legacy_recipe_reports_the_first_eligibility_rule(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.LegacyExteriorWorld();
        var patch = world.Patches[0];
        var space = patch.Records.OfType<MapSpaceDoc>().Single();
        var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
        MapSurfaceRef? surface = world.Surfaces[0];
        var badSpace = fault == "space" ? space with { Kind = MapSpaceKind.Cave } : space;
        var badFootprint = fault == "upper" ? footprint with { Upper = new(MapBoundKind.Ceiling, surface.Id, null) }
            : fault == "lattice" ? footprint with { Lattice = new("other", 0, 0) } : footprint;
        surface = fault switch
        {
            "null" => null,
            "role" => surface with { Role = MapSurfaceRole.Ceiling },
            "presence" => surface with { PresencePolicy = MapPresencePolicy.Native },
            "frame" => surface with { Frame = surface.Frame with { RowDirection = MapRowDirection.PositiveZ } },
            _ => surface,
        };
        var finding = Assert.IsType<MapDocumentFinding>(MapLegacyExteriorRecipe.CheckFinding(badFootprint, badSpace, surface));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("world-cells", finding.RecordId);
        Assert.Equal("world", finding.SpaceId);
        Assert.Equal(badFootprint.Lattice, finding.Patch);
        Assert.Equal("plane-0", finding.SurfaceId);
        Assert.Equal(fault switch
        {
            "space" => "Space.Kind",
            "upper" => "Upper.Kind",
            "null" => "Lower.Surface",
            "role" => "Lower.Surface.Role",
            "presence" => "Lower.Surface.PresencePolicy",
            "frame" => "Lower.Surface.Frame",
            _ => "Lattice.SurfaceId",
        }, finding.FieldPath);
        Assert.Equal(2, patch.Records.Count);
        Assert.Null(MapLegacyExteriorRecipe.CheckFinding(footprint, space, world.Surfaces[0]));
        Assert.Null(MapLegacyExteriorRecipe.Check(footprint, space, world.Surfaces[0]));
    }

    static IReadOnlyList<MapDocumentFinding> Findings(TopologyWorld world)
    {
        var findings = new List<MapDocumentFinding>();
        var errors = MapTopologyReferenceValidator.Validate(world.Surfaces, world.Patches, findings);
        Assert.NotEmpty(errors);
        Assert.NotEmpty(findings);
        return findings;
    }
    static MapSurfaceRef Surface() => new("r", MapLatticeFrame.ImportedMetreCentimetre,
        MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
    static TopologyWorld World() => new(new() { Surface() }, new() { SurfacePatchFixtures.Row(1) });
    static MapSpaceDoc Space(string id) => new(id, MapSpaceKind.Exterior, null, null, [], [], [], []);
    sealed record UnsupportedRecord(string Id) : MapTopologyRecord(Id);
}
