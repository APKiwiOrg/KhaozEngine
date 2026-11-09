using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneSculptCorruptionTests
{
    [Theory]
    [InlineData("string")]
    [InlineData("integer-range")]
    [InlineData("float-range")]
    public void MalformedSculptNumber_ReturnsCorruptSource(string corruption)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        fixture.Document.TerrainOverrides = new(MapTerrainOverrides.DefaultCellSize);
        fixture.Document.TerrainOverrides.PutTile(new(0, 0,
            Enumerable.Repeat(.25f, TerrainSculpt.TileSize * TerrainSculpt.TileSize).ToArray()));
        using var document = NativeSceneFixtures.OpenTiled(fixture);
        string directory = document.Tiles.SourceDirectory!;
        try
        {
            string path = Directory.GetFiles(Path.Combine(directory, "tiles"), "*.json", SearchOption.AllDirectories).Single();
            var tile = JsonNode.Parse(File.ReadAllText(path))!;
            var sculpt = tile["sculpt"]![0]!;
            if (corruption == "string") sculpt["deltas"]![0] = "bad";
            else if (corruption == "integer-range") sculpt["tileX"] = 2147483648L;
            else sculpt["deltas"]![0] = 1e100;
            File.WriteAllText(path, tile.ToJsonString());
            var result = MapSceneSource.Capture(document, fixture.Assets);
            Assert.Equal(MapSceneSourceStatus.Corrupt, result.Status);
            Assert.Null(result.Source);
            Assert.Equal(0, result.Work.RetainedBytes);
        }
        finally { Directory.Delete(directory, true); }
    }
}
