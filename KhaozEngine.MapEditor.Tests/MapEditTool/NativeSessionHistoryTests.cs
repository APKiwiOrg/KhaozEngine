using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class NativeSessionHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedNativeHistory_RestoresRootsClosureEffectsAndDirty(bool water)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("mixed-history"));
        var states = PrefabEditorFixtures.ApplyMixedHistory(session, water);
        Assert.Equal(4, states.Count);
        using (var middle = JsonDocument.Parse(states[2].Effects!))
        {
            Assert.NotEmpty(middle.RootElement.GetProperty("waterBodyIds").EnumerateArray());
            Assert.True(((MapNativeInvalidation)middle.RootElement.GetProperty("invalidates").GetInt32()).HasFlag(MapNativeInvalidation.Medium));
        }
        long events = 3;
        for (int i = 2; i >= 0; i--)
        {
            var effects = Assert.IsType<MapNativeEditEffects>(session.UndoNative());
            Assert.NotEqual(MapNativeInvalidation.None, effects.Invalidates);
            AssertEffects(effects, states[i + 1].Effects!, reverse: true);
            PrefabEditorFixtures.AssertPublishedState(session, states[i]);
            Assert.Equal(++events, session.NativeHistoryState.Events);
        }
        for (int i = 1; i < 4; i++)
        {
            var effects = Assert.IsType<MapNativeEditEffects>(session.RedoNative());
            AssertEffects(effects, states[i].Effects!, reverse: false);
            PrefabEditorFixtures.AssertPublishedState(session, states[i]);
            Assert.Equal(++events, session.NativeHistoryState.Events);
        }
    }
    [Fact]
    public void TerrainOnlyEdit_IsNotMistakenForALegacyHashNoOp()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        var before = PrefabEditorFixtures.State(session);
        new MutationService(session).TerrainApply(new MapSetCornerHeights(new("exterior", 0, 0), 0, 0, 17, 17,
            System.Linq.Enumerable.Repeat(-175, 289).ToArray()));
        Assert.Equal((1, 1, 1L), session.NativeHistoryState);
        Assert.NotEqual(before.Identity, PrefabEditorFixtures.State(session).Identity);
        Assert.NotNull(session.UndoNative());
        Assert.Equal(before.DocumentBytes, PrefabEditorFixtures.State(session).DocumentBytes);
    }
    [Fact]
    public void OuterValidationReject_DoesNotAcceptCommandOrMoveHistory()
    {
        var f = PrefabEditorFixtures.Create("empty-map");
        var session = PrefabEditorFixtures.Session(f);
        var before = PrefabEditorFixtures.State(session);
        var command = new PrefabEditCommand(new MapPlacePrefab("new", "hut", new(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabEditorFixtures.ParentRef(f)));
        session.BeforeNativePublication = () => throw new MapDocumentException("outer rejection");
        Assert.Throws<MapDocumentException>(() => session.ExecuteNativeCommand(command));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.BeforeNativePublication = null;
        session.ExecuteNativeCommand(command);
        Assert.Equal((1, 1, 1L), session.NativeHistoryState);
        Assert.NotNull(session.UndoNative());
        Assert.NotNull(session.RedoNative());
    }
    [Fact]
    public void SessionBoundPreparation_UsesTheSameCommandAndRetriesAtomically()
    {
        var f = PrefabEditorFixtures.Create("empty-map");
        var session = PrefabEditorFixtures.Session(f);
        var edit = new MapPlacePrefab("new", "hut", new(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabEditorFixtures.ParentRef(f));
        var command = new PrefabEditCommand(edit);
        Assert.Throws<MapDocumentException>(() => command.Apply(f.Document));
        session.ExecuteNativeCommand(command);
        var other = PrefabEditorFixtures.Session(f);
        var serviceEffects = new MutationService(other).PrefabApply(edit).Effects;
        Assert.Equal(PrefabEditorFixtures.State(session).DocumentBytes, PrefabEditorFixtures.State(other).DocumentBytes);
        Assert.Equal(serviceEffects.Invalidates, session.LastNativeEffects!.Invalidates);
        Assert.Equal(serviceEffects.SpaceIds, session.LastNativeEffects.SpaceIds);
    }
    [Fact]
    public void StorageBoundary_ResetsHistoryOnlyAfterSuccess()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        PrefabEditorFixtures.ApplyGeneric(session, "scatter");
        var before = PrefabEditorFixtures.State(session);
        Assert.Throws<MapDocumentException>(() => session.Open(session.DocumentPath! + ".missing"));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.Throws<MapDocumentException>(() => session.ConvertToTiled(session.DocumentPath!));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.Save();
        Assert.Equal(1, session.NativeHistoryState.Depth);
        string target = Path.Combine(Path.GetDirectoryName(session.DocumentPath)!, "converted.map.json");
        session.ConvertToSingle(target);
        Assert.Equal(0, session.NativeHistoryState.Depth);
        Assert.Null(session.UndoNative());
    }
    static void AssertEffects(MapNativeEditEffects effects, string forward, bool reverse)
    {
        using var expected = JsonDocument.Parse(forward); using var actual = JsonDocument.Parse(effects.Describe());
        foreach (string name in new[] { "patches", "spaceIds", "dependencyIds", "waterBodyIds", "instanceIds", "childIds", "roofIds", "lightIds", "markerIds", "invalidates" })
            if (expected.RootElement.TryGetProperty(name, out var value))
                Assert.Equal(value.GetRawText(), actual.RootElement.GetProperty(name).GetRawText());
        Assert.Equal(expected.RootElement.GetProperty(reverse ? "newBounds" : "oldBounds").GetRawText(), actual.RootElement.GetProperty("oldBounds").GetRawText());
        Assert.Equal(expected.RootElement.GetProperty(reverse ? "oldBounds" : "newBounds").GetRawText(), actual.RootElement.GetProperty("newBounds").GetRawText());
    }
    [Theory]
    [InlineData("single")]
    [InlineData("tiled")]
    [InlineData("retile")]
    [InlineData("window")]
    [InlineData("open")]
    public void SuccessfulReplacementOrStoragePublication_IsAHistoryBarrier(string operation)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        PrefabEditorFixtures.ApplyGeneric(session, "scatter");
        string root = Path.GetDirectoryName(session.DocumentPath)!;
        string tiled = Path.Combine(root, "tiled");
        if (operation is "tiled" or "window")
        {
            session.WithNativeAssets(c =>
            {
                foreach (var resource in c.Assets.CapturedResources)
                {
                    string target = Path.Combine(tiled, resource.Reference.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, resource.Bytes.ToArray());
                }
                return 0;
            });
            session.ConvertToTiled(tiled);
            if (operation == "window")
            {
                new MutationService(session).TerrainEdit(seed: 42);
                session.SetWindow(-32, -32, 32, 32, true);
            }
        }
        else if (operation == "single") session.ConvertToSingle(Path.Combine(root, "converted.map.json"));
        else if (operation == "retile") session.Retile(32);
        else { session.Save(); session.Open(session.DocumentPath!); }
        Assert.Equal(0, session.NativeHistoryState.Depth); Assert.False(session.IsDirty);
        Assert.Null(session.UndoNative()); Assert.Null(session.RedoNative());
    }
    [Theory]
    [InlineData("save")]
    [InlineData("retile")]
    [InlineData("window")]
    public void FailedStorageOrReplacement_DoesNotChangeHistoryClosureOrDirty(string operation)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        PrefabEditorFixtures.ApplyGeneric(session, "scatter");
        var before = PrefabEditorFixtures.State(session);
        if (operation == "save")
        {
            string resource = session.WithNativeAssets(c => Path.Combine(c.ResourceRoot, c.Assets.Roots.Single().Path));
            byte[] bytes = File.ReadAllBytes(resource);
            try
            {
                File.WriteAllText(resource, "corrupt");
                Assert.Throws<MapDocumentException>(() => session.Save());
            }
            finally { File.WriteAllBytes(resource, bytes); }
        }
        else if (operation == "retile")
        {
            File.Delete(session.DocumentPath!);
            Assert.Throws<MapDocumentException>(() => session.Retile(32));
        }
        else Assert.Throws<InvalidOperationException>(() => session.SetWindow(-32, -32, 32, 32));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }
    [Fact]
    public void BindingSameClosurePreservesHistory_AndDifferentAcceptedClosureClearsIt()
    {
        var f = PrefabFixtures.Create("roomless"); var session = PrefabEditorFixtures.Session(f);
        new MutationService(session).PrefabApply(new MapMovePrefab("a", new(new(2, 0, -2), 0, 1)));
        var before = PrefabEditorFixtures.State(session);
        session.BindNativeAssets(session.WithNativeAssets(c => c.Assets));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        var asset = f.Assets.GetPrefab("hut"); var child = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        var value = MapPrefabJson.Copy(child.Value); value.X = 3;
        var afterAssets = PrefabFixtures.PublishPrefab(f.Assets, MapPrefabAssetEdits.Replace(asset, child with { Value = value }));
        Assert.Throws<MapDocumentException>(() => session.BindNativeAssets(afterAssets));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.WithDocument((doc, registry) =>
        {
            var prepared = MapPrefabEdits.PrepareAssetUpdate(doc, f.Assets, afterAssets, new("hut", f.Assets.GetResource("hut").Reference.Sha256,
                "hut", afterAssets.GetResource("hut").Reference.Sha256));
            NativeDocumentSnapshot.Publish(doc, prepared.After); return 0;
        });
        session.BindNativeAssets(afterAssets);
        Assert.Equal(0, session.NativeHistoryState.Depth); Assert.Null(session.UndoNative());
    }
    [Fact]
    public void StagedGestureMergeAndRejectedUndo_DoNotAdvanceStateBeforePublication()
    {
        var data = PrefabEditorFixtures.CoalescedAttachedLeaf("move");
        var session = PrefabEditorFixtures.Session(new(data.Document, data.Assets));
        session.ExecuteNativeCommand((EditorCommand)data.First);
        var first = PrefabEditorFixtures.State(session);
        session.BeforeNativePublication = () => throw new MapDocumentException("outer rejection");
        Assert.Throws<MapDocumentException>(() => session.ExecuteNativeCommand((EditorCommand)data.Second));
        Assert.Equal(first, PrefabEditorFixtures.State(session));
        session.BeforeNativePublication = null;
        session.ExecuteNativeCommand((EditorCommand)data.Second);
        var final = PrefabEditorFixtures.State(session);
        Assert.Equal((1, 1, 2L), session.NativeHistoryState);
        session.BeforeNativePublication = () => throw new MapDocumentException("outer rejection");
        Assert.Throws<MapDocumentException>(() => session.UndoNative()); Assert.Equal(final, PrefabEditorFixtures.State(session));
        session.BeforeNativePublication = null;
        Assert.NotNull(session.UndoNative()); Assert.NotNull(session.RedoNative());
        Assert.Equal(final.DocumentBytes, PrefabEditorFixtures.State(session).DocumentBytes);
    }

    [Fact]
    public void NamedFactoryEdits_CannotSupplyAnUnpublishedDependency()
    {
        var f = PrefabFixtures.Create(); var parent = f.Document.Surfaces.Clone();
        f.Document.Prefabs = new(1, 1, [], []); f.Document.Surfaces = new();
        var session = PrefabEditorFixtures.Session(f); var before = PrefabEditorFixtures.State(session);
        var mutation = new MutationService(session);
        Assert.Throws<MapDocumentException>(() => mutation.Apply((candidate, _) =>
        {
            candidate.Surfaces = parent.Clone();
            return new PrefabEditCommand(new MapPlacePrefab("new", "hut", new(Vector3.Zero, 0, 1), new("exterior", new("exterior", 0, 0))));
        }, "test", _ => "test", true));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_SealsTheGestureOnlyAfterSuccess(bool fail)
    {
        var data = PrefabEditorFixtures.CoalescedAttachedLeaf("move");
        var session = PrefabEditorFixtures.Session(new(data.Document, data.Assets));
        session.ExecuteNativeCommand((EditorCommand)data.First);
        if (fail)
        {
            string resource = session.WithNativeAssets(c => Path.Combine(c.ResourceRoot, c.Assets.Roots.Single().Path));
            byte[] bytes = File.ReadAllBytes(resource);
            try { File.WriteAllText(resource, "corrupt"); Assert.Throws<MapDocumentException>(() => session.Save()); }
            finally { File.WriteAllBytes(resource, bytes); }
        }
        else session.Save();
        var saved = PrefabEditorFixtures.State(session);
        session.ExecuteNativeCommand((EditorCommand)data.Second);
        Assert.Equal(fail ? 1 : 2, session.NativeHistoryState.Depth);
        Assert.NotNull(session.UndoNative());
        if (!fail) Assert.Equal(saved.DocumentBytes, PrefabEditorFixtures.State(session).DocumentBytes);
    }

}
