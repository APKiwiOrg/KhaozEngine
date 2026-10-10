using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FormatFourResolverExpectationTests
{
    [Fact]
    public void FormatFourFixtures_ReproduceTheRecordedReleasedResolver()
    {
        ResolverExpectations e = FormatFourFixtures.LoadExpectations();
        MapResolvedDocument r = FormatFourFixtures.ResolveRecording(MapDocumentFile.Load(FormatFourFixtures.MonolithicPath), out var calls);
        Assert.Equal(new[] { "a", "b", "c" }, r.Placements.Select(p => p.PlacementId));
        Assert.Equal(new[] { 2.5f, 2.5f, 7.5f }, r.Placements.Select(p => p.Transform.Position.Y));
        Assert.Equal(new[] { (-30f, 20f), (10f, 10f) }, calls);
        FormatFourFixtures.AssertMatchesExpectations(r, calls);
        Assert.NotEqual(e.AuthoredHashFormatFour, r.AuthoredHash);      // Task 6 flips only this line to NotEqual
    }

    [Fact]
    public void FormatFourFixtures_AreImmutable() =>
        Assert.Equal(FormatFourFixtures.PinnedFixtureDigest, FormatFourFixtures.ComputeFixtureDigest());

    [Fact]
    public void FormatFourTiledFixture_MatchesTheMonolithicWorld()
    {
        Assert.Empty(MapDocumentFile.VerifyTiled(FormatFourFixtures.TiledDirectory));
        MapDocument monolithic = MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);
        MapDocument tiled = MapDocumentFile.LoadTiled(FormatFourFixtures.TiledDirectory);
        // Tiled loading groups placements by tile. Compare all fields after normalizing only that list order.
        monolithic.Placements.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        tiled.Placements.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        Assert.Equal(MapDocumentFile.SaveText(monolithic), MapDocumentFile.SaveText(tiled));
    }
}
