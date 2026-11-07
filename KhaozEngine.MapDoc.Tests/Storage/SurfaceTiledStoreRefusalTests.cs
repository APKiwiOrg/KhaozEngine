using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceTiledStoreRefusalTests
{
    [Fact]
    public void DictionaryKeyMismatch_LeavesSavedContentUntouched()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            MapSurfacePatch patch = Assert.Single(doc.Surfaces.Patches.Values);
            MapPatchKey key = patch.Key;
            patch.Key = key with { SlotX = 1 };
            var before = AssertFixtures.Files(root);
            Assert.NotEmpty(before);
            var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, root));
            Assert.Equal(MapDocumentRefusal.StoragePatchKeyMismatch, error.Reason);
            Assert.Equal(key, error.ExpectedPatch);
            Assert.Equal(patch.Key, error.Patch);
            Assert.Equal(before, AssertFixtures.Files(root));
            patch.Key = key;
            MapDocumentFile.SaveTiled(doc, root);
            Assert.Empty(MapDocumentFile.VerifyTiled(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, 17, 16)]
    [InlineData(false, 0, 1)]
    [InlineData(false, 17, 16)]
    public void Packing_ReportsTheFirstFailedFieldAndAdmitsItsBoundary(bool index, int invalid, int valid)
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            var packing = index ? new MapSurfacePacking(invalid, 16) : new MapSurfacePacking(16, invalid);
            var before = AssertFixtures.Files(root);
            Assert.NotEmpty(before);
            var error = Assert.Throws<MapDocumentException>(() => MapTiledFile.Save(doc, root, MapDocRegistry.CreateDefault(), null, packing));
            Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
            Assert.Equal(index ? "IndexBlockSlots" : "DirectoryBlockPages", error.FieldPath);
            Assert.Equal(invalid, error.ActualInteger);
            Assert.Equal(1, error.MinimumInteger);
            Assert.Equal(16, error.MaximumInteger);
            Assert.Equal(before, AssertFixtures.Files(root));
            packing = index ? new(valid, 16) : new(16, valid);
            MapTiledFile.Save(doc, root, MapDocRegistry.CreateDefault(), null, packing);
            Assert.Empty(MapDocumentFile.VerifyTiled(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PageBlockOverflow_UsesTheLastRepresentableBlockAsItsControl(bool x)
    {
        long invalid = long.MaxValue / 16;
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceTiledStore.PageBlockForTests(x ? invalid : 0, x ? 0 : invalid, 16));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("PackSurfaces", error.Operation);
        Assert.Equal("Covers", error.FieldPath);
        MapSlotRect control = MapSurfaceTiledStore.PageBlockForTests(x ? invalid - 1 : 0, x ? 0 : invalid - 1, 16);
        Assert.Equal(invalid * 16, x ? control.MaxXExclusive : control.MaxZExclusive);
    }

    [Fact]
    public void ChangedPreparedPayload_PreservesManifestAndSweepsUnreferencedPayload()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(2);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            byte[] manifest = File.ReadAllBytes(Path.Combine(root, "map.json"));
            string saved = MapDocumentFile.SaveText(MapDocumentFile.LoadTiled(root));
            // X27: unreferenced content may be written before the manifest commits.
            MapSurfacePatch first = doc.Surfaces.Patches.Values.First();
            first.Heights[0]++;
            MapSurfaceTiledStore.Prepared prepared = MapSurfaceTiledStore.Prepare(doc, root, null);
            Assert.Equal(2, prepared.Payloads.Count);
            string stray = MapSurfaceStorageLayout.PathOf(root, 'p', prepared.Payloads[0].Digest);
            Assert.False(File.Exists(stray));
            var payload = prepared.Payloads[1];
            payload.Patch.Heights[0]++;
            string changed = MapSurfaceStorageLayout.Digest(MapSurfacePatchCodec.Encode(payload.Patch));
            var before = AssertFixtures.Files(root);
            Assert.NotEmpty(before);
            var error = Assert.Throws<MapDocumentException>(() => MapSurfaceTiledStore.Write(root, prepared, new(), new HashSet<string>()));
            Assert.Equal(MapDocumentRefusal.StorageSaveSnapshotChanged, error.Reason);
            Assert.Equal(payload.Patch.Key, error.Patch);
            Assert.Equal(payload.Digest, error.ExpectedHash);
            Assert.Equal(changed, error.ActualHash);
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(root, "map.json")));
            Assert.Equal(saved, MapDocumentFile.SaveText(MapDocumentFile.LoadTiled(root)));
            Assert.All(before, file => Assert.Equal(file.Sha256, AssertFixtures.Sha256(Path.Combine(root, file.Path))));
            Assert.True(File.Exists(stray));
            payload.Patch.Heights[0]--;
            first.Heights[0]--;
            MapDocumentFile.SaveTiled(doc, root);
            Assert.Empty(MapDocumentFile.VerifyTiled(root));
            Assert.False(File.Exists(stray));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData('d', false)]
    [InlineData('d', true)]
    [InlineData('i', false)]
    [InlineData('i', true)]
    [InlineData('p', false)]
    [InlineData('p', true)]
    public void UnavailableStorage_ReportsItsPinnedSubjectAndPreservesKnownPayloadReasons(char kind, bool corrupt)
    {
        string root = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.FlatPatches(1));
        try
        {
            string file = Assert.Single(Directory.GetFiles(Path.Combine(root, "tiles", "surfaces", kind.ToString()), "*.json", SearchOption.AllDirectories));
            byte[] original = File.ReadAllBytes(file);
            if (corrupt) File.WriteAllBytes(file, [0]);
            else File.Delete(file);
            var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadTiled(root));
            Assert.Equal(corrupt && kind == 'p' ? MapDocumentRefusal.DigestMismatch : MapDocumentRefusal.PatchUnavailable, error.Reason);
            Assert.Equal(corrupt ? MapPatchStatus.Corrupt : MapPatchStatus.Missing, error.PatchStatus);
            Assert.Equal(Path.GetFileNameWithoutExtension(file), error.ExpectedHash);
            if (kind == 'p') Assert.Equal(new MapPatchKey("ground", 0, 0), error.Patch);
            else Assert.Equal("ground", error.SurfaceId);
            File.WriteAllBytes(file, original);
            Assert.Single(MapDocumentFile.LoadTiled(root).Surfaces.Patches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WholeSurfaceDigest_ReportsTheSurfaceAndBothPins()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            MapSurfaceRef surface = Assert.Single(doc.Surfaces.Refs);
            string expected = new('a', 64);
            doc.Surfaces.Refs[0] = surface with { SemanticSha256 = expected };
            doc.Surfaces.Patches.Clear();
            var error = Assert.Throws<MapDocumentException>(() => MapSurfaceTiledStore.Load(root, doc, doc.Tiles!, null));
            Assert.Equal(MapDocumentRefusal.DigestMismatch, error.Reason);
            Assert.Equal(surface.Id, error.SurfaceId);
            Assert.Equal(expected, error.ExpectedHash);
            Assert.Equal(surface.SemanticSha256, error.ActualHash);
            doc.Surfaces.Refs[0] = surface;
            doc.Surfaces.Patches.Clear();
            MapSurfaceTiledStore.Load(root, doc, doc.Tiles!, null);
            Assert.Single(doc.Surfaces.Patches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WindowOverflow_ReportsTheLoadOperation()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        string root = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            var invalid = new MapTileIndex(float.MaxValue, MapDocumentHash.SchemeVersion, root, [], surfaces: doc.Tiles!.Surfaces);
            var window = new MapTileRect(new(0, 0), new(0, 0));
            var error = Assert.Throws<MapDocumentException>(() => MapSurfaceTiledStore.Load(root, doc, invalid, window));
            Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
            Assert.Equal("LoadSurfaces", error.Operation);
            Assert.Equal("Window", error.FieldPath);
            doc.Surfaces.Patches.Clear();
            MapSurfaceTiledStore.Load(root, doc, doc.Tiles!, window);
            Assert.Single(doc.Surfaces.Patches);
        }
        finally { Directory.Delete(root, true); }
    }
}
