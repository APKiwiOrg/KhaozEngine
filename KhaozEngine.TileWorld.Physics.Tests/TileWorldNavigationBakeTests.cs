using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.Movement.TileWorldMovementNavigationTests;

namespace KhaozEngine.Tests.Movement;

/// <summary>Bakes profiles from the real TileWorld physics bridge through the public bake API, with the complete world
/// keeping its ground for capture and movement proofs running through the registration's movement query view. Loading
/// happens after the physics world is disposed.</summary>
public class TileWorldNavigationBakeTests
{
    private const int MaxSampledPairs = 400;
    private static readonly NavAreaFilter DryOnly = new(Dry, Wet);
    private static readonly NavBakeProfile[] Profiles =
    [
        new("player", Tuning with { CapsuleRadius = 0.3f }, DryOnly),
        new("wide", Tuning with { CapsuleRadius = 0.55f }, DryOnly),
    ];
    private static readonly Vector3 Origin = new(128f, 4f, 64f);
    private static readonly PhysicsNavBakeOptions Options = Bounds(128, -64, 7, 12);

    // Deck and bed water from the rebased deck fact, the door from the one metre door fact shifted to document row -56,
    // and one no-draw hole whose analytic lattice height is zero but which physics never hits.
    private static readonly Vector3 Deck = new(131.5f, 1.5f, 60.5f), Bed = new(131.5f, -1f, 60.5f);
    private static readonly Vector3 DeckWest = new(130.5f, 1.5f, 60.5f), DeckEast = new(132.5f, 1.5f, 60.5f);
    private static readonly Vector3 DoorNorth = new(131.5f, 0f, 57.5f), DoorSouth = new(131.5f, 0f, 54.5f);
    private static readonly Vector3 DoorIn = new(131.5f, 0f, 56.5f), DoorOut = new(131.5f, 0f, 55.5f);
    private static readonly Vector3 Hole = new(129.5f, 0f, 57.5f);

    [Fact]
    public void BakedTileWorldLoadsEquivalentToAFreshBuild()
    {
        GroundNavigationBake fresh;
        byte[] file;
        NavBakeExpectation expected;
        using (var scene = new Scene(DeckDoorWorld(), Origin))
        {
            Assert.Equal(0f, scene.Colliders.Ground.HeightAt(Hole.X, Hole.Z));
            Assert.False(scene.World.Raycast(Hole + Vector3.UnitY * 5f, -Vector3.UnitY, 10f, out _));
            Assert.Same(scene.World, scene.Context.MovementQueries!.SourceWorld);
            expected = new NavBakeExpectation(Options, Sources(scene), Profiles);
            using PhysicsNavBake capture = scene.Capture(Options, ClassifyWater(scene));
            fresh = GroundNavigationBake.Create(capture, Sources(scene), Profiles);
            file = Write(fresh);
        }

        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(file), expected);

        Assert.Equal(NavBakeLoadStatus.Loaded, result.Status);
        Assert.Equal("", result.Detail);
        GroundNavigationBake loaded = Assert.IsType<GroundNavigationBake>(result.Bake);
        Assert.Equal(file, Write(loaded));
        Assert.Equal(fresh.Fingerprint.ToArray(), loaded.Fingerprint.ToArray());
        Assert.Equal(["player", "wide"], loaded.ProfileNames);
        Vector3[] bridgeQueries = [Deck, Bed, DeckWest, DeckEast, DoorNorth, DoorSouth, DoorIn, DoorOut, Hole];
        foreach (string name in loaded.ProfileNames)
            AssertSameAnswers(fresh.GetProfile(name), loaded.GetProfile(name), bridgeQueries);

        GroundNavigation player = loaded.GetProfile("player"), wide = loaded.GetProfile("wide");
        Assert.True(player.AllowsSegment(Deck, Deck));
        Assert.False(player.AllowsSegment(Bed, Bed));
        Assert.Equal(NavPathStatus.Complete, Route(player, DeckWest, DeckEast).Status);
        Assert.Equal(NavPathStatus.Complete, Route(player, DoorNorth, DoorSouth).Status);
        Assert.True(player.AllowsSegment(DoorIn, DoorOut));
        Assert.NotEqual(NavPathStatus.Complete, Route(wide, DoorNorth, DoorSouth).Status);
        AssertNoSurface(player, Hole);
        Assert.False(player.AllowsSegment(Hole, Hole));
        Assert.Equal(NavPathStatus.Unreachable, Route(player, DoorNorth, Hole).Status);
    }

    [Fact]
    public void ColliderEditRefusesTheBakeNamingItsSource()
    {
        TileWorldDocument doc = DeckDoorWorld();
        byte[] file, baked;
        using (var scene = new Scene(doc, Origin))
        {
            baked = scene.Colliders.Hash;
            using PhysicsNavBake capture = scene.Capture(Options, ClassifyWater(scene));
            file = Write(GroundNavigationBake.Create(capture, Sources(scene), Profiles));
        }
        doc.SetCornerHeightCm(133, -54, 0, 30);
        byte[] edited;
        using (var scene = new Scene(doc, Origin)) edited = scene.Colliders.Hash;
        Assert.NotEqual(baked, edited);

        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(file),
            new NavBakeExpectation(Options, new NavBakeSources().Add("colliders", edited), Profiles));

        Assert.Equal(NavBakeLoadStatus.SourcesChanged, result.Status);
        Assert.Contains("colliders", result.Detail, StringComparison.Ordinal);
        Assert.Null(result.Bake);
    }

    [Fact]
    public void ExactOuterEdgeMissStaysEmptyAfterLoad()
    {
        // The seam fact's half-cell-offset bounds put the outermost column centres exactly on the drawn outer edge.
        PhysicsNavBakeOptions options = new(61.5f, -5.5f, 66.5f, -1.5f, 1f, 5f, 10f, Tuning.MaxSlopeRadians, 128, 512);
        NavBakeProfile[] profiles = [new("player", Tuning with { CapsuleRadius = 0.3f }, default)];
        Vector3 outerEdge = new(62f, 0f, -4f), seam = new(64f, 0f, -4f);
        Vector3 west = new(63f, 0f, -4f), east = new(65f, 0f, -4f);
        bool edgeHit;
        GroundNavigationBake fresh;
        byte[] file;
        NavBakeExpectation expected;
        using (var scene = new Scene(Drawn(62, 2, 4, 4)))
        {
            edgeHit = scene.World.Raycast(outerEdge + Vector3.UnitY * 5f, -Vector3.UnitY, 10f, out _);
            expected = new NavBakeExpectation(options, Sources(scene), profiles);
            using PhysicsNavBake capture = scene.Capture(options);
            fresh = GroundNavigationBake.Create(capture, Sources(scene), profiles);
            file = Write(fresh);
        }

        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(file), expected);

        Assert.Equal(NavBakeLoadStatus.Loaded, result.Status);
        GroundNavigation loaded = Assert.IsType<GroundNavigationBake>(result.Bake).GetProfile("player");
        AssertSameAnswers(fresh.GetProfile("player"), loaded, [outerEdge, seam, west, east]);
        // A miss stays a missing column after load. It never gains analytic support.
        if (!edgeHit)
        {
            AssertNoSurface(loaded, outerEdge);
            Assert.False(loaded.AllowsSegment(outerEdge, outerEdge));
        }
        Assert.True(loaded.AllowsSegment(seam, seam));
        Assert.Equal(NavPathStatus.Complete, Route(loaded, west, east).Status);
        Assert.Equal(NavPathStatus.Complete, Route(loaded, east, west).Status);
    }

    private static TileWorldDocument DeckDoorWorld()
    {
        TileWorldDocument doc = Drawn(128, -64, 7, 12);
        for (int z = -63; z <= -60; z++)
            for (int x = 129; x <= 132; x++) doc.SetUnderlay(x, z, 0, 2);
        for (int z = -62; z <= -60; z++)
            for (int x = 130; x <= 132; x++) doc.SetCornerHeightCm(x, z, 0, -100);
        doc.AddObject("deck", 130, -62, 0, 0);
        for (int x = 128; x <= 134; x++)
            if (x != 131) doc.AddObject("fence", x, -56, 0, 3);
        doc.SetSettings(129, -58, 0, TileSettings.NoDraw);
        return doc;
    }

    private static NavBakeSources Sources(Scene scene) => new NavBakeSources().Add("colliders", scene.Colliders.Hash);

    private static NavAreaClassifier ClassifyWater(Scene scene)
        => feet => scene.Colliders.Medium.MediumAt(feet.X, feet.Z, feet.Y).InWater ? Wet : Dry;

    private static byte[] Write(GroundNavigationBake bake)
    {
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        return stream.ToArray();
    }

    private static NavPath Route(GroundNavigation nav, Vector3 from, Vector3 to)
        => nav.Planner.FindPath(from, to, nav.AgentRadius, PathQueryBudget.Default);

    private static void AssertNoSurface(GroundNavigation nav, Vector3 feet)
    {
        foreach (NavGrid grid in nav.Space.Layers)
        {
            (int x, int z) = grid.CellOf(feet.X, feet.Z);
            Assert.True(grid.InBounds(x, z));
            Assert.Null(grid.SurfaceHeightAt(x, z));
        }
    }

    // Public surface only: space, graph, then AllowsSegment and planner answers over the named bridge points plus a
    // stride sample of every cell centre pair. Floats compare as bits.
    private static void AssertSameAnswers(GroundNavigation fresh, GroundNavigation loaded, Vector3[] queries)
    {
        Bits(fresh.AgentRadius, loaded.AgentRadius, "AgentRadius");
        Bits(fresh.AgentHeight, loaded.AgentHeight, "AgentHeight");
        Assert.Equal(fresh.Space.Layers.Count, loaded.Space.Layers.Count);
        for (int layer = 0; layer < fresh.Space.Layers.Count; layer++)
            AssertSameLayer(fresh, loaded, layer);
        Assert.Equal(fresh.Space.Links, loaded.Space.Links);
        Assert.Equal(fresh.Graph.Links, loaded.Graph.Links);

        foreach (Vector3 from in queries)
            foreach (Vector3 to in queries) AssertSameQuery(fresh, loaded, from, to);
        List<Vector3> centres = CellCentres(fresh.Space);
        long pairs = (long)centres.Count * centres.Count;
        long stride = Math.Max(1, (pairs + MaxSampledPairs - 1) / MaxSampledPairs);
        for (long pair = 0; pair < pairs; pair += stride)
            AssertSameQuery(fresh, loaded, centres[(int)(pair / centres.Count)], centres[(int)(pair % centres.Count)]);
    }

    private static void AssertSameLayer(GroundNavigation fresh, GroundNavigation loaded, int layer)
    {
        NavGrid a = fresh.Space.Layers[layer], b = loaded.Space.Layers[layer];
        Assert.Equal((a.Width, a.Height, a.HasSurfaceHeights), (b.Width, b.Height, b.HasSurfaceHeights));
        Bits(a.CellSize, b.CellSize, "CellSize");
        Bits(a.OriginX, b.OriginX, "OriginX");
        Bits(a.OriginZ, b.OriginZ, "OriginZ");
        Bits(a.YawRadians, b.YawRadians, "YawRadians");
        Bits(a.YMin, b.YMin, "YMin");
        Bits(a.YMax, b.YMax, "YMax");
        for (int z = 0; z < a.Height; z++)
            for (int x = 0; x < a.Width; x++)
            {
                Assert.Equal(a.ClearanceAt(x, z), b.ClearanceAt(x, z));
                float? ha = a.SurfaceHeightAt(x, z), hb = b.SurfaceHeightAt(x, z);
                Assert.Equal(ha.HasValue, hb.HasValue);
                if (ha is float y) Bits(y, hb!.Value, $"SurfaceHeightAt({x}, {z}) on layer {layer}");
                Assert.Equal(fresh.Graph.IsNodePassable(layer, x, z), loaded.Graph.IsNodePassable(layer, x, z));
                Assert.Equal(fresh.Graph.Layers[layer].ExitMask(x, z), loaded.Graph.Layers[layer].ExitMask(x, z));
            }
    }

    private static void AssertSameQuery(GroundNavigation fresh, GroundNavigation loaded, Vector3 from, Vector3 to)
    {
        Assert.Equal(fresh.AllowsSegment(from, to), loaded.AllowsSegment(from, to));
        NavPath a = Route(fresh, from, to), b = Route(loaded, from, to);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Waypoints.Count, b.Waypoints.Count);
        for (int i = 0; i < a.Waypoints.Count; i++)
        {
            Bits(a.Waypoints[i].Position.X, b.Waypoints[i].Position.X, $"waypoint {i} X");
            Bits(a.Waypoints[i].Position.Y, b.Waypoints[i].Position.Y, $"waypoint {i} Z");
            Assert.Equal(a.Waypoints[i].Layer, b.Waypoints[i].Layer);
            Assert.Equal(a.Waypoints[i].Kind, b.Waypoints[i].Kind);
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

    private static void Bits(float fresh, float loaded, string what) =>
        Assert.True(BitConverter.SingleToUInt32Bits(fresh) == BitConverter.SingleToUInt32Bits(loaded),
            $"{what}: fresh {fresh} and loaded {loaded} differ in bits.");
}
