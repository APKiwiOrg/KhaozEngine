using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TopologyRecordTests
{
    static readonly MapPatchKey Floor00 = new("floor", 0, 0), Ceiling00 = new("ceiling", 0, 0);

    [Fact]
    public void Records_RoundTripThroughTheCodecAndDigestDeterministically()
    {
        MapSurfacePatch p = TopologyRecordFixtures.SampleWithRecords();
        string digest = MapSurfaceSemantics.PatchDigest(p);
        Assert.Equal(digest, MapSurfaceSemantics.PatchDigest(MapSurfacePatchCodec.Decode(MapSurfacePatchCodec.Encode(p), p.Key)));
        p.Records.Reverse();
        Assert.Equal(digest, MapSurfaceSemantics.PatchDigest(p));
        p.Heights[2] = 1434;
        Assert.NotEqual(digest, MapSurfaceSemantics.PatchDigest(p));
    }
    [Fact]
    public void ReferenceValidator_RefusesDuplicateSideDanglingAndWrongAnchor()
    {
        TopologyWorld w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        Assert.Empty(w.Validate());
        TopologyRecordFixtures.ReplaceWall(w, "b", new(new("wall", Floor00), MapSide.Front));
        Assert.Contains(w.Validate(), f => f.Contains("duplicate"));
        TopologyRecordFixtures.ReplaceWall(w, "b", new(new("missing-wall", Floor00), MapSide.Back));
        Assert.Contains(w.Validate(), f => f.Contains("missing-wall"));
        TopologyRecordFixtures.ReplaceWall(w, "b", new(new("wall", Ceiling00), MapSide.Back));
        Assert.Contains(w.Validate(), f => f.Contains("anchor"));
    }
    [Fact]
    public void ReferenceValidator_RefusesOpenTopOnACaveAndAPresentOpeningButAcceptsAnyRationalBound()
    {
        TopologyWorld w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        TopologyRecordFixtures.SetUpper(w, "fragment-a", new(MapBoundKind.OpenTop, null, null));
        Assert.Contains(w.Validate(), f => f.Contains("open top"));
        w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        TopologyRecordFixtures.OpeningOverPresentCell(w);
        Assert.Contains(w.Validate(), f => f.Contains("present"));
        foreach (MapRational unit in new MapRational[] { new(1, 2), new(1, 3), new(2, 3), new(1, 64), new(5, 7) })
        {
            w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
            TopologyRecordFixtures.SetLowerSurface(w, "fragment-a", "other", new MapLatticeFrame(unit, new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0));
            Assert.Empty(w.Validate());                                  // D7: any rational lattice is a legal bound reference
        }
    }
    [Fact]
    public void ReferenceValidator_AcceptsTheLegacyExteriorTagOnlyInItsExactRecipe()
    {
        Assert.Empty(TopologyRecordFixtures.LegacyExteriorWorld().Validate());
        TopologyWorld cave = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        TopologyRecordFixtures.SetLowerKind(cave, "fragment-a", MapBoundKind.LegacyExteriorV1);
        Assert.Contains(cave.Validate(), f => f.Contains("legacy recipe: space") && f.Contains("fragment-a"));        // never a finite cave floor
        TopologyWorld upper = TopologyRecordFixtures.LegacyExteriorWorld();
        TopologyRecordFixtures.SetUpper(upper, "world-cells", new(MapBoundKind.LegacyExteriorV1, "plane-0", null));
        Assert.Contains(upper.Validate(), f => f.Contains("legacy recipe: upper"));
        TopologyWorld native = TopologyRecordFixtures.LegacyExteriorWorld();
        TopologyRecordFixtures.SetLowerSurface(native, "world-cells", "native", new MapLatticeFrame(new(1, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0));
        Assert.Contains(native.Validate(), f => f.Contains("legacy recipe: surface"));
        Assert.Equal(("kemap/legacy-exterior/1", (byte)4), (MapLegacyExteriorRecipe.PolicyId, (byte)MapBoundKind.LegacyExteriorV1));
    }
    [Fact]
    public void Codec_RefusesARecordSetOverTheBound()
    {
        MapSurfacePatch p = SurfacePatchFixtures.Row(1);
        for (int i = 0; i < 40; i++)
            p.Records.Add(new MapBoundaryChain($"c{i}", MapChainKind.Authored, null,
                Enumerable.Range(0, 4097).Select(x => new MapChainVertex(new MapLatticeVertex("r", MapLatticeAddress.Corner(x, 0)), 0)).ToList()));
        Assert.Contains("1048576", Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(p)).Message);
    }

    [Fact]
    public void PatchClone_PreservesRecordsAndIsolatesNestedAuthoredCollections()
    {
        MapSurfacePatch patch = TopologyRecordFixtures.SampleWithRecords();
        MapSurfacePatch clone = patch.Clone();
        Assert.Equal(MapSurfaceSemantics.PatchDigest(patch), MapSurfaceSemantics.PatchDigest(clone));
        var original = Assert.IsType<MapBoundaryChain>(patch.Records.Single(r => r.Id == "upper"));
        var copied = Assert.IsType<MapBoundaryChain>(clone.Records.Single(r => r.Id == "upper"));
        // This fixture deliberately supplied a mutable array through the read-only interface.
        MapChainVertex[] supplied = Assert.IsType<MapChainVertex[]>(original.Vertices);
        supplied[0] = supplied[0] with { HeightUnits = 301 };
        Assert.Equal(300, copied.Vertices[0].HeightUnits);
        Assert.NotEqual(MapSurfaceSemantics.PatchDigest(patch), MapSurfaceSemantics.PatchDigest(clone));
        clone.Records.Clear();
        Assert.Equal(3, patch.Records.Count);
    }

    [Fact]
    public void AllRecordDiscriminatorsRoundTripWithReferenceIntegrity()
    {
        TopologyWorld world = TopologyRecordFixtures.WithEveryRecordKind();
        Assert.Empty(world.Validate());
        MapSurfacePatch floor = world.Patches.Single(p => p.Key == Floor00);
        byte[] bytes = MapSurfacePatchCodec.Encode(floor);
        MapSurfacePatch decoded = MapSurfacePatchCodec.Decode(bytes, Floor00);
        Assert.Equal(bytes, MapSurfacePatchCodec.Encode(decoded));
        Assert.Equal(8, decoded.Records.Select(r => r.GetType()).Distinct().Count());
        world.Patches[world.Patches.IndexOf(floor)] = decoded;
        Assert.Empty(world.Validate());
        int aIndex = decoded.Records.FindIndex(r => r.Id == "a");
        var a = Assert.IsType<MapSpaceDoc>(decoded.Records[aIndex]);
        decoded.Records[aIndex] = a with { Portals = Array.Empty<MapBoundaryRef>(), Links = Array.Empty<MapRecordRef>() };
        Assert.Contains(world.Validate(), f => f.Contains("portal list"));
        Assert.Contains(world.Validate(), f => f.Contains("link list"));
    }

    [Fact]
    public void ReferenceValidatorRefusesParentCyclesAliasChainsWrongTypesAndDuplicateIds()
    {
        TopologyWorld world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        MapSurfacePatch floor = world.Patches.Single(p => p.Key == Floor00);
        int ai = floor.Records.FindIndex(r => r.Id == "a"), bi = floor.Records.FindIndex(r => r.Id == "b");
        var a = Assert.IsType<MapSpaceDoc>(floor.Records[ai]);
        var b = Assert.IsType<MapSpaceDoc>(floor.Records[bi]);
        floor.Records[ai] = a with { Parent = new("b", Floor00) };
        floor.Records[bi] = b with { Parent = new("a", Floor00) };
        Assert.Contains(world.Validate(), f => f.Contains("cycle"));
        floor.Records[ai] = a with { AliasOf = new("b", Floor00) };
        floor.Records[bi] = b with { AliasOf = new("a", Floor00) };
        Assert.Contains(world.Validate(), f => f.Contains("alias"));
        floor.Records[ai] = a;
        floor.Records[bi] = b;
        int wi = floor.Records.FindIndex(r => r.Id == "wall");
        var wall = Assert.IsType<MapWallStrip>(floor.Records[wi]);
        floor.Records[wi] = wall with { LowerChain = new("a", Floor00) };
        Assert.Contains(world.Validate(), f => f.Contains("type"));
        floor.Records[wi] = wall;
        floor.Records.Add(a);
        Assert.Contains(world.Validate(), f => f.Contains("duplicate id"));
    }

    [Fact]
    public void SurfaceDigestExcludesItsOwnHashAndCanonicalizesPatchOrder()
    {
        TopologyWorld world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        MapSurfaceRef surface = world.Surfaces.Single(s => s.Id == "floor");
        MapSurfacePatch floor = world.Patches.Single(p => p.Key == Floor00);
        var first = new KeyValuePair<MapPatchKey, string>(Floor00, MapSurfaceSemantics.PatchDigest(floor));
        var second = new KeyValuePair<MapPatchKey, string>(new("floor", -1, 0), new string('a', 64));
        string digest = MapSurfaceSemantics.SurfaceDigest(surface, new[] { first, second });
        Assert.Equal(digest, MapSurfaceSemantics.SurfaceDigest(surface with { SemanticSha256 = "ignored" }, new[] { second, first }));
        Assert.NotEqual(digest, MapSurfaceSemantics.SurfaceDigest(surface with { Role = MapSurfaceRole.Ceiling }, new[] { first, second }));
        Assert.Throws<MapDocumentException>(() => MapSurfaceSemantics.SurfaceDigest(surface, new[] { first, first }));
        Assert.Throws<MapDocumentException>(() => MapSurfaceSemantics.SurfaceDigest(surface, new[]
        {
            new KeyValuePair<MapPatchKey, string>(Ceiling00, first.Value),
        }));
    }

    [Theory]
    [InlineData("missing", "missing")]
    [InlineData("anchor", "anchor")]
    [InlineData("type", "type")]
    public void ReferenceValidator_RefusesInvalidIndoorParent(string fault, string diagnostic)
    {
        TopologyWorld world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        int index = world.Surfaces.FindIndex(s => s.Id == "floor");
        MapSurfaceRef surface = world.Surfaces[index];
        var span = new MapIndoorSpan("indoor", new("a", Floor00), 0, 300, Array.Empty<string>());
        world.Surfaces[index] = surface with { IndoorSpan = span };
        Assert.Empty(world.Validate());
        MapRecordRef parent = fault switch
        {
            "missing" => new("missing-space", Floor00),
            "anchor" => new("a", Ceiling00),
            _ => new("wall", Floor00),
        };
        world.Surfaces[index] = surface with { IndoorSpan = span with { ParentSpace = parent } };
        Assert.Contains(world.Validate(), finding => finding.Contains(diagnostic));
    }

    [Theory]
    [InlineData("pairs")]
    [InlineData("domainTags")]
    [InlineData("openings")]
    [InlineData("linkPortals")]
    [InlineData("geometryOwners")]
    [InlineData("walls")]
    [InlineData("spacePortals")]
    [InlineData("links")]
    [InlineData("interval")]
    public void Codec_RefusesOversizedListsBeforeEnumeration(string field)
    {
        MapSurfacePatch patch = SurfacePatchFixtures.Row(1);
        var reference = new MapRecordRef("target", patch.Key);
        var vertex = new MapLatticeVertex("r", MapLatticeAddress.Corner(0, 0));
        var edge = new MapSurfaceEdgeRef(patch.Key, vertex, vertex);
        MapTopologyRecord record = field switch
        {
            "pairs" => new MapSurfaceSeam("large", edge, edge,
                new UnenumerableList<(MapLatticeVertex First, MapLatticeVertex Second)>()),
            "interval" => new MapCavePortal("large", reference, reference,
                new UnenumerableList<MapLatticeVertex>(), reference, null),
            "domainTags" or "walls" or "spacePortals" or "links" => new MapSpaceDoc("large", MapSpaceKind.Exterior,
                null, null, field == "domainTags" ? new UnenumerableList<string>() : Array.Empty<string>(),
                field == "walls" ? new UnenumerableList<MapBoundaryRef>() : Array.Empty<MapBoundaryRef>(),
                field == "spacePortals" ? new UnenumerableList<MapBoundaryRef>() : Array.Empty<MapBoundaryRef>(),
                field == "links" ? new UnenumerableList<MapRecordRef>() : Array.Empty<MapRecordRef>()),
            _ => new MapVerticalLink("large", reference, reference,
                field == "openings" ? new UnenumerableList<MapRecordRef>() : Array.Empty<MapRecordRef>(),
                field == "linkPortals" ? new UnenumerableList<MapRecordRef>() : Array.Empty<MapRecordRef>(),
                field == "geometryOwners" ? new UnenumerableList<MapRecordRef>() : Array.Empty<MapRecordRef>()),
        };
        patch.Records.Add(record);
        Assert.Contains("1048576", Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch)).Message);
    }

    sealed class UnenumerableList<T> : IReadOnlyList<T>
    {
        public int Count => int.MaxValue;
        public T this[int index] => throw new InvalidOperationException("collection read before budget refusal");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("collection enumerated before budget refusal");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
