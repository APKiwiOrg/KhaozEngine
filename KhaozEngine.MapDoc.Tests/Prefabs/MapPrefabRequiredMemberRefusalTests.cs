using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabRequiredMemberRefusalTests
{
    static readonly MapRecordRef Space = new("room", new("floor", 0, 0));
    static readonly MapLatticeVertex Corner = new("floor", MapLatticeAddress.Corner(0, 0));

    // #1398. A null required member is refused by name before the validator dereferences it, rather than surfacing
    // as a NullReferenceException read as unclassified prefab state or escaping validation. Transfer and storey rows
    // carry a standalone prism, whose storey check reads both collections. The indoor-parent row is closure bound.
    [Theory]
    [InlineData("transfer", "$.prefabs.transfers[0]")]
    [InlineData("transfer-binding", "$.prefabs.transfers[0].binding")]
    [InlineData("storeys", null)]
    [InlineData("storey-element", null)]
    [InlineData("bindings", "$.bindings")]
    [InlineData("replace-value", "$.value")]
    [InlineData("add-value", "$.value")]
    [InlineData("references", "$.references")]
    [InlineData("placement", "$.value")]
    [InlineData("surface", "$.surface")]
    [InlineData("patches", "$.patches")]
    [InlineData("indoor-parent", "$.surface.indoorSpan.parentSpace")]
    [InlineData("indoor-tags", "$.surface.indoorSpan.domainTags")]
    [InlineData("record", "$.value")]
    [InlineData("medium", "$.value")]
    [InlineData("water", "$.value")]
    [InlineData("water-material", "$.value.renderMaterial")]
    [InlineData("water-covers", "$.value.covers")]
    [InlineData("water-cover", "$.value.covers[0]")]
    [InlineData("light", "$.value")]
    [InlineData("marker", "$.value")]
    [InlineData("roof-link", "$.value")]
    [InlineData("roof-group", "$.value")]
    [InlineData("link-roof", "$.roof")]
    [InlineData("link-rooms", "$.rooms")]
    [InlineData("link-room", "$.rooms[0]")]
    [InlineData("link-groups", "$.groups")]
    [InlineData("link-group", "$.groups[0]")]
    [InlineData("group-rooms", "$.rooms")]
    [InlineData("group-room", "$.rooms[0]")]
    [InlineData("seam-first", "$.value.first")]
    [InlineData("seam-second", "$.value.second")]
    [InlineData("seam-pairs", "$.value.pairs")]
    [InlineData("chain-vertices", "$.value.vertices")]
    [InlineData("chain-vertex", "$.value.vertices[0]")]
    [InlineData("strip-lower", "$.value.lowerChain")]
    [InlineData("strip-upper", "$.value.upperChain")]
    [InlineData("portal-from", "$.value.fromSpace")]
    [InlineData("portal-to", "$.value.toSpace")]
    [InlineData("portal-interval", "$.value.interval")]
    [InlineData("portal-band", "$.value.bandBottom")]
    [InlineData("vertical-upper", "$.value.upperSpace")]
    [InlineData("vertical-lower", "$.value.lowerSpace")]
    [InlineData("vertical-openings", "$.value.openings")]
    [InlineData("vertical-opening", "$.value.openings[0]")]
    [InlineData("vertical-portals", "$.value.portals")]
    [InlineData("vertical-portal", "$.value.portals[0]")]
    [InlineData("vertical-owners", "$.value.geometryOwners")]
    [InlineData("vertical-owner", "$.value.geometryOwners[0]")]
    [InlineData("space-tags", "$.value.domainTags")]
    [InlineData("space-walls", "$.value.walls")]
    [InlineData("space-wall", "$.value.walls[0]")]
    [InlineData("space-wall-record", "$.value.walls[0].record")]
    [InlineData("space-portals", "$.value.portals")]
    [InlineData("space-portal", "$.value.portals[0]")]
    [InlineData("space-portal-record", "$.value.portals[0].record")]
    [InlineData("space-links", "$.value.links")]
    [InlineData("space-link", "$.value.links[0]")]
    [InlineData("prism-space", "$.value.space")]
    [InlineData("prism-parent", "$.value.parent")]
    [InlineData("prism-footprint", "$.value.footprint")]
    [InlineData("prism-rects", "$.value.footprint.rects")]
    [InlineData("prism-rings", "$.value.footprint.rings")]
    [InlineData("prism-ring", "$.value.footprint.rings[0]")]
    [InlineData("prism-ring-vertices", "$.value.footprint.rings[0].vertices")]
    [InlineData("footprint-space", "$.value.space")]
    [InlineData("footprint-cells", "$.value.slotCells")]
    [InlineData("footprint-lower", "$.value.lower")]
    [InlineData("footprint-upper", "$.value.upper")]
    public void Null_required_members_identify_the_member_and_its_owner(string fault, string? path)
    {
        var (valid, child) = Children(fault);
        bool overridden = fault is "replace-value" or "add-value" || !ReferenceEquals(child, valid);
        bool closure = fault == "indoor-parent";
        bool transfer = fault.StartsWith("transfer", StringComparison.Ordinal);
        bool storey = fault.StartsWith("storey", StringComparison.Ordinal);
        PrefabFixture? fixture = closure ? PrefabFixtures.Create("roomless") : transfer || storey ? PrefabFixtures.StandalonePrism() : null;
        var document = fixture?.Document ?? MapPrefabDocumentRefusalTests.Empty();
        MapPrefabInstance instance = closure ? Assert.Single(document.Prefabs!.Instances)
            : new("a", "hut", new string('a', 64), new(Vector3.Zero, 0, 1), null, new([]), []);
        MapStoreyDoc[] storeys = document.BuildingIntent?.Storeys.ToArray() ?? [];
        Assert.Equal(transfer || storey, document.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapPrismDomain>().Any());
        MapPrefabOverride Override(MapPrefabChild value) => fault == "replace-value"
            ? new MapPrefabReplaceOverride(valid.Key, value) : new MapPrefabAddOverride(valid.Key, value);
        document.Prefabs = new(1, 1, [instance with
        {
            Bindings = fault == "bindings" ? null! : instance.Bindings,
            Overrides = overridden ? [Override(fault is "replace-value" or "add-value" ? null! : child)] : [],
        }], fault switch { "transfer" => [null!], "transfer-binding" => [new(instance.Id, null!)], _ => [] });
        if (!closure) document.BuildingIntent = new(1, [Link(fault)], [Group(fault)], [], [])
        { Storeys = fault switch { "storeys" => null!, "storey-element" => [null!, .. storeys], _ => storeys } };

        var finding = MapPrefabDocumentRefusalTests.Refused(document, closure ? fixture!.Assets : null, MapDocumentRefusal.SchemaValueRequired);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(path?.Contains("[0]", StringComparison.Ordinal) == true || fault == "storey-element" ? 0 : null, finding.Index);
        if (transfer) Assert.Equal(document.Id, finding.DocumentId);
        if (storey) Assert.Equal("storey", finding.SubjectKind);
        if (fault is "transfer-binding" or "bindings") Assert.Equal(instance.Id, finding.InstanceId);
        if (fault.StartsWith("link-", StringComparison.Ordinal)) Assert.Equal("link", finding.EntityId);
        if (fault.StartsWith("group-", StringComparison.Ordinal)) Assert.Equal("group", finding.EntityId);
        if (overridden) Assert.Equal(valid.Key, finding.Child);

        document.Prefabs = new(1, 1, [instance with { Overrides = closure ? instance.Overrides : overridden ? [Override(valid)] : [] }], []);
        if (!closure) document.BuildingIntent = new(1, [Link("")], [Group("")], [], []) { Storeys = storeys };
        Assert.Empty(MapPrefabValidation.Validate(document, closure ? fixture!.Assets : null));
    }

    static MapRoofLinkDoc Link(string fault)
    {
        MapPrefabReference room = new(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "room");
        MapPrefabReference group = new(MapPrefabReferenceScope.World, MapPrefabChildKind.RoofGroup, "group");
        var link = new MapRoofLinkDoc("link", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "roof"), [room], [group], 0);
        return fault switch
        {
            "link-roof" => link with { Roof = null! },
            "link-rooms" => link with { Rooms = null! },
            "link-room" => link with { Rooms = [null!] },
            "link-groups" => link with { Groups = null! },
            "link-group" => link with { Groups = [null!] },
            _ => link,
        };
    }

    static MapRoofGroupDoc Group(string fault)
    {
        var group = new MapRoofGroupDoc("group", [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "room")]);
        return fault switch { "group-rooms" => group with { Rooms = null! }, "group-room" => group with { Rooms = [null!] }, _ => group };
    }

    // The valid child and the child that breaks only the row's member. Rows that break no child return the same child.
    static (MapPrefabChild Valid, MapPrefabChild Child) Children(string fault)
    {
        var placement = PrefabFixtures.Create("roomless").Assets.GetPrefab("hut").Children.OfType<MapPrefabPlacementChild>().First();
        var water = PrefabFixtures.WaterCaves().Assets.GetPrefab("hut").Children;
        var floor = water.OfType<MapPrefabSurfaceChild>().First();
        var surface = floor with { Surface = floor.Surface with { IndoorSpan = new("span", Space, 0, 1, []) } };
        var body = water.OfType<MapPrefabWaterChild>().First();
        var medium = water.OfType<MapPrefabMediumChild>().First();
        var marker = MarkerFixtures.BasicMarker();
        var light = new MapPrefabLightChild(new(MapPrefabChildKind.Light, "glow"), PrefabFixtures.GlowLight());
        var markerChild = new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, marker.Id), marker);
        var link = new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, "link"), Link(""));
        var group = new MapPrefabRoofGroupChild(new(MapPrefabChildKind.RoofGroup, "group"), Group(""));
        var space = Record(new MapSpaceDoc("space", MapSpaceKind.Room, null, null, [], [], [], []));
        if (RecordCase(fault) is var (validRecord, invalidRecord)) return (Record(validRecord), Record(invalidRecord));
        return fault switch
        {
            "references" => (placement, placement with { References = null! }),
            "placement" => (placement, placement with { Value = null! }),
            "surface" => (surface, surface with { Surface = null! }),
            "patches" => (surface, surface with { Patches = null! }),
            "indoor-parent" => (surface, surface with { Surface = surface.Surface with { IndoorSpan = surface.Surface.IndoorSpan! with { ParentSpace = null! } } }),
            "indoor-tags" => (surface, surface with { Surface = surface.Surface with { IndoorSpan = surface.Surface.IndoorSpan! with { DomainTags = null! } } }),
            "record" => (space, space with { Value = null! }),
            "medium" => (medium, medium with { Value = null! }),
            "water" => (body, body with { Value = null! }),
            "water-material" => (body, body with { Value = body.Value with { RenderMaterial = null! } }),
            "water-covers" => (body, body with { Value = body.Value with { Covers = null! } }),
            "water-cover" => (body, body with { Value = body.Value with { Covers = [null!] } }),
            "light" => (light, light with { Value = null! }),
            "marker" => (markerChild, markerChild with { Value = null! }),
            "roof-link" => (link, link with { Value = null! }),
            "roof-group" => (group, group with { Value = null! }),
            _ => (placement, placement),
        };
    }

    static MapPrefabRecordChild Record(MapTopologyRecord record) => new(new(MapPrefabChildKind.Record, record.Id), record, new("floor", 0, 0));

    static (MapTopologyRecord Valid, MapTopologyRecord Invalid)? RecordCase(string fault)
    {
        var edge = new MapSurfaceEdgeRef(new("floor", 0, 0), Corner, Corner);
        var seam = new MapSurfaceSeam("seam", edge, edge, []);
        var chain = new MapBoundaryChain("chain", MapChainKind.Authored, null,
            [new MapChainVertex(Corner, 0), new MapChainVertex(Corner with { Address = MapLatticeAddress.Corner(1, 0) }, 0)]);
        var strip = new MapWallStrip("strip", Space, Space, MapStripFacing.Front, 0);
        var portal = new MapCavePortal("portal", Space, Space, [Corner], Space, null);
        var vertical = new MapVerticalLink("vertical", Space, Space, [Space], [Space], [Space]);
        var wall = new MapBoundaryRef(Space, MapSide.Front);
        var space = new MapSpaceDoc("space", MapSpaceKind.Room, null, null, [], [wall], [wall], [Space]);
        var prism = new MapPrismDomain("prism", 1, Space, Space, "", "storey",
            new MapWaterDomainDoc(MapLatticeFrame.ImportedMetreCentimetre, [], [new MapWaterRing([])]), 0, 1);
        var footprint = new MapSpaceFootprint("footprint", Space, new("floor", 0, 0), [0],
            new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.Ceiling, "ceiling", null));
        return fault switch
        {
            "seam-first" => (seam, seam with { First = null! }),
            "seam-second" => (seam, seam with { Second = null! }),
            "seam-pairs" => (seam, seam with { Pairs = null! }),
            "chain-vertices" => (chain, chain with { Vertices = null! }),
            "chain-vertex" => (chain, chain with { Vertices = [null!] }),
            "strip-lower" => (strip, strip with { LowerChain = null! }),
            "strip-upper" => (strip, strip with { UpperChain = null! }),
            "portal-from" => (portal, portal with { FromSpace = null! }),
            "portal-to" => (portal, portal with { ToSpace = null! }),
            "portal-interval" => (portal, portal with { Interval = null! }),
            "portal-band" => (portal, portal with { BandBottom = null! }),
            "vertical-upper" => (vertical, vertical with { UpperSpace = null! }),
            "vertical-lower" => (vertical, vertical with { LowerSpace = null! }),
            "vertical-openings" => (vertical, vertical with { Openings = null! }),
            "vertical-opening" => (vertical, vertical with { Openings = [null!] }),
            "vertical-portals" => (vertical, vertical with { Portals = null! }),
            "vertical-portal" => (vertical, vertical with { Portals = [null!] }),
            "vertical-owners" => (vertical, vertical with { GeometryOwners = null! }),
            "vertical-owner" => (vertical, vertical with { GeometryOwners = [null!] }),
            "space-tags" => (space, space with { DomainTags = null! }),
            "space-walls" => (space, space with { Walls = null! }),
            "space-wall" => (space, space with { Walls = [null!] }),
            "space-wall-record" => (space, space with { Walls = [wall with { Record = null! }] }),
            "space-portals" => (space, space with { Portals = null! }),
            "space-portal" => (space, space with { Portals = [null!] }),
            "space-portal-record" => (space, space with { Portals = [wall with { Record = null! }] }),
            "space-links" => (space, space with { Links = null! }),
            "space-link" => (space, space with { Links = [null!] }),
            "prism-space" => (prism, prism with { Space = null! }),
            "prism-parent" => (prism, prism with { Parent = null! }),
            "prism-footprint" => (prism, prism with { Footprint = null! }),
            "prism-rects" => (prism, prism with { Footprint = prism.Footprint with { Rects = null! } }),
            "prism-rings" => (prism, prism with { Footprint = prism.Footprint with { Rings = null! } }),
            "prism-ring" => (prism, prism with { Footprint = prism.Footprint with { Rings = [null!] } }),
            "prism-ring-vertices" => (prism, prism with { Footprint = prism.Footprint with { Rings = [new MapWaterRing(null!)] } }),
            "footprint-space" => (footprint, footprint with { Space = null! }),
            "footprint-cells" => (footprint, footprint with { SlotCells = null! }),
            "footprint-lower" => (footprint, footprint with { Lower = null! }),
            "footprint-upper" => (footprint, footprint with { Upper = null! }),
            _ => null,
        };
    }
}
