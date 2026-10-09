using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class ResolverV2Tests
{
    // ResolverV2Tests
    [Fact]
    public void ResolverV2_ExplicitYIsUnchangedAndBindingsResolve()
    {
        MapDocument doc = SupportFixtures.CaveWithPlacements();
        MapSupportedResolution r = MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2);
        Assert.Equal(new[] { ("p-explicit", 12.25f), ("p-space", 4.0f), ("p-surface", 5.0f) },
            r.Document.Placements.Select(p => (p.PlacementId, p.Transform.Position.Y)).OrderBy(t => t.PlacementId, StringComparer.Ordinal));
    }
    [Fact]
    public void ResolverV2_AmbiguousBindingRefusesNamingThePlacement()
    {
        MapDocument doc = SupportFixtures.EqualSlabsWithBinding();
        string m = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2)).Message;
        Assert.Contains("p-amb", m);
        Assert.Contains("Ambiguous", m);
    }
    [Fact]
    public void ResolverV2_SpaceBindingWithoutBoundedSearchRefuses()
    {
        MapDocument doc = SupportFixtures.CaveWithPlacementsWithoutBoundedSearch();
        string m = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2)).Message;
        Assert.Contains("p-space", m);
        Assert.Contains("search", m);
    }
    [Fact]
    public void ResolverAdoption_RefusesUntilEveryMissingYPlacementIsConverted()
    {
        MapDocument v1 = MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);
        var onlyA = new Dictionary<string, MapPlacementSupportChoice> { ["a"] = new(2.5f, null) };
        Assert.Contains("'c'", Assert.Throws<MapDocumentException>(() => MapResolverAdoption.ConvertToAuthoredSupport(v1, onlyA)).Message);
        MapDocument window = MapDocumentFile.LoadTiled(FormatFourFixtures.CopyTiledToTemp(), new MapTileRect(new(0, 0), new(0, 0)));
        Assert.Contains("window", Assert.Throws<MapDocumentException>(() => MapResolverAdoption.ConvertToAuthoredSupport(window, onlyA)).Message);
        MapDocument adopted = MapResolverAdoption.ConvertToAuthoredSupport(v1, new Dictionary<string, MapPlacementSupportChoice> { ["a"] = new(2.5f, null), ["c"] = new(7.5f, null) });
        Assert.Equal((new MapResolverIdentityDoc(1, 2), MapSupportRecipe.AuthoredBindingsV2), (adopted.ResolverIdentity, adopted.SupportRecipe));
        Assert.Equal(new float?[] { 2.5f, 2.5f, 7.5f }, adopted.Placements.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Y));
        Assert.Equal(new MapResolverIdentityDoc(1, 1), v1.ResolverIdentity);
    }
}
