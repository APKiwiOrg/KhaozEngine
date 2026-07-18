using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public class PropSurfacesTests
{
    static readonly ScatterConfig Cfg = ScatterConfig.ForestRing(seed: 1337);
    static TerrainField Field() => new(TerrainPresets.Clearing());
    static PropSurface FlatTop(float y) { float n = float.NaN; return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, y, n, y, y, y, n, y, n }); }

    [Fact]
    public void FromScatter_OneSurfacePerWalkablePlacement()
    {
        var f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        Assert.NotEmpty(placements);
        WorldSurfaces set = PropSurfaces.FromScatter(placements, _ => FlatTop(1.5f));
        Assert.Equal(placements.Count, set.Count);
    }

    [Fact]
    public void FromScatter_SkipsPlacementsWithoutASurface()
    {
        var f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        WorldSurfaces set = PropSurfaces.FromScatter(placements, _ => null);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void FromScatter_ObstaclesIncluded()
    {
        var f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        var roof = new WorldSurface(FlatTop(4f), new Vector2(0f, 12f), 1f, 0f, 0f);
        WorldSurfaces set = PropSurfaces.FromScatter(placements, _ => null, obstacles: new[] { roof });
        Assert.Equal(1, set.Count);
    }
}
