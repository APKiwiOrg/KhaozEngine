using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapEditTool;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Finalization stamps travel from the pure result through shared command state, staging, the final callback,
/// accepted state and replay, reusing captured sides and rebuilding only a stale one. The context preflight refuses
/// before any snapshot copy on every entry.</summary>
[Collection(McpWireCollection.Name)]
public sealed class NativeFoliageStampTests
{
    [Fact]
    public void CommandStamp_ReusesCapturedSidesAndRebuildsOnlyStaleAfter()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        MapPrefabAsset hut = f.Assets.GetPrefab("hut");
        var place = new MapPlacePrefab("b", "hut", new(new(8, 0, 0), 0, 1), new("exterior", new("exterior", 0, 0)))
        { InitialOverrides = PrefabFixtures.MarkerNames(hut, "b") };
        var room = new MapSetInterior(FoliageFixtures.DependentRoom(f.Document) with { UpperY = 2.5f });
        var marker = new MapSetMarker(FoliageFixtures.MovedMarker(FoliageFixtures.DependentMarker(f.Document, FoliageFixtures.PostId), 4));
        var ordinary = new (EditorCommand Command, Func<FoliageFixture, MapPrefabEditResult> Pure, Func<MapDocumentFinalizationStamp?> Accepted)[3];
        var prefabCommand = new PrefabEditCommand(place);
        var roomCommand = new InteriorEditCommand(room);
        var markerCommand = new MarkerEditCommand(marker);
        ordinary[0] = (prefabCommand, x => MapPrefabEdits.Prepare(x.Document, x.Assets, place), () => prefabCommand.AcceptedStamp);
        ordinary[1] = (roomCommand, x => MapInteriorEdits.Prepare(x.Document, x.Assets, room), () => roomCommand.AcceptedStamp);
        ordinary[2] = (markerCommand, x => MapMarkerEdits.Prepare(x.Document, x.Assets, marker), () => markerCommand.AcceptedStamp);
        FoliageFixture control = FoliageFixtures.Dependents(foliage: false);
        var plain = new EditorCommand[] { new PrefabEditCommand(place), new InteriorEditCommand(room), new MarkerEditCommand(marker) };
        for (int i = 0; i < ordinary.Length; i++)
            AssertStampTransport(f, ordinary[i].Command, ordinary[i].Pure, ordinary[i].Accepted, f.Assets, f.Assets, control, plain[i]);

        // An asset switch retains both verified closures and reverses them on replay.
        FoliageFixture placed = new(MapPrefabEdits.Prepare(f.Document, f.Assets, place).After, f.Assets);
        var item = (MapPrefabPlacementChild)hut.Children.Single(c => c.Key.Key == "item");
        MapPlacement moved = MapPrefabReferenceWalker.Copy(item.Value, item.Value.Id, item.Value.SupportBinding);
        moved.X = 2;
        MapAssetClosure afterAssets = PrefabFixtures.PublishPrefab(placed.Assets, MapPrefabAssetEdits.Replace(hut, item with { Value = moved }));
        var update = new MapUpdatePrefabAsset("hut", placed.Assets.GetResource("hut").Reference.Sha256, "hut", afterAssets.GetResource("hut").Reference.Sha256);
        var assetCommand = new PrefabAssetEditCommand(update, placed.Assets, afterAssets);
        FoliageFixture placedControl = new(MapPrefabEdits.Prepare(control.Document, control.Assets, place).After, control.Assets);
        MapDocumentFinalizationStamp switched = AssertStampTransport(placed, assetCommand,
            x => MapPrefabEdits.PrepareAssetUpdate(x.Document, placed.Assets, afterAssets, update), () => assetCommand.AcceptedStamp, placed.Assets, afterAssets,
            placedControl, new PrefabAssetEditCommand(update, placed.Assets, afterAssets));
        Assert.False(switched.After.Matches(switched.After.Fingerprint, placed.Assets));
        Assert.True(switched.After.Matches(switched.After.Fingerprint, afterAssets));

        // A pure result staged through the shared command state keeps both exact context objects with no new work.
        FoliageFixture basic = FoliageFixtures.Create();
        var paint = new MapPaintFoliage(FoliageFixtures.LayerId, 1, -1, 2, 9, 1);
        MapPrefabEditResult pureResult = MapFoliageEdits.Prepare(basic.Document, basic.Assets, paint);
        MapFoliageGenerationContext pureBeforeContext = pureResult.Stamp!.Before.Context!, pureAfterContext = pureResult.Stamp.After.Context!;
        NativeStagedPublication? stagedPublication = null;
        var stageWork = FoliageEditorFixtures.Work(() => stagedPublication = NativeDocumentTransaction.Stage(basic.Document, new Prepared(pureResult), false, basic.Assets));
        long unchangedStageContextCaptureDelta = stageWork.Captures;
        Assert.Equal(0, unchangedStageContextCaptureDelta);
        Assert.Equal((0L, 0L), (stageWork.Expansions, stageWork.Writes));
        MapFoliageGenerationContext stagedBeforeContext = stagedPublication!.Stamp!.Before.Context!, unchangedStagedAfterContext = stagedPublication.Stamp.After.Context!;
        Assert.Same(pureBeforeContext, stagedBeforeContext);
        Assert.Same(pureAfterContext, unchangedStagedAfterContext);
        Assert.Equal(pureResult.Effects.FoliageChunks, stagedPublication.Effects.FoliageChunks);

        // The real late callback: a different command is staged, then the callback changes density and a rule. Only the
        // stale after side is captured again, the before context object is kept, and nothing else is expanded.
        MapEditSession session = FoliageEditorFixtures.Session(basic);
        try
        {
            string original = FoliageEditorFixtures.Bytes(session);
            MapPrefabEditResult late = MapFoliageEdits.Prepare(basic.Document, basic.Assets, paint);
            MapDocumentFinalizationStamp? staged = null;
            (long Clones, long Expansions, long Captures, long Writes) afterCallback = default, atPublication = default;
            session.BeforeNativePublication = () => atPublication = Counters();
            session.Mutate((doc, registry) =>
            {
                session.ApplyNative(new Prepared(late), doc, registry);
                staged = session.PendingNativeStamp;
                MapFoliageLayer layer = doc.Foliage!.Layers[0];
                MapFoliageLayer denser = MapFoliageBrush.Apply(layer, new(layer.Id, 5, -3, 1.5f, 200, 1));
                doc.Foliage = new(1, [FoliageFixtures.ClearanceLayer(denser,
                    rules: [new("late-rule", "tree", null, MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))])]);
                afterCallback = Counters();
                return 0;
            }, false);
            session.BeforeNativePublication = null;
            long changedCallbackContextCaptureDelta = atPublication.Captures - afterCallback.Captures;
            Assert.Equal(1, changedCallbackContextCaptureDelta);
            Assert.InRange(atPublication.Expansions - afterCallback.Expansions, 0, 1);
            MapDocumentFinalizationStamp published = session.LastNativeStamp!;
            Assert.Same(late.Stamp!.Before.Context, staged!.Before.Context);
            Assert.Same(staged.Before.Context, published.Before.Context);
            Assert.NotSame(staged.After.Context, published.After.Context);
            FoliageFixture final = FoliageEditorFixtures.Fixture(session);
            Assert.Equal(FoliageEditorFixtures.Expected(basic.Document, final.Document, basic.Assets), session.LastNativeEffects!.FoliageChunks);
            Assert.Equal((paint.Label, (string?)null), session.NativeHistoryLabels);
            Assert.Single(final.Document.Foliage!.Layers[0].ClearanceRules, r => r.Id == "late-rule");
            string publishedBytes = FoliageEditorFixtures.Bytes(session);
            IReadOnlyList<MapFoliageChunkChange> publishedChunkEffects = session.LastNativeEffects.FoliageChunks;

            // Undo and redo of the replacement entry reuse its sides with direction-correct fingerprints and closures.
            var undoWork = FoliageEditorFixtures.Work(() => session.UndoNative());
            Assert.Equal((0L, 0L), (undoWork.Expansions, undoWork.Captures));
            Assert.Equal((published.After.Fingerprint, published.Before.Fingerprint),
                (session.LastNativeStamp!.Before.Fingerprint, session.LastNativeStamp.After.Fingerprint));
            Assert.Equal((published.After.Assets.Hash, published.Before.Assets.Hash),
                (session.LastNativeStamp.Before.Assets.Hash, session.LastNativeStamp.After.Assets.Hash));
            Assert.Equal(original, FoliageEditorFixtures.Bytes(session));
            var redoWork = FoliageEditorFixtures.Work(() => session.RedoNative());
            Assert.Equal((0L, 0L), (redoWork.Expansions, redoWork.Captures));
            string redoBytes = FoliageEditorFixtures.Bytes(session);
            IReadOnlyList<MapFoliageChunkChange> redoChunkEffects = session.LastNativeEffects!.FoliageChunks;
            Assert.Equal(publishedBytes, redoBytes);
            Assert.Equal(publishedChunkEffects, redoChunkEffects);
            Assert.Same(published.After.Context, session.LastNativeStamp!.After.Context);

            // The next edit's before state is the one published snapshot.
            MapFoliageChunkChange home = publishedChunkEffects.First(r => r.AfterDigest is not null);
            MapNativeEditEffects next = session.ExecuteNativeCommand(new FoliageEditCommand(new MapPaintFoliage(home.LayerId, 1, -1, 1, 3, 1)));
            string? publishedDependencyDigest = home.AfterDigest;
            string? nextEditBeforeDigest = next.FoliageChunks.Single(r => r.Chunk == home.Chunk).BeforeDigest;
            Assert.Equal(publishedDependencyDigest, nextEditBeforeDigest);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public async Task CommandPreflight_RefusesBeforeSnapshotClone()
    {
        // More distinct paint targets than the default MaxPaintTargets 4096, from metadata alone.
        FoliageFixture f = FoliageFixtures.PaintTargets(4097);
        Assert.Equal(4096, new MapFoliageContextLimits().MaxPaintTargets);
        MapEditSession session = FoliageEditorFixtures.Session(f);
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            string beforeState = FoliageEditorFixtures.State(session);
            var marker = new MapSetMarker(new MapLocalMarkerDoc(1, "m", "M", new(1, 1, -1), 0, "role", true, []));
            void Refuses(Action action)
            {
                MapDocumentException? error = null;
                var work = FoliageEditorFixtures.Work(() => error = Assert.Throws<MapDocumentException>(action));
                Assert.StartsWith("Foliage CapacityExceeded", error!.Message, StringComparison.Ordinal);
                Assert.Equal((0L, 0L, 0L), (work.Clones, work.Expansions, work.Captures));
                Assert.Equal(beforeState, FoliageEditorFixtures.State(session));
            }
            Refuses(() => session.Mutate((doc, _) => { doc.Placements.Clear(); return 0; }, false));
            var mutation = new MutationService(session);
            Refuses(() => mutation.PrefabApply(new MapRemovePrefab("missing")));
            Refuses(() => mutation.MarkerApply(marker));
            MapDocument live = MapDocumentFile.LoadText(FoliageEditorFixtures.Bytes(session));
            Refuses(() => NativeDocumentTransaction.Stage(live, new MarkerEditCommand(marker), false, f.Assets));
            Refuses(() => new NativePrefabCommandState().Prepare(live, false, f.Assets, f.Assets, () => throw new InvalidOperationException()));
            Refuses(() => _ = new NativeCompleteSnapshotCommand("snapshot", live, live, f.Assets, f.Assets, new(null, null, [], [], [], [], MapNativeInvalidation.None)));

            // The real wire mutation refuses with the same status and changes nothing on the live session.
            await using var harness = await McpWire.StartAsync();
            CallToolResult opened = await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = seed.DocumentPath });
            Assert.NotEqual(true, opened.IsError);
            var wireSession = harness.Services.GetRequiredService<MapEditSession>();
            string wireBefore = FoliageEditorFixtures.State(wireSession);
            CallToolResult refused = await harness.Client.CallAsync("marker_set", new Dictionary<string, object?>
            { ["markerJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(marker.Marker)) });
            Assert.True(refused.IsError == true);
            Assert.Contains("Foliage CapacityExceeded", string.Join("\n", refused.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.Ordinal);
            Assert.Equal(wireBefore, FoliageEditorFixtures.State(wireSession));
        }
        finally
        {
            FoliageEditorFixtures.Delete(session);
            FoliageEditorFixtures.Delete(seed);
        }
    }

    // A cold fresh pair captures two contexts. Staging the real command captures nothing more and expands neither
    // finalized side, so it expands exactly two documents fewer than the same transaction without foliage, whose other
    // expansions are released placement sizing. Acceptance keeps that finalization, and undo and redo replay it with
    // reversed sides and no new work.
    static MapDocumentFinalizationStamp AssertStampTransport(FoliageFixture f, EditorCommand command, Func<FoliageFixture, MapPrefabEditResult> pure,
        Func<MapDocumentFinalizationStamp?> accepted, MapAssetClosure beforeAssets, MapAssetClosure afterAssets, FoliageFixture control,
        EditorCommand plain)
    {
        var (controlGui, controlHistory) = FoliageEditorFixtures.Gui(control);
        var controlPure = FoliageEditorFixtures.Work(() => pure(new(controlGui, beforeAssets)));
        var controlStage = FoliageEditorFixtures.Work(() => controlHistory.Execute(controlGui, plain));
        var (gui, history) = FoliageEditorFixtures.Gui(f);
        MapPrefabEditResult? result = null;
        var pureWork = FoliageEditorFixtures.Work(() => result = pure(new(gui, beforeAssets)));
        Assert.Equal(2, pureWork.Captures);
        var stageWork = FoliageEditorFixtures.Work(() => history.Execute(gui, command));
        Assert.Equal((pureWork.Captures, 0L), (stageWork.Captures, stageWork.Writes));
        Assert.Equal(controlStage.Expansions - controlPure.Expansions - 2, stageWork.Expansions - pureWork.Expansions);
        Assert.Equal(result!.Effects.FoliageChunks, history.LastNativeEffects!.FoliageChunks);
        MapDocumentFinalizationStamp stamp = history.LastNativeStamp!;
        Assert.Same(stamp, accepted());
        Assert.Equal((beforeAssets.Hash, afterAssets.Hash), (stamp.Before.Assets.Hash, stamp.After.Assets.Hash));
        Assert.NotNull(stamp.Before.Context);
        Assert.NotNull(stamp.After.Context);
        // Undo reuses the accepted after side as its before side. Its own after side is the captured before side when undo
        // restores that exact snapshot, and is captured once when undo keeps a retired reservation the original lacked.
        var undoWork = FoliageEditorFixtures.Work(() => Assert.True(history.Undo(gui)));
        MapDocumentFinalizationStamp undone = history.LastNativeStamp!;
        bool restored = undone.After.Fingerprint == stamp.Before.Fingerprint;
        Assert.Equal(restored ? 0 : 1, undoWork.Captures);
        Assert.Same(stamp.After.Context, undone.Before.Context);
        if (restored) Assert.Same(stamp.Before.Context, undone.After.Context);
        Assert.Equal((afterAssets.Hash, beforeAssets.Hash), (undone.Before.Assets.Hash, undone.After.Assets.Hash));
        if (restored) Assert.Equal(FoliageEditorFixtures.Reversed(stamp.Rows), history.LastNativeEffects!.FoliageChunks);
        // Redo returns to the accepted after snapshot with both sides already captured, so it does no new work.
        var redoWork = FoliageEditorFixtures.Work(() => Assert.True(history.Redo(gui)));
        Assert.Equal((0L, 0L), (redoWork.Captures, redoWork.Expansions - (controlStage.Expansions - controlPure.Expansions - 2)));
        Assert.Same(stamp.After.Context, history.LastNativeStamp!.After.Context);
        Assert.Same(undone.After.Context, history.LastNativeStamp.Before.Context);
        if (restored) Assert.Equal(stamp.Rows, history.LastNativeEffects!.FoliageChunks);
        return stamp;
    }

    static (long Clones, long Expansions, long Captures, long Writes) Counters() => (NativeDocumentSnapshot.ThreadClones,
        MapPrefabResolver.ThreadExpansions, MapFoliageGenerationContext.ThreadCaptures, MapFoliageJson.ThreadPayloadWrites);

    // Stages one already finalized pure result through the shared command state, as a bound command would.
    sealed class Prepared(MapPrefabEditResult result) : EditorCommand, INativeBoundDocumentCommand
    {
        readonly NativePrefabCommandState _state = new();
        public override string Label => "Paint foliage";
        internal override bool AffectsWorld => false;
        public override void Apply(MapDocument doc) => throw new NotSupportedException();
        public override void Revert(MapDocument doc) => throw new NotSupportedException();
        NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
            _state.Prepare(candidate, undo, assets, assets, () => result);
    }
}
