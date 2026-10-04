using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Netcode;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// <see cref="ShardedWorldServerConfig.CellOrigin"/>: a world that extends below an axis origin chooses where its
/// cells fall instead of always being split at 0.
/// </summary>
public class ShardedCellOriginTests
{
    private const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    // One 1024 m cell covering a region column at x -64 to -1 beside a world that starts at x 0, and z from -320.
    private static readonly Vector2 WorldOrigin = new(-64f, -320f);

    private static ShardedWorldServerConfig Config(float cellSize, Vector2 origin, bool frameAnchoring = false,
        Action<CellCoord>? onPhysicsWorld = null) => new()
        {
            TickSeconds = Dt,
            CellSize = cellSize,
            CellOrigin = origin,
            OverlapMargin = 24f,
            InterestRadius = 24f,
            MaxPlayers = 8,
            FrameAnchoring = frameAnchoring,
            PhysicsWorldFactory = onPhysicsWorld is null ? null : coord =>
            {
                onPhysicsWorld(coord);
                return new BepuPhysicsWorld();
            },
        };

    private static ShardedWorldServer Server(ShardedWorldServerConfig config)
    {
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        return new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
    }

    [Fact]
    public void Default_CellOrigin_IsTheWorldOrigin()
    {
        Assert.Equal(Vector2.Zero, new ShardedWorldServerConfig().CellOrigin);
    }

    [Fact]
    public void With_the_origin_bodies_either_side_of_x_zero_share_one_cell_and_one_physics_world()
    {
        var built = new List<CellCoord>();
        using ShardedWorldServer server = Server(Config(1024f, WorldOrigin, onPhysicsWorld: built.Add));

        server.SpawnEntity(-63.5f, -100f);
        server.SpawnEntity(319.5f, -100f);
        server.SpawnEntity(-63.5f, -319.5f);
        server.SpawnEntity(319.5f, -0.5f);

        Assert.Equal(new[] { new CellCoord(0, 0) }, server.LiveCellCoords);
        Assert.Equal(new[] { new CellCoord(0, 0) }, built);
        Assert.True(server.TryGetCellCoord(-63.5f, -100f, out CellCoord west));
        Assert.True(server.TryGetCellCoord(319.5f, -100f, out CellCoord east));
        Assert.Equal(west, east);
    }

    [Fact]
    public void Without_the_origin_the_same_bodies_split_into_two_cells_at_x_zero()
    {
        var built = new List<CellCoord>();
        using ShardedWorldServer server = Server(Config(1024f, Vector2.Zero, onPhysicsWorld: built.Add));

        server.SpawnEntity(-63.5f, -100f);
        server.SpawnEntity(319.5f, -100f);

        Assert.Equal(new[] { new CellCoord(-1, -1), new CellCoord(0, -1) }, server.LiveCellCoords);
        Assert.Equal(2, built.Count);
    }

    [Fact]
    public void Each_cell_frame_is_nearest_its_centre_on_the_shifted_grid()
    {
        // 300 m cells keep FrameAnchoring's precision guard satisfied. Cell (0, 0) spans x [-64, 236) and
        // z [-320, -20), so its centre is (86, -170).
        using ShardedWorldServer server = Server(Config(300f, WorldOrigin, frameAnchoring: true));
        Assert.Equal(WorldFrame.Nearest(86f, -170f), server.FrameFor(new CellCoord(0, 0)));
        Assert.Equal(WorldFrame.Nearest(386f, 130f), server.FrameFor(new CellCoord(1, 1)));
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 0f)]
    public void A_non_finite_origin_is_refused_at_construction(float x, float z)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Server(Config(1024f, new Vector2(x, z))));
        Assert.Contains("CellOrigin", ex.Message, StringComparison.Ordinal);
    }
}
