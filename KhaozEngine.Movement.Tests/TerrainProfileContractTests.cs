using System.IO;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class TerrainProfileContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DisabledZeroPermissionRejectsAnInternallySealedDrop(bool accepted, bool downhill)
    {
        // #1697 F1. Keep the exact equal-height hostile file and an actually descending, accepted variant.
        // The planner already rejects accepted level Drops, but permission and the stored workload remain load contracts.
        var profile = new NavBakeProfile("player", ContactNavigationFixture.Tuning, default);
        NavLink drop = new(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop };
        byte[] control = TerrainProfileFile.Create(profile, [[0, 0]], [], out var expected);
        Assert.Equal(NavBakeLoadStatus.Loaded, GroundNavigationBake.Load(new MemoryStream(control), expected).Status);
        byte[] hostile = TerrainProfileFile.Create(profile, [[downhill ? 1 : 0, 0]], [drop], out expected, accepted);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(new MemoryStream(hostile), expected).Status);
    }

    [Theory]
    [InlineData("reordered")]
    [InlineData("incomplete")]
    [InlineData("same-layer")]
    public void DisabledCandidatesKeepTheFrozenRecipeAndOrder(string fault)
    {
        // #1697 F1. Disabled workloads retain the complete frozen recipe and its order.
        var profile = new NavBakeProfile("player", ContactNavigationFixture.Tuning, default);
        NavLink[] recipe = [new(0, 0, 0, 1, 1, 0), new(1, 1, 0, 0, 0, 0),
            new(0, 1, 0, 1, 0, 0), new(1, 0, 0, 0, 1, 0)];
        byte[] control = TerrainProfileFile.Create(profile, [[0, 0], [0, 0]], recipe, out var expected);
        var loaded = GroundNavigationBake.Load(new MemoryStream(control), expected);
        Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
        Assert.Equal(recipe, loaded.Bake!.GetProfile("player").Space.Links);
        NavLink[] malformed = fault == "reordered" ? [recipe[1], recipe[0], recipe[2], recipe[3]] :
            fault == "incomplete" ? [recipe[0], recipe[1]] : [new(0, 0, 0, 0, 1, 0)];
        byte[] hostile = TerrainProfileFile.Create(profile, [[0, 0], [0, 0]], malformed, out expected);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(new MemoryStream(hostile), expected).Status);
    }

    [Theory]
    [InlineData("level-drop")]
    [InlineData("uphill-drop")]
    [InlineData("over-allowance")]
    [InlineData("same-layer-stair")]
    [InlineData("same-column-stair")]
    [InlineData("high-stair")]
    public void EnabledStoredCandidatesStillRequireLegalLinkShapes(string fault)
    {
        // #1697 F1. Authentication does not grant structurally illegal links.
        var profile = new NavBakeProfile("player", ContactNavigationFixture.Tuning, default)
        { MaxDropMetres = 6, AllowTerrainDescents = true };
        NavLink drop = new(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop };
        byte[] control = TerrainProfileFile.Create(profile, [[1, 0]], [drop], out var expected, accepted: false);
        Assert.Equal(NavBakeLoadStatus.Loaded, GroundNavigationBake.Load(new MemoryStream(control), expected).Status);
        float[][] heights = fault switch
        {
            "level-drop" => [[0, 0]],
            "uphill-drop" => [[0, 1]],
            "over-allowance" => [[7, 0]],
            "high-stair" => [[0, 0], [1, 1]],
            _ => [[0, 0], [0, 0]]
        };
        NavLink link = fault.Contains("stair", System.StringComparison.Ordinal)
            ? fault == "same-layer-stair" ? new(0, 0, 0, 0, 1, 0) :
                fault == "same-column-stair" ? new(0, 0, 0, 1, 0, 0) : new(0, 0, 0, 1, 1, 0) : drop;
        byte[] hostile = TerrainProfileFile.Create(profile, heights, [link], out expected, accepted: false);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(new MemoryStream(hostile), expected).Status);
    }
}
