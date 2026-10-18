using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerAdoptionTests
{
    [Fact]
    public void SameSourceIdInTwoLists_RequiresDistinctExplicitDestinations()
    {
        MarkerFixture fixture = MarkerFixtures.LegacyLists();
        MapDocument original = fixture.Document;
        string originalBytes = MapDocumentFile.SaveText(original);
        MapDocument adopted = MapMarkerAdoption.ConvertSpawns(original, MarkerFixtures.Choices());
        var rows = MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture with { Document = adopted }));
        var actor = rows.Single(m => m.Id == "actor-new");
        var player = rows.Single(m => m.Id == "start-new");
        var actorDoc = adopted.BuildingIntent!.Markers.Single(m => m.Id == "actor-new");
        var playerDoc = adopted.BuildingIntent.Markers.Single(m => m.Id == "start-new");
        var choices = MarkerFixtures.Choices().ToDictionary();
        var key = new MapLegacySpawnKey(MapLegacySpawnKind.Spawn, "old");
        choices[key] = choices[key] with { YawRadians = 1.25f };
        var yawDocument = MapMarkerAdoption.ConvertSpawns(original, choices);
        var actorWithYaw = MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture with { Document = yawDocument })).Single(m => m.Id == "actor-new");

        Assert.Equal(2, original.Spawns.Count + original.PlayerSpawns.Count);
        Assert.Empty(adopted.Spawns);
        Assert.Empty(adopted.PlayerSpawns);
        Assert.Equal("kit.actor", actor.ArchetypeId);
        Assert.False(actor.Enabled);
        Assert.Equal(new[] { "second", "first" }, actor.Tags);
        Assert.Equal(0f, actor.YawRadians);
        Assert.Equal(1.25f, actorWithYaw.YawRadians);
        Assert.Equal(0.371f, player.YawRadians);
        Assert.Equal(MapMarkerHeightMode.Explicit, actorDoc.HeightMode);
        Assert.Equal(12.25f, actor.Position.Y.ToSingle());
        Assert.Equal(MapMarkerHeightMode.Support, playerDoc.HeightMode);
        Assert.Equal(0f, playerDoc.Position.Y);
        Assert.Equal(20f, player.Position.Y.ToSingle());
        Assert.Equal("actor-home", actor.Name);
        Assert.Equal("actor-role", actor.Role);
        Assert.Equal("player-home", player.Name);
        Assert.Equal("start-role", player.Role);
        Assert.Equal(-0.25f, player.Position.X.ToSingle());
        Assert.Equal(-1.5f, player.Position.Z.ToSingle());
        Assert.Equal(originalBytes, MapDocumentFile.SaveText(original));
        original.Spawns[0].Tags[0] = "source-edited";
        Assert.Equal(new[] { "second", "first" }, actorDoc.Tags);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("destination")]
    [InlineData("name")]
    [InlineData("live-id")]
    [InlineData("live-name")]
    [InlineData("reservation")]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("bad-yaw")]
    public void InvalidChoices_RefuseWithoutMutation(string variant)
    {
        var fixture = MarkerFixtures.LegacyLists();
        var choices = MarkerFixtures.Choices().ToDictionary();
        var actor = new MapLegacySpawnKey(MapLegacySpawnKind.Spawn, "old");
        var player = new MapLegacySpawnKey(MapLegacySpawnKind.PlayerSpawn, "old");
        switch (variant)
        {
            case "missing": choices.Remove(player); break;
            case "extra": choices.Add(new(MapLegacySpawnKind.Spawn, "extra"), choices[player] with { MarkerId = "extra" }); break;
            case "destination": choices[player] = choices[player] with { MarkerId = "actor-new" }; break;
            case "name": choices[player] = choices[player] with { Name = "actor-home" }; break;
            case "live-id": fixture.Document.BuildingIntent = new(1, [], [], [], [MarkerFixtures.BasicMarker() with { Id = "actor-new" }]); break;
            case "live-name": fixture.Document.BuildingIntent = new(1, [], [], [], [MarkerFixtures.BasicMarker() with { Name = "actor-home" }]); break;
            case "reservation":
                var prefab = MarkerFixtures.Create("prefab-basic").Document.Prefabs!;
                fixture.Document.Prefabs = new(prefab.PayloadVersion, prefab.ExpansionVersion,
                    prefab.Instances.Select(i => i with { Removed = true }).ToArray(), prefab.Transfers)
                { Attachments = prefab.Attachments };
                choices[actor] = choices[actor] with { MarkerId = "a/entry" };
                break;
            case "both": choices[actor] = choices[actor] with { Height = new(1, MarkerFixtures.SurfaceBinding("adoption-floor")) }; break;
            case "neither": choices[actor] = choices[actor] with { Height = new(null, null) }; break;
            case "bad-yaw": choices[actor] = choices[actor] with { YawRadians = float.NaN }; break;
        }
        string before = MapDocumentFile.SaveText(fixture.Document);
        Assert.Throws<MapDocumentException>(() => MapMarkerAdoption.ConvertSpawns(fixture.Document, choices));
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
    }

    [Fact]
    public void AdoptionEqualsExplicitMarkerCommands()
    {
        var fixture = MarkerFixtures.LegacyLists();
        MapDocument expected = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        expected.Spawns.Clear();
        expected.PlayerSpawns.Clear();
        foreach (var marker in new[]
        {
            MapMarkerHeights.Apply(new(1, "actor-new", "actor-home", new(-0.25f, 0, -1.5f), 0, "actor-role", false, new[] { "second", "first" }), new(12.25f, null)) with { ArchetypeId = "kit.actor" },
            MapMarkerHeights.Apply(new(1, "start-new", "player-home", new(-0.25f, 0, -1.5f), 0.371f, "start-role", true, new[] { "second", "first" }), new(null, MarkerFixtures.SurfaceBinding("adoption-floor"))),
        }) expected = MapMarkerEdits.Prepare(expected, fixture.Assets, new MapSetMarker(marker)).After;
        MapDocument adopted = MapMarkerAdoption.ConvertSpawns(fixture.Document, MarkerFixtures.Choices());
        Assert.Equal(MapDocumentFile.SaveText(expected), MapDocumentFile.SaveText(adopted));
        Assert.Equal(MapPrefabJson.Bytes(MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture with { Document = expected }))),
            MapPrefabJson.Bytes(MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture with { Document = adopted }))));
    }

    [Fact]
    public void PartialDocuments_RefuseBeforeCopyingAnyRecords()
    {
        var fixture = MarkerFixtures.LegacyLists();
        fixture.Document.Tiles = new(64, MapDocumentHash.SchemeVersion, null, [new(new(0, 0), "unloaded", false)]);
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerAdoption.ConvertSpawns(fixture.Document, MarkerFixtures.Choices()));
        Assert.Contains("partial", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Document.Spawns);
        Assert.Single(fixture.Document.PlayerSpawns);
        Assert.Null(fixture.Document.BuildingIntent);
    }

    [Fact]
    public void AnalyticAndResolverOne_RequireNativeResolverTwo()
    {
        var analytic = MapDocumentFile.LoadText(MarkerFixtures.FormatSevenJson(true));
        Assert.Throws<MapDocumentException>(() => MapMarkerAdoption.ConvertSpawns(analytic, MarkerFixtures.Choices()));
        var legacy = FormatFourFixtures.Build().Document;
        string before = MapDocumentFile.SaveText(legacy);
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerAdoption.ConvertSpawns(legacy, MarkerFixtures.Choices()));
        Assert.Contains("resolver adoption", error.Message, StringComparison.Ordinal);
        Assert.Contains("MapResolverAdoption.ConvertToAuthoredSupport", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(legacy));
    }
}
