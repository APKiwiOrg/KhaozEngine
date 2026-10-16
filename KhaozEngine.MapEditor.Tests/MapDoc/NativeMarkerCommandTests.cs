using System;
using System.Linq;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The undoable native marker command through pure preparation, the GUI history and the tool session.</summary>
public sealed class NativeMarkerCommandTests
{
    const MapNativeInvalidation PhysicsNavUnbounded = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded;

    [Fact]
    public void MarkerSet_ByIdKeepsIdentityAndExactHistory()
    {
        MarkerFixture fixture = MarkerFixtures.Create();
        string original = MapDocumentFile.SaveText(fixture.Document);
        MapLocalMarkerDoc changed = MapMarkerHeights.Apply(MarkerFixtures.BasicMarker() with
        {
            Name = "arrival-new",
            Tags = new[] { "second", "first", "first" },
            Enabled = false,
        }, new(13.125f, null));
        var edit = new MapSetMarker(changed);

        var pure = MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, edit);
        string accepted = MapDocumentFile.SaveText(pure.After);
        AssertSet(pure.After, fixture.Assets, pure.Effects);
        Assert.Equal(original, MapDocumentFile.SaveText(fixture.Document));

        // The GUI history publishes the same document and effects, and undo and redo reproduce exact bytes.
        MapDocument gui = MapDocumentFile.LoadText(original);
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        history.Execute(gui, new MarkerEditCommand(edit));
        Assert.Equal(accepted, MapDocumentFile.SaveText(gui));
        AssertSet(gui, fixture.Assets, history.LastNativeEffects!);
        Assert.Equal("Set marker", history.UndoLabel);
        Assert.True(history.Undo(gui));
        Assert.Equal(original, MapDocumentFile.SaveText(gui));
        Assert.Equal(new MapMarkerNameChange("m", "arrival-new", "arrival"), Assert.Single(history.LastNativeEffects!.MarkerNameChanges));
        Assert.Equal(1, Assert.Single(gui.BuildingIntent!.Markers).PayloadVersion);
        Assert.True(history.Redo(gui));
        Assert.Equal(accepted, MapDocumentFile.SaveText(gui));
        AssertSet(gui, fixture.Assets, history.LastNativeEffects!);

        // The tool session takes the same command through its one native history.
        MapEditSession session = MarkerEditorFixtures.Session(fixture);
        string opened = MarkerEditorFixtures.Bytes(session);
        var result = new MutationService(session).MarkerApply(edit);
        Assert.False(result.Mutation.WorldChanged);
        Assert.Equal("m", result.Mutation.Id);
        string applied = MarkerEditorFixtures.Bytes(session);
        AssertSet(PrefabEditorFixtures.Document(session), fixture.Assets, result.Effects);
        Assert.Equal(StoredMarker(pure.After), StoredMarker(PrefabEditorFixtures.Document(session)));
        Assert.NotNull(session.UndoNative());
        Assert.Equal(opened, MarkerEditorFixtures.Bytes(session));
        Assert.NotNull(session.RedoNative());
        Assert.Equal(applied, MarkerEditorFixtures.Bytes(session));
        Assert.Equal(("Set marker", (string?)null), session.NativeHistoryLabels);

        // Save and reload keep the data. History is not persisted.
        Assert.True(session.Save().Saved);
        var reopened = new MapEditSession();
        reopened.Open(session.DocumentPath!);
        AssertData(PrefabEditorFixtures.Document(reopened), fixture.Assets);
        Assert.Equal(0, reopened.NativeHistoryState.Depth);
    }

    [Fact]
    public void TransferredSpaceBoundMarkerSet_KeepsFrameLocalBinding()
    {
        const string id = "a/space-marker";
        MarkerFixture packed = MarkerFixtures.TransferredSpace();
        MapDocument unpacked = MapPrefabEdits.Prepare(packed.Document, packed.Assets, new MapUnpackPrefab("a")).After;
        MarkerFixture fixture = MarkerFixtures.Reload(packed with { Document = unpacked }, false);
        MapDocument doc = fixture.Document;
        MapAssetClosure assets = fixture.Assets;
        var history = PrefabEditorFixtures.History(doc, assets);
        MapEffectiveTransform frame = FrameOf(doc, id);
        Assert.Equal(MapEffectiveTransform.From(MarkerFixtures.TransferredFrame), frame);
        MapLocalMarkerDoc local = Authored(doc, id);
        string binding = BindingJson(local);
        var transformsBefore = Others(doc, id);
        string unchanged = MapDocumentFile.SaveText(doc);

        // A metadata set in world terms keeps the stored binding bytes, local point and precise frame verbatim.
        MapLocalMarkerDoc world = MarkerEditorFixtures.WorldEntry(doc, assets, id);
        history.Execute(doc, new MarkerEditCommand(new MapSetMarker(world with { Name = "space-renamed", Tags = new[] { "space", "renamed" } })));
        var metadata = history.LastNativeEffects!;
        Assert.Equal(MapNativeInvalidation.None, metadata.Invalidates);
        Assert.Null(metadata.OldBounds);
        Assert.Null(metadata.NewBounds);
        Assert.Equal(new[] { id }, metadata.MarkerIds);
        Assert.Equal(new MapMarkerNameChange(id, local.Name, "space-renamed"), Assert.Single(metadata.MarkerNameChanges));
        MapLocalMarkerDoc renamed = Authored(doc, id);
        Assert.Equal(binding, BindingJson(renamed));
        Assert.Equal(Bits(local), Bits(renamed));
        Assert.Equal(frame, FrameOf(doc, id));
        string afterRename = MapDocumentFile.SaveText(doc);

        // An absolute move by world (0.5, 0, -0.25) stores the frame-local equivalent through the unchanged frame.
        world = MarkerEditorFixtures.WorldEntry(doc, assets, id);
        MapLocalMarkerDoc target = world with { Position = new(world.Position.X + 0.5f, world.Position.Y, world.Position.Z - 0.25f) };
        history.Execute(doc, new MarkerEditCommand(new MapSetMarker(target)));
        var moved = history.LastNativeEffects!;
        var (s, c) = Math.SinCos(frame.YawRadians);
        double dx = target.Position.X - frame.X, dz = target.Position.Z - frame.Z;
        MapLocalMarkerDoc stored = Authored(doc, id);
        Assert.Equal((float)((c * dx - s * dz) / frame.Scale), stored.Position.X);
        Assert.Equal((float)((s * dx + c * dz) / frame.Scale), stored.Position.Z);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(stored.Position.Y));
        Assert.Equal(BitConverter.SingleToUInt32Bits(local.YawRadians), BitConverter.SingleToUInt32Bits(stored.YawRadians));
        Assert.Equal(binding, BindingJson(stored));
        Assert.Equal(frame, FrameOf(doc, id));
        // The effective request is the frame applied once to the stored local request.
        MapEffectiveMarker effective = Marker(doc, assets, id);
        float search = (float)(1d * 1.2f);
        Assert.Equal(MarkerFixtures.SpaceBinding("a/room", Frozen(-450d + 0.5d * 1.2f), search, search), effective.HeightProvenance!.Binding);
        Assert.InRange(Math.Abs(effective.Position.X.ToDouble() - target.Position.X), 0, 0.00001);
        Assert.InRange(Math.Abs(effective.Position.Z.ToDouble() - target.Position.Z), 0, 0.00001);
        Assert.Equal(MapNativeInvalidation.Residency, moved.Invalidates);
        Assert.NotNull(moved.OldBounds);
        Assert.NotNull(moved.NewBounds);
        Assert.Equal(new[] { id }, moved.MarkerIds);
        Assert.Empty(moved.MarkerNameChanges);
        string afterMove = MapDocumentFile.SaveText(doc);

        // A changed world reference height stores its frame-local form and keeps the unchanged searches verbatim.
        world = MarkerEditorFixtures.WorldEntry(doc, assets, id);
        MapSupportBinding request = world.SupportBinding! with { ReferenceY = world.SupportBinding!.ReferenceY + 0.25f };
        history.Execute(doc, new MarkerEditCommand(new MapSetMarker(world with { SupportBinding = request })));
        MapSupportBinding rebound = Authored(doc, id).SupportBinding!;
        Assert.Equal((float)((request.ReferenceY!.Value - frame.Y) / frame.Scale), rebound.ReferenceY);
        Assert.Equal(BitConverter.SingleToUInt32Bits(local.SupportBinding!.SearchBelow!.Value), BitConverter.SingleToUInt32Bits(rebound.SearchBelow!.Value));
        Assert.Equal(BitConverter.SingleToUInt32Bits(local.SupportBinding.SearchAbove!.Value), BitConverter.SingleToUInt32Bits(rebound.SearchAbove!.Value));
        Assert.Equal("a/room", rebound.SpaceId);
        Assert.Equal(Frozen(frame.Y + (double)rebound.ReferenceY!.Value * frame.Scale), Marker(doc, assets, id).HeightProvenance!.Binding!.ReferenceY);
        Assert.Equal(frame, FrameOf(doc, id));
        Assert.Equal(transformsBefore, Others(doc, id));
        string afterRebind = MapDocumentFile.SaveText(doc);

        // Undo restores the exact prior bytes at every step, and redo returns to each accepted state.
        Assert.True(history.Undo(doc));
        Assert.Equal(afterMove, MapDocumentFile.SaveText(doc));
        Assert.True(history.Undo(doc));
        Assert.Equal(afterRename, MapDocumentFile.SaveText(doc));
        Assert.True(history.Undo(doc));
        Assert.Equal(unchanged, MapDocumentFile.SaveText(doc));
        Assert.True(history.Redo(doc));
        Assert.True(history.Redo(doc));
        Assert.True(history.Redo(doc));
        Assert.Equal(afterRebind, MapDocumentFile.SaveText(doc));

        // Remove retires the active transfer, removes only its own transform and keeps every reservation.
        var transfer = Assert.Single(doc.Prefabs!.Transfers, t => t.Binding.Child.Kind == MapPrefabChildKind.Marker && t.Binding.WorldId == id);
        Assert.False(transfer.Retired);
        var otherTransfers = doc.Prefabs.Transfers.Where(t => t != transfer).ToArray();
        string instances = MapPrefabJson.Digest("test/instances", doc.Prefabs.Instances);
        history.Execute(doc, new MarkerEditCommand(new MapRemoveMarker(id)));
        Assert.DoesNotContain(doc.BuildingIntent!.Markers, m => m.Id == id);
        Assert.DoesNotContain(doc.NativeResourceTransforms!, t => t.Kind == MapPrefabChildKind.Marker && t.OwnerId == id);
        Assert.Equal(transformsBefore, Others(doc, id));
        Assert.Equal(transfer with { Retired = true }, Assert.Single(doc.Prefabs!.Transfers, t => t.Binding.Child.Kind == MapPrefabChildKind.Marker && t.Binding.WorldId == id));
        Assert.Equal(otherTransfers, doc.Prefabs.Transfers.Where(t => t.Binding.WorldId != id).ToArray());
        Assert.Equal(instances, MapPrefabJson.Digest("test/instances", doc.Prefabs.Instances));
        Assert.Equal(new MapMarkerNameChange(id, "space-renamed", null), Assert.Single(history.LastNativeEffects!.MarkerNameChanges));
        Assert.Equal(MapNativeInvalidation.None, history.LastNativeEffects!.Invalidates & PhysicsNavUnbounded);
        string removed = MapDocumentFile.SaveText(doc);

        // A later set cannot seize the dormant reservation, and undo restores the active transfer exactly.
        var seize = Assert.Throws<MapDocumentException>(() => history.Execute(doc, new MarkerEditCommand(new MapSetMarker(target))));
        Assert.Contains("reservation", seize.Message, StringComparison.Ordinal);
        Assert.Equal(removed, MapDocumentFile.SaveText(doc));
        Assert.True(history.Undo(doc));
        Assert.Equal(afterRebind, MapDocumentFile.SaveText(doc));
    }

    [Fact]
    public void RenameConflict_WritesNothing()
    {
        MarkerFixture fixture = MarkerEditorFixtures.TwoPoints();
        MapEditSession session = MarkerEditorFixtures.Session(fixture);
        var mutation = new MutationService(session);
        mutation.MarkerApply(new MapSetMarker(MarkerEditorFixtures.Other() with { Tags = new[] { "tagged" } }));
        string before = MarkerEditorFixtures.State(session);

        var refused = Assert.Throws<MapDocumentException>(() => mutation.MarkerApply(new MapSetMarker(MarkerEditorFixtures.Other() with { Name = "arrival" })));
        Assert.Contains("duplicate marker name 'arrival'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, MarkerEditorFixtures.State(session));

        // The GUI history refuses the same command without publishing or recording it.
        MapDocument gui = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        string guiBefore = MapDocumentFile.SaveText(gui);
        Assert.Throws<MapDocumentException>(() => history.Execute(gui, new MarkerEditCommand(new MapSetMarker(MarkerEditorFixtures.Other() with { Name = "arrival" }))));
        Assert.Equal(guiBefore, MapDocumentFile.SaveText(gui));
        Assert.False(history.CanUndo);

        // Positive control: a unique new name is accepted as one history entry.
        var (cursor, depth, events) = session.NativeHistoryState;
        var renamed = mutation.MarkerApply(new MapSetMarker(MarkerEditorFixtures.Other() with { Name = "arrival-2" }));
        Assert.Equal(new MapMarkerNameChange("other", "other-name", "arrival-2"), Assert.Single(renamed.Effects.MarkerNameChanges));
        Assert.Equal((cursor + 1, depth + 1, events + 1), session.NativeHistoryState);
        Assert.NotEqual(before, MarkerEditorFixtures.State(session));
    }

    static void AssertSet(MapDocument document, MapAssetClosure assets, MapNativeEditEffects effects)
    {
        MapEffectiveMarker marker = AssertData(document, assets);
        Assert.Equal("m", marker.Id);
        Assert.Equal(new MapMarkerNameChange("m", "arrival", "arrival-new"), Assert.Single(effects.MarkerNameChanges));
        Assert.Equal(MapNativeInvalidation.Residency, effects.Invalidates);
        Assert.Equal(new[] { "m" }, effects.MarkerIds);
        Assert.Equal(MapNativeInvalidation.None, effects.Invalidates & PhysicsNavUnbounded);
    }

    static MapEffectiveMarker AssertData(MapDocument document, MapAssetClosure assets)
    {
        MapLocalMarkerDoc authored = Assert.Single(document.BuildingIntent!.Markers);
        MapEffectiveMarker marker = Marker(document, assets, "m");
        Assert.Equal("m", authored.Id);
        Assert.Equal("arrival-new", marker.Name);
        Assert.Equal(2, authored.PayloadVersion);
        Assert.Equal(MapMarkerHeightMode.Explicit, authored.HeightMode);
        Assert.Equal(new[] { "second", "first", "first" }, marker.Tags);
        Assert.Equal(13.125f, marker.Position.Y.ToSingle());
        Assert.False(marker.Enabled);
        return marker;
    }

    static MapEffectiveMarker Marker(MapDocument document, MapAssetClosure assets, string id) =>
        MapEffectiveAnnotations.Markers(MapPrefabResolver.Expand(document, assets)).Single(m => m.Id == id);
    static MapLocalMarkerDoc Authored(MapDocument document, string id) => document.BuildingIntent!.Markers.Single(m => m.Id == id);
    static string StoredMarker(MapDocument document) => Encoding.UTF8.GetString(MapPrefabJson.Bytes(Assert.Single(document.BuildingIntent!.Markers)));
    static string BindingJson(MapLocalMarkerDoc marker) => Encoding.UTF8.GetString(MapPrefabJson.Bytes(marker.SupportBinding));
    static (uint X, uint Y, uint Z, uint Yaw) Bits(MapLocalMarkerDoc marker) => (BitConverter.SingleToUInt32Bits(marker.Position.X),
        BitConverter.SingleToUInt32Bits(marker.Position.Y), BitConverter.SingleToUInt32Bits(marker.Position.Z), BitConverter.SingleToUInt32Bits(marker.YawRadians));
    static MapEffectiveTransform FrameOf(MapDocument document, string id) =>
        document.NativeResourceTransforms!.Single(t => t.Kind == MapPrefabChildKind.Marker && t.OwnerId == id).Transform;
    static MapStandaloneResourceTransform[] Others(MapDocument document, string id) =>
        document.NativeResourceTransforms!.Where(t => !(t.Kind == MapPrefabChildKind.Marker && t.OwnerId == id)).ToArray();
    static float Frozen(double metres) => (float)(Math.Round(metres * 1048576, MidpointRounding.ToEven) / 1048576);
}
