using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.WaterPrismCensus;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>One water field, the absolute extent its leases serve and the boundary questions asked of it, grouped by
/// the physics origin they are asked at and given in that origin's local frame. <see cref="Select"/> is absolute.
/// <see cref="Sampler"/> is the unwrapped tile producer when there is one.</summary>
internal sealed record WaterBoundaryParityWorld(string Name, IMovementWaterField Field, TileMediumSampler? Sampler,
    Vector3 Minimum, Vector3 Maximum, MoveTuning Tuning, Vector3 Select,
    IReadOnlyList<(Vector3 Origin, IReadOnlyList<MovementMediumSweepQuery> Queries)> Groups);

/// <summary>The water boundary parity corpus. Every world here is one level or no water within reach, except the
/// three that must decline: a weir with two levels, a closed analytic slab and a body split by a region border.</summary>
internal static class WaterBoundaryParityCorpus
{
    internal const string World = "water-world";
    internal static readonly MovementSpaceKey Space = new(World, "ordinary");

    internal static readonly string[] Certifying =
    [
        .. Array.ConvertAll(WaterPrismScenario.Names, name => "scenario/" + name), "scenario/shore-traverse",
        "rebased/bank-across", "rebased/swim-entry", "bank-lift-grid", "real-shore",
        "drawn-bed/sloped", "drawn-bed/level", "drawn-bed/flat-patch", "drawn-bed/coarse",
    ];

    internal static readonly string[] Declining = ["weir", "closed-level", "region-seam"];

    // The scenario's own resident declaration, so replayed questions meet the envelope they met when recorded.
    static readonly Vector3 ScenarioMinimum = new(63, -120, -321), ScenarioMaximum = new(129, 20, -191);

    internal static WaterBoundaryParityWorld Create(string name) => name switch
    {
        _ when name.StartsWith("scenario/", StringComparison.Ordinal) => Scenario(name["scenario/".Length..], false),
        _ when name.StartsWith("rebased/", StringComparison.Ordinal) => Scenario(name["rebased/".Length..], true),
        "bank-lift-grid" => BankLiftGrid(),
        "real-shore" => RealShore(),
        "drawn-bed/sloped" => DrawnBed(name, false),
        "drawn-bed/level" => DrawnBed(name, true),
        "drawn-bed/flat-patch" => FlatPatch(),
        "drawn-bed/coarse" => Coarse(),
        "weir" => Weir(),
        "closed-level" => ClosedLevel(),
        "region-seam" => RegionSeam(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // Every trace an authority run asks of the producer, recorded through the scenario's wrapper. The wrapper
    // exposes only IMovementWaterField, so the recorded run takes the full solve and every question reaches it.
    static WaterBoundaryParityWorld Scenario(string scenario, bool rebased)
    {
        var queries = new List<MovementMediumSweepQuery>();
        var seen = new HashSet<MovementMediumSweepQuery>();
        using (var scene = new WaterPrismScenario(scenario))
        {
            scene.Field.Recorded = (query, _, _, _) => { if (seen.Add(query)) queries.Add(query); };
            Advance(scene, scenario);
            scene.Field.Recorded = null;
            var field = scene.Colliders.Medium;
            Vector3 select = scene.Basis.Move.Position;
            if (!rebased)
                return new("scenario/" + scenario, field, field, ScenarioMinimum, ScenarioMaximum,
                    WaterPrismScenario.Tuning, select, [(Vector3.Zero, queries)]);
            // The recorded paths asked from moved frames. Nonzero Y origins move every converted surface through the
            // outward rounding of the pin.
            Vector3 first = new(96.5f, 0.3f, -240.25f), second = new(120.0625f, -1.7f, -230.3f);
            return new("rebased/" + scenario, field, field, ScenarioMinimum, ScenarioMaximum, WaterPrismScenario.Tuning,
                select, [(first, queries.ConvertAll(q => Local(q, first))), (second, queries.ConvertAll(q => Local(q, second)))]);
        }
    }

    static void Advance(WaterPrismScenario scene, string scenario)
    {
        var state = scene.Basis;
        if (scenario != "shore-traverse")
        {
            for (int tick = 0; tick < 8; tick++) state = scene.Step(state, scene.Command);
            return;
        }
        // Down the shore into swimming water, then back out onto the bank.
        var command = new MoveCommand(Vector2.UnitY, false, 0);
        bool outward = false;
        for (int tick = 0; tick < 400; tick++)
        {
            if (state.Move.Position.Z < -244) outward = true;
            if (outward) command = new(-Vector2.UnitY, false, 0);
            state = scene.Step(state, command);
            if (outward && state.Move.Position.Z >= -235.8f) return;
        }
    }

    // #1761. The continuous bank lifts at edge and diagonal offsets and adjacent floats.
    static WaterBoundaryParityWorld BankLiftGrid()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document(109, 3));
        var ground = field.CaptureTraceForTests().Ground;
        float anchor = ground.HeightAt(110.375f, -236.79904f);
        var queries = new List<MovementMediumSweepQuery>();
        foreach (float z in new[] { -236.59904f, -236.69904f, -236.79904f, -236.89905f, -236.97406f })
            foreach (float x in TileWaterBankLiftGridTests.Laterals(z))
                foreach (float lift in new[] { 0.096971154f, 0.2f, 0.4f })
                {
                    float y = -0.45606077f + (ground.HeightAt(x, z) - anchor);
                    queries.Add(new(new(new(x, y, z), 0.3f, 0.75f, Space, null), new(0, lift, 0)));
                }
        return new("bank-lift-grid", field, field, new(100, -8, -250), new(120, 8, -225), GrazingWaterBank.Tuning,
            new(110.375f, 0, -236.8f), [(Vector3.Zero, queries)]);
    }

    // #1697. The run-mode seating sweep that the kernel refuses with PrecisionExceeded, its stationary control and
    // nearby seats on the same one-level shore.
    static WaterBoundaryParityWorld RealShore()
    {
        var field = MovementWaterFieldTests.Field(ContactDropPrecisionTests.Shore());
        var queries = new List<MovementMediumSweepQuery>();
        foreach (Vector2 axis in new Vector2[] { new(99.11088f, -296.68784f), new(99f, -296.68784f), new(99.5f, -297.2f) })
            foreach (float drop in new[] { 0f, -0.42954257f, -0.2f, 0.1f })
                queries.Add(new(new(new(axis.X, 0.4276524f, axis.Y), 0.3f, 0.75f, Space, null), new(0, drop, 0)));
        return new("real-shore", field, field, new(90, -8, -305), new(110, 8, -290), GrazingWaterBank.Tuning,
            new(99.11088f, 0.5f, -296.68784f), [(Vector3.Zero, queries)]);
    }

    static WaterBoundaryParityWorld DrawnBed(string name, bool levelTriangle)
    {
        var colliders = TileWorldColliders.Build(DrawnWaterBedTests.World(levelTriangle), TileWorldPhysicsTestData.Catalogs(), World);
        var field = colliders.Medium;
        var queries = new List<MovementMediumSweepQuery>
        {
            Query(new(8.75f, colliders.MovementGroundHeight(8.75f, -8.75f) - 0.04f, -8.75f), 0.005f, 0.005f, new(0.01f, 0, 0.01f)),
            Query(new(8.5f, colliders.MovementGroundHeight(8.5f, -8.5f) + 0.04f, -8.5f), 0.005f, 0.005f, new(0.01f, 0, 0.01f)),
            Query(new(7.5f, -0.3f, -8.5f), 0.125f, 0.125f, Vector3.UnitX),
            Query(new(9.75f, 0, -9.75f), 0.005f, 0.005f, Vector3.Zero),
        };
        foreach (Vector3 delta in new Vector3[] { Vector3.Zero, Vector3.UnitX, new(0, -0.2f, 0), new(0, 0.3f, 0) })
            foreach (float y in new[] { -0.6f, -0.3f, 0.4f, 1.2f })
            {
                queries.Add(Query(new(8.5f, y, -8.5f), 0.3f, 0.75f, delta));
                queries.Add(Query(new(7.5f, y, -9.2f), 0.3f, 0.75f, delta * 2));
            }
        // The sub-ULP rebase of the represented bed rounding row.
        var subUlp = new List<MovementMediumSweepQuery> { Query(new(8.5f, -0.7f, -8.5f), 5e-10f, 5e-10f, Vector3.Zero) };
        return new(name, field, field, new(0, -8, -20), new(20, 8, 0), GrazingWaterBank.Tuning, new(5, 1, -5),
            [(Vector3.Zero, queries), (new(0, 3e-8f, 0), subUlp)]);
    }

    // A one-column flat strip at 0.05 m tiles beside a far sloped rim.
    static WaterBoundaryParityWorld FlatPatch()
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        document.TileSize = 0.05f;
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                document.SetCornerHeightCm(x, z, 0, -400);
                if (x == 8) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        document.SetCornerHeightCm(8, 0, 0, 2);
        var field = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), World).Medium;
        var queries = new List<MovementMediumSweepQuery>();
        foreach (float radius in new[] { 0.25f, MathF.BitIncrement(0.25f) })
            foreach (float y in new[] { -3.25f, -0.5f, 0.6f })
                foreach (Vector3 delta in new Vector3[] { new(1.2f, 0, 0), Vector3.Zero, new(0, -1, 0) })
                    queries.Add(Query(new(0, y, -1), radius, 0.75f, delta));
        return new("drawn-bed/flat-patch", field, field, new(-3, -8, -5), new(5, 8, 1), GrazingWaterBank.Tuning,
            new(-1, 1, -1), [(Vector3.Zero, queries)]);
    }

    // A single water tile at coarse absolute coordinates, read through a large origin.
    static WaterBoundaryParityWorld Coarse()
    {
        const float originX = 4194304f;
        var field = MovementWaterFieldTests.SingleWaterTile(33554434, 0, 0.125f);
        var queries = new List<MovementMediumSweepQuery>();
        foreach (float x in new[] { 0f, 0.125f, -0.0001f, 0.5001f })
            foreach (Vector3 delta in new Vector3[] { Vector3.Zero, new(0.25f, 0, 0), new(0, -1, 0) })
                queries.Add(Query(new(x, -1, -0.0625f), 0.03125f, 0.75f, delta));
        return new("drawn-bed/coarse", field, field, new(originX - 4, -8, -4), new(originX + 8, 8, 4),
            GrazingWaterBank.Tuning, new(originX, 1, 0), [(new(originX, 0, 0), queries)]);
    }

    // Two bodies one dry tile apart, surfaces at -0.02 m and -0.52 m. Every question reaches both.
    static WaterBoundaryParityWorld Weir()
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        Pool(document, 10, 11, 10, 11, -200);
        Pool(document, 13, 14, 10, 11, -200);
        document.SetCornerHeightCm(10, 10, 0, 0);
        document.SetCornerHeightCm(15, 12, 0, -50);
        var field = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), World).Medium;
        return new("weir", field, field, new(0, -8, -30), new(30, 8, 0), GrazingWaterBank.Tuning, new(5, 1, -5),
            [(Vector3.Zero, Straddling(12.5f, -10.5f))]);
    }

    // One body crossing the region border at x 64, each clipped piece at its own rim, -0.02 m and -0.42 m.
    static WaterBoundaryParityWorld RegionSeam()
    {
        var document = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, 0), new RegionCoord(1, 0));
        Pool(document, 62, 65, 10, 11, -200);
        document.SetCornerHeightCm(62, 10, 0, 0);
        document.SetCornerHeightCm(66, 12, 0, -40);
        var field = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), World).Medium;
        return new("region-seam", field, field, new(40, -8, -30), new(90, 8, 0), GrazingWaterBank.Tuning,
            new(50, 1, -5), [(Vector3.Zero, Straddling(64f, -10.5f))]);
    }

    // A closed slab from -4 m to a 0 m ceiling, with no certificate interface.
    static WaterBoundaryParityWorld ClosedLevel()
    {
        var queries = new List<MovementMediumSweepQuery>();
        foreach (float y in new[] { -5f, -3.9f, -0.5f, 0.3f, 0.9f, 2f })
            foreach (Vector3 delta in new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY })
                queries.Add(Query(new(0.25f, y, -0.5f), 0.3f, 0.75f, delta));
        return new("closed-level", new ClosedLevelWaterField(World, 0, -4), null, new(-10, -8, -10), new(10, 8, 10),
            GrazingWaterBank.Tuning, new(0, 1, 0), [(Vector3.Zero, queries)]);
    }

    // Paths whose directed lateral bounds cover the dry gap or border at centre x, from either side.
    static List<MovementMediumSweepQuery> Straddling(float x, float z)
    {
        var queries = new List<MovementMediumSweepQuery>();
        foreach (float y in new[] { -1.2f, -0.6f, -0.3f, 0.2f, 0.8f })
        {
            queries.Add(Query(new(x - 1, y, z), 0.3f, 0.75f, new(2, 0, 0)));
            queries.Add(Query(new(x + 1, y, z - 0.5f), 0.3f, 0.75f, new(-2, 0.1f, 0)));
            queries.Add(Query(new(x, y, z), 0.7f, 0.75f, Vector3.Zero));
            queries.Add(Query(new(x - 0.5f, y, z), 0.3f, 0.75f, new(1, -0.4f, 0)));
        }
        return queries;
    }

    static MovementMediumSweepQuery Local(MovementMediumSweepQuery absolute, Vector3 origin)
    {
        MovementBodyQuery body = absolute.Body;
        Vector3 centre = new((float)((double)body.Centre.X - origin.X), (float)((double)body.Centre.Y - origin.Y),
            (float)((double)body.Centre.Z - origin.Z));
        return new(new(centre, body.Radius, body.HalfHeight, body.CurrentSpace, body.CurrentSupport), absolute.Delta);
    }

    static void Pool(TileWorldDocument document, int x0, int x1, int z0, int z1, short bedCm)
    {
        for (int z = z0; z <= z1 + 1; z++)
            for (int x = x0; x <= x1 + 1; x++) document.SetCornerHeightCm(x, z, 0, bedCm);
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
    }

    static MovementMediumSweepQuery Query(Vector3 centre, float radius, float halfHeight, Vector3 delta) =>
        new(new(centre, radius, halfHeight, Space, null), delta);
}
