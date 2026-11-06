using System;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceStorageLayoutRefusalTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, MapSurfaceStorageLayout.MaxPageBytes + 1)]
    [InlineData(true, MapSurfaceStorageLayout.MaxPageBytes + 1)]
    public void StorageBytes_RefuseOutsideTheDomainWithoutWritingAndAcceptTheEndpoint(bool write, int length) => TiledDocFixture.InDirectory(dir =>
    {
        byte[] bytes = length == 0 ? [] : Encoding.UTF8.GetBytes("0" + new string(' ', length - 1));
        string digest = MapSurfaceStorageLayout.Digest(bytes);
        if (!write)
        {
            string path = MapSurfaceStorageLayout.PathOf(dir, 'i', digest);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        File.WriteAllText(Path.Combine(dir, "existing"), "unchanged");
        var saved = AssertFixtures.Files(dir);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (write) MapSurfaceStorageLayout.Write(dir, 'i', bytes, new());
            else _ = MapSurfaceStorageLayout.Read(dir, 'i', digest);
        });
        Assert.Equal(length == 0 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(length, error.ActualByteCount);
        Assert.Equal("ByteCount", error.FieldPath);
        if (length == 0) Assert.Equal(1, error.MinimumInteger);
        else
        {
            Assert.Equal("MaxPageBytes", error.Budget);
            Assert.Equal(MapSurfaceStorageLayout.MaxPageBytes, error.Limit);
        }
        Assert.Equal(saved, AssertFixtures.Files(dir));
        int[] acceptedLengths = length == 0 ? [1] : [MapSurfaceStorageLayout.MaxPageBytes, MapSurfaceStorageLayout.MaxPageBytes - 1];
        foreach (int acceptedLength in acceptedLengths)
        {
            byte[] control = Encoding.UTF8.GetBytes("0" + new string(' ', acceptedLength - 1));
            MapSurfaceStorageLayout.Write(dir, 'i', control, new());
            Assert.Equal(control, MapSurfaceStorageLayout.Read(dir, 'i', MapSurfaceStorageLayout.Digest(control)));
        }
    });

    [Theory]
    [InlineData("../outside")]
    [InlineData("short")]
    [InlineData("upper")]
    public void DigestNames_RefuseBeforeAddressingAFile(string shape) => TiledDocFixture.InDirectory(dir =>
    {
        string invalid = shape == "upper" ? new string('A', 64) : shape;
        var saved = AssertFixtures.Files(dir);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceStorageLayout.Read(dir, 'p', invalid));
        Assert.Equal(MapDocumentRefusal.DigestMalformed, error.Reason);
        Assert.Equal(invalid, error.ActualHash);
        Assert.Equal("sha256", error.FieldPath);
        Assert.Equal(saved, AssertFixtures.Files(dir));
        byte[] control = [123, 125];
        MapSurfaceStorageLayout.Write(dir, 'p', control, new());
        Assert.Equal(control, MapSurfaceStorageLayout.Read(dir, 'p', MapSurfaceStorageLayout.Digest(control)));
    });

    [Fact]
    public void GrowingRead_IdentifiesTheChangedSnapshotBeforeDigestValidation()
    {
        byte[] bytes = [123, 125];
        string digest = MapSurfaceStorageLayout.Digest(bytes);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceStorageLayout.RequireUnchangedReadForTests(bytes, digest, 32));
        Assert.Equal(MapDocumentRefusal.StorageReadChanged, error.Reason);
        Assert.Equal(2, error.ExpectedByteCount);
        Assert.Equal(3, error.ActualByteCount);
        Assert.Equal(digest, error.ExpectedHash);
        Assert.Null(error.ActualHash);
        MapSurfaceStorageLayout.RequireUnchangedReadForTests(bytes, digest, -1);
    }

    [Fact]
    public void UnrepresentableStorageBounds_RefuseBeforeWriting() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        MapDocumentFile.SaveTiled(doc, dir);
        var saved = AssertFixtures.Files(dir);
        var original = doc.Surfaces.Patches.Single();
        MapSurfacePatch patch = original.Value.Clone();
        patch.Key = new("ground", long.MaxValue, 0);
        doc.Surfaces.Patches.Clear();
        doc.Surfaces.Patches.Add(patch.Key, patch);
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, dir));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("SaveSurfaces", error.Operation);
        Assert.Equal("Surfaces", error.FieldPath);
        Assert.Equal(saved, AssertFixtures.Files(dir));
        doc.Surfaces.Patches.Clear();
        doc.Surfaces.Patches.Add(original.Key, original.Value);
        MapDocumentFile.SaveTiled(doc, dir);
        Assert.Empty(MapDocumentFile.VerifyTiled(dir));
    });
}
