using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterFormatTests
{
    [Fact]
    public void FormatFive_MigratesToSix_WithNoWaterSection()
    {
        MapDocument doc = MapDocumentFile.LoadText(WaterFixtures.FormatFiveNativeJson());
        Assert.Equal(MapDocumentFile.CurrentFormatVersion, doc.FormatVersion);
        Assert.Null(doc.Water);
        Assert.DoesNotContain("\"water\"", MapDocumentFile.SaveText(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedWater_RoundTripsMonolithicAndTiled_ByteStable()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string text = MapDocumentFile.SaveText(doc);
        Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument tiled = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(doc.Water!.Digest(), tiled.Water!.Digest());
        Assert.Equal(MapDocumentHash.OfWorld(doc), MapDocumentHash.OfWorld(tiled));
    }

    [Fact]
    public void Rings_RoundTripExactly_MonolithicAndTiled()
    {
        MapDocument doc = WaterFixtures.WithRingBody(MapLatticeAddress.Create(-1, -1, 2), MapLatticeAddress.Create(704, 64, 64),
            MapLatticeAddress.Create(11, 3, 1), MapLatticeAddress.Create(1, 10, 3));
        MapWaterRing ring = doc.Water!.Bodies.Single(b => b.Id == "ring").Domain.Rings.Single();
        MapDocument loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(ring.Vertices, loaded.Water!.Bodies.Single(b => b.Id == "ring").Domain.Rings.Single().Vertices);
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        Assert.Equal(ring.Vertices, MapDocumentFile.LoadTiled(dir).Water!.Bodies.Single(b => b.Id == "ring").Domain.Rings.Single().Vertices);
    }

    [Theory]
    [InlineData("address-missing-d")]
    [InlineData("address-unknown-member")]
    [InlineData("address-duplicate-member")]
    [InlineData("address-leading-zero")]
    [InlineData("address-denominator-128")]
    public void LatticeAddressJson_RefusesBadMembers(string variant) =>
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(WaterFixtures.CorruptAddressJson(variant)));

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void SignedDecimal_RoundTripsTheSignedRange(long value)
    {
        Assert.True(MapSignedDecimal.TryParse(MapSignedDecimal.Format(value), out long back));
        Assert.Equal(value, back);
    }

    [Theory]
    [InlineData("-0")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("")]
    public void SignedDecimal_RefusesNonCanonicalText(string text) => Assert.False(MapSignedDecimal.TryParse(text, out _));

    [Fact]
    public void UnknownWaterMember_Refuses()
    {
        string text = MapDocumentFile.SaveText(WaterFixtures.OceanOverCaves())
            .Replace("\"surfaceY\":", "\"tide\":1,\"surfaceY\":", StringComparison.Ordinal);
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(text));
    }

    [Theory]
    [InlineData("legacy-document", new[] { "water", "resolver-2" })]
    [InlineData("native-resolver-one", new[] { "water", "resolver-2" })]
    [InlineData("duplicate-medium", new[] { "sea-water" })]
    [InlineData("zero-wade", new[] { "sea-water", "wadeSpeedScale", "0", "1" })]
    [InlineData("duplicate-body", new[] { "ocean" })]
    [InlineData("infinite-level", new[] { "ocean", "surfaceY", "1000000" })]
    [InlineData("tiny-level", new[] { "ocean", "surfaceY", "not representable" })]
    [InlineData("unknown-medium", new[] { "ocean", "brine" })]
    [InlineData("empty-material", new[] { "ocean", "renderMaterial" })]
    [InlineData("empty-domain", new[] { "ocean", "domain" })]
    [InlineData("empty-rect", new[] { "ocean", "rect", "0" })]
    [InlineData("far-rect", new[] { "ocean", "domain", "1000000" })]
    [InlineData("short-ring", new[] { "ocean", "ring", "0", "3" })]
    [InlineData("no-cover", new[] { "ocean" })]
    [InlineData("repeated-cover", new[] { "ocean", "sea" })]
    [InlineData("bow-tie", new[] { "ocean", "ring", "0" })]
    [InlineData("collinear", new[] { "ocean", "ring", "0" })]
    [InlineData("overlapping-pieces", new[] { "ocean", "rect", "0", "1" })]
    [InlineData("overlapping-ring", new[] { "ocean", "rect", "ring", "0" })]
    [InlineData("overlapping-bodies", new[] { "cave-lake", "second-lake", "lake-cave" })]
    [InlineData("too-many-pieces", new[] { "ocean", "domain", "65536" })]
    [InlineData("too-many-vertices", new[] { "ocean", "domain", "262144" })]
    public void Validation_RefusesInvalidWater(string variant, string[] diagnosticValues)
    {
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(WaterFixtures.Broken(variant), MapDocRegistry.CreateDefault());
        Assert.Contains(errors, error => diagnosticValues.All(value =>
            Regex.IsMatch(error, @"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])")));
    }

    [Fact]
    public void WaterSection_EntersTheWorldHashAndTheNativeRoot()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        (string world, string root) = (MapDocumentHash.OfWorld(doc), MapSurfaceSemantics.RootDigest(doc));
        doc.Water!.Bodies[0] = doc.Water.Bodies[0] with { SurfaceY = 0.25f };
        Assert.NotEqual(world, MapDocumentHash.OfWorld(doc));
        Assert.NotEqual(root, MapSurfaceSemantics.RootDigest(doc));
    }

    [Fact]
    public void WaterOrder_ChangesNeitherBytesNorHash()
    {
        MapDocument a = WaterFixtures.OceanOverCaves(), b = WaterFixtures.OceanOverCaves();
        b.Water!.Bodies.Reverse();
        b.Water.Media.Insert(0, new MapWaterMediumDoc("brine", MapWaterMediumKind.Water, 0.5f));
        a.Water!.Media.Add(new MapWaterMediumDoc("brine", MapWaterMediumKind.Water, 0.5f));
        Assert.Equal(MapDocumentFile.SaveText(a), MapDocumentFile.SaveText(b));
        Assert.Equal(MapDocumentHash.OfWorld(a), MapDocumentHash.OfWorld(b));
    }

    [Fact]
    public void PresentEmptySection_DiffersFromAbsent()
    {
        MapDocument absent = WaterFixtures.OceanOverCaves(), empty = WaterFixtures.OceanOverCaves();
        absent.Water = null;
        empty.Water = new MapWaterSet();
        Assert.NotEqual(MapDocumentHash.OfWorld(absent), MapDocumentHash.OfWorld(empty));
        Assert.Contains("\"water\"", MapDocumentFile.SaveText(empty), StringComparison.Ordinal);
    }
}
