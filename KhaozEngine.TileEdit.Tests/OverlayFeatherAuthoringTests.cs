using KhaozEngine.TileEdit;
using KhaozEngine.TileEdit.Tools;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

public sealed class OverlayFeatherAuthoringTests
{
    [Fact]
    public void Named_flag_survives_editor_undo_redo_and_save_without_changing_collision()
    {
        using var temp = new TempDir();
        string path = temp.Sub("world");
        TileEditSession session = TileEditTestWorld.NewSession(path);
        var query = new QueryService(session);
        var mutate = new MutationService(session);
        var tools = new TileTools(query, mutate);
        tools.TileSet(10, 10, 0, underlay: 1, overlay: 2, settings: "Blocked, Bridge");
        TileInfo before = query.TileGet(10, 10, 0);
        tools.TileSet(10, 10, 0, settings: "Blocked, Bridge, FeatherOverlay");
        Assert.Equal(before.Blocked, query.TileGet(10, 10, 0).Blocked);
        mutate.Undo();
        Assert.Equal("Blocked,Bridge", query.TileGet(10, 10, 0).Settings);
        mutate.Redo();
        session.Save();
        TileWorldDocument loaded = TileWorldFile.Load(path);
        Assert.Equal(TileSettings.Blocked | TileSettings.Bridge | TileSettings.FeatherOverlay,
            loaded.GetSettings(10, 10, 0));
        loaded.SetSettings(10, 10, 0, TileSettings.None);
        TileCollisionMap hard = TileCollisionBaker.Bake(loaded, TileWorldCatalogs.Greybox());
        loaded.SetSettings(10, 10, 0, TileSettings.FeatherOverlay);
        TileCollisionMap soft = TileCollisionBaker.Bake(loaded, TileWorldCatalogs.Greybox());
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++) Assert.Equal(hard.Get(x, z, 0), soft.Get(x, z, 0));
    }
}
