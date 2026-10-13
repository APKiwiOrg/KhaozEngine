using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class GroundNavigationBakeFormatTwoTests
{
    [Theory]
    [InlineData(false, false, false, NavBakeLoadStatus.Loaded)]
    [InlineData(false, true, false, NavBakeLoadStatus.ProfilesChanged)]
    [InlineData(true, false, false, NavBakeLoadStatus.ProfilesChanged)]
    [InlineData(true, true, false, NavBakeLoadStatus.Loaded)]
    [InlineData(false, false, true, NavBakeLoadStatus.EngineChanged)]
    [InlineData(false, true, true, NavBakeLoadStatus.EngineChanged)]
    [InlineData(true, false, true, NavBakeLoadStatus.EngineChanged)]
    [InlineData(true, true, true, NavBakeLoadStatus.EngineChanged)]
    public void LoadMatrix(bool storedContact, bool expectedContact, bool otherEngine, NavBakeLoadStatus status)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        MoveTuning legacy = Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        var profile = new NavBakeProfile("player", storedContact ? Tuning : legacy, default);
        byte[] file = Write(Bake(context, FlatOptions, profile));
        var expected = new NavBakeExpectation(FlatOptions, Sources(),
            [profile with { Tuning = expectedContact ? Tuning : legacy }]);
        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(file), expected,
            otherEngine ? "0.0.0-other" : NavBakeIdentity.CurrentEngineVersion);
        Assert.Equal(status, result.Status);
        if (status != NavBakeLoadStatus.Loaded) Assert.Null(result.Bake);
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("legacy-foot")]
    [InlineData("legacy-water")]
    [InlineData("legacy-jump")]
    [InlineData("contact-water-zero")]
    [InlineData("contact-foot-zero")]
    [InlineData("contact-foot-nan")]
    [InlineData("contact-foot-above-one")]
    [InlineData("contact-water-unknown")]
    [InlineData("contact-jump-negative")]
    [InlineData("contact-aquatic")]
    [InlineData("placement-offset")]
    public void IdentityCorruptionRows(string fault)
    {
        byte[] file = FlatFile(1, 1, Tuning, 0, out var expected);
        int end = 52 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        int fields = end - 14;
        if (fault.StartsWith("legacy-", StringComparison.Ordinal))
        {
            file[fields] = 0;
            file[fields + 5] = 0;
        }
        switch (fault)
        {
            case "unknown-kind": file[fields] = 2; break;
            case "legacy-foot": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 1), 0.6f); break;
            case "legacy-water": file[fields + 5] = 3; break;
            case "legacy-jump": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 6), 1); break;
            case "contact-water-zero": file[fields + 5] = 0; break;
            case "contact-foot-zero": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 1), 0); break;
            case "contact-foot-nan": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 1), float.NaN); break;
            case "contact-foot-above-one": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 1), 1.1f); break;
            case "contact-water-unknown": file[fields + 5] = 4; break;
            case "contact-jump-negative": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 6), -1); break;
            case "contact-aquatic": file[fields - 106] = 1; break;
            case "placement-offset": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(fields + 10), 0.1f); break;
        }
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(new MemoryStream(file), expected).Status);
    }

    [Fact]
    public void MixedProfileIdentityOutOfOrderIsCorrupt()
    {
        var expected = new NavBakeExpectation(Options, Sources(),
        [
            new("alpha", Tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default),
            new("bravo", Tuning, default),
        ]);
        byte[] identity = NavBakeIdentity.Encode(expected, NavBakeIdentity.CurrentEngineVersion);
        const int ProfileLength = 1 + 5 + 8 + 1 + 105 + 14;
        int start = identity.Length - 2 * ProfileLength;
        byte[] reversed = [.. identity.AsSpan(start + ProfileLength), .. identity.AsSpan(start, ProfileLength)];
        reversed.CopyTo(identity, start);
        byte[] file = Container(identity, [], 2);
        var result = GroundNavigationBake.Load(new MemoryStream(file), expected);
        Assert.Equal(NavBakeLoadStatus.Corrupt, result.Status);
        Assert.Contains("profile names out of order", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("options", NavBakeLoadStatus.OptionsChanged, "ProbeRange")]
    [InlineData("sources", NavBakeLoadStatus.SourcesChanged, "world")]
    [InlineData("Gravity", NavBakeLoadStatus.ProfilesChanged, "Gravity")]
    [InlineData("FootRadiusFraction", NavBakeLoadStatus.ProfilesChanged, "FootRadiusFraction")]
    [InlineData("Water.Mode", NavBakeLoadStatus.ProfilesChanged, "Water.Mode")]
    [InlineData("Water.SurfaceJumpSpeed", NavBakeLoadStatus.ProfilesChanged, "Water.SurfaceJumpSpeed")]
    public void ContactIdentityDifferencesFollowComparisonOrder(string field, NavBakeLoadStatus status, string detail)
    {
        byte[] file = FlatFile(1, 1, Tuning, 0, out var expected);
        NavBakeProfile profile = expected.Profiles[0];
        MoveTuning changed = field switch
        {
            "Gravity" => Tuning with { Gravity = Tuning.Gravity + 1 },
            "FootRadiusFraction" => Tuning with { FootRadiusFraction = 0.6f },
            "Water.Mode" => Tuning with { Water = new(WaterTraversalMode.WadeOnly) },
            "Water.SurfaceJumpSpeed" => Tuning with { Water = new(Tuning.Water.Mode, 1) },
            _ => Tuning,
        };
        expected = expected with { Profiles = [profile with { Tuning = changed }] };
        if (field == "options") expected = expected with
        { Options = expected.Options with { ProbeRange = 7 }, Profiles = [profile with { Tuning = Tuning with { Gravity = Tuning.Gravity + 1 } }] };
        if (field == "sources") expected = expected with
        { Sources = new NavBakeSources().Add("world", Enumerable.Repeat((byte)1, 32).ToArray()), Profiles = [profile with { Tuning = Tuning with { FootRadiusFraction = 0.6f } }] };
        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(file), expected);
        Assert.Equal(status, result.Status);
        Assert.Contains(detail, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ContactRoundTripIsByteExact()
    {
        using var world = LowLipTraversalTests.LipWorld();
        using var context = Context(world);
        var profile = new NavBakeProfile("player", Tuning with { CapsuleRadius = 0.3f }, default);
        var fresh = Bake(context, Options, profile);
        byte[] file = Write(fresh);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        var loaded = Load(file, new(Options, Sources(), [profile]));
        Assert.Equal(file, Write(loaded));
        GroundNavigation a = fresh.GetProfile("player"), b = loaded.GetProfile("player");
        SameGraph(a, b);
        Assert.Equal(a.Identity, b.Identity);
        for (int layer = 0; layer < a.Space.Layers.Count; layer++)
        {
            NavGrid grid = a.Space.Layers[layer];
            Assert.True(b.Space.Layers[layer].HasRestingHeights);
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    float? y = grid.RestingHeightAt(x, z);
                    Assert.Equal(y, b.Space.Layers[layer].RestingHeightAt(x, z));
                    if (y is not float feetY) continue;
                    Vector2 centre = grid.CellCenter(x, z);
                    Vector3 feet = new(centre.X, feetY, centre.Y);
                    Assert.Equal(a.Footprint.Accepts(feet), b.Footprint.Accepts(feet));
                }
        }
        Vector3 from = new(-0.825f, 0, 0.125f), to = new(0.925f, 0.025f, 0.125f);
        NavPath freshPath = a.Planner.FindPath(from, to, a.AgentRadius, PathQueryBudget.Default);
        NavPath loadedPath = b.Planner.FindPath(from, to, b.AgentRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, freshPath.Status);
        Assert.Equal(freshPath.Status, loadedPath.Status);
        Assert.NotEmpty(freshPath.Waypoints);
        Assert.Equal(freshPath.Waypoints, loadedPath.Waypoints);
        Assert.True(b.AllowsSegment(new(-0.325f, 0.025f, 0.125f), new(0.175f, 0.025f, 0.125f)));
        var atAnchor = new NavGoalRegion(new(-0.075f, 0.025f, 0.125f), 0.01f,
            feet => MathF.Abs(feet.X + 0.075f) < 0.001f && MathF.Abs(feet.Y - 0.025f) <= 0.001f);
        var atRaw = new NavGoalRegion(new(-0.075f, 0, 0.125f), 0.01f,
            feet => MathF.Abs(feet.X + 0.075f) < 0.001f && MathF.Abs(feet.Y) <= 0.001f);
        NavPath freshRegion = a.Planner.FindPath(from, atAnchor, a.AgentRadius, PathQueryBudget.Default);
        NavPath loadedRegion = b.Planner.FindPath(from, atAnchor, a.AgentRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, freshRegion.Status);
        Assert.Equal(freshRegion.Status, loadedRegion.Status);
        Assert.Equal(freshRegion.Waypoints, loadedRegion.Waypoints);
        foreach (var graph in new[] { a, b })
        {
            Assert.Equal(NavPathStatus.Unreachable, graph.Planner.FindPath(from, atRaw, a.AgentRadius, PathQueryBudget.Default).Status);
            Assert.False(graph.Footprint.Accepts(from + Vector3.UnitY * 100));
            Assert.False(graph.Footprint.Accepts(new(100, 0, 0)));
        }
    }

    [Fact]
    public void FullyOpenSampledWaterContactBakeExceedsFormatOneBound()
    {
        byte[] file = FlatFile(100, 100, Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, 0,
            out var expected, sampleWater: true);
        // Format 1's maximum for C=10000, M=1, Lmax=1, N=1 is 301318 bytes.
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(12)) > 301318);
        var loaded = Load(file, expected);
        Assert.Equal(file, Write(loaded));
        Assert.Equal(100, loaded.GetProfile("player").Space.Layers[0].Width);
    }

    [Theory]
    [InlineData("missing-resting-byte")]
    [InlineData("trailing-byte")]
    [InlineData("above-band")]
    [InlineData("below-band")]
    [InlineData("nonfinite")]
    public void RestingSectionCorruption(string fault)
    {
        byte[] file = FlatFile(1, 1, Tuning, 0, out var expected);
        int resting = file.Length - 8;
        switch (fault)
        {
            case "missing-resting-byte": file = [.. file.AsSpan(0, resting), .. file.AsSpan(resting + 1)]; break;
            case "trailing-byte": file = [.. file, 0]; break;
            case "above-band": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(resting), MathF.BitIncrement(Tuning.StepHeight)); break;
            case "below-band": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(resting), MathF.BitDecrement(-Tuning.GroundedEpsilon)); break;
            case "nonfinite": BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(resting), float.PositiveInfinity); break;
        }
        Reseal(file);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(new MemoryStream(file), expected).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothRestingBandEndsRoundTrip(bool upper)
    {
        float resting = upper ? Tuning.StepHeight : -Tuning.GroundedEpsilon;
        byte[] file = FlatFile(1, 1, Tuning, resting, out var expected);
        var loaded = Load(file, expected);
        Assert.Equal(resting, loaded.GetProfile("player").Space.Layers[0].RestingHeightAt(0, 0));
        Assert.Equal(file, Write(loaded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedLegacyAquaticAndContactRoundTrip(bool contactFirst)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var field = MovementWaterField.Level("world", 0.1f, -4);
        using var context = Context(world, water: field, medium: (_, _, _) => new MovementMedium(0.1f, true, 1));
        var options = Options with { SampleWater = true };
        var contact = new NavBakeProfile(contactFirst ? "a-contact" : "z-contact",
            Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default);
        var legacy = new NavBakeProfile(contactFirst ? "z-legacy" : "a-legacy",
            Tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default)
        { Aquatic = true };
        var fresh = Bake(context, options, legacy, contact);
        byte[] file = Write(fresh);
        var loaded = Load(file, new(options, Sources(), [contact, legacy]));
        Assert.Equal(file, Write(loaded));
        foreach (string name in fresh.ProfileNames) SameGraph(fresh.GetProfile(name), loaded.GetProfile(name));
        Assert.False(loaded.GetProfile(legacy.Name).Space.Layers[0].HasRestingHeights);
        Assert.True(loaded.GetProfile(contact.Name).Space.Layers[0].HasRestingHeights);
    }
}
