using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Movement;

/// <summary>Asserts every equivalence item of the bake design between a fresh and a loaded profile: space, graph,
/// columns, and segment and planner behavior over a deterministic query set. Floats compare as bits.</summary>
internal static class BakeEquivalence
{
    private const int MaxPairs = 400;

    // ignoreSampleWater compares capture options without SampleWater, for two captures of one world that differ only
    // in water sampling.
    public static void AssertEquivalent(GroundNavigation fresh, GroundNavigation loaded, bool ignoreSampleWater = false)
    {
        AssertSpace(fresh.Space, loaded.Space);
        AssertGraph(fresh, loaded);
        AssertColumns(fresh, loaded, ignoreSampleWater);
        AssertBehavior(fresh, loaded);
    }

    private static void AssertSpace(NavSpace fresh, NavSpace loaded)
    {
        Assert.Equal(fresh.Layers.Count, loaded.Layers.Count);
        for (int layer = 0; layer < fresh.Layers.Count; layer++)
        {
            NavGrid a = fresh.Layers[layer], b = loaded.Layers[layer];
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.Height, b.Height);
            Bits(a.CellSize, b.CellSize, "CellSize");
            Bits(a.OriginX, b.OriginX, "OriginX");
            Bits(a.OriginZ, b.OriginZ, "OriginZ");
            Bits(a.YawRadians, b.YawRadians, "YawRadians");
            Bits(a.YMin, b.YMin, "YMin");
            Bits(a.YMax, b.YMax, "YMax");
            Assert.Equal(a.HasSurfaceHeights, b.HasSurfaceHeights);
            for (int z = 0; z < a.Height; z++)
                for (int x = 0; x < a.Width; x++)
                {
                    Assert.Equal(a.ClearanceAt(x, z), b.ClearanceAt(x, z));
                    float? ha = a.SurfaceHeightAt(x, z), hb = b.SurfaceHeightAt(x, z);
                    Assert.Equal(ha.HasValue, hb.HasValue);
                    if (ha is float y) Bits(y, hb!.Value, $"SurfaceHeightAt({x}, {z}) on layer {layer}");
                }
        }
        Assert.Equal(fresh.Links, loaded.Links);
    }

    private static void AssertGraph(GroundNavigation fresh, GroundNavigation loaded)
    {
        Bits(fresh.AgentRadius, loaded.AgentRadius, "AgentRadius");
        Bits(fresh.AgentHeight, loaded.AgentHeight, "AgentHeight");
        NavTraversalGraph a = fresh.Graph, b = loaded.Graph;
        for (int layer = 0; layer < fresh.Space.Layers.Count; layer++)
        {
            NavGrid grid = fresh.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    Assert.Equal(a.IsNodePassable(layer, x, z), b.IsNodePassable(layer, x, z));
                    Assert.Equal(a.Layers[layer].ExitMask(x, z), b.Layers[layer].ExitMask(x, z));
                }
        }
        Assert.Equal(a.Links, b.Links);
    }

    private static void AssertColumns(GroundNavigation fresh, GroundNavigation loaded, bool ignoreSampleWater)
    {
        PhysicsNavColumns a = fresh.Footprint.Columns, b = loaded.Footprint.Columns;
        if (ignoreSampleWater)
            Assert.Equal(fresh.Footprint.Options with { SampleWater = false }, loaded.Footprint.Options with { SampleWater = false });
        else
            Assert.Equal(fresh.Footprint.Options, loaded.Footprint.Options);
        Assert.Equal(fresh.Footprint.Areas, loaded.Footprint.Areas);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.SurfaceCount, b.SurfaceCount);
        for (int z = 0; z < a.Height; z++)
            for (int x = 0; x < a.Width; x++)
            {
                ReadOnlySpan<PhysicsNavSurface> ca = a.GetColumn(x, z), cb = b.GetColumn(x, z);
                Assert.Equal(ca.Length, cb.Length);
                for (int i = 0; i < ca.Length; i++)
                {
                    Bits(ca[i].Height, cb[i].Height, $"column ({x}, {z}) height {i}");
                    Bits(ca[i].Headroom, cb[i].Headroom, $"column ({x}, {z}) headroom {i}");
                    Assert.Equal(ca[i].Areas, cb[i].Areas);
                }
            }
    }

    private static void AssertBehavior(GroundNavigation fresh, GroundNavigation loaded)
    {
        List<Vector3> points = CellCentres(fresh.Space);
        long pairs = (long)points.Count * points.Count;
        long stride = Math.Max(1, (pairs + MaxPairs - 1) / MaxPairs);
        for (long pair = 0; pair < pairs; pair += stride)
        {
            Vector3 from = points[(int)(pair / points.Count)], to = points[(int)(pair % points.Count)];
            Assert.Equal(fresh.AllowsSegment(from, to), loaded.AllowsSegment(from, to));
            AssertPath(fresh.Planner.FindPath(from, to, fresh.AgentRadius, PathQueryBudget.Default),
                loaded.Planner.FindPath(from, to, loaded.AgentRadius, PathQueryBudget.Default));
            AssertPath(fresh.Planner.FindPath(from, Region(to), fresh.AgentRadius, PathQueryBudget.Default),
                loaded.Planner.FindPath(from, Region(to), loaded.AgentRadius, PathQueryBudget.Default));
        }
    }

    // Every cell of every layer, at its surface height when open and at the band floor or zero when blocked.
    private static List<Vector3> CellCentres(NavSpace space)
    {
        var points = new List<Vector3>();
        foreach (NavGrid grid in space.Layers)
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    Vector2 centre = grid.CellCenter(x, z);
                    float y = grid.SurfaceHeightAt(x, z) ?? (float.IsFinite(grid.YMin) ? grid.YMin : 0f);
                    points.Add(new Vector3(centre.X, y, centre.Y));
                }
        return points;
    }

    private static NavGoalRegion Region(Vector3 anchor) => new(anchor, 0.25f, feet =>
        Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(anchor.X, anchor.Z)) < 0.3f &&
        MathF.Abs(feet.Y - anchor.Y) < 0.5f);

    private static void AssertPath(NavPath fresh, NavPath loaded)
    {
        Assert.Equal(fresh.Status, loaded.Status);
        Assert.Equal(fresh.Waypoints.Count, loaded.Waypoints.Count);
        for (int i = 0; i < fresh.Waypoints.Count; i++)
        {
            NavWaypoint a = fresh.Waypoints[i], b = loaded.Waypoints[i];
            Bits(a.Position.X, b.Position.X, $"waypoint {i} X");
            Bits(a.Position.Y, b.Position.Y, $"waypoint {i} Z");
            Assert.Equal(a.Layer, b.Layer);
            Assert.Equal(a.Kind, b.Kind);
        }
    }

    private static void Bits(float fresh, float loaded, string what) =>
        Assert.True(BitConverter.SingleToUInt32Bits(fresh) == BitConverter.SingleToUInt32Bits(loaded),
            $"{what}: fresh {fresh} and loaded {loaded} differ in bits.");
}
