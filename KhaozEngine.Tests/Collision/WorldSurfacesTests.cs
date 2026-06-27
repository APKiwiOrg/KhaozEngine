using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class WorldSurfacesTests
{
    static PropSurface FlatTop(float y)
    {
        float n = float.NaN;
        return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, y, n, y, y, y, n, y, n });
    }

    [Fact]
    public void Empty_QueryIsNull()
    {
        var set = new WorldSurfaces(new List<WorldSurface>());
        Assert.True(set.IsEmpty);
        Assert.Null(set.Query(0f, 0f));
    }

    [Fact]
    public void Query_ReturnsMaxOverOverlapping()
    {
        var low = new WorldSurface(FlatTop(1f), new Vector2(0f, 0f), 1f, 0f, 0f);
        var high = new WorldSurface(FlatTop(3f), new Vector2(0f, 0f), 1f, 0f, 0f);
        var set = new WorldSurfaces(new[] { low, high });
        Assert.Equal(3f, set.Query(0f, 0f)!.Value, 3); // the higher surface wins
    }

    [Fact]
    public void Query_FarFromAny_IsNull()
    {
        var set = new WorldSurfaces(new[] { new WorldSurface(FlatTop(2f), new Vector2(100f, 100f), 1f, 0f, 0f) });
        Assert.Null(set.Query(0f, 0f));
    }
}
