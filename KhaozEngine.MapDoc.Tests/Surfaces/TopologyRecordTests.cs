using System;
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
}
