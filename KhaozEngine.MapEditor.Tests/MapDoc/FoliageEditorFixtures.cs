using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Editor-only foliage session fixtures over the public invented foliage documents.</summary>
internal static class FoliageEditorFixtures
{
    /// <summary>A session opened from a saved copy of <paramref name="fixture"/>, with its closure written beside it.</summary>
    internal static MapEditSession Session(FoliageFixture fixture) =>
        PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets));

    /// <summary>A canonical snapshot of the document, closure, identity, dirty state, cursor, depth, labels and events, so
    /// a refused command can be shown to write nothing.</summary>
    internal static string State(MapEditSession session)
    {
        var (undo, redo) = session.NativeHistoryLabels;
        return string.Join("\n", PrefabEditorFixtures.State(session).ToString(), "undo=" + undo, "redo=" + redo);
    }

    /// <summary>The canonical text of the session's open document.</summary>
    internal static string Bytes(MapEditSession session) => PrefabEditorFixtures.State(session).DocumentBytes;

    /// <summary>A detached copy of the session's open document with its bound closure.</summary>
    internal static FoliageFixture Fixture(MapEditSession session) =>
        session.WithNativeAssets(c => new FoliageFixture(NativeDocumentSnapshot.Clone(c.Document, MapDocRegistry.CreateDefault()), c.Assets));

    /// <summary>A bound editor history over a detached copy of <paramref name="fixture"/>, sized by the tool's bounds provider.</summary>
    internal static (MapDocument Document, EditorHistory History) Gui(FoliageFixture fixture)
    {
        MapDocument gui = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        return (gui, PrefabEditorFixtures.History(gui, fixture.Assets));
    }

    /// <summary>The foliage channel of <paramref name="effects"/>, its batch rows and layer entries, in canonical form.</summary>
    internal static string Channel(MapNativeEditEffects effects) => Channel(effects.FoliageChunks, effects.FoliageLayers);

    internal static string Channel(IReadOnlyList<MapFoliageChunkChange> rows, IReadOnlyList<MapFoliageLayerChange> layers) =>
        new MapNativeEditEffects(null, null, [], [], [], [], MapNativeInvalidation.None) { FoliageChunks = rows, FoliageLayers = layers }.Describe();

    /// <summary>The channel the public effects boundary publishes between two documents, from freshly captured contexts.</summary>
    internal static string Expected(MapDocument before, MapDocument after, MapAssetClosure beforeAssets, MapAssetClosure? afterAssets = null)
    {
        IReadOnlyList<MapFoliageChunkChange> rows = MapFoliageEffects.Describe(before, after,
            before.Foliage is null ? null : FoliageFixtures.Context(new(before, beforeAssets)),
            after.Foliage is null ? null : FoliageFixtures.Context(new(after, afterAssets ?? beforeAssets)),
            new(null, null, [], [], [], [], MapNativeInvalidation.None), out IReadOnlyList<MapFoliageLayerChange> layers);
        return Channel(rows, layers);
    }

    /// <summary>The same channel read from the after side back to the before side, as undo publishes it.</summary>
    internal static string Reversed(MapNativeEditEffects effects) => Channel(
        effects.FoliageChunks.Select(r => r with { OldBounds = r.NewBounds, NewBounds = r.OldBounds, BeforeDigest = r.AfterDigest, AfterDigest = r.BeforeDigest }).ToArray(),
        effects.FoliageLayers.Select(l => l with { BeforeDigest = l.AfterDigest, AfterDigest = l.BeforeDigest }).ToArray());

    /// <summary>The logical foliage digest of the session's open document.</summary>
    internal static string Foliage(MapEditSession session) => session.WithDocument((d, _) => MapFoliageProjection.Digest(d.Foliage));

    /// <summary>Runs <paramref name="action"/> and returns the snapshot clones, expansions, context captures and payload
    /// writes this thread made during it.</summary>
    internal static (long Clones, long Expansions, long Captures, long Writes) Work(Action action)
    {
        long clones = NativeDocumentSnapshot.ThreadClones, expansions = MapPrefabResolver.ThreadExpansions;
        long captures = MapFoliageGenerationContext.ThreadCaptures, writes = MapFoliageJson.ThreadPayloadWrites;
        action();
        return (NativeDocumentSnapshot.ThreadClones - clones, MapPrefabResolver.ThreadExpansions - expansions,
            MapFoliageGenerationContext.ThreadCaptures - captures, MapFoliageJson.ThreadPayloadWrites - writes);
    }

    /// <summary>Deletes the session's temporary directory.</summary>
    internal static void Delete(MapEditSession session)
    {
        string directory = Path.GetDirectoryName(session.DocumentPath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
