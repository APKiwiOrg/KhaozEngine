using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FinePatchConversionTests
{
    static MapConversionResult Convert(MapSurfaceSet set, MapFinePatchRequest r) => MapFinePatchConversion.Convert(set, r);
    static Dictionary<MapPatchKey, string> Digests(MapSurfaceSet set) => set.Patches.ToDictionary(p => p.Key, p => MapSurfaceSemantics.PatchDigest(p.Value));

    [Fact]
    public void Conversion_HalvesPreserveDiagonalTrianglesPaintAndFlagsExactly()
    {
        MapConversionResult r = Convert(ConversionFixtures.Slopes(), ConversionFixtures.Request(2, false));
        Assert.Equal(MapCellConversionClass.ExactDiagonal, r.Cells.Single().Class);
        MapSurfacePatch fine = ConversionFixtures.Fine(r);
        Assert.Equal(new[] { 320, 410, 500, 370, 460, 580, 420, 540, 660 }, ConversionFixtures.FineCorners(fine));
        Assert.Equal((MapCellTopology.ForceNwSe, MapCellTopology.ForceNwSe), (ConversionFixtures.FineCell(fine, 1, 0).Topology, ConversionFixtures.FineCell(fine, 0, 1).Topology));
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 2; x++)
            {
                MapSurfaceCell c = ConversionFixtures.FineCell(fine, x, z);
                Assert.Equal(((ushort)7, MapOverlayCut.Full, MapCellFlags.FeatherOverlay | MapCellFlags.Blocked), (c.Overlay, c.Cut, c.Flags));
            }
        Assert.False(r.Candidate.Patches[new("coarse", 0, 0)].IsPresent(1, 1));
        Assert.Equal((new MapRational(1, 2), new MapRational(1, 200)), (ConversionFixtures.FineSurface(r).Frame.CellUnitMetres, ConversionFixtures.FineSurface(r).Frame.HeightUnitMetres));
    }

    [Theory, InlineData(MapOverlayCut.Full, (byte)0), InlineData(MapOverlayCut.DiagonalHalf, (byte)1)]
    public void Conversion_ThirdsAreExactWithForcedDiagonalsAndHalfPaint(MapOverlayCut cut, byte rotation)
    {
        MapConversionResult r = Convert(ConversionFixtures.Slopes(cut, rotation), ConversionFixtures.Request(3, false));
        Assert.Equal(MapCellConversionClass.ExactDiagonal, r.Cells.Single().Class);
        MapSurfacePatch fine = ConversionFixtures.Fine(r);
        Assert.Equal(new[] { 480, 570, 660, 750, 530, 620, 710, 830, 580, 670, 790, 910, 630, 750, 870, 990 }, ConversionFixtures.FineCorners(fine));
        foreach (var (x, z) in new[] { (0, 2), (1, 1), (2, 0) }) Assert.Equal(MapCellTopology.ForceNwSe, ConversionFixtures.FineCell(fine, x, z).Topology);
        if (cut == MapOverlayCut.DiagonalHalf)
        {
            MapSurfaceCell centre = ConversionFixtures.FineCell(fine, 1, 1);
            Assert.Equal((MapOverlayCut.DiagonalHalf, (byte)1, (ushort)7), (centre.Cut, centre.Rotation, centre.Overlay));
            Assert.Equal(((ushort)0, (ushort)7), (ConversionFixtures.FineCell(fine, 0, 0).Overlay, ConversionFixtures.FineCell(fine, 2, 2).Overlay));
        }
        Assert.Equal(ConversionFixtures.OverlayArea(ConversionFixtures.Slopes(cut, rotation)), ConversionFixtures.OverlayArea(r.Candidate));
    }

    [Theory, InlineData(2, 8), InlineData(3, 12)]
    public void Conversion_SubdividesRimEdgesWithStableOwners(int k, int dependencies)
    {
        MapConversionResult r = Convert(ConversionFixtures.Slopes(), ConversionFixtures.Request(k, false));
        MapSurfacePatch coarse = r.Candidate.Patches[new("coarse", 0, 0)];
        Assert.Equal(new[] { new MapEdgeSubdivision(1, 0, MapCellEdge.North, k), new(0, 1, MapCellEdge.East, k), new(2, 1, MapCellEdge.West, k), new(1, 2, MapCellEdge.South, k) }.ToHashSet(),
            coarse.EdgeSubdivisions.ToHashSet());
        MapSurfacePatch fine = ConversionFixtures.Fine(r);
        Assert.Equal(dependencies, fine.CornerDependencies.Count);                    // every boundary vertex, 4k
        Assert.All(fine.CornerDependencies, d => Assert.Equal(new MapPatchKey("coarse", 0, 0), d.Owner.Patch));
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(r.Candidate);
        var seams = r.Candidate.AllRecords().OfType<MapSurfaceSeam>().ToList();
        Assert.Equal(4, seams.Count);
        Assert.All(seams, s => Assert.Empty(MapSeamValidator.Validate(s, view)));
        Assert.All(r.Candidate.Patches.Values, p => Assert.Empty(p.ValidateLocal()));
    }

    [Fact]
    public void Conversion_OddSubdivisionNextToACornerCutRimMatchesBothSequences()
    {
        MapConversionResult r = Convert(ConversionFixtures.Slopes(southCut: MapOverlayCut.CornerQuarter, southRotation: 2), ConversionFixtures.Request(3, false));
        Assert.Contains(new MapEdgeSubdivision(1, 0, MapCellEdge.South, 2), ConversionFixtures.Fine(r).EdgeSubdivisions);
        MapSurfaceSeam south = ConversionFixtures.RimSeam(r, MapCellEdge.South);
        Assert.Equal(new[] { new MapExactValue(0, 1), new(1, 3), new(1, 2), new(2, 3), new(1, 1) }, south.Pairs.Select(p => ConversionFixtures.FractionAlongCoarseEdge(r, p.First)));
        Assert.Empty(MapSeamValidator.Validate(south, MapScopedSurfaces.CompleteView(r.Candidate)));
        Assert.Equal(new[] { (1L, 0L), (0L, 1L), (2L, 1L), (1L, 2L) }.ToHashSet(),
            r.Differences.Where(d => d.Kind == MapDifferenceKind.Retessellated).Select(d => (d.CellX, d.CellZ)).ToHashSet());
        Assert.DoesNotContain(r.Differences, d => d.Kind is MapDifferenceKind.Geometry or MapDifferenceKind.Paint);
    }

    [Fact]
    public void Conversion_SixtyFourthsStayExact()
    {
        MapSurfaceSet before = ConversionFixtures.Slopes();
        MapConversionResult r = Convert(before, ConversionFixtures.Request(64, false));
        MapSurfacePatch fine = ConversionFixtures.Fine(r);
        IReadOnlyList<int> corners = ConversionFixtures.FineCorners(fine);
        Assert.Equal((65 * 65, 160 * 64, 330 * 64), (corners.Count, corners[0], corners[^1]));
        for (int z = 0; z <= 8; z++)
            for (int x = 0; x <= 8; x++)
                Assert.Equal(ConversionFixtures.ExactHeight(before, new(8 + x, 8), new(8 + z, 8)), ConversionFixtures.ExactHeight(r.Candidate, new(8 + x, 8), new(8 + z, 8)));
        Assert.Equal(ConversionFixtures.OverlayArea(before), ConversionFixtures.OverlayArea(r.Candidate));
        Assert.True(MapSurfacePatchCodec.Encode(fine).Length < 1_048_576);
    }

    [Theory, InlineData(2), InlineData(3)]
    public void Conversion_NoDriftOnAnyExactSamplePoint(int k)
    {
        MapSurfaceSet before = ConversionFixtures.Slopes();
        MapConversionResult r = Convert(before, ConversionFixtures.Request(k, false));
        for (int z = 0; z <= 24; z++)
            for (int x = 0; x <= 24; x++)
                Assert.Equal(ConversionFixtures.ExactHeight(before, new(x, 8), new(z, 8)), ConversionFixtures.ExactHeight(r.Candidate, new(x, 8), new(z, 8)));
        Assert.Equal(ConversionFixtures.OverlayArea(before), ConversionFixtures.OverlayArea(r.Candidate));
        MapSurfacePatch fine = ConversionFixtures.Fine(r);
        Assert.Equal(MapSurfaceSemantics.PatchDigest(fine), MapSurfaceSemantics.PatchDigest(MapSurfacePatchCodec.Decode(MapSurfacePatchCodec.Encode(fine), fine.Key)));
    }

    [Fact]
    public void Conversion_NonCoplanarCornerCutIsUnsupportedEncodingUnlessAccepted()
    {
        MapSurfaceSet input = ConversionFixtures.Slopes(MapOverlayCut.CornerQuarter);
        string message = Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.Request(2, false))).Message;
        Assert.Contains("authored difference", message);
        Assert.Contains("cell (1, 1)", message);
        MapCellConversion c = MapConversionClassifier.Classify(ConversionFixtures.Coarse(input).Surface, ConversionFixtures.Coarse(input).Patch, 1, 1, 2);
        Assert.Equal(MapCellConversionClass.UnsupportedEncoding, c.Class);
        Assert.Contains("crease", c.Reason);
        MapConversionResult accepted = Convert(input, ConversionFixtures.Request(2, true));
        Assert.Equal((4, 8), (accepted.Differences.Count(d => d.Kind == MapDifferenceKind.Geometry && d.OldFace is not null),
                              accepted.Differences.Count(d => d.Kind == MapDifferenceKind.Geometry && d.NewFace is not null)));
    }

    [Theory, InlineData(2, MapCellConversionClass.ExactCoplanar), InlineData(3, MapCellConversionClass.UnsupportedEncoding)]
    public void Conversion_CoplanarCornerCutPaintNeedsAnEvenSubdivision(int k, MapCellConversionClass expected)
    {
        var coarse = ConversionFixtures.Coarse(ConversionFixtures.Slopes(MapOverlayCut.CornerQuarter, coplanarCentre: true));
        MapCellConversion c = MapConversionClassifier.Classify(coarse.Surface, coarse.Patch, 1, 1, k);
        Assert.Equal(expected, c.Class);
        if (k % 2 == 1) Assert.Contains("paint", c.Reason);
    }

    [Fact]
    public void Conversion_LegacyFallbackIsNotRepresentableAndArithmeticIsAnExplicitDifference()
    {
        MapSurfaceSet input = ConversionFixtures.LegacyRowWithNoDraw();
        Assert.Contains("authored difference", Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.LegacyRequest(2, false))).Message);
        var legacy = ConversionFixtures.Legacy(input);
        MapCellConversion c = MapConversionClassifier.Classify(legacy.Surface, legacy.Patch, 1, 0, 2);
        Assert.Equal(MapCellConversionClass.NotRepresentable, c.Class);
        Assert.Contains("legacy fallback", c.Reason);
        MapConversionResult r = Convert(input, ConversionFixtures.LegacyRequest(2, true));
        Assert.Contains(r.Differences, d => d.Kind == MapDifferenceKind.FallbackRemoved && d.CellX == 1);
        Assert.Contains(r.Differences, d => d.Kind == MapDifferenceKind.FlagRemoved && d.CellX == 1);
        Assert.Contains(r.Differences, d => d.Kind == MapDifferenceKind.ArithmeticPolicy && d.CellX == 0 && d.Detail.Contains("max delta"));
        MapSpaceFootprint world = r.Candidate.AllRecords().OfType<MapSpaceFootprint>().Single(f => f.Id == "world-cells");
        Assert.Equal((MapBoundKind.SupportFloor, "plane-0-fine-1"), (world.Lower.Kind, world.Lower.SurfaceId));   // fallback became physical faces, so the tag is gone
        Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
    }

    [Fact]
    public void Conversion_RetargetsOnlyTheConvertedFloorAndKeepsTheCoarseCeiling()
    {
        MapSurfaceSet input = ConversionFixtures.WithCaveCeiling();
        string roof = MapSurfaceSemantics.PatchDigest(input.Patches[new("roof", 0, 0)]);
        MapConversionResult r = Convert(input, ConversionFixtures.Request(3, false));
        MapSpaceFootprint room = r.Candidate.AllRecords().OfType<MapSpaceFootprint>().Single(f => f.Id == "room-cells");
        Assert.Equal(("coarse-fine-1", "roof"), (room.Lower.SurfaceId, room.Upper.SurfaceId));
        Assert.Equal(roof, MapSurfaceSemantics.PatchDigest(r.Candidate.Patches[new("roof", 0, 0)]));
        Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
    }

    [Fact]
    public void Conversion_OnAMixedFootprintRetargetsOnlyTheConvertedBound()
    {
        MapSurfaceSet input = ConversionFixtures.MixedPorch();
        // D4 retargets a fully moved footprint in place, and porch-cells is stored in half-floor, so the only
        // permitted change to that patch is the upper bound of that one record.
        MapSurfacePatch expectedFloor = input.Patches[new("half-floor", 0, 0)].Clone();
        int porchIndex = expectedFloor.Records.FindIndex(x => x.Id == "porch-cells");
        var originalPorch = (MapSpaceFootprint)expectedFloor.Records[porchIndex];
        expectedFloor.Records[porchIndex] = originalPorch with { Upper = originalPorch.Upper with { SurfaceId = "third-roof-fine-1" } };
        MapConversionResult r = Convert(input, ConversionFixtures.PorchRoofRequest(width: 3));
        MapSpaceFootprint porch = r.Candidate.AllRecords().OfType<MapSpaceFootprint>().Single(f => f.Id == "porch-cells");
        Assert.Equal(("half-floor", "third-roof-fine-1"), (porch.Lower.SurfaceId, porch.Upper.SurfaceId));
        Assert.Equal(MapSurfaceSemantics.PatchDigest(expectedFloor), MapSurfaceSemantics.PatchDigest(r.Candidate.Patches[new("half-floor", 0, 0)]));
        Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
    }

    [Fact]
    public void Conversion_RefusesARegionThatSplitsAFootprintCellBeforeMutating()
    {
        MapSurfaceSet input = ConversionFixtures.MixedPorch();
        var digests = Digests(input);
        string message = Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.PorchRoofRequest(width: 2))).Message;  // x = 2/3 m crosses [1/2, 1)
        Assert.Contains("footprint straddle: footprint 'porch-cells' cell 1 crosses the conversion region boundary.", message);   // cells 1 and 65 straddle, 1 is first
        Assert.Contains("Align the region to whole footprint cells or split the footprint first.", message);
        Assert.Equal(digests, Digests(input));
    }

    [Fact]
    public void Conversion_RefusesARecordOnAConvertedEdgeBeforeMutating()
    {
        MapSurfaceSet input = ConversionFixtures.WithWallOnTheCentreEdge();
        var digests = Digests(input);
        Assert.Contains("west-foot", Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.Request(2, false))).Message);
        Assert.Equal(digests, Digests(input));
    }
}
