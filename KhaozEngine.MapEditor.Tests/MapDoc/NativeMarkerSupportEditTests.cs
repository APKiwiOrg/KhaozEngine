using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A support edit through any native transaction reseats a bound marker with both points reported, and a
/// removed owner refuses naming the marker. Each move and removal first succeeds on the marker-absent control.</summary>
public sealed class NativeMarkerSupportEditTests
{
    const MapNativeInvalidation PhysicsNav = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav;
    static readonly MapPatchKey Floor2 = new("floor-2", 0, 0), Ceiling2 = new("ceiling-2", 0, 0);
    static readonly MapPrefabChildKey DeckKey = new(MapPrefabChildKind.Placement, "deck");

    [Theory]
    [InlineData("terrain")]
    [InlineData("support-placement")]
    [InlineData("prefab-floor")]
    [InlineData("interior")]
    public void BoundMarkerReseat_NamesBothPointsAndRefusesLostOwner(string kind)
    {
        switch (kind)
        {
            case "terrain": Terrain(); break;
            case "support-placement": SupportPlacement(); break;
            case "prefab-floor": PrefabFloor(); break;
            case "interior": Interior(); break;
        }
    }

    /// <summary>Each stroke after the first reads the accepted expansion of the previous one as its before side, so it
    /// expands only its candidate, and reports exactly what a cold session reports.</summary>
    [Fact]
    public void TerrainStroke_ReusesAcceptedExpansionAsBefore()
    {
        var fixture = MarkerFixtures.SupportEdits("stacked-bound");
        var history = PrefabEditorFixtures.History(fixture.Document, fixture.Assets);
        int[] heights = [2010, 2020, 2030];
        for (int stroke = 0; stroke < heights.Length; stroke++)
        {
            // A reloaded document is a new instance, so the cold control never sees an accepted expansion.
            var cold = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
            var coldHistory = PrefabEditorFixtures.History(cold, fixture.Assets);
            coldHistory.Execute(cold, Corner(heights[stroke]));
            long before = MapPrefabResolver.ThreadExpansions;
            history.Execute(fixture.Document, Corner(heights[stroke]));
            long delta = MapPrefabResolver.ThreadExpansions - before;
            if (stroke > 0) Assert.Equal(1, delta);
            Assert.Equal(coldHistory.LastNativeEffects!.Describe(), history.LastNativeEffects!.Describe());
            Assert.Equal(new[] { "bound" }, history.LastNativeEffects.MarkerIds);
        }
    }

    /// <summary>The production terrain path, a session mutation that stages one command and leaves its candidate as
    /// staged, keeps the accepted expansion. Each stroke after the first reads it as its before side, and reports exactly
    /// what a cold session over the same document reports. The first stroke expands its before side, its candidate and
    /// the unchanged session revalidation's candidate. Later strokes expand only the last two.</summary>
    [Fact]
    public void TerrainApplyStrokes_ReuseAcceptedExpansion()
    {
        var session = Session(MarkerFixtures.SupportEdits("stacked-bound"));
        var service = new MutationService(session);
        int[] heights = [2010, 2020, 2030];
        for (int stroke = 0; stroke < heights.Length; stroke++)
        {
            var fixture = Fixture(session);
            var cold = new MutationService(PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets)));
            var coldEffects = cold.TerrainApply(CornerEdit(heights[stroke])).Effects;
            long before = MapPrefabResolver.ThreadExpansions;
            var effects = service.TerrainApply(CornerEdit(heights[stroke])).Effects;
            long delta = MapPrefabResolver.ThreadExpansions - before;
            Assert.Equal(stroke == 0 ? 3 : 2, delta);
            Assert.Equal(coldEffects.Describe(), effects.Describe());
            Assert.Equal(new[] { "bound" }, effects.MarkerIds);
        }
    }

    /// <summary>A callback that edits its candidate after staging a publication shares that edit with the publication.
    /// The accepted expansion built before the edit is not remembered, so the next edit matches a cold session.</summary>
    [Fact]
    public void RevalidatedCallbackEdit_ForgetsAcceptedExpansion()
    {
        var session = Session(MarkerFixtures.SupportEdits("stacked-bound"));
        session.Mutate((document, registry) =>
        {
            session.ApplyNative(Corner(2010), document, registry);
            // The staged publication shares this patch object, so the later edit is published with it.
            var floor = document.Surfaces.Patches[Floor2];
            for (int i = 0; i < floor.Heights.Length; i++) floor.Heights[i] = 2040;
            return 0;
        }, true);
        Assert.Equal(20.4f, Bound(session).Position.Y.ToSingle());
        var fixture = Fixture(session);
        var cold = PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets));

        var effects = session.ExecuteNativeCommand(Corner(2060));
        var coldEffects = cold.ExecuteNativeCommand(Corner(2060));

        Assert.Equal(coldEffects.Describe(), effects.Describe());
        Assert.InRange((double)20.4f, effects.OldBounds!.Value.MinY, effects.OldBounds.Value.MaxY);
    }

    // The four corners of the cell under the bound marker, so the stroke reseats it whatever the cell's diagonal.
    static TerrainEditCommand Corner(int height) => new(CornerEdit(height));
    static MapSetCornerHeights CornerEdit(int height) => new(Floor2, 0, 0, 2, 2, [height, height, height, height]);

    static void Terrain()
    {
        var control = Session(MarkerFixtures.SupportEdits("stacked-bound", withMarker: false));
        var terrainOnly = control.ExecuteNativeCommand(RaiseFloor2());
        control.ExecuteNativeCommand(RemoveFloor2(control));
        Assert.Equal(2, control.NativeHistoryState.Cursor);

        var session = Session(MarkerFixtures.SupportEdits("stacked-bound"));
        string descriptor = Descriptor(session);
        var effects = session.ExecuteNativeCommand(RaiseFloor2());
        Assert.Equal(new[] { "bound" }, effects.MarkerIds);
        AssertReseat(effects, terrainOnly, 20, 21, 0.5);
        Assert.Equal(descriptor, Descriptor(session));
        Assert.Equal(21f, Bound(session).Position.Y.ToSingle());
        AssertRefuses(session, RemoveFloor2(session));

        var explicitSession = Session(MarkerFixtures.WithExplicit(MarkerFixtures.SupportEdits("stacked-bound"), "bound"));
        explicitSession.ExecuteNativeCommand(RemoveFloor2(explicitSession));
        Assert.Equal(1, explicitSession.NativeHistoryState.Cursor);
    }

    static void SupportPlacement()
    {
        var control = Session(MarkerFixtures.SupportEdits("deck-bound", withMarker: false));
        var controlRaise = control.ExecuteNativeCommand(RaiseDeck());
        control.ExecuteNativeCommand(new PrefabEditCommand(new MapUnpackPrefab("a")));
        var controlMove = control.ExecuteNativeCommand(MoveDeck(control));
        control.ExecuteNativeCommand(new RemovePlacementCommand(DeckId(control)));
        Assert.Equal(4, control.NativeHistoryState.Cursor);

        var session = Session(MarkerFixtures.SupportEdits("deck-bound"));
        Assert.Empty(MarkerFixtures.Effective(Fixture(session)).Surfaces.Patches[new MapPatchKey(PrefabFixtures.SupportFloorId, 0, 0)].Records);
        string descriptor = Descriptor(session);
        var raise = session.ExecuteNativeCommand(RaiseDeck());
        Assert.Equal(new[] { "bound" }, raise.MarkerIds);
        AssertReseat(raise, controlRaise, 0, 0.5, 1.5);
        session.ExecuteNativeCommand(new PrefabEditCommand(new MapUnpackPrefab("a")));
        var move = session.ExecuteNativeCommand(MoveDeck(session));
        Assert.Equal(new[] { "bound" }, move.MarkerIds);
        AssertReseat(move, controlMove, 0.5, 1, 1.5);
        Assert.Equal(descriptor, Descriptor(session));
        AssertRefuses(session, new RemovePlacementCommand(DeckId(session)));
    }

    static void PrefabFloor()
    {
        var control = Session(MarkerFixtures.SupportEdits("floor-bound", withMarker: false));
        var controlMove = control.ExecuteNativeCommand(MovePrefab());
        control.ExecuteNativeCommand(new PrefabEditCommand(new MapRemovePrefab("a")));
        Assert.Equal(2, control.NativeHistoryState.Cursor);

        var session = Session(MarkerFixtures.SupportEdits("floor-bound"));
        string descriptor = Descriptor(session);
        float movedFrom = Bound(session).Position.Y.ToSingle();
        var move = session.ExecuteNativeCommand(MovePrefab());
        Assert.Equal(movedFrom + 1.5f, Bound(session).Position.Y.ToSingle());
        // The instance's own explicit marker moves with it. The bound world marker reseats on its floor.
        Assert.Equal(new[] { "a/entry", "bound" }, move.MarkerIds);
        AssertReseat(move, controlMove, 0, 1.5, 1.5);
        Assert.Equal(descriptor, Descriptor(session));
        AssertRefuses(session, new PrefabEditCommand(new MapRemovePrefab("a")));
    }

    static void Interior()
    {
        var control = Session(MarkerFixtures.SupportEdits("room-bound", withMarker: false));
        Assert.DoesNotContain(MarkerFixtures.Effective(Fixture(control)).Surfaces.AllRecords(), r => r is MapCavePortal);
        Assert.DoesNotContain(Fixture(control).Document.Surfaces.AllRecords(), r => r.Id != "a/room" && r is not MapPrismDomain { Space.Id: "a/room" } &&
            MapSurfaceReferences.Records(r).Any(reference => reference.Id == "a/room"));
        control.ExecuteNativeCommand(RemoveRoom());
        Assert.Equal(1, control.NativeHistoryState.Cursor);

        var session = Session(MarkerFixtures.SupportEdits("room-bound"));
        AssertRefuses(session, RemoveRoom());

        var explicitSession = Session(MarkerFixtures.WithExplicit(MarkerFixtures.SupportEdits("room-bound"), "bound"));
        explicitSession.ExecuteNativeCommand(RemoveRoom());
        Assert.Equal(1, explicitSession.NativeHistoryState.Cursor);
    }

    static TerrainEditCommand RaiseFloor2() => new(new MapSetCornerHeights(Floor2, 0, 0, 3, 3, Enumerable.Repeat(2100, 9).ToArray()));
    // T3-R2. Released topology refuses removing a patch that still carries records, so one composite strips floor-2's
    // level-2 records and then removes both patches and their surfaces.
    static TerrainEditCommand RemoveFloor2(MapEditSession session)
    {
        var floor = PrefabEditorFixtures.Document(session).Surfaces.Patches[Floor2].Clone();
        floor.Records.Clear();
        return new(new MapCompositeEdit([new MapReplaceTopology([floor], [], [], []),
            new MapReplaceTopology([], [Floor2, Ceiling2], [], ["floor-2", "ceiling-2"])]));
    }
    static PrefabEditCommand RaiseDeck() => new(new MapSetPrefabOverride("a", new MapPrefabPlacementOverride(DeckKey, new(new(0, 0.5f, 0), 0, 1), null, null)));
    static PrefabEditCommand MovePrefab() => new(new MapMovePrefab("a", new(new(0, 1.5f, 0), 0, 1)));
    static InteriorEditCommand RemoveRoom() => new(new MapRemoveInterior("a/room"));

    static string DeckId(MapEditSession session) => PrefabEditorFixtures.Document(session).Placements.Single(p => p.AssetId == "deck").Id;
    static MovePlacementCommand MoveDeck(MapEditSession session)
    {
        var deck = PrefabEditorFixtures.Document(session).Placements.Single(p => p.AssetId == "deck");
        return new(deck.Id, deck.X, deck.Z, deck.Y!.Value + 0.5f);
    }

    static MapEditSession Session(MarkerFixture fixture)
    {
        Assert.Equal(MapSupportStatus.Supported, MarkerFixtures.Effective(fixture).MarkerSupports.SingleOrDefault(s => s.MarkerId == "bound")?.Support.Status ??
            MapSupportStatus.Supported);
        return PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets));
    }

    static MarkerFixture Fixture(MapEditSession session) => session.WithNativeAssets(c => new MarkerFixture(NativeDocumentSnapshot.Clone(c.Document, MapDocRegistry.CreateDefault()), c.Assets));
    static MapEffectiveMarker Bound(MapEditSession session) => MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(Fixture(session))).Single(m => m.Id == "bound");

    // The authored descriptor of the bound marker. A reseat changes only its effective point.
    static string Descriptor(MapEditSession session) =>
        MapPrefabJson.Digest("test/marker-descriptor", PrefabEditorFixtures.Document(session).BuildingIntent!.Markers.Single(m => m.Id == "bound"));

    static void AssertReseat(MapNativeEditEffects effects, MapNativeEditEffects markerAbsent, double oldY, double newY, double x)
    {
        Assert.InRange(oldY, effects.OldBounds!.Value.MinY, effects.OldBounds.Value.MaxY);
        Assert.InRange(newY, effects.NewBounds!.Value.MinY, effects.NewBounds.Value.MaxY);
        Assert.InRange(x, effects.NewBounds.Value.MinX, effects.NewBounds.Value.MaxX);
        Assert.InRange(x, effects.OldBounds.Value.MinX, effects.OldBounds.Value.MaxX);
        Assert.True(effects.Invalidates.HasFlag(MapNativeInvalidation.Residency));
        Assert.Equal(markerAbsent.Invalidates & PhysicsNav, effects.Invalidates & PhysicsNav);
        Assert.DoesNotContain("fixed", effects.MarkerIds);
        Assert.Empty(effects.MarkerNameChanges);
    }

    static void AssertRefuses(MapEditSession session, EditorCommand command)
    {
        var stateBefore = PrefabEditorFixtures.State(session);
        var removal = Assert.Throws<MapDocumentException>(() => session.ExecuteNativeCommand(command));
        Assert.Contains("Marker 'bound'", removal.Message, StringComparison.Ordinal);
        var stateAfterRefusal = PrefabEditorFixtures.State(session);
        Assert.Equal(stateBefore, stateAfterRefusal);
    }
}
