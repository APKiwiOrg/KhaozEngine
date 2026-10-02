using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class TileWorldMovementNavigationTests(ITestOutputHelper output)
{
    private const uint Dry = 1u, Wet = 2u;
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        CapsuleRadius = 0.2f, CapsuleHalfHeight = 0.75f, MaxSlopeRadians = 0.8f,
        StepHeight = 0.4f, WalkSpeed = 9f, RunSpeed = 18f,
    };

    [Fact]
    public void NormalDrawnFixtureRoutesAfterPhysicsDisposal()
    {
        GroundNavigation nav;
        using (var scene = new Scene(Drawn(0, 0, 4, 4)))
        using (PhysicsNavBake bake = scene.Capture(Bounds(0, 0, 4, 4)))
        {
            nav = bake.BuildProfile(Tuning, default);
        }

        AssertRoute(nav, new(0.5f, 0f, -0.5f), new(3.5f, 0f, -3.5f));
        int nodes = 0, exits = 0;
        foreach (NavTraversalLayer layer in nav.Graph.Layers)
            for (int z = 0; z < layer.Height; z++)
                for (int x = 0; x < layer.Width; x++)
                {
                    if (layer.IsAccepted(x, z)) nodes++;
                    exits += BitOperations.PopCount((uint)layer.ExitMask(x, z));
                }
        output.WriteLine($"Normal 4x4 fixture profile: {nodes} stored accepted nodes, {exits} stored directed neighbor exits, " +
            $"{nav.Graph.Links.Count} accepted links, {nav.Space.Links.Count} candidate links.");
    }

    [Fact]
    public void BlockedBorderKeepsGroundRoutesInsideTheDrawnInterior()
    {
        TileWorldDocument doc = Drawn(0, 0, 8, 6);
        for (int z = 0; z < 6; z++)
            for (int x = 0; x < 8; x++)
                if (x is 0 or 7 || z is 0 or 5) doc.SetSettings(x, z, 0, TileSettings.Blocked);
        using var scene = new Scene(doc);
        using PhysicsNavBake bake = scene.Capture(Bounds(0, 0, 8, 6));
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3 start = new(1.5f, 0f, -2.5f), goal = new(6.5f, 0f, -2.5f);

        AssertRoute(nav, start, goal);
        Vector3[] border = [new(0.5f, 0f, -2.5f), new(7.5f, 0f, -2.5f),
            new(3.5f, 0f, -0.5f), new(3.5f, 0f, -5.5f)];
        foreach (Vector3 blocked in border)
        {
            Assert.False(nav.AllowsSegment(blocked, blocked));
            Assert.Equal(NavPathStatus.Unreachable, Route(nav, start, blocked).Status);
        }
    }

    [Fact]
    public void NoDrawHoleCannotBecomeANodeFromTheAnalyticLatticeHeight()
    {
        TileWorldDocument doc = Drawn(0, 0, 5, 5);
        doc.SetSettings(2, 2, 0, TileSettings.NoDraw);
        using var scene = new Scene(doc);
        Vector3 hole = new(2.5f, 0f, -2.5f);
        Assert.Equal(0f, scene.Colliders.Ground.HeightAt(hole.X, hole.Z));
        Assert.False(scene.World.Raycast(hole + Vector3.UnitY * 5f, -Vector3.UnitY, 10f, out _));
        using PhysicsNavBake bake = scene.Capture(Bounds(0, 0, 5, 5));
        GroundNavigation nav = bake.BuildProfile(Tuning, default);

        Assert.False(nav.AllowsSegment(hole, hole));
        Assert.Equal(NavPathStatus.Unreachable, Route(nav, new(1.5f, 0f, -2.5f), hole).Status);
        AssertRoute(nav, new(1.5f, 0f, -2.5f), new(3.5f, 0f, -2.5f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredTenthMetreFenceBlocksBothDirectionsBetweenSupportedColumns(bool alongZ)
    {
        TileWorldDocument doc = Drawn(0, 0, 6, 6);
        for (int i = 0; i < 6; i++)
            doc.AddObject("fence", alongZ ? i : 3, alongZ ? 3 : i, 0, alongZ ? 3 : 0);
        using var scene = new Scene(doc);
        foreach (TileCollider collider in scene.Colliders.Colliders)
            if (collider.Kind == TileColliderKind.Wall)
            {
                BoxShape wall = Assert.IsType<BoxShape>(collider.Shape);
                Assert.Equal(0.1f, 2f * (alongZ ? wall.HalfExtents.Z : wall.HalfExtents.X));
            }
        using PhysicsNavBake bake = scene.Capture(Bounds(0, 0, 6, 6));
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3 from = new(2.5f, 0f, -2.5f);
        Vector3 to = alongZ ? new(2.5f, 0f, -3.5f) : new(3.5f, 0f, -2.5f);

        Assert.True(nav.AllowsSegment(from, from));
        Assert.True(nav.AllowsSegment(to, to));
        Assert.False(nav.AllowsSegment(from, to));
        Assert.False(nav.AllowsSegment(to, from));
        Assert.Equal(NavPathStatus.Unreachable, Route(nav, from, to).Status);
        Assert.Equal(NavPathStatus.Unreachable, Route(nav, to, from).Status);
    }

    [Fact]
    public void OneMetreAuthoredDoorRoutesSmallCapsuleAndRefusesWideCapsule()
    {
        TileWorldDocument doc = Drawn(0, 0, 7, 6);
        for (int x = 0; x < 7; x++)
            if (x != 3) doc.AddObject("fence", x, 3, 0, 3);
        using var scene = new Scene(doc);
        using PhysicsNavBake bake = scene.Capture(Bounds(0, 0, 7, 6));
        GroundNavigation small = bake.BuildProfile(Tuning, default);
        GroundNavigation wide = bake.BuildProfile(Tuning with { CapsuleRadius = 0.55f }, default);
        Vector3 from = new(3.5f, 0f, -1.5f), to = new(3.5f, 0f, -4.5f);

        AssertRoute(small, from, to);
        Assert.True(small.AllowsSegment(new(3.5f, 0f, -2.5f), new(3.5f, 0f, -3.5f)));
        Assert.NotEqual(NavPathStatus.Complete, Route(wide, from, to).Status);
    }

    [Fact]
    public void GentleDrawnSlopeRoutesAtItsCapturedSurfaceHeights()
    {
        TileWorldDocument doc = Drawn(0, 0, 6, 4);
        for (int z = 0; z <= 4; z++)
            for (int x = 0; x <= 6; x++) doc.SetCornerHeightCm(x, z, 0, (short)(2 * x));
        using var scene = new Scene(doc);
        using PhysicsNavBake bake = scene.Capture(Bounds(0, 0, 6, 4));
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3 from = new(1.5f, 0.03f, -2.5f), to = new(4.5f, 0.09f, -2.5f);

        Assert.Equal(0.03f, Surface(nav, from), 5);
        Assert.Equal(0.09f, Surface(nav, to), 5);
        Assert.True(scene.Colliders.Ground.NormalAt(from.X, from.Z).X < 0f);
        AssertRoute(nav, from, to);
        AssertRoute(nav, to, from);
    }

    [Fact]
    public void RebasedDeckAndBedUseAbsoluteSurfaceFeetForWaterClassification()
    {
        TileWorldDocument doc = Drawn(128, -64, 6, 6);
        for (int z = -63; z <= -60; z++)
            for (int x = 129; x <= 132; x++) doc.SetUnderlay(x, z, 0, 2);
        for (int z = -62; z <= -60; z++)
            for (int x = 130; x <= 132; x++) doc.SetCornerHeightCm(x, z, 0, -100);
        doc.AddObject("deck", 130, -62, 0, 0);
        using var scene = new Scene(doc, new(128f, 4f, 64f));
        Vector3 deck = new(131.5f, 1.5f, 60.5f), bed = new(131.5f, -1f, 60.5f);
        Assert.False(scene.Colliders.Medium.MediumAt(deck.X, deck.Z, deck.Y).InWater);
        Assert.True(scene.Colliders.Medium.MediumAt(bed.X, bed.Z, bed.Y).InWater);
        var classifications = new List<(Vector3 Feet, uint Area)>();
        using PhysicsNavBake bake = scene.Capture(Bounds(128, -64, 6, 6), feet =>
        {
            uint area = scene.Colliders.Medium.MediumAt(feet.X, feet.Z, feet.Y).InWater ? Wet : Dry;
            classifications.Add((feet, area));
            return area;
        });
        GroundNavigation dry = bake.BuildProfile(Tuning, new NavAreaFilter(Dry, Wet));
        GroundNavigation wet = bake.BuildProfile(Tuning, new NavAreaFilter(Wet, Dry));

        Assert.Contains(classifications, sample => Near(sample.Feet, deck) && sample.Area == Dry);
        Assert.Contains(classifications, sample => Near(sample.Feet, bed) && sample.Area == Wet);
        Assert.True(dry.AllowsSegment(deck, deck));
        Assert.False(dry.AllowsSegment(bed, bed));
        Assert.True(wet.AllowsSegment(bed, bed));
        Assert.False(wet.AllowsSegment(deck, deck));
        AssertRoute(dry, new(130.5f, 1.5f, 60.5f), new(132.5f, 1.5f, 60.5f));
        AssertRoute(wet, new(130.5f, -1f, 61.5f), new(131.5f, -1f, 61.5f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonzeroDocumentCoordinatesKeepIdenticalWaypointsWhenPhysicsRebases(bool afterRegistration)
    {
        TileWorldDocument doc = Drawn(128, -64, 6, 6);
        PhysicsNavBakeOptions options = Bounds(128, -64, 6, 6);
        GroundNavigation zero = BuildDisposed(doc, options, Vector3.Zero);
        GroundNavigation rebased = BuildDisposed(doc, options, new(130.25f, 7f, 61.75f), afterRegistration);
        Vector3 from = new(129.5f, 0f, 62.5f), to = new(132.5f, 0f, 59.5f);

        AssertRoute(zero, from, to);
        AssertRoute(rebased, from, to);
        Assert.Equal(Route(zero, from, to).Waypoints, Route(rebased, from, to).Waypoints);
    }

    [Fact]
    public void InteriorRegionSeamRoutesWhileAnExactOuterEdgeMayMiss()
    {
        TileWorldDocument doc = Drawn(62, 2, 4, 4);
        using var scene = new Scene(doc);
        Vector3 outerEdge = new(62f, 0f, -4f);
        bool edgeHit = scene.World.Raycast(outerEdge + Vector3.UnitY * 5f, -Vector3.UnitY, 10f, out _);
        using PhysicsNavBake bake = scene.Capture(new(61.5f, -5.5f, 66.5f, -1.5f,
            1f, 5f, 10f, Tuning.MaxSlopeRadians, 128, 512));
        GroundNavigation nav = bake.BuildProfile(Tuning, default);

        // The half-open outer edge may hit or miss. A miss must never gain analytic fallback support.
        if (!edgeHit) Assert.False(nav.AllowsSegment(outerEdge, outerEdge));
        Assert.True(nav.AllowsSegment(new(64f, 0f, -4f), new(64f, 0f, -4f)));
        AssertRoute(nav, new(63f, 0f, -4f), new(65f, 0f, -4f));
        AssertRoute(nav, new(65f, 0f, -4f), new(63f, 0f, -4f));
    }

    private static NavPath Route(GroundNavigation nav, Vector3 from, Vector3 to)
        => nav.Planner.FindPath(from, to, nav.AgentRadius, PathQueryBudget.Default);

    private static void AssertRoute(GroundNavigation nav, Vector3 from, Vector3 to)
    {
        NavPath path = Route(nav, from, to);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.NotEmpty(path.Waypoints);
        Vector3 previous = from;
        foreach (NavWaypoint waypoint in path.Waypoints)
        {
            Assert.Equal(NavWaypointKind.Walk, waypoint.Kind);
            NavGrid grid = nav.Space.Layers[waypoint.Layer];
            (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
            Vector3 next = new(waypoint.Position.X, grid.SurfaceHeightAt(x, z)!.Value, waypoint.Position.Y);
            Assert.True(nav.AllowsSegment(previous, next), $"Refused route segment {previous} to {next}");
            previous = next;
        }
        Assert.True(nav.AllowsSegment(previous, to));
    }

    private static float Surface(GroundNavigation nav, Vector3 feet)
    {
        NavGrid grid = nav.Space.Layers[nav.Space.LayerAt(feet)];
        (int x, int z) = grid.CellOf(feet.X, feet.Z);
        return grid.SurfaceHeightAt(x, z)!.Value;
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.001f;

    private static GroundNavigation BuildDisposed(TileWorldDocument doc, PhysicsNavBakeOptions options,
        Vector3 origin, bool afterRegistration = false)
    {
        using var scene = new Scene(doc, origin, afterRegistration);
        using PhysicsNavBake bake = scene.Capture(options);
        return bake.BuildProfile(Tuning, default);
    }

    private static PhysicsNavBakeOptions Bounds(int x, int z, int width, int height)
        => new(x, -z - height, x + width, -z, 1f, 5f, 10f, Tuning.MaxSlopeRadians, 128, 512);

    private static TileWorldDocument Drawn(int x0, int z0, int width, int height)
    {
        var doc = new TileWorldDocument { Id = "movement-bridge-test" };
        RegionCoord first = RegionCoord.Of(x0, z0), last = RegionCoord.Of(x0 + width - 1, z0 + height - 1);
        for (int rz = first.Rz; rz <= last.Rz; rz++)
            for (int rx = first.Rx; rx <= last.Rx; rx++)
            {
                var region = new RegionCoord(rx, rz);
                doc.GetOrCreateRegion(region);
                for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                    for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                    {
                        doc.SetUnderlay(x, z, 0, 1);
                        doc.SetSettings(x, z, 0, TileSettings.NoDraw);
                    }
            }
        for (int z = z0; z < z0 + height; z++)
            for (int x = x0; x < x0 + width; x++) doc.SetSettings(x, z, 0, TileSettings.None);
        return doc;
    }

    private sealed class Scene : IDisposable
    {
        private static readonly TileWorldCatalogs Catalogs = TileWorldCatalogs.LoadJson(
            """
            {
              "materials": [
                { "id": 1, "name": "ground", "color": "#448844", "kind": "Ground" },
                { "id": 2, "name": "water", "color": "#224488", "kind": "Water" }
              ],
              "archetypes": [
                { "id": "fence", "name": "fence", "meshRef": "test/fence.glb",
                  "collisionKind": "Wall", "collisionHeight": 2.5 },
                { "id": "deck", "name": "deck", "meshRef": "test/deck.glb", "sizeX": 3, "sizeZ": 3,
                  "walkSurfaces": [ { "height": 2.5 } ] }
              ]
            }
            """, "movement-bridge-tests");
        private readonly TileColliderRegistration _registration;
        private readonly IPhysicsWorldQueryView _movementQueries;
        public BepuPhysicsWorld World { get; } = new();
        public TileWorldColliders Colliders { get; }
        public GroundMoveContext Context { get; }

        public Scene(TileWorldDocument doc, Vector3 origin = default, bool afterRegistration = false)
        {
            TileColliderRegistration? registration = null;
            IPhysicsWorldQueryView? movementQueries = null;
            try
            {
                Colliders = TileWorldColliders.Build(doc, Catalogs,
                    new TileColliderOptions { WallThickness = 0.1f, BlockedHeight = 3f, WalkSurfaceThickness = 0.1f });
                if (!afterRegistration && origin != Vector3.Zero) World.Rebase(origin);
                registration = Colliders.AddTo(World);
                movementQueries = registration.CreateMovementQueryView();
                if (afterRegistration && origin != Vector3.Zero) World.Rebase(origin);
                Context = new GroundMoveContext(Colliders.Ground.HeightDelegate, Colliders.Ground.NormalDelegate,
                    physics: World, clampXz: null, medium: Colliders.Medium.MediumDelegate,
                    movementQueries: movementQueries);
                _registration = registration;
                _movementQueries = movementQueries;
            }
            catch
            {
                try { movementQueries?.Dispose(); }
                finally
                {
                    try { registration?.Dispose(); }
                    finally { World.Dispose(); }
                }
                throw;
            }
        }

        public PhysicsNavBake Capture(PhysicsNavBakeOptions options, NavAreaClassifier? classify = null)
            => PhysicsNavBake.Capture(Context, options, classify ?? (_ => Dry));

        public void Dispose()
        {
            _movementQueries.Dispose();
            _registration.Dispose();
            World.Dispose();
        }
    }
}
