using System;
using System.Collections.Generic;
using System.Linq;
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
        Assert.Equal(6, doc.FormatVersion);
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
    [InlineData("legacy-document", "water: bounded water requires a resolver-2 native document")]
    [InlineData("native-resolver-one", "water: bounded water requires a resolver-2 native document")]
    [InlineData("duplicate-medium", "water medium 'sea-water' is duplicated")]
    [InlineData("zero-wade", "water medium 'sea-water': wadeSpeedScale must be in (0, 1]")]
    [InlineData("duplicate-body", "water body 'ocean' is duplicated")]
    [InlineData("infinite-level", "water body 'ocean': surfaceY must be finite within 1000000 m")]
    [InlineData("tiny-level", "water body 'ocean': surfaceY is not representable")]
    [InlineData("unknown-medium", "water body 'ocean': unknown medium 'brine'")]
    [InlineData("empty-material", "water body 'ocean': renderMaterial must be non-empty")]
    [InlineData("empty-domain", "water body 'ocean': domain is empty")]
    [InlineData("empty-rect", "water body 'ocean': rect 0 is empty")]
    [InlineData("far-rect", "water body 'ocean': domain exceeds 1000000 m")]
    [InlineData("short-ring", "water body 'ocean': ring 0 needs at least 3 vertices")]
    [InlineData("no-cover", "water body 'ocean': covers no space")]
    [InlineData("repeated-cover", "water body 'ocean': cover 'sea' is repeated")]
    public void Validation_RefusesBadMediaAndBodies(string variant, string message)
    {
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(WaterFixtures.Broken(variant), MapDocRegistry.CreateDefault());
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
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
