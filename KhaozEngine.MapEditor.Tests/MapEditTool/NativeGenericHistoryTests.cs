using System;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class NativeGenericHistoryTests
{
    [Theory]
    [InlineData("scatter")]
    [InlineData("bake")]
    public void GenericSnapshot_ScatterAndBakeSurviveHistoryAndReload(string variant)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        var before = PrefabEditorFixtures.State(session);
        PrefabEditorFixtures.ApplyGeneric(session, variant);
        PrefabEditorFixtures.AssertGenericRoots(session, variant, true);
        Assert.NotEqual(before.Identity, PrefabEditorFixtures.State(session).Identity);
        Assert.Equal((1, 1, 1L), session.NativeHistoryState);
        session.Save();
        Assert.False(session.IsDirty);
        Assert.Equal((1, 1, 1L), session.NativeHistoryState);
        PrefabEditorFixtures.AssertSavedGenericRoots(session, variant, true);
        Assert.NotNull(session.UndoNative());
        PrefabEditorFixtures.AssertGenericRoots(session, variant, false);
        Assert.True(session.IsDirty);
        Assert.Equal((0, 1, 2L), session.NativeHistoryState);
        PrefabEditorFixtures.AssertSavedGenericRoots(session, variant, false);
        Assert.NotNull(session.RedoNative());
        PrefabEditorFixtures.AssertGenericRoots(session, variant, true);
        Assert.Equal((1, 1, 3L), session.NativeHistoryState);
        PrefabEditorFixtures.AssertSavedGenericRoots(session, variant, true);
    }
    [Theory]
    [InlineData("scatter")]
    [InlineData("bake")]
    public void OuterValidationReject_DiscardsEntireGenericCandidate(string variant)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        var before = PrefabEditorFixtures.State(session);
        session.BeforeNativePublication = () => throw new MapDocumentException("outer rejection");
        Assert.Throws<MapDocumentException>(() => PrefabEditorFixtures.ApplyGeneric(session, variant));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.BeforeNativePublication = null;
        PrefabEditorFixtures.ApplyGeneric(session, variant);
        Assert.Equal((1, 1, 1L), session.NativeHistoryState);
    }
    [Fact]
    public void InvalidDensityAndNoOp_LeaveHistoryAndDirtyUntouched()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        var before = PrefabEditorFixtures.State(session);
        Assert.Throws<InvalidOperationException>(() => new MutationService(session).ScatterOverrideEdit(0, densityMultiplier: -1));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        new MutationService(session).ScatterOverrideEdit(0, densityMultiplier: 1);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }
    [Theory]
    [InlineData("terrain")]
    [InlineData("sculpt")]
    [InlineData("flatten")]
    [InlineData("clear")]
    [InlineData("freeze")]
    public void CompleteCallbacks_RestoreEveryCapturedRoot(string variant)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        var mutation = new MutationService(session);
        if (variant == "clear") mutation.SculptApply("raise", 0, 0, 2, 1, 1);
        var before = PrefabEditorFixtures.State(session);
        switch (variant)
        {
            case "terrain": mutation.TerrainEdit(-7, 23, 16, 0.04f, 0.25f, 0.05f, 4); break;
            case "sculpt": mutation.SculptApply("raise", 0, 0, 2, 1, 1); break;
            case "flatten": mutation.SculptFlattenRegion(0, 0, 2, 2, 5); break;
            case "clear": mutation.SculptClear(); break;
            default: mutation.FreezeZone(); break;
        }
        var after = PrefabEditorFixtures.State(session);
        Assert.NotEqual(before.DocumentBytes, after.DocumentBytes);
        Assert.Equal(before.Cursor + 1, after.Cursor); Assert.Equal(before.EventCount + 1, after.EventCount);
        if (variant == "terrain")
        {
            var terrain = PrefabEditorFixtures.Document(session).Terrain;
            Assert.Equal((-7f, 23, 16f, 0.04f, 0.25f, 0.05f, 4),
                (terrain.WaterLevel, terrain.Seed, terrain.BiomeBlend, terrain.GentleFrequency, terrain.GentleAmplitude, terrain.DetailFrequency, terrain.DetailOctaves));
        }
        if (variant == "freeze")
        {
            var doc = PrefabEditorFixtures.Document(session);
            Assert.Empty(doc.ScatterLayers); Assert.Empty(doc.CompanionLayers); Assert.Empty(doc.Exclusions); Assert.Empty(doc.ScatterOverrides);
            Assert.Empty(doc.Placements);
        }
        Assert.NotNull(session.UndoNative()); Assert.Equal(before.DocumentBytes, PrefabEditorFixtures.State(session).DocumentBytes);
        Assert.NotNull(session.RedoNative()); Assert.Equal(after.DocumentBytes, PrefabEditorFixtures.State(session).DocumentBytes);
    }
    [Fact]
    public void GenericTiledEdit_UsesLoadedContentRatherThanStoredTileHashes()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("native-generic"));
        var doc = PrefabEditorFixtures.Document(session);
        string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(session.DocumentPath)!, "tiled");
        session.WithNativeAssets(c =>
        {
            foreach (var resource in c.Assets.CapturedResources)
            {
                string target = System.IO.Path.Combine(dir, resource.Reference.Path);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                System.IO.File.WriteAllBytes(target, resource.Bytes.ToArray());
            }
            return 0;
        });
        session.ConvertToTiled(dir);
        var before = PrefabEditorFixtures.State(session);
        session.Mutate((candidate, _) => { candidate.PlayerSpawns.Add(new() { Id = "native-start", X = 2, Z = -2 }); return 0; }, false);
        Assert.Equal((1, 1, before.EventCount + 1), session.NativeHistoryState);
        Assert.Single(PrefabEditorFixtures.Document(session).PlayerSpawns);
        Assert.NotNull(session.UndoNative()); Assert.Empty(PrefabEditorFixtures.Document(session).PlayerSpawns);
    }

}
