using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceStorageTests
{
    static readonly MapPatchKey Ridge = new("ridge", 0, 0), Ground0 = new("ground", 0, 0),
        Ground1 = new("ground", 1, 0), FarEast = new("far", 300, 0), FarWest = new("far", -300, 0);
    static string Refusal(MapDocument doc, string dir) => Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, dir)).Message;

    [Theory]
    [InlineData((int)MapTiledSaveStep.BeforeTileWrite)]
    [InlineData((int)MapTiledSaveStep.BeforeManifestRename)]
    [InlineData((int)MapTiledSaveStep.AfterManifestRename)]
    public void SurfaceStorage_ManifestLastCommitAndForms(int failAtValue) => TiledDocFixture.InDirectory(dir =>
    {
        var failAt = (MapTiledSaveStep)failAtValue;
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        string expected = SurfaceStorageFixtures.SemanticSnapshot(doc);
        MapDocument textRoundTrip = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(expected, SurfaceStorageFixtures.SemanticSnapshot(textRoundTrip));
        Assert.Null(textRoundTrip.Tiles);
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument whole = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(expected, SurfaceStorageFixtures.SemanticSnapshot(whole));
        Assert.False(whole.Tiles!.IsPartial);
        Assert.False(whole.Tiles.HasUnloadedTiles);
        Assert.False(whole.Tiles.Surfaces!.IsPartial);
        Assert.Equal(5, whole.Surfaces.Patches.Count);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        doc.Surfaces.Patches[Ridge].Heights[0] = 1250;
        var save = new MapDocumentSaveOptions { OnStep = s => { if (s == failAt) throw new IOException("injected"); } };
        Assert.Throws<IOException>(() => MapDocumentFile.SaveTiled(doc, dir, save: save));
        bool committed = failAt == MapTiledSaveStep.AfterManifestRename;
        Assert.Equal(committed, !manifest.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "map.json"))));
        Assert.Equal(committed ? 1250 : 1500, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[Ridge].Heights[0]);
        Assert.All(MapDocumentFile.VerifyTiled(dir), f => Assert.True(f.StartsWith("orphan", StringComparison.Ordinal) || f.StartsWith("stray temp", StringComparison.Ordinal), f));
    });
    [Fact]
    public void SurfaceStorage_SweepKeepsTilesAndReachablePagesAndRemovesStrays() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));
        MapDocumentFile.SaveTiled(doc, dir);
        string stray = Path.Combine(dir, "tiles", "surfaces", "p", "aa", new string('a', 64) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "{}");
        doc.Surfaces.Patches[Ridge].Heights[0] = 1250;
        MapDocumentFile.SaveTiled(doc, dir);
        Assert.False(File.Exists(stray));
        Assert.Empty(MapDocumentFile.VerifyTiled(dir));
        Assert.Contains(TiledDocFixture.TileFiles(dir), f => !f.StartsWith(Path.Combine("tiles", "surfaces"), StringComparison.Ordinal));
        Assert.Equal(1250, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[Ridge].Heights[0]);
        foreach (string relative in SurfaceStorageFixtures.SurfaceFiles(dir))
        {
            string full = Path.Combine(dir, relative);
            string digest = AssertFixtures.Sha256(full);
            Assert.Equal(digest, Path.GetFileNameWithoutExtension(full));
            string normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
            bool payload = normalized.StartsWith("tiles/surfaces/p/", StringComparison.Ordinal);
            Assert.True(payload || normalized.StartsWith("tiles/surfaces/i/", StringComparison.Ordinal) ||
                normalized.StartsWith("tiles/surfaces/d/", StringComparison.Ordinal), relative);
            Assert.InRange(new FileInfo(full).Length, 1L, 1_048_576L);
            if (payload) Assert.Equal($"tiles/surfaces/p/{digest[..2]}/{digest}.json", normalized);
        }
    });
    [Fact]
    public void SurfaceStorage_SweepIsSkippedAfterAnUnreadablePreviousManifest() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        MapDocumentFile.SaveTiled(doc, dir);
        var before = SurfaceStorageFixtures.SurfaceFiles(dir);
        File.WriteAllText(Path.Combine(dir, "map.json"), "{");
        doc.Surfaces.Patches[Ridge].Heights[0] = 1250;
        MapDocumentFile.SaveTiled(doc, dir); // whole document: no stale check, no sweep
        Assert.All(before, relative => Assert.True(File.Exists(Path.Combine(dir, relative)), relative));
        Assert.Equal(1250, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[Ridge].Heights[0]);
    });
    [Fact]
    public void SurfaceStorage_WindowCarriesUnloadedPatchesAndRefusesUnloadedDependencies() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        byte[] ground1 = SurfaceStorageFixtures.PayloadBytes(dir, Ground1);
        byte[] east = SurfaceStorageFixtures.PayloadBytes(dir, FarEast);
        byte[] west = SurfaceStorageFixtures.PayloadBytes(dir, FarWest);
        string[] before = SurfaceStorageFixtures.SurfaceFiles(dir).ToArray();
        var unchangedStamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string relative in before)
            File.SetLastWriteTimeUtc(Path.Combine(dir, relative), unchangedStamp);
        MapDocument w0 = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        Assert.True(w0.Tiles!.IsPartial);
        Assert.False(w0.Tiles.HasUnloadedTiles);
        Assert.True(w0.Tiles.Surfaces!.IsPartial);
        MapDirectoryPageRef[] unread = w0.Tiles.Surfaces.Directory.Where(p => p.SurfaceId == "far").ToArray();
        Assert.NotEmpty(unread);
        w0.Surfaces.Patches[Ground0].Heights[6] = 1100; // interior corner (1, 1), no dependent
        MapDocumentFile.SaveTiled(w0, dir);
        Assert.Equal(ground1, SurfaceStorageFixtures.PayloadBytes(dir, Ground1));
        Assert.Equal(east, SurfaceStorageFixtures.PayloadBytes(dir, FarEast));
        Assert.Equal(west, SurfaceStorageFixtures.PayloadBytes(dir, FarWest));
        Assert.Equal(unread, w0.Tiles!.Surfaces!.Directory.Where(p => p.SurfaceId == "far"));
        foreach (string relative in before.Intersect(SurfaceStorageFixtures.SurfaceFiles(dir)))
            Assert.Equal(unchangedStamp, File.GetLastWriteTimeUtc(Path.Combine(dir, relative)));
        MapDocument roundTrip = MapDocumentFile.LoadTiled(dir);
        Assert.Equal((5, 1100, 2000, 2000), (roundTrip.Surfaces.Patches.Count,
            roundTrip.Surfaces.Patches[Ground0].Heights[6], roundTrip.Surfaces.Patches[FarEast].Heights[0],
            roundTrip.Surfaces.Patches[FarWest].Heights[0]));
        MapDocument w2 = SurfaceStorageFixtures.LoadSlotWindow(dir, 2);
        Assert.Equal((true, false), (w2.Surfaces.Patches.ContainsKey(Ground1), w2.Surfaces.Patches.ContainsKey(Ground0)));
        var files = SurfaceStorageFixtures.SurfaceFiles(dir);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        w2.Surfaces.Patches[Ground1].Heights[0] = 1200; // the dependent corner at cell X 64
        Assert.Contains("unloaded", Refusal(w2, dir));
        Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
    });
    [Fact]
    public void SurfaceStorage_NewPatchUnderAnUnreadIndexPageRefuses() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        var pageSeed = new MapPatchKey("ground", 41, 0);
        doc.Surfaces.Patches.Add(pageSeed, SurfaceStorageFixtures.FlatPatch(pageSeed, 1000));
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument w0 = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        var key = new MapPatchKey("ground", 40, 0);
        Assert.False(w0.Surfaces.Patches.ContainsKey(pageSeed));
        Assert.Equal(MapPatchStatus.Unloaded, w0.Tiles!.Surfaces!.StatusOf(key));
        var files = SurfaceStorageFixtures.SurfaceFiles(dir);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        w0.Surfaces.Patches.Add(key, SurfaceStorageFixtures.FlatPatch(key, 1000));
        Assert.Contains("unloaded", Refusal(w0, dir));
        Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
    });
    [Theory]
    [InlineData("dependant")]
    [InlineData("page")]
    [InlineData("records")]
    [InlineData("delete")]
    [InlineData("presence")]
    public void PartialSave_RefusesEditsWithUnknownOrUnloadedReverseDependants(string variant) => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(variant switch
        {
            "records" => SurfaceStorageFixtures.ThreeSurfaces(),
            "delete" or "presence" => SurfaceStorageFixtures.RecordReferrer(),
            _ => SurfaceStorageFixtures.ReverseDependants(withFinePage: variant == "page"),
        }, dir);
        var files = SurfaceStorageFixtures.SurfaceFiles(dir);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        MapDocument w0 = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        string expected = "unsupported partial edit: structure";
        switch (variant)
        {
            case "records":
                Assert.Equal(1, w0.Surfaces.Patches[Ridge].Records.RemoveAll(r => r.Id == "ridge-rim")); // nothing references it, still refused
                expected = "unsupported partial edit: records";
                break;
            case "delete":
                Assert.False(w0.Surfaces.Patches.ContainsKey(SurfaceStorageFixtures.YardFar)); // yard's only referrer: unloaded, not spatial, not incident
                Assert.True(w0.Surfaces.Patches.Remove(SurfaceStorageFixtures.Yard0)); // no new Records left to compare, still refused
                break;
            case "presence":
                w0.Surfaces.Patches[SurfaceStorageFixtures.Yard0].SetPresent(1, 1, false);
                break;
            default:
                Assert.False(w0.Surfaces.Patches.ContainsKey(SurfaceStorageFixtures.Edge1));
                w0.Surfaces.Patches[SurfaceStorageFixtures.Wide0].Heights[1] = 1100; // corner (1, 0), never a dependency target
                expected = variant == "page" ? "unloaded: reverse dependants unknown" : "unloaded: reverse dependant 'edge";
                break;
        }
        Assert.Contains(expected, Refusal(w0, dir));
        Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
    });
    [Fact]
    public void RawSave_RefusesACornerDependencyWhoseOwnerVertexIsElsewhere() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        var deps = doc.Surfaces.Patches[Ground1].CornerDependencies;
        deps[0] = deps[0] with { Owner = new MapVertexOwner(Ground0, MapLatticeAddress.Corner(63, 0)) }; // a real vertex of ground(0,0), 1 m west of cell X 64
        Assert.Contains("dependency position", Refusal(doc, dir));
        Assert.Contains("dependency position", Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc)).Message);
        Assert.Empty(SurfaceStorageFixtures.SurfaceFiles(dir));
    });
    [Fact]
    public void StaleWindow_RefusesAfterAnotherWriterReplacedAnUnloadedSurfacePatch() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        MapDocument whole = MapDocumentFile.LoadTiled(dir);
        whole.Surfaces.Patches[FarEast].Heights[0] = 2100;
        MapDocumentFile.SaveTiled(whole, dir);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        window.Surfaces.Patches[Ground0].Heights[6] = 1100;
        Assert.Contains("stale window", Refusal(window, dir));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
        Assert.Equal(2100, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[FarEast].Heights[0]);
    });
    [Fact]
    public void SurfacesOnlyWindows_SecondSaveRefusesAsStale() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        MapDocument a = SurfaceStorageFixtures.LoadSlotWindow(dir, 300), b = SurfaceStorageFixtures.LoadSlotWindow(dir, -300);
        Assert.Equal((0, false, true), (a.Tiles!.Entries.Count, a.Tiles.HasUnloadedTiles, a.Tiles.IsPartial));
        b.Surfaces.Patches[FarWest].Heights[0] = 2100;
        MapDocumentFile.SaveTiled(b, dir);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        a.Surfaces.Patches[FarEast].Heights[0] = 2200;
        Assert.Contains("stale window", Refusal(a, dir));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
        MapDocument whole = MapDocumentFile.LoadTiled(dir);
        Assert.Equal((2100, 2000), (whole.Surfaces.Patches[FarWest].Heights[0], whole.Surfaces.Patches[FarEast].Heights[0]));
    });
    [Fact]
    public void SurfacesOnlyWindow_IsPartialEverywhereACompleteDocumentIsRequired() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        MapDocument w = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
        Assert.Contains("windowed", Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(w)).Message);
        TiledDocFixture.InDirectory(other => Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveAs(w, Path.Combine(other, "m.json"), MapDocumentForm.Monolithic)));
        Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.Validate(w, SurfaceStorageFixtures.Assets()));
        Assert.Throws<MapDocumentException>(() => NativeDocumentSnapshot.Clone(w, MapDocRegistry.CreateDefault()));
        MapDocumentFile.SaveTiled(w, dir); // own-directory save of the unchanged window
        Assert.Equal(AssertFixtures.Sha256(Path.Combine(dir, "map.json")), w.Tiles!.ManifestSha256);
        Assert.True(w.Tiles.Surfaces!.IsPartial);
        using MapDocumentSource source = MapDocumentSource.OpenTiled(dir);
        Assert.Empty(source.Tiles.Surfaces!.ReadDirectoryPages);
        Assert.Empty(source.Tiles.Surfaces.ReadIndexPages);
        Assert.False(source.Tiles.HasUnloadedTiles);
        Assert.Equal(MapPatchStatus.Unloaded, source.Tiles.Surfaces.StatusOf(FarEast));
        Assert.True(source.Manifest.Tiles!.IsPartial);
        source.Refresh();
        Assert.True(source.Manifest.Tiles!.IsPartial);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
        var files = SurfaceStorageFixtures.SurfaceFiles(dir);
        MapDocument removedRef = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
        Assert.Equal(1, removedRef.Surfaces.Refs.RemoveAll(s => s.Id == "ridge"));
        Assert.Contains("unsupported partial edit: surface", Refusal(removedRef, dir));
        MapDocument reframed = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
        int farIndex = reframed.Surfaces.Refs.FindIndex(s => s.Id == "far");
        Assert.True(farIndex >= 0);
        MapSurfaceRef far = reframed.Surfaces.Refs[farIndex];
        reframed.Surfaces.Refs[farIndex] = far with { Frame = far.Frame with { RowDirection = MapRowDirection.NegativeZ } };
        Assert.Contains("unsupported partial edit: surface", Refusal(reframed, dir));
        Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
    });
    [Fact]
    public void SurfaceStorage_MonolithicEmbeddingIsBounded() => TiledDocFixture.InDirectory(dir =>
    {
        Assert.Equal((256, 8_388_608L), (MapSurfaceEmbedding.MaxPatches, MapSurfaceEmbedding.MaxEncodedBytes));
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(3);
        Assert.Contains("tiled", Assert.Throws<MapDocumentException>(() => MapSurfaceEmbedding.Check(doc.Surfaces, 2, long.MaxValue)).Message);
        Assert.Contains("tiled", Assert.Throws<MapDocumentException>(() => MapSurfaceEmbedding.Check(doc.Surfaces, 3, 10)).Message);
        MapSurfaceEmbedding.Check(doc.Surfaces, 3, long.MaxValue);
        string path = Path.Combine(dir, "m.json");
        string text = MapDocumentFile.SaveText(doc);
        using (JsonDocument embedded = JsonDocument.Parse(text))
        {
            Assert.Equal(3, embedded.RootElement.GetProperty("surfacePatches").GetArrayLength());
        }
        MapDocumentFile.Save(doc, path);
        MapDocument monolithic = MapDocumentFile.Load(path);
        Assert.Equal(3, monolithic.Surfaces.Patches.Count);
        Assert.Null(monolithic.Tiles);
        string semantic = SurfaceStorageFixtures.SemanticSnapshot(monolithic);
        MapTiledFile.Save(monolithic, dir, MapDocRegistry.CreateDefault(), null, new MapSurfacePacking(1, 1));
        string repackedGeneration = AssertFixtures.Sha256(Path.Combine(dir, "map.json"));
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "map.json"))))
        {
            Assert.True(manifest.RootElement.GetProperty("surfaceStorage").GetArrayLength() > 0);
        }
        Assert.Equal(semantic, SurfaceStorageFixtures.SemanticSnapshot(MapDocumentFile.LoadTiled(dir)));
        MapDocumentFile.SaveTiled(monolithic, dir);
        Assert.NotEqual(repackedGeneration, AssertFixtures.Sha256(Path.Combine(dir, "map.json")));
        Assert.Equal(semantic, SurfaceStorageFixtures.SemanticSnapshot(MapDocumentFile.LoadTiled(dir)));
        Assert.Contains("tiled", Assert.Throws<MapDocumentException>(() =>
            MapDocumentFile.SaveText(SurfaceStorageFixtures.FlatPatches(257))).Message);
    });
}
