using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal static class ContactNavigationFixture
{
    internal static MoveTuning Tuning => GroundTraversalProbeTests.Tuning with
    {
        Controller = MovementControllerKind.Contact,
        Water = new(WaterTraversalMode.DryOnly),
        TractionHysteresisRadians = 0,
    };

    internal static PhysicsNavBakeOptions Options => new(-1.7f, -1, 1.3f, 1,
        0.25f, 3, 7, Tuning.MaxSlopeRadians, 128, 512, MaxEdgeProbeSteps: 160);

    internal static GroundMoveContext Context(BepuPhysicsWorld world,
        Func<float, float, float>? height = null, IMovementWaterField? water = null,
        Func<float, float, float, MovementMedium>? medium = null,
        OrdinaryMovementExtent? extent = null)
    {
        var bounds = extent ?? new(new(-20, -4, -20), new(20, 4, 20), -2, 3);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", world, bounds);
        var handles = new HashSet<StaticHandle>();
        // Register the existing world's standable statics without replacing its scene builder.
        foreach (float x in new[] { -7f, -2f, -0.5f, -0.075f, 0.075f, 0.5f, 2f, 7f })
            foreach (float z in new[] { -7f, -2f, -0.5f, 0f, 0.125f, 0.5f, 2f, 7f })
            {
                float top = 3.9f;
                while (world.Raycast(new(x, top, z), -Vector3.UnitY, top + 4,
                    out var hit, QueryFilter.StaticsOnly))
                {
                    if (hit.Body is { } handle) handles.Add(handle);
                    float next = hit.Point.Y - 0.001f;
                    if (next >= top || next <= -4) break;
                    top = next;
                }
            }
        int index = 0;
        foreach (StaticHandle handle in handles)
            builder.Add(handle, 0, int.MaxValue, new("world", $"static-{index++}"), bounds.Minimum, bounds.Maximum);
        OrdinaryMovementRegistration registration;
        using (var lease = world.AcquireQueryReadLease()) registration = builder.Build(lease);
        return new(height ?? ((_, _) => 0), null, world, null, medium, null,
            water, registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
    }

    internal static GroundMoveContext Context(OrdinaryContactScene scene, IMovementWaterField? water = null) =>
        new(scene.Height, scene.Normal, scene.Physics, null, null, null, water, scene.Registration,
            MovementBoundary.Rectangle(-20, -20, 20, 20));

    internal static void Box(BepuPhysicsWorld world, OrdinaryMovementRegistration.Builder builder,
        string owner, Vector3 min, Vector3 max)
    {
        var handle = world.AddStatic(new BoxShape((max - min) / 2), Pose.At((max + min) / 2));
        builder.Add(handle, 0, int.MaxValue, new("world", owner), min, max);
    }

    internal static float? Resting(GroundNavigation navigation, Vector3 raw)
    {
        foreach (NavGrid grid in navigation.Space.Layers)
        {
            (int x, int z) = grid.CellOf(raw.X, raw.Z);
            // Fixture labels name geometric heights. Axis ray reconstruction can differ by a float ulp.
            if (grid.SurfaceHeightAt(x, z) is float height && MathF.Round(height, 6) == MathF.Round(raw.Y, 6))
                return grid.RestingHeightAt(x, z);
        }
        return null;
    }

    internal static void SameGraph(GroundNavigation a, GroundNavigation b)
    {
        Assert.Equal(a.Space.Layers.Count, b.Space.Layers.Count);
        Assert.Equal(a.Space.Links, b.Space.Links);
        Assert.Equal(a.Graph.Links, b.Graph.Links);
        for (int layer = 0; layer < a.Space.Layers.Count; layer++)
        {
            NavGrid grid = a.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    Assert.Equal(grid.SurfaceHeightAt(x, z), b.Space.Layers[layer].SurfaceHeightAt(x, z));
                    Assert.Equal(a.Graph.Layers[layer].IsAccepted(x, z), b.Graph.Layers[layer].IsAccepted(x, z));
                    Assert.Equal(a.Graph.Layers[layer].ExitMask(x, z), b.Graph.Layers[layer].ExitMask(x, z));
                }
        }
    }

    internal static void SameGraphBits(GroundNavigation expected, GroundNavigation actual)
    {
        Assert.Equal(Bits(expected.AgentRadius), Bits(actual.AgentRadius));
        Assert.Equal(Bits(expected.AgentHeight), Bits(actual.AgentHeight));
        Assert.Equal(expected.Space.Layers.Count, actual.Space.Layers.Count);
        Assert.Equal(expected.Graph.Layers.Count, actual.Graph.Layers.Count);
        Assert.Equal(expected.Space.Links, actual.Space.Links);
        Assert.Equal(expected.Graph.Links, actual.Graph.Links);
        var expectedLinks = new HashSet<NavLink>(expected.Graph.Links);
        var actualLinks = new HashSet<NavLink>(actual.Graph.Links);
        for (int candidate = 0; candidate < expected.Space.Links.Count; candidate++)
            Assert.Equal(expectedLinks.Contains(expected.Space.Links[candidate]),
                actualLinks.Contains(actual.Space.Links[candidate]));

        for (int layer = 0; layer < expected.Space.Layers.Count; layer++)
        {
            NavGrid a = expected.Space.Layers[layer], b = actual.Space.Layers[layer];
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.Height, b.Height);
            Assert.Equal(Bits(a.CellSize), Bits(b.CellSize));
            Assert.Equal(Bits(a.OriginX), Bits(b.OriginX));
            Assert.Equal(Bits(a.OriginZ), Bits(b.OriginZ));
            Assert.Equal(Bits(a.YawRadians), Bits(b.YawRadians));
            Assert.Equal(Bits(a.YMin), Bits(b.YMin));
            Assert.Equal(Bits(a.YMax), Bits(b.YMax));
            Assert.Equal(a.HasSurfaceHeights, b.HasSurfaceHeights);
            Assert.Equal(a.HasRestingHeights, b.HasRestingHeights);
            Assert.Equal(expected.Graph.Layers[layer].Width, actual.Graph.Layers[layer].Width);
            Assert.Equal(expected.Graph.Layers[layer].Height, actual.Graph.Layers[layer].Height);
            for (int z = 0; z < a.Height; z++)
                for (int x = 0; x < a.Width; x++)
                {
                    Assert.Equal(a.ClearanceAt(x, z), b.ClearanceAt(x, z));
                    Assert.Equal(Bits(a.SurfaceHeightAt(x, z)), Bits(b.SurfaceHeightAt(x, z)));
                    Assert.Equal(Bits(a.RestingHeightAt(x, z)), Bits(b.RestingHeightAt(x, z)));
                    Assert.Equal(expected.Graph.Layers[layer].IsAccepted(x, z), actual.Graph.Layers[layer].IsAccepted(x, z));
                    Assert.Equal(expected.Graph.Layers[layer].ExitMask(x, z), actual.Graph.Layers[layer].ExitMask(x, z));
                }
        }

        static int? Bits(float? value) => value.HasValue ? BitConverter.SingleToInt32Bits(value.Value) : null;
    }
}
