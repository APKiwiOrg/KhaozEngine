using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.Movement.Fixtures;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileSlopedRangeEquivalenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1f)]
    [InlineData(0.3f)]
    public void CapturedSlopesKeepDrawnTriangleSelectionAndHeightBits(float tileSize)
    {
        // #1754. A plane equation or a different seam tie can change these exact sampled bits.
        var document = ContactTerrainScene.Document(0, tileSize, _ => 0);
        for (int z = 0; z <= (int)(3 / tileSize); z++)
            for (int x = 0; x <= (int)(5 / tileSize); x++)
                document.SetCornerHeightCm(x, z, 0, (short)(3 * x + 5 * z + x * z % 3));
        using var scene = new ContactTerrainScene(0, document: document);
        var source = Assert.IsAssignableFrom<IMovementSupportedAnalyticSupportSource>(scene.Source);
        Assert.Equal(MovementAvailability.Known,
            source.CaptureSupportedRegion(new(-0f, -2.75f * tileSize), new(4.5f * tileSize, 0), out var region));
        Assert.NotNull(region);
        float[] phases = [-0f, 0f, MathF.BitDecrement(tileSize), tileSize, MathF.BitIncrement(tileSize),
            1.5f * tileSize, 2 * tileSize, 2.5f * tileSize];
        Assert.Equal(8, phases.Length);
        foreach (float x in phases)
            foreach (float z in phases)
            {
                Assert.Equal(MovementAvailability.Known, region.Sample(new(x, -z), out float height, out var normal));
                Same(scene.Colliders.MovementGroundHeight(x, -z), height);
                Same(scene.Colliders.MovementGroundNormal(x, -z), normal);
            }
        Assert.Equal(MovementAvailability.Unresolved,
            region.Sample(new(MathF.BitIncrement(4.5f * tileSize), 0), out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverySlopedTrialAndArrivalMatchesTheOrdinaryKernel(bool rebase)
    {
        // #1754. Include rounded cell and triangle seams, signed zero and tiny commands, not only endpoints.
        var document = ContactTerrainScene.Document(0, profile: x => 0.07f * x);
        for (int z = 0; z <= 3; z++)
            for (int x = 0; x <= 5; x++)
                document.SetCornerHeightCm(x, z, 0, (short)(7 * x + 3 * z + x * z % 3));
        using var scene = new ContactTerrainScene(0, document: document);
        if (rebase) scene.World.Rebase(new(2, 0.125f, -3));
        MoveTuning tuning = ContactTerrainScene.Tuning with { FacingTurnSpeed = 2.5f };
        var body = new MoveState
        {
            Position = new(1.99f, scene.Colliders.MovementGroundHeight(1.99f, -1.5f) + tuning.CapsuleHalfHeight, -1.5f),
            Grounded = true,
            FacingYaw = 1.5f,
            SpeedScale = 1.25f,
            VerticalVelocity = -3,
            TimeSinceGrounded = 0.2f,
            HorizontalVelocity = new(2, -3),
            LandingImpactSpeed = 4,
        };
        const float dt = 0.04f;
        var random = new Random(1754);
        var commands = new Vector2[132];
        var expected = new MoveState[commands.Length];
        commands[1] = new(-0f, 0);
        commands[2] = new(float.Epsilon, -float.Epsilon);
        commands[3] = new(1e-20f, -1e-20f);
        for (int i = 4; i < commands.Length; i++)
        {
            float angle = random.NextSingle() * MathF.Tau;
            commands[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * random.NextSingle();
        }
        for (int i = 0; i < commands.Length; i++) expected[i] = scene.Context.Step(body, commands[i], true, dt, tuning);
        var endpoint = scene.Context.Step(body, Vector2.UnitX, true, dt, tuning);
        using (var preview = scene.Context.OpenGroundRangePreview(body, endpoint, tuning, true, dt))
        {
            Assert.NotNull(preview);
            Assert.Equal(132, expected.Length);
            for (int i = 0; i < commands.Length; i++) Same(expected[i], preview.Step(commands[i]));
        }
        int reference = 0, full = 0, certified = 0;
        for (int i = 4; i < commands.Length; i++)
        {
            Vector2 command = commands[i];
            MoveState predicted = expected[i];
            var target = (i % 3) switch
            {
                0 => ReachTarget.Point(predicted.Position),
                1 => ReachTarget.Capsule(new(predicted.Position, 0.03f, 0.1f)),
                _ => ReachTarget.Box(predicted.Position, new(0.03f, 0.1f, 0.07f), 0.7f),
            };
            float range = random.NextSingle() * 0.05f;
            Vector2 original = ArrivalReference.Stop(body, tuning, target, range, true, dt, scene.Context,
                command, predicted, Admit, out int count);
            using var work = new ArrivalPreviewDiagnostics();
            Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, range, true, dt, scene.Context,
                command, predicted, Admit);
            Same(original, actual);
            Same(scene.Context.Step(body, original, true, dt, tuning), scene.Context.Step(body, actual, true, dt, tuning));
            reference += count;
            full += work.FullSteps;
            certified += work.CertifiedTrials;
        }
        Assert.True(reference > 0);
        Assert.Equal(0, full);
        Assert.Equal(reference, certified);
        output.WriteLine($"rebase={rebase}: reference={reference}, full={full}, certified={certified}");
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("water")]
    [InlineData("obstruction")]
    [InlineData("unmatched")]
    [InlineData("steep")]
    public void UnprovedSlopeRegionsKeepTheOriginalFallback(string scenario)
    {
        // #1754. Each refused certificate differs from the clear control by one proof prerequisite.
        using var scene = new ContactTerrainScene(0, identity: scenario != "unmatched",
            document: ContactTerrainScene.Document(0, profile: x => x * 0.125f),
            water: scenario == "water" ? MovementWaterField.Level("world", 0.1f, -2) : null,
            extra: scenario == "obstruction" ? (world, builder) =>
            {
                Vector3 min = new(2.4f, 0, -2), max = new(2.5f, 3, -1);
                var handle = world.AddStatic(new BoxShape((max - min) / 2), Pose.At((min + max) / 2));
                builder.Add(handle, 0, int.MaxValue, new("world", "wall"), min, max);
            }
        : null);
        MoveTuning tuning = ContactTerrainScene.Tuning;
        if (scenario == "steep") tuning = tuning with { MaxSlopeRadians = 0.01f, TractionHysteresisRadians = 0 };
        var body = new MoveState { Position = new(2, 0.25f + tuning.CapsuleHalfHeight, -1.5f), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        var target = ReachTarget.Point(predicted.Position);
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.05f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Admit, out int count);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.05f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Admit);
        Same(expected, actual);
        Same(scene.Context.Step(body, expected, false, 0.1f, tuning), scene.Context.Step(body, actual, false, 0.1f, tuning));
        Assert.True(count > 0);
        Assert.Equal(scenario == "clear" ? 0 : count, work.FullSteps);
        Assert.Equal(scenario == "clear" ? count : 0, work.CertifiedTrials);
    }

    static bool Admit(in MoveState from, in MoveState to, in MoveTuning tuning) => to.Grounded && !to.Swimming;
    static void Same<T>(T expected, T actual) =>
        LocomotionStateBits.AssertEqual(expected, actual, LocomotionStateBits.Policy.IncludeContinuityHandle);
}
