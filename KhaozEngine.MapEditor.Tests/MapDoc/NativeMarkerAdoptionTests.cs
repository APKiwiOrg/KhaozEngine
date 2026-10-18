using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMarkerAdoptionTests
{
    [Fact]
    public void Adoption_UndoRedoRestoresExactListsMarkersAndIdentity()
    {
        var fixture = MarkerFixtures.LegacyLists();
        var session = MarkerEditorFixtures.Session(fixture);
        var before = PrefabEditorFixtures.State(session);
        string source = NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(session));
        int events = 0;
        session.NativeEdited += _ => events++;
        var result = new MutationService(session).SpawnsAdopt(MarkerFixtures.Choices());
        var after = PrefabEditorFixtures.State(session);
        Assert.Equal("spawns_adopt", result.Mutation.Verb);
        Assert.False(result.Mutation.WorldChanged);
        Assert.Equal((1, 1, 1L), session.NativeHistoryState);
        Assert.Equal(1, events);
        Assert.Empty(PrefabEditorFixtures.Document(session).Spawns);
        Assert.Empty(PrefabEditorFixtures.Document(session).PlayerSpawns);
        Assert.NotEqual(before.Identity, after.Identity);
        Assert.Equal(MapNativeInvalidation.Residency, result.Effects.Invalidates);
        Assert.Equal(new[] { "actor-new", "start-new" }, result.Effects.MarkerIds);
        Assert.NotNull(session.UndoNative());
        Assert.Equal(source, NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(session)));
        Assert.Equal(before.DocumentBytes, MarkerEditorFixtures.Bytes(session));
        Assert.Equal(before.Identity, PrefabEditorFixtures.State(session).Identity);
        Assert.NotNull(session.RedoNative());
        Assert.Equal(after.DocumentBytes, MarkerEditorFixtures.Bytes(session));
        Assert.Equal(after.Identity, PrefabEditorFixtures.State(session).Identity);
        Assert.Equal(new MapMarkerHeightMode?[] { MapMarkerHeightMode.Explicit, MapMarkerHeightMode.Support },
            PrefabEditorFixtures.Document(session).BuildingIntent!.Markers.OrderBy(m => m.Id).Select(m => m.HeightMode));
        Assert.Equal(3, events);

        MapDocument gui = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        history.Execute(gui, new AdoptSpawnsCommand(MarkerFixtures.Choices()));
        Assert.Equal(after.DocumentBytes, MapDocumentFile.SaveText(gui));
        Assert.Equal(1, history.UndoDepth);
        Assert.True(history.Undo(gui));
        Assert.Equal(source, NativeLegacySpawnAdmission.Canonical(gui));
        Assert.True(history.Redo(gui));
        Assert.Equal(after.DocumentBytes, MapDocumentFile.SaveText(gui));
    }

    [Fact]
    public void SupportChangesAfterAdoption_ReseatOnlyBoundMarkers()
    {
        var fixture = MarkerFixtures.LegacyLists();
        MapDocument document = fixture.Document;
        var history = PrefabEditorFixtures.History(document, fixture.Assets);
        history.Execute(document, new AdoptSpawnsCommand(MarkerFixtures.Choices()));
        var patch = document.Surfaces.Patches[new("adoption-floor", -1, -1)];
        history.Execute(document, new TerrainEditCommand(new MapSetCornerHeights(patch.Key, 0, 0, 3, 3, Enumerable.Repeat(2100, 9).ToArray())));
        var rows = MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture));
        Assert.Equal(12.25f, rows.Single(m => m.Id == "actor-new").Position.Y.ToSingle());
        Assert.Equal(21f, rows.Single(m => m.Id == "start-new").Position.Y.ToSingle());
    }

    [Fact]
    public void InvalidSupportAndEffectiveNameCollision_RefuseBeforeAcceptanceAndCanRetry()
    {
        var fixture = MarkerFixtures.LegacyLists();
        var session = MarkerEditorFixtures.Session(fixture);
        var choices = MarkerFixtures.Choices().ToDictionary();
        var player = new MapLegacySpawnKey(MapLegacySpawnKind.PlayerSpawn, "old");
        choices[player] = choices[player] with { Height = new(null, MarkerFixtures.SurfaceBinding("absent")) };
        string before = MarkerEditorFixtures.State(session);
        Assert.Throws<MapDocumentException>(() => new MutationService(session).SpawnsAdopt(choices));
        Assert.Equal(before, MarkerEditorFixtures.State(session));
        var command = new AdoptSpawnsCommand(MarkerFixtures.Choices());
        session.BeforeNativePublication = () => throw new MapDocumentException("refuse publication");
        Assert.Throws<MapDocumentException>(() => session.ExecuteNativeCommand(command));
        Assert.Equal(before, MarkerEditorFixtures.State(session));
        session.BeforeNativePublication = null;
        session.ExecuteNativeCommand(command);
        Assert.Equal(1, session.NativeHistoryState.Depth);

        var packed = MarkerFixtures.Create("prefab-basic");
        fixture.Document.Prefabs = packed.Document.Prefabs;
        choices = MarkerFixtures.Choices().ToDictionary();
        choices[player] = choices[player] with { Name = "arrival-a" };
        var collision = MarkerEditorFixtures.Session(fixture);
        before = MarkerEditorFixtures.State(collision);
        Assert.Throws<MapDocumentException>(() => new MutationService(collision).SpawnsAdopt(choices));
        Assert.Equal(before, MarkerEditorFixtures.State(collision));
    }
}
