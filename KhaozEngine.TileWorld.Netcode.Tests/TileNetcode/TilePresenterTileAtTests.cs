using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// <see cref="TilePresenter.TryTileAt"/>, the inverse of <see cref="TilePresenter.PoseAt(TileCoord, TileDirection)"/>
/// that an admin teleport snaps a world position with. The property that matters is the round trip: a position read
/// back off a tile lands on that tile again, planes included.
/// </summary>
public class TilePresenterTileAtTests
{
    // A ground that ramps east and differs per plane by more than the ramp does across a tile, so a vertical snap
    // that read the plane floor instead of the drawn height, or sampled off the tile centre, picks the wrong plane.
    sealed class Ramp : ITileGroundHeight
    {
        public float HeightAt(float tileX, float tileZ, int plane) => plane * 4f + tileX * 0.25f + tileZ * 0.05f;
    }

    [Theory]
    [InlineData(1f, 3f)]
    [InlineData(1.5f, 2.5f)]
    [InlineData(0.5f, 0f)]
    public void Every_tile_pose_snaps_back_onto_its_own_tile(float tileSize, float planeHeight)
    {
        var presenter = new TilePresenter(tileSize, planeHeight);
        int planes = planeHeight == 0f ? 1 : 4;
        foreach (int x in new[] { -70, -1, 0, 5, 63, 64, 1000 })
            foreach (int z in new[] { -65, -1, 0, 9, 63, 200 })
                for (int plane = 0; plane < planes; plane++)
                {
                    var tile = new TileCoord(x, z, plane);
                    Assert.True(presenter.TryTileAt(presenter.PoseAt(tile).Position, 4, out TileCoord back));
                    Assert.Equal(tile, back);
                }
    }

    [Fact]
    public void Every_point_inside_a_tiles_span_answers_that_tile()
    {
        var presenter = new TilePresenter(2f, 3f);
        var tile = new TileCoord(7, 4, 0);
        // Tile (7, 4) spans x 14..16 metres and, z being negated, world z -10..-8.
        foreach (Vector3 p in new[]
                 {
                     new Vector3(14.001f, 0f, -8.001f), new Vector3(15.999f, 0f, -9.999f),
                     new Vector3(15f, 0.4f, -9f), new Vector3(14f, 0f, -8.5f),
                 })
        {
            Assert.True(presenter.TryTileAt(p, 4, out TileCoord back));
            Assert.Equal(tile, back);
        }
    }

    [Fact]
    public void The_plane_is_the_nearest_drawn_height_and_the_ends_clamp()
    {
        var presenter = new TilePresenter(1f, 3f);

        Assert.True(presenter.TryTileAt(new Vector3(0.5f, 4.4f, -0.5f), 4, out TileCoord t));
        Assert.Equal(1, t.Plane);
        Assert.True(presenter.TryTileAt(new Vector3(0.5f, 4.6f, -0.5f), 4, out t));
        Assert.Equal(2, t.Plane);
        // Above the top plane snaps onto it, below the bottom one onto plane 0.
        Assert.True(presenter.TryTileAt(new Vector3(0.5f, 500f, -0.5f), 4, out t));
        Assert.Equal(3, t.Plane);
        Assert.True(presenter.TryTileAt(new Vector3(0.5f, -50f, -0.5f), 4, out t));
        Assert.Equal(0, t.Plane);
        // Exactly between two planes is a tie, and a tie goes to the lower plane.
        Assert.True(presenter.TryTileAt(new Vector3(0.5f, 1.5f, -0.5f), 4, out t));
        Assert.Equal(0, t.Plane);
        // Planes that all draw at one height have nothing to tell apart.
        Assert.True(new TilePresenter(1f, 0f).TryTileAt(new Vector3(0.5f, 9f, -0.5f), 4, out t));
        Assert.Equal(0, t.Plane);
    }

    [Fact]
    public void A_presenter_with_ground_snaps_the_plane_against_the_ground_it_draws()
    {
        var presenter = new TilePresenter(1f, 3f, new Ramp());
        foreach (int x in new[] { 0, 3, 11 })
            for (int plane = 0; plane < 4; plane++)
            {
                var tile = new TileCoord(x, 2, plane);
                Assert.True(presenter.TryTileAt(presenter.PoseAt(tile).Position, 4, out TileCoord back));
                Assert.Equal(tile, back);
            }
    }

    [Fact]
    public void A_point_that_names_no_tile_is_refused()
    {
        var presenter = new TilePresenter(1f, 3f);

        Assert.False(presenter.TryTileAt(new Vector3(float.NaN, 0f, 0f), 4, out TileCoord t));
        Assert.Equal(default, t);
        Assert.False(presenter.TryTileAt(new Vector3(0f, float.PositiveInfinity, 0f), 4, out _));
        Assert.False(presenter.TryTileAt(new Vector3(0f, 0f, float.NegativeInfinity), 4, out _));
        Assert.False(presenter.TryTileAt(new Vector3(1e30f, 0f, 0f), 4, out _));
        Assert.False(presenter.TryTileAt(new Vector3(0f, 0f, -1e30f), 4, out _));
        Assert.False(presenter.TryTileAt(Vector3.Zero, 0, out _));
    }
}
