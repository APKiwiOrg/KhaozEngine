using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The two handedness facts <see cref="TileSteering"/> stands on, both established from the code rather than
/// derived, and both pinned here so a later change to either one fails loudly.
/// <para>ONE: tile north is world MINUS z. <c>TileWorldSpace.WorldZ</c> negates
/// (KhaozEngine.TileWorld/TileWorldSpace.cs:18) and <c>TileWorldSpace.TileZ</c> negates back
/// (KhaozEngine.TileWorld/TileWorldSpace.cs:24), so a camera looking along world +z is looking tile SOUTH. Tile
/// x and world x agree (KhaozEngine.TileWorld/TileWorldSpace.cs:15).</para>
/// <para>TWO: the engine's cameras are right handed with y up, so screen right of a camera whose ground forward
/// is world f is world (-f.Z, 0, f.X). <c>FollowCamera3D.View</c> is
/// <c>Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY)</c> (KhaozEngine.Render3D/Camera/FollowCamera3D.cs:319),
/// whose right-handed basis puts world +x on the LEFT of a camera facing world +z, exactly as
/// KhaozEngine.TileWorld/TileWorldSpace.cs:5 says. Looking tile north is looking world -z, and screen right there
/// is world +x, which is tile east.</para>
/// <para>The two mirrors cancel in tile space, so screen right of a TILE forward (fx, fz) is (fz, -fx). Every
/// case below is written from the player's seat: W walks away from the camera, S toward it, D toward screen
/// right and A toward screen left.</para>
/// </summary>
public class TileSteeringTests
{
    // World -z is tile north. The downward y component is the tilt of a follow camera and must not matter.
    static readonly Vector3 LookingNorth = new(0f, -0.7f, -1f);

    [Theory]
    [InlineData(0, 1, TileDirection.N)]
    [InlineData(0, -1, TileDirection.S)]
    [InlineData(1, 0, TileDirection.E)]
    [InlineData(-1, 0, TileDirection.W)]
    [InlineData(1, 1, TileDirection.NE)]
    [InlineData(-1, 1, TileDirection.NW)]
    [InlineData(1, -1, TileDirection.SE)]
    [InlineData(-1, -1, TileDirection.SW)]
    public void Looking_north_the_keys_are_the_compass(int right, int forward, TileDirection expected) =>
        Assert.Equal(expected, TileSteering.FromAxes(right, forward, LookingNorth));

    [Fact]
    public void Looking_east_forward_is_east_and_right_is_south()
    {
        var east = new Vector3(1f, -0.5f, 0f);
        Assert.Equal(TileDirection.E, TileSteering.FromAxes(0, 1, east));
        Assert.Equal(TileDirection.S, TileSteering.FromAxes(1, 0, east));
    }

    /// <summary>
    /// The mirror itself. A camera looking along world +z is looking tile SOUTH, so W walks south and D, which is
    /// screen right and therefore world -x, walks tile west. This is the case a helper that read world z as tile
    /// z would get exactly backwards.
    /// </summary>
    [Fact]
    public void Looking_along_world_plus_z_is_looking_tile_south()
    {
        var worldPlusZ = new Vector3(0f, -0.5f, 1f);
        Assert.Equal(TileDirection.S, TileSteering.FromAxes(0, 1, worldPlusZ));
        Assert.Equal(TileDirection.N, TileSteering.FromAxes(0, -1, worldPlusZ));
        Assert.Equal(TileDirection.W, TileSteering.FromAxes(1, 0, worldPlusZ));
        Assert.Equal(TileDirection.E, TileSteering.FromAxes(-1, 0, worldPlusZ));
    }

    [Fact]
    public void A_camera_between_compass_points_snaps_forward_to_the_nearest_direction()
    {
        // 0.7 rad (40 degrees) east of north is nearer NE, 0.3 rad (17 degrees) is nearer N. The boundary is 22.5.
        // East of north is +x and -world z, so the z component carries the tile mirror.
        Assert.Equal(TileDirection.NE, TileSteering.FromAxes(0, 1, new Vector3(MathF.Sin(0.7f), 0f, -MathF.Cos(0.7f))));
        Assert.Equal(TileDirection.N, TileSteering.FromAxes(0, 1, new Vector3(MathF.Sin(0.3f), 0f, -MathF.Cos(0.3f))));
    }

    [Fact]
    public void No_input_is_null() => Assert.Null(TileSteering.FromAxes(0, 0, LookingNorth));

    [Fact]
    public void A_camera_looking_straight_down_is_null() =>
        Assert.Null(TileSteering.FromAxes(0, 1, new Vector3(0f, -1f, 0f)));

    [Fact]
    public void An_unnormalized_look_vector_answers_the_same() =>
        Assert.Equal(TileSteering.FromAxes(1, 1, LookingNorth), TileSteering.FromAxes(1, 1, LookingNorth * 40f));

    [Fact]
    public void Axes_beyond_one_are_clamped() =>
        Assert.Equal(TileDirection.NE, TileSteering.FromAxes(5, 9, LookingNorth));

    [Fact]
    public void An_exact_octant_boundary_answers_the_same_direction_every_time()
    {
        // 22.5 degrees east of north, the seam between N and NE. Which of the two it picks is the rounding rule's
        // business and is not asserted to the last bit here. That it never wavers is.
        var boundary = new Vector3(MathF.Sin(MathF.PI / 8f), 0f, -MathF.Cos(MathF.PI / 8f));
        TileDirection? first = TileSteering.FromAxes(0, 1, boundary);
        Assert.NotNull(first);
        Assert.True(first is TileDirection.N or TileDirection.NE, $"expected N or NE, got {first}");
        for (int i = 0; i < 8; i++) Assert.Equal(first, TileSteering.FromAxes(0, 1, boundary));
    }
}
