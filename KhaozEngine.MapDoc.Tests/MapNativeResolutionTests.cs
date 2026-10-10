using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapNativeResolutionTests
{
    [Fact]
    public void ResolverOne_MatchesTheAnalyticResolver()
    {
        var f = NativeResolverFixtures.Create();
        f.Document.Placements[0].Y = null;
        MapResolvedDocument expected = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 7f, f.Options);
        MapResolvedDocument actual = MapNativeResolution.Resolve(f.Document, f.Assets, f.Options, (_, _) => 7f);
        Assert.Equal(expected.AuthoredHash, actual.AuthoredHash);
        Assert.Equal(expected.Placements.Select(p => (p.PlacementId, p.Transform)),
            actual.Placements.Select(p => (p.PlacementId, p.Transform)));
    }

    [Fact]
    public void ResolverTwo_MatchesTheAuthoredBindingResolver()
    {
        MapDocument document = SupportFixtures.CaveWithPlacements();
        MapResolvedDocument expected = MapResolverV2.Resolve(document, SupportFixtures.Assets(),
            MapDocumentSurfaceSource.Capture(document), SupportFixtures.V2).Document;
        MapResolvedDocument actual = MapNativeResolution.Resolve(document, SupportFixtures.Assets(), SupportFixtures.V2);
        Assert.Equal(expected.AuthoredHash, actual.AuthoredHash);
        Assert.Equal(expected.Placements.Select(p => (p.PlacementId, p.Transform)),
            actual.Placements.Select(p => (p.PlacementId, p.Transform)));
    }

    [Fact]
    public void ResolverTwoWithResolverOneOptions_Refuses()
    {
        MapDocument document = SupportFixtures.CaveWithPlacements();
        var options = SupportFixtures.V2 with { ResolverVersion = 1 };
        Assert.Contains("resolver", Assert.Throws<MapDocumentException>(() =>
            MapNativeResolution.Resolve(document, SupportFixtures.Assets(), options, (_, _) => 0f)).Message);
    }
}
