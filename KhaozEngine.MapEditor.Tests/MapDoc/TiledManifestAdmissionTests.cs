using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TiledManifestAdmissionTests
{
    [Theory]
    [InlineData("missing", MapDocumentRefusal.DocumentManifestMissing)]
    [InlineData("read", MapDocumentRefusal.DocumentManifestReadFailed)]
    [InlineData("tile", MapDocumentRefusal.SchemaObjectRequired)]
    [InlineData("nonfinite", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("zero", MapDocumentRefusal.ValueOutsideRange)]
    public void ManifestAdmission_IdentifiesTheRefusedInputAndAcceptsItsRepair(string variant, MapDocumentRefusal reason) => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir);
        string path = Path.Combine(dir, "map.json");
        byte[] original = File.ReadAllBytes(path);
        MapDocumentException error;
        if (variant == "missing") File.Delete(path);
        else if (variant != "read")
        {
            var root = JsonNode.Parse(original)!.AsObject();
            if (variant == "tile") root["tiles"]![0] = 0;
            else root["sculptCellSize"] = variant == "zero" ? JsonValue.Create(0) : JsonNode.Parse("1e100");
            File.WriteAllText(path, root.ToJsonString());
        }
        var saved = AssertFixtures.Files(dir);
        if (variant == "read")
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            error = Assert.Throws<MapDocumentException>(() => MapDocumentSource.OpenTiled(dir));
        }
        else error = Assert.Throws<MapDocumentException>(() => MapDocumentSource.OpenTiled(dir));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.Path);
        if (variant == "missing") Assert.Equal("map.json", error.FileName);
        else if (variant == "tile")
        {
            Assert.Equal("$.tiles[0]", error.FieldPath);
            Assert.Equal(0, error.Index);
        }
        else if (variant is "nonfinite" or "zero")
        {
            Assert.Equal("$.sculptCellSize", error.FieldPath);
            Assert.Equal(variant == "zero" ? 0d : double.PositiveInfinity, error.ActualNumber);
        }
        Assert.Equal(saved, AssertFixtures.Files(dir));
        File.WriteAllBytes(path, original);
        using var control = MapDocumentSource.OpenTiled(dir);
        Assert.NotEmpty(control.Tiles.Entries);
    });

    [Theory]
    [InlineData(false, MapDocumentRefusal.DocumentStorageFormConflict)]
    [InlineData(true, MapDocumentRefusal.DocumentLoadedIndexRequired)]
    public void SaveAdmission_LeavesTheExistingTargetByteIdentical(bool existingTiled, MapDocumentRefusal reason) => TiledDocFixture.InDirectory(dir =>
    {
        string target = Path.Combine(dir, "target");
        if (existingTiled) MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), target);
        else MapDocumentFile.Save(TiledDocFixture.SampleDoc(), target);
        var saved = AssertFixtures.Files(dir);
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), target));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(Path.GetFullPath(target), error.Path);
        if (!existingTiled)
        {
            Assert.Equal(MapDocumentForm.Monolithic, error.DocumentForm);
            Assert.Equal(MapDocumentForm.Tiled, error.ExpectedDocumentForm);
        }
        Assert.Equal(saved, AssertFixtures.Files(dir));
        if (existingTiled) MapDocumentFile.SaveTiled(MapDocumentFile.LoadTiled(target), target);
        else
        {
            File.Delete(target);
            MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), target);
        }
        Assert.Empty(MapDocumentFile.VerifyTiled(target));
    });
}
