using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class GroundNavigationBakeRoundTripTests
{
    private const float Dt = 1f / 30f;
    private static MoveTuning Tuning => GroundTraversalProbeTests.Tuning;
    private static readonly PhysicsNavBakeOptions Options = new(-1.5f, -0.5f, 1.5f, 0.5f,
        1f, 5f, 6f, 0.8f, 128, 512);
    private static readonly MoveTuning Tiny = Tuning with { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.1f, MaxStepClimbSpeed = 0f };

    public static TheoryData<string> Fixtures => new()
    {
        "thin-wall", "door", "step", "deck", "rebased", "stair-open", "stair-fence", "pool", "pool-padded", "channel",
    };

    [Theory, MemberData(nameof(Fixtures))]
    public void LoadedProfilesMatchTheFreshBuild(string fixture)
    {
        Baked baked = Bake(fixture);

        GroundNavigationBake loaded = LoadOk(baked.File, baked.Expected);

        Assert.Equal(baked.Bake.ProfileNames, loaded.ProfileNames);
        Assert.True(baked.Bake.Fingerprint.SequenceEqual(loaded.Fingerprint));
        foreach (string name in baked.Bake.ProfileNames)
            BakeEquivalence.AssertEquivalent(baked.Bake.GetProfile(name), loaded.GetProfile(name));
        Assert.Equal(baked.File, Write(loaded));
    }

    [Fact]
    public void StairSeamAcceptanceRoundTrips()
    {
        GroundNavigation open = LoadedSeamProfile("stair-open", out NavLink openSeam);
        GroundNavigation fenced = LoadedSeamProfile("stair-fence", out NavLink fencedSeam);

        Assert.Contains(openSeam, open.Graph.Links);
        Assert.Contains(fencedSeam, fenced.Space.Links);
        Assert.DoesNotContain(fencedSeam, fenced.Graph.Links);
        Assert.True(open.AllowsSegment(new Vector3(-0.5f, 0.1f, 0f), new Vector3(0.5f, 0.3f, 0f)));
        Assert.False(fenced.AllowsSegment(new Vector3(-0.5f, 0.1f, 0f), new Vector3(0.5f, 0.3f, 0f)));
    }

    [Fact]
    public void TwoCreatesWriteIdenticalBytes()
    {
        Baked first = Bake("deck"), second = Bake("deck");

        Assert.Equal(first.File, second.File);
        Assert.Equal(first.File, Write(first.Bake));
        Assert.Equal(SHA256.HashData(first.File.AsSpan(52, Header(first.File).IdentityLength)), first.Bake.Fingerprint.ToArray());
    }

    [Fact]
    public void RewritingALoadedBakeReproducesItsBytes()
    {
        foreach (string fixture in new[] { "deck", "stair-open", "door", "pool", "channel" })
        {
            Baked baked = Bake(fixture);
            Assert.Equal(baked.File, Write(LoadOk(baked.File, baked.Expected)));
        }
    }

    [Fact]
    public void ProfilesShareOneColumnSnapshotAfterLoad()
    {
        Baked baked = Bake("deck");
        GroundNavigationBake loaded = LoadOk(baked.File, baked.Expected);

        Assert.Same(loaded.GetProfile("dry").Footprint.Columns, loaded.GetProfile("wet").Footprint.Columns);
        Assert.Same(baked.Bake.GetProfile("dry").Footprint.Columns, baked.Bake.GetProfile("wet").Footprint.Columns);
        Assert.NotSame(baked.Bake.GetProfile("dry").Footprint.Columns, loaded.GetProfile("dry").Footprint.Columns);
    }

    [Fact]
    public void LoadedProfileDrivesMoveToRangeLikeTheFreshOne()
    {
        MoveTuning tuning = Tuning with { WalkSpeed = 2f, RunSpeed = 5f };
        var options = new PhysicsNavBakeOptions(-3.25f, -2.75f, 3.25f, 2.75f, 0.5f, 5f, 6f, 0.8f, 256, 1024);
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(0.1f, 1f, 1f)), Pose.At(new Vector3(0f, 1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        NavBakeProfile[] profiles = [new("npc", tuning, default)];
        GroundNavigationBake bake;
        using (PhysicsNavBake capture = PhysicsNavBake.Capture(context, options, _ => 0u))
            bake = GroundNavigationBake.Create(capture, Sources(), profiles);
        GroundNavigationBake loaded = LoadOk(Write(bake), new NavBakeExpectation(options, Sources(), profiles));
        var freshMove = new MoveToRange(bake.GetProfile("npc"));
        var loadedMove = new MoveToRange(loaded.GetProfile("npc"));
        var target = ReachTarget.Capsule(new MovementBody(new Vector3(2f, 0.75f, 0f), 0.2f, 0.75f));
        MoveState freshBody = new() { Position = new Vector3(-2f, 0.75f, 0f), Grounded = true };
        MoveState loadedBody = freshBody;
        int following = 0;

        for (int tick = 0; tick < 60; tick++)
        {
            RangeSteering a = freshMove.Tick(freshBody, tuning, target, 0.6f, false, Dt, context);
            RangeSteering b = loadedMove.Tick(loadedBody, tuning, target, 0.6f, false, Dt, context);
            Assert.Equal(a, b);
            if (a.Status == RangeMoveStatus.Following) following++;
            freshBody = NpcGroundMovement.Step(freshBody, a, false, Dt, tuning, context);
            loadedBody = NpcGroundMovement.Step(loadedBody, b, false, Dt, tuning, context);
            Assert.Equal(freshBody, loadedBody);
        }
        Assert.True(following > 30, $"Only {following} of 60 ticks followed a route.");
        Assert.True(MathF.Abs(freshBody.Position.Z) > 0.5f, "The walk never detoured around the wall.");
    }

    [Fact]
    public void CreateRefusesADisposedCaptureAndAnOversizedSurfaceCap()
    {
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        NavBakeProfile[] profiles = [new("player", Tuning, default)];
        PhysicsNavBake disposed = PhysicsNavBake.Capture(context, Options, _ => 0u);
        disposed.Dispose();
        using PhysicsNavBake oversized = PhysicsNavBake.Capture(context, Options with { MaxSurfacesPerColumn = 256 }, _ => 0u);
        using PhysicsNavBake largest = PhysicsNavBake.Capture(context, Options with { MaxSurfacesPerColumn = 255 }, _ => 0u);

        Assert.Throws<ObjectDisposedException>(() => GroundNavigationBake.Create(disposed, Sources(), profiles));
        Assert.Equal("capture", Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroundNavigationBake.Create(oversized, Sources(), profiles)).ParamName);
        Assert.Equal(new[] { "player" }, GroundNavigationBake.Create(largest, Sources(), profiles).ProfileNames);
    }

    [Fact]
    public void SourcesAreSnapshotAtCreateAndLoad()
    {
        BakeFixture fixture = Fixture("deck");
        NavBakeSources sources = Sources();
        List<NavBakeProfile> profiles = [.. fixture.Profiles];
        GroundNavigationBake bake;
        using (BepuPhysicsWorld world = fixture.World())
        using (PhysicsNavBake capture = PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: world),
            fixture.Options, fixture.Classify))
            bake = GroundNavigationBake.Create(capture, sources, profiles);
        byte[] file = Write(bake);
        byte[] fingerprint = bake.Fingerprint.ToArray();

        sources.Add("late", new byte[32]);
        profiles.Clear();
        Assert.Equal(file, Write(bake));
        Assert.Equal(fingerprint, bake.Fingerprint.ToArray());
        Assert.Equal(new[] { "dry", "wet" }, bake.ProfileNames);

        NavBakeSources expectedSources = Sources();
        GroundNavigationBake loaded = LoadOk(file, new NavBakeExpectation(fixture.Options, expectedSources, fixture.Profiles));
        expectedSources.Add("late", new byte[32]);
        Assert.Equal(file, Write(loaded));
    }

    [Fact]
    public void LoadedAquaticProfileMatchesTheFreshBuild()
    {
        Baked baked = Bake("channel");

        GroundNavigationBake loaded = LoadOk(baked.File, baked.Expected);

        Assert.True(baked.Bake.GetProfile("duck-swim").Aquatic);
        Assert.True(loaded.GetProfile("duck-swim").Aquatic);
        Assert.False(loaded.GetProfile("duck").Aquatic);
        Assert.False(loaded.GetProfile("wide").Aquatic);
        foreach (string name in new[] { "duck", "duck-swim", "wide" })
            BakeEquivalence.AssertEquivalent(baked.Bake.GetProfile(name), loaded.GetProfile(name));
    }

    [Fact]
    public void RewritingALoadedAquaticBakeReproducesItsBytes()
    {
        Baked baked = Bake("channel");

        byte[] rewritten = Write(LoadOk(baked.File, baked.Expected));

        Assert.Equal(baked.File, rewritten);
        Assert.Equal(baked.File, Write(LoadOk(rewritten, baked.Expected)));
    }

    [Fact]
    public void GroundProfilesStillShareOneColumnSnapshot()
    {
        Baked baked = Bake("channel");
        GroundNavigationBake loaded = LoadOk(baked.File, baked.Expected);

        foreach (GroundNavigationBake bake in new[] { baked.Bake, loaded })
        {
            PhysicsNavColumns duck = bake.GetProfile("duck").Footprint.Columns;
            PhysicsNavColumns swim = bake.GetProfile("duck-swim").Footprint.Columns;
            Assert.Same(duck, bake.GetProfile("wide").Footprint.Columns);
            Assert.NotSame(duck, swim);
            Assert.False(duck.IsFloat(8, 4, 0));
            Assert.True(swim.IsFloat(8, 4, 0));
        }
        Assert.NotSame(baked.Bake.GetProfile("duck-swim").Footprint.Columns, loaded.GetProfile("duck-swim").Footprint.Columns);
    }

    [Fact]
    public void AquaticProfileWithoutSampledWaterIsRefusedAtCreate()
    {
        BakeFixture fixture = Fixture("channel");
        PhysicsNavBakeOptions dry = fixture.Options with { SampleWater = false };
        using BepuPhysicsWorld world = fixture.World();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(
            new GroundMoveContext(fixture.Ground!, physics: world, medium: fixture.Medium), dry, fixture.Classify);

        Assert.Equal("options", Assert.Throws<ArgumentException>(() =>
            GroundNavigationBake.Create(capture, Sources(), fixture.Profiles)).ParamName);
        Assert.Throws<ArgumentException>(() => GroundNavigationBake.Load(new MemoryStream(Bake("channel").File),
            new NavBakeExpectation(dry, Sources(), fixture.Profiles)));
        NavBakeProfile[] disordered = [fixture.Profiles[1] with
        {
            Tuning = fixture.Profiles[1].Tuning with { SwimSurfaceSubmersionFraction = 0.5f },
        }];
        Assert.Throws<ArgumentException>(() => GroundNavigationBake.Load(new MemoryStream(Bake("channel").File),
            new NavBakeExpectation(fixture.Options, Sources(), disordered)));
    }

    [Fact]
    public void GetProfileRejectsUnknownNames()
    {
        Baked baked = Bake("door");
        GroundNavigationBake loaded = LoadOk(baked.File, baked.Expected);

        Assert.Equal(new[] { "small", "wide" }, loaded.ProfileNames);
        Assert.Throws<KeyNotFoundException>(() => baked.Bake.GetProfile("medium"));
        Assert.Throws<KeyNotFoundException>(() => loaded.GetProfile("Small"));
        Assert.NotNull(loaded.GetProfile("wide"));
    }

    internal sealed record BakeFixture(Func<BepuPhysicsWorld> World, PhysicsNavBakeOptions Options,
        NavAreaClassifier Classify, NavBakeProfile[] Profiles, Func<float, float, float, MovementMedium>? Medium = null,
        Func<float, float, float>? Ground = null);

    internal sealed record Baked(GroundNavigationBake Bake, NavBakeExpectation Expected, byte[] File);

    internal static BakeFixture Fixture(string name) => name switch
    {
        "thin-wall" => new(() => With(GroundTraversalProbeTests.FlatWorld(), new Vector3(0.05f, 1f, 2f), new Vector3(0.5f, 1f, 0f)),
            Options with { MinZ = -1.5f, MaxZ = 1.5f, CellSize = 0.5f }, _ => 0u, [new("player", Tuning, default)]),
        "door" => new(() => With(With(GroundTraversalProbeTests.FlatWorld(),
                new Vector3(0.05f, 1f, 1f), new Vector3(0f, 1f, 1.5f)), new Vector3(0.05f, 1f, 1f), new Vector3(0f, 1f, -1.5f)),
            Options with { MinX = -2.25f, MaxX = 2.25f, MinZ = -1.25f, MaxZ = 1.25f, CellSize = 0.5f }, _ => 0u,
            [new("wide", Tuning with { CapsuleRadius = 0.6f }, default), new("small", Tuning, default)]),
        "step" => new(GroundTraversalProbeTests.StepWorld,
            Options with { MinZ = -1f, MaxZ = 1f, CellSize = 0.5f }, _ => 0u,
            [new("player", Tuning, default), new("slow-climb", Tuning with { MaxStepClimbSpeed = 0.001f }, default)]),
        "deck" => new(() => With(GroundTraversalProbeTests.FlatWorld(), new Vector3(4f, 0.1f, 4f), new Vector3(0f, 2.9f, 0f)),
            Options with { MinX = -5f, MaxX = 5f, MinZ = -0.75f, MaxZ = 0.75f, CellSize = 0.5f },
            feet => feet.Y < 1f ? 0x02u : 0x01u,
            [new("wet", Tuning, new NavAreaFilter(0x02u, 0u)), new("dry", Tuning, new NavAreaFilter(0u, 0x02u))]),
        "rebased" => new(() =>
            {
                BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
                world.Rebase(new Vector3(100f, 5f, -40f));
                return world;
            },
            Options with { MinZ = -1.5f, MaxZ = 1.5f }, _ => 0u, [new("player", Tuning, default)]),
        "stair-open" => new(() => StairWorld(fence: false), Options with { MinX = -1f, MaxX = 1f }, _ => 0u,
            [new("tiny", Tiny, default)]),
        "stair-fence" => new(() => StairWorld(fence: true), Options with { MinX = -1f, MaxX = 1f }, _ => 0u,
            [new("tiny", Tiny, default)]),
        "pool" => new(GroundTraversalProbeTests.FlatWorld,
            Options with { MinX = -2f, MaxX = 2f, MinZ = -1f, MaxZ = 1f, SampleWater = true },
            feet => feet.Y > 1f ? 0x04u : 0x01u, [new("player", Tuning, default)], WaterCaptureTests.Pool),
        // Centres x -1.5 to 1.5 against MaxX 1.4, so the last column is padding.
        "pool-padded" => new(GroundTraversalProbeTests.FlatWorld,
            Options with { MinX = -2f, MaxX = 1.4f, MinZ = -1f, MaxZ = 1f, SampleWater = true },
            _ => 0x01u, [new("player", Tuning, default)], WaterCaptureTests.Pool),
        // Two ground profiles and one aquatic profile over AquaticProfileTests' steep channel.
        "channel" => new(AquaticProfileTests.ChannelWorld,
            Options with
            {
                MinX = -2f,
                MaxX = 2f,
                MinZ = -1f,
                MaxZ = 1f,
                CellSize = 0.25f,
                ProbeHeight = 2f,
                ProbeRange = 5f,
                MaxCells = 256,
                MaxLayerCells = 1024,
                SampleWater = true,
            },
            _ => 0u,
            [
                new("duck", SwimTraversalProbeTests.Duck, default),
                new("duck-swim", SwimTraversalProbeTests.Duck, default) { Aquatic = true },
                new("wide", SwimTraversalProbeTests.Duck with { CapsuleRadius = 0.24f }, default),
            ],
            AquaticProfileTests.ChannelMedium, AquaticProfileTests.ChannelGround),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    internal static Baked Bake(string name)
    {
        BakeFixture fixture = Fixture(name);
        using BepuPhysicsWorld world = fixture.World();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(
            new GroundMoveContext(fixture.Ground ?? ((_, _) => 0f), physics: world, medium: fixture.Medium),
            fixture.Options, fixture.Classify);
        GroundNavigationBake bake = GroundNavigationBake.Create(capture, Sources(), fixture.Profiles);
        return new Baked(bake, new NavBakeExpectation(fixture.Options, Sources(), fixture.Profiles), Write(bake));
    }

    internal static NavBakeSources Sources() => new NavBakeSources()
        .Add("world", Enumerable.Repeat((byte)0x11, 32).ToArray())
        .AddHashOf("classifier", "fixture-v1"u8);

    internal static byte[] Write(GroundNavigationBake bake)
    {
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        return stream.ToArray();
    }

    internal static GroundNavigationBake LoadOk(byte[] file, NavBakeExpectation expected)
    {
        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(file), expected);
        Assert.True(result.Status == NavBakeLoadStatus.Loaded, $"{result.Status}: {result.Detail}");
        Assert.Equal("", result.Detail);
        return result.Bake!;
    }

    internal static (int IdentityLength, ulong PayloadLength) Header(byte[] file) =>
        ((int)BitConverter.ToUInt32(file, 8), BitConverter.ToUInt64(file, 12));

    // The two decks and optional fence of PhysicsNavProfileTests.CandidateStairLinksRequireTheSamePhysicalProof.
    private static BepuPhysicsWorld StairWorld(bool fence)
    {
        BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 0.05f, 2f)), Pose.At(new Vector3(-2f, 0.05f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(2f, 0.025f, 2f)), Pose.At(new Vector3(2f, 0.275f, 0f)));
        if (fence)
            world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 0f)));
        return world;
    }

    private static BepuPhysicsWorld With(BepuPhysicsWorld world, Vector3 extents, Vector3 centre)
    {
        world.AddStatic(new BoxShape(extents), Pose.At(centre));
        return world;
    }

    private static GroundNavigation LoadedSeamProfile(string fixture, out NavLink seam)
    {
        Baked baked = Bake(fixture);
        GroundNavigation loaded = LoadOk(baked.File, baked.Expected).GetProfile("tiny");
        seam = Assert.Single(loaded.Space.Links, link => link.FromX == 0 && link.ToX == 1);
        Assert.Equal(NavLinkKind.Stair, seam.Kind);
        return loaded;
    }
}
