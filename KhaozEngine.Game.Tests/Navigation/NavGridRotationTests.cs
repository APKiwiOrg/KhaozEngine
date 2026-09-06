using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavGridRotationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(MathF.PI / 2)]
    [InlineData(-0.7f)]
    public void RotatedWall_PathSegmentsRemainClear(float yaw)
    {
        var origin = new Vector2(37, -81);
        NavGrid grid = NavGrid.FromWalkable(30, 30, 0.5f, origin.X, origin.Y,
            (x, z) => !(x == 15 && z <= 25), yawRadians: yaw);
        Vector2 start = Transform(new Vector2(3.25f, 3.25f), origin, yaw);
        Vector2 goal = Transform(new Vector2(12.25f, 3.25f), origin, yaw);
        NavPath path = new GridPathPlanner(NavSpace.Single(grid)).FindPath(
            new Vector3(start.X, 0, start.Y), new Vector3(goal.X, 0, goal.Y), 0.2f);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(path.Waypoints.Count >= 3, "A straight segment would cross the wall.");
        var points = new List<Vector2> { start };
        foreach (NavWaypoint waypoint in path.Waypoints) points.Add(waypoint.Position);
        Matrix3x2 inverseRotation = Matrix3x2.CreateRotation(-yaw);
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 a = Vector2.Transform(points[i - 1] - origin, inverseRotation);
            Vector2 b = Vector2.Transform(points[i] - origin, inverseRotation);
            Assert.True(GridRay.IsClear(a, b, grid.CellSize,
                (x, z) => !grid.IsPassable(x, z, 0.2f), includeEndpointCells: false),
                $"Segment {i} cuts through the rotated wall.");
        }
        Assert.InRange(Vector2.Distance(goal, points[^1]), 0, 0.001f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MathF.PI / 2)]
    [InlineData(-0.7f)]
    public void CellMapping_RoundTripsCentersAndClassifiesOffCenterPoints(float yaw)
    {
        var origin = new Vector2(37, -81);
        NavGrid grid = NavGrid.FromWalkable(8, 5, 2, origin.X, origin.Y, (_, _) => true, yawRadians: yaw);
        for (int z = -1; z <= 5; z++)
        for (int x = -1; x <= 8; x++)
        {
            Vector2 expected = Transform(new Vector2((x + 0.5f) * 2, (z + 0.5f) * 2), origin, yaw);
            Assert.InRange(Vector2.Distance(expected, grid.CellCenter(x, z)), 0, 0.0001f);
            Assert.Equal((x, z), grid.CellOf(expected.X, expected.Y));
            Vector2 corner = Transform(new Vector2((x + 0.01f) * 2, (z + 0.99f) * 2), origin, yaw);
            Assert.Equal((x, z), grid.CellOf(corner.X, corner.Y));
        }
    }

    [Fact]
    public void LayerLinks_RejectDifferentRotations()
    {
        NavGrid a = SurfaceGrid(0);
        NavGrid b = SurfaceGrid(0.3f);
        Assert.Throws<ArgumentException>(() => NavLayerLinks.Generate(new[] { a, b }, 0.5f, 1));
    }

    [Fact]
    public void SurfaceGrid_PreservesHeightsAndLinksWithMatchingRotation()
    {
        NavGrid a = SurfaceGrid(0.3f);
        NavGrid b = SurfaceGrid(0.3f);
        Assert.Equal(5f, a.SurfaceHeightAt(2, 2));
        Assert.Equal((2, 2), a.CellOf(a.CellCenter(2, 2).X, a.CellCenter(2, 2).Y));
        Assert.NotEmpty(NavLayerLinks.Generate(new[] { a, b }, 0.5f, 1));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFiniteRotation_IsRejected(float yaw)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceGrid(yaw));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NavGrid.FromWalkable(3, 3, 1, 0, 0, (_, _) => true, yawRadians: yaw));
    }

    static NavGrid SurfaceGrid(float yaw) => NavGrid.FromSurfaces(5, 5, 1, 10, 20,
        (_, _) => new NavSurfaceSample(true, 5, 10), 0.5f, 1.8f, yawRadians: yaw);

    static Vector2 Transform(Vector2 local, Vector2 origin, float yaw)
        => Vector2.Transform(local, Matrix3x2.CreateRotation(yaw)) + origin;
}
