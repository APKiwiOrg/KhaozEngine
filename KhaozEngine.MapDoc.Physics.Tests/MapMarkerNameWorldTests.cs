using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapMarkerNameWorldTests
{
    [Theory]
    [InlineData("disabled-child")]
    [InlineData("disabled-world")]
    public void DuplicateMarkerNames_FailBothHeadsBeforeStaticInstall(string variant)
    {
        MarkerFixture colliding = MarkerFixtures.DuplicateNames(variant);
        MarkerFixture renamed, removed;
        if (variant == "disabled-child")
        {
            var entry = colliding.Assets.GetPrefab(MarkerFixtures.DuplicateResource).Children.OfType<MapPrefabMarkerChild>().Single();
            renamed = MarkerFixtures.WithEntryOverride(colliding,
                new MapPrefabReplaceOverride(MarkerFixtures.EntryKey, entry with { Value = entry.Value with { Name = "arrival-child" } }));
            removed = MarkerFixtures.WithEntryOverride(colliding, new MapPrefabRemoveOverride(MarkerFixtures.EntryKey));
        }
        else
        {
            renamed = MarkerFixtures.WithWorldMarkers(colliding, MarkerFixtures.DuplicateWorldMarker(variant) with { Name = "arrival-world" });
            removed = MarkerFixtures.WithWorldMarkers(colliding);
        }
        MarkerFixture restored = variant == "disabled-child"
            ? MarkerFixtures.WithEntryOverride(renamed, null)
            : MarkerFixtures.WithWorldMarkers(renamed, MarkerFixtures.DuplicateWorldMarker(variant));

        foreach (string _ in new[] { "client", "server" })
        {
            Refuses(colliding);
            Assert.Equal(new[] { "a/entry", "w" }, Build(renamed).Markers.Select(m => m.Id));
            Assert.Single(Build(removed).Markers);
            Refuses(restored);
        }
    }

    // Each head loads its own copy of the document, as an independent client and server would.
    static void Refuses(MarkerFixture fixture)
    {
        string text = MapDocumentFile.SaveText(fixture.Document);
        Assert.Throws<MapDocumentException>(() => MapWorldBuilder.Build(MapDocumentFile.LoadText(text), fixture.Assets, Options()));
    }

    static MapBuiltWorld Build(MarkerFixture fixture) =>
        MapWorldBuilder.Build(MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document)), fixture.Assets, Options());

    static MapWorldBuildOptions Options() => new(MarkerFixtures.Options(), "public-marker-names/1", new());
}
