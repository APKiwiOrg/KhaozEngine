using System.IO;
using KhaozEngine.Dungeon;
using KhaozEngine.MapEditor;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase;

public class RoomMapEditorDungeonOptionsTests
{
    [Fact]
    public void ShowcaseEditorLoadsTheKitUsedByItsDungeonAction()
    {
        string assets = Path.Combine("test-root", "assets");

        MapEditorOptions options = RoomMapEditor.CreateOptions(assets);

        Assert.Contains(Path.Combine(assets, "dungeon", "dungeon.manifest.json"), options.ManifestPaths);
        Assert.NotNull(options.DungeonKit);
        Assert.Equal("dungeon_floor", options.DungeonKit.Require(DungeonPiece.Floor));
        Assert.Equal("dungeon_wall", options.DungeonKit.Require(DungeonPiece.Wall));
        Assert.Equal("dungeon_ceiling", options.DungeonKit.Require(DungeonPiece.Ceiling));
    }
}
