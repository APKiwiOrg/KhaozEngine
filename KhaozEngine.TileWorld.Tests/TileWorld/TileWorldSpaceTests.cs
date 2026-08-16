using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The one seam between tile space and world space: the z flip, its round trip, and the compass fact
/// the flip exists for.</summary>
public class TileWorldSpaceTests
{
    [Fact]
    public void ToWorld_keeps_x_and_negates_z()
    {
        Assert.Equal(new Vector3(10f, 2f, -20f), TileWorldSpace.ToWorld(10f, 2f, 20f, 1f));
        Assert.Equal(new Vector3(20f, 2f, -40f), TileWorldSpace.ToWorld(10f, 2f, 20f, 2f));
    }

    [Fact]
    public void The_two_conversions_round_trip_on_both_axes()
    {
        const float TileSize = 2.5f;
        Assert.Equal(10f, TileWorldSpace.TileX(TileWorldSpace.WorldX(10f, TileSize), TileSize), 4);
        Assert.Equal(-7.5f, TileWorldSpace.TileZ(TileWorldSpace.WorldZ(-7.5f, TileSize), TileSize), 4);
        Assert.Equal(12f, TileWorldSpace.WorldZ(TileWorldSpace.TileZ(12f, TileSize), TileSize), 4);
    }

    [Fact]
    public void Facing_north_puts_east_on_the_right()
    {
        // The basis a right-handed viewer standing in the world has: forward is where north points in world
        // space, up is world up, right is forward cross up. That right has to come out EAST, which is the whole
        // reason world z is minus tile z. Map north onto +z instead and the same cross product points west, so
        // the world renders as its own mirror image against a compass.
        Vector3 forward = Vector3.Normalize(TileWorldSpace.ToWorld(0f, 0f, 1f, 1f));
        Vector3 right = Vector3.Cross(forward, Vector3.UnitY);

        Assert.Equal(new Vector3(0f, 0f, -1f), forward);
        Assert.Equal(1f, right.X, 4);
        Assert.Equal(0f, right.Y, 4);
        Assert.Equal(0f, right.Z, 4);
        // East is +x in both spaces, which is what a right of (+1, 0, 0) says.
        Assert.Equal(1f, TileWorldSpace.WorldX(1f, 1f), 4);
    }
}
