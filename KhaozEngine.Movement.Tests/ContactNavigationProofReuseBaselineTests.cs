using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public sealed class ContactNavigationProofReuseBaselineTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Matrix => ContactNavigationBaselineTests.Baselines.SelectMany(row =>
        new[] { 1, 2, 3, 8, Rows((string)row[0]) + 1 }.Select(degree => new object[] { row[0], row[1], degree }));

    internal static int Rows(string fixture) => fixture switch
    {
        "deck-over-ground" => 16,
        "wade-only-pool" => 12,
        "stair-accepted-refused" => 6,
        "multi-layer-row-bands" => 20,
        _ => 8,
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryPinnedFileAndDegreeMatchesUnchangedSerialBytes(string fixture, string hash, int degree)
    {
        var reference = ContactNavigationReuseFixture.Build(fixture, ContactNavigationReuseMode.Off, 1);
        byte[] serialActualFile = GroundNavigationBakeRoundTripTests.Write(reference.Bake);
        int totalRows = reference.Bake.GetProfile(fixture == "mixed-legacy-contact" ? "z-contact" : "player")
            .Space.Layers.Sum(grid => grid.Height);
        Assert.Equal(Rows(fixture), totalRows);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(ContactNavigationBaselineTests.NormalizeFile(reference))));
        foreach (ContactNavigationReuseMode mode in Enum.GetValues<ContactNavigationReuseMode>())
        {
            var candidate = ContactNavigationReuseFixture.Build(fixture, mode, degree);
            Assert.Equal(serialActualFile, GroundNavigationBakeRoundTripTests.Write(candidate.Bake));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(ContactNavigationBaselineTests.NormalizeFile(candidate))));
            foreach (string name in reference.Bake.ProfileNames)
                SameGraphBits(reference.Bake.GetProfile(name), candidate.Bake.GetProfile(name));
            ContactNavigationBaselineTests.AssertFixture(fixture, candidate);
            output.WriteLine($"{fixture} mode={mode} degree={degree} rows={totalRows} hash={hash}");
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void SourceAndDirectionOrderDoNotChangeBakeBytes(string fixture, string hash, int degree)
    {
        foreach (ContactNavigationReuseMode mode in Enum.GetValues<ContactNavigationReuseMode>())
            InPinnedScene(fixture, (context, options, profiles) =>
            {
                using var hooks = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
                var execution = new NavBakeExecution(degree) { UseStableContactHoldReuse = mode == ContactNavigationReuseMode.Combined };
                using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
                var expected = new NavBakeExpectation(options, ContactBakeFile.Sources(), profiles);
                var bake = GroundNavigationBake.Create(capture, expected.Sources, profiles, execution);
                byte[] canonical = GroundNavigationBakeRoundTripTests.Write(bake);
                Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(
                    ContactNavigationBaselineTests.NormalizeFile(new(bake, expected)))));
                foreach (var order in new[] { (true, false), (false, true), (true, true) })
                {
                    ContactNavigationReuseFixture.ProveInOrder(capture, context, bake, order.Item1, order.Item2,
                        ContactNavigationReusePolicy.Capture(execution));
                    Assert.Equal(canonical, GroundNavigationBakeRoundTripTests.Write(bake));
                    ContactNavigationReuseFixture.Released();
                    output.WriteLine($"{fixture} mode={mode} degree={degree} reverseSources={order.Item1} reverseDirections={order.Item2}");
                }
            });
    }

    // Independent reconstruction retains every original operand. Its first file must still match the original pin.
    static void InPinnedScene(string fixture, Action<GroundMoveContext, PhysicsNavBakeOptions, NavBakeProfile[]> run)
    {
        if (fixture is "low-lip-bank" or "wade-only-pool" or "mixed-legacy-contact")
        {
            using var world = fixture == "low-lip-bank" ? LowLipTraversalTests.LipWorld() :
                fixture == "wade-only-pool" ? SwimTraversalProbeTests.PoolWorld() : GroundTraversalProbeTests.FlatWorld();
            IMovementWaterField? water = fixture == "wade-only-pool" ? MovementWaterField.Level("world", 0, -4) :
                fixture == "mixed-legacy-contact" ? MovementWaterField.Level("world", 0.1f, -4) : null;
            using var context = Context(world, fixture == "wade-only-pool" ? ContactNavigationWaterTests.PoolHeight : null,
                water, fixture == "mixed-legacy-contact" ? (_, _, _) => new MovementMedium(0.1f, true, 1) : null);
            NavBakeProfile[] profiles = fixture switch
            {
                "low-lip-bank" => [new("player", Tuning with { CapsuleRadius = 0.3f }, default)],
                "wade-only-pool" => [new("player", Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default)],
                _ => [new("a-legacy", Tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default) { Aquatic = true },
                    new("z-contact", Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default)],
            };
            run(context, fixture == "wade-only-pool" ? ContactNavigationWaterTests.PoolOptions :
                Options with { SampleWater = fixture == "mixed-legacy-contact" }, profiles);
            return;
        }
        bool stairs = fixture is "stair-accepted-refused" or "multi-layer-row-bands";
        bool extended = fixture == "stair-accepted-refused";
        MoveTuning tuning = stairs ? Tuning with
        { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.15625f, StepHeight = 0.21f, MaxStepClimbSpeed = 0 } :
            fixture == "rebased-origin" ? Tuning with { CapsuleRadius = 0.3f } : Tuning;
        using var scene = new OrdinaryContactScene(tuning,
            origin: fixture == "rebased-origin" || stairs && !extended ? new(8, 2, -4) : Vector3.Zero,
            install: (world, builder) =>
            {
                Box(world, builder, "floor", new(-8, fixture == "rebased-origin" ? -0.25f : -0.2f, -8), new(8, 0, 8));
                if (stairs)
                {
                    Box(world, builder, "left", new(-4, 0, -2), new(0, 0.1875f, 2));
                    Box(world, builder, "right", new(0, 0.34375f, -2), new(4, 0.375f, 2));
                    if (extended) Box(world, builder, "wall", new(-0.02f, 0.1875f, 0.5f), new(0.02f, 1, 1.5f));
                }
                else if (fixture == "rebased-origin") Box(world, builder, "lip", new(0, 0, -2), new(2, 0.03125f, 2));
                else Box(world, builder, "deck", new(-2, 2, -2), new(2, 2.1f, 2));
            });
        using var sceneContext = Context(scene);
        var options = fixture switch
        {
            "rebased-origin" => Options with { MinX = -1.5f, MaxX = 1.5f },
            "stair-accepted-refused" => Options with { MinX = -1, MaxX = 1, MinZ = -1.5f, MaxZ = 1.5f, CellSize = 1 },
            "multi-layer-row-bands" => Options with { MinX = -0.3f, MaxX = 0.3f, MinZ = -1.5f, MaxZ = 1.5f, CellSize = 0.3f },
            _ => Options,
        };
        run(sceneContext, options, [new("player", tuning, default)]);
    }
}
