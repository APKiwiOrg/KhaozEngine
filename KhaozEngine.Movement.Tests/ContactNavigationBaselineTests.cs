using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationBaselineTests(ITestOutputHelper output)
{
    public static TheoryData<string, string> Baselines => new()
    {
        { "low-lip-bank", "494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922" },
        { "deck-over-ground", "77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc" },
        { "wade-only-pool", "a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635" },
        { "rebased-origin", "e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee" },
        { "mixed-legacy-contact", "94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856" },
        { "stair-accepted-refused", "62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a" },
        { "multi-layer-row-bands", "ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a" },
    };

    [Theory]
    [MemberData(nameof(Baselines))]
    public void SerialBakeMatchesNormalizedFullFileBaseline(string fixture, string sha256)
    {
        BaselineBake first = BuildFixture(fixture), second = BuildFixture(fixture);
        Assert.Equal(first.Bake.ProfileNames, second.Bake.ProfileNames);
        foreach (string name in first.Bake.ProfileNames)
            SameGraphBits(first.Bake.GetProfile(name), second.Bake.GetProfile(name));
        byte[] firstFile = NormalizeFile(first), secondFile = NormalizeFile(second);
        Assert.Equal(firstFile, secondFile);
        AssertFixture(fixture, first);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(firstFile));
        output.WriteLine($"{fixture}: {actualHash}");
        Assert.Equal(sha256, actualHash);
    }

    internal sealed record BaselineBake(GroundNavigationBake Bake, NavBakeExpectation Expectation);

    internal static BaselineBake BuildFixture(string fixture) => fixture switch
    {
        "low-lip-bank" => LowLipBank(),
        "deck-over-ground" => DeckOverGround(),
        "wade-only-pool" => WadeOnlyPool(),
        "rebased-origin" => RebasedOrigin(),
        "mixed-legacy-contact" => MixedLegacyContact(),
        "stair-accepted-refused" => Stair(extended: true),
        "multi-layer-row-bands" => Stair(extended: false),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
    };

    internal static byte[] NormalizeFile(BaselineBake fixture)
    {
        byte[] file = GroundNavigationBakeRoundTripTests.Write(fixture.Bake);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        Assert.Equal(NavBakeIdentity.Encode(fixture.Expectation, NavBakeIdentity.CurrentEngineVersion),
            file.AsSpan(52, length).ToArray());
        // Only the engine identity varies between releases. Keep the header and entire payload pinned.
        byte[] identity = NavBakeIdentity.Encode(fixture.Expectation, "0.0.0-golden");
        byte[] normalized = [.. file.AsSpan(0, 52), .. identity, .. file.AsSpan(52 + length)];
        BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(8), (uint)identity.Length);
        return normalized;
    }

    static BaselineBake Bake(GroundMoveContext context, PhysicsNavBakeOptions options, params NavBakeProfile[] profiles)
    {
        var expected = new NavBakeExpectation(options, ContactBakeFile.Sources(), profiles);
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        return new(GroundNavigationBake.Create(capture, expected.Sources, profiles), expected);
    }

    static BaselineBake LowLipBank()
    {
        using var world = LowLipTraversalTests.LipWorld();
        using var context = Context(world);
        return Bake(context, Options, new NavBakeProfile("player", Tuning with { CapsuleRadius = 0.3f }, default));
    }

    static BaselineBake DeckOverGround()
    {
        using var scene = new OrdinaryContactScene(Tuning, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "deck", new(-2, 2, -2), new(2, 2.1f, 2));
        });
        using var context = Context(scene);
        return Bake(context, Options, new NavBakeProfile("player", Tuning, default));
    }

    static BaselineBake WadeOnlyPool()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, ContactNavigationWaterTests.PoolHeight, MovementWaterField.Level("world", 0, -4));
        return Bake(context, ContactNavigationWaterTests.PoolOptions,
            new NavBakeProfile("player", Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default));
    }

    static BaselineBake RebasedOrigin()
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f };
        using var scene = new OrdinaryContactScene(tuning, origin: new(8, 2, -4), install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.25f, -8), new(8, 0, 8));
            Box(world, builder, "lip", new(0, 0, -2), new(2, 0.03125f, 2));
        });
        using var context = Context(scene);
        return Bake(context, Options with { MinX = -1.5f, MaxX = 1.5f }, new NavBakeProfile("player", tuning, default));
    }

    static BaselineBake MixedLegacyContact()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var water = MovementWaterField.Level("world", 0.1f, -4);
        using var context = Context(world, water: water, medium: (_, _, _) => new MovementMedium(0.1f, true, 1));
        return Bake(context, Options with { SampleWater = true },
            new("a-legacy", Tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default) { Aquatic = true },
            new("z-contact", Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default));
    }

    static BaselineBake Stair(bool extended)
    {
        MoveTuning tuning = Tuning with
        { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.15625f, StepHeight = 0.21f, MaxStepClimbSpeed = 0 };
        using var scene = new OrdinaryContactScene(tuning, origin: extended ? Vector3.Zero : new(8, 2, -4),
            install: (world, builder) =>
            {
                Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
                Box(world, builder, "left", new(-4, 0, -2), new(0, 0.1875f, 2));
                Box(world, builder, "right", new(0, 0.34375f, -2), new(4, 0.375f, 2));
                if (extended)
                    Box(world, builder, "wall", new(-0.02f, 0.1875f, 0.5f), new(0.02f, 1, 1.5f));
            });
        using var context = Context(scene);
        var options = extended
            ? Options with { MinX = -1, MaxX = 1, MinZ = -1.5f, MaxZ = 1.5f, CellSize = 1 }
            : Options with { MinX = -0.3f, MaxX = 0.3f, MinZ = -1.5f, MaxZ = 1.5f, CellSize = 0.3f };
        return Bake(context, options, new NavBakeProfile("player", tuning, default));
    }

    static void AssertFixture(string fixture, BaselineBake result)
    {
        GroundNavigation nav = result.Bake.GetProfile(fixture == "mixed-legacy-contact" ? "z-contact" : "player");
        switch (fixture)
        {
            case "low-lip-bank":
                Assert.Equal(0.025f, Resting(nav, new(-0.075f, 0, 0.125f)));
                break;
            case "deck-over-ground":
                Assert.Equal(2, nav.Space.Layers.Count);
                Assert.Equal(0f, Resting(nav, new(-0.075f, 0, 0.125f)));
                Assert.Equal(2.1f, Resting(nav, new(-0.075f, 2.1f, 0.125f)));
                break;
            case "wade-only-pool":
                Assert.Null(Resting(nav, new(0.25f, -2, 0.25f)));
                Assert.Equal(0f, Resting(nav, new(-2.25f, 0, 0.25f)));
                break;
            case "rebased-origin":
                Assert.Equal(0.03125f, Resting(nav, new(-0.125f, 0, 0.125f)));
                break;
            case "mixed-legacy-contact":
                Assert.All(result.Bake.GetProfile("a-legacy").Space.Layers, grid => Assert.False(grid.HasRestingHeights));
                Assert.All(nav.Space.Layers, grid => Assert.True(grid.HasRestingHeights));
                break;
            case "stair-accepted-refused":
                Assert.NotEmpty(nav.Graph.Links);
                Assert.Contains(nav.Space.Links, link => !nav.Graph.Links.Contains(link) &&
                    nav.Graph.IsNodePassable(link.FromLayer, link.FromX, link.FromZ) &&
                    nav.Graph.IsNodePassable(link.ToLayer, link.ToX, link.ToZ));
                break;
            case "multi-layer-row-bands":
                Assert.True(nav.Space.Layers.Count > 1);
                Assert.Equal(0.3f, nav.Space.Layers[0].CellSize);
                Assert.True(nav.Space.Layers[0].Height >= 9);
                for (int row = 1; row < nav.Space.Layers[0].Height; row++)
                {
                    Assert.Contains(nav.Graph.Links, link => link.FromZ == row - 1 && link.ToZ == row);
                    Assert.Contains(nav.Graph.Layers, layer => Enumerable.Range(0, layer.Width)
                        .Any(x => (layer.ExitMask(x, row - 1) & (1 << 2)) != 0));
                }
                break;
        }
    }
}
