using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.Movement.Fixtures;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileFlatRangePreviewTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlatCertificateCoversAuthoredSeamsAndDeclinesNonuniformBeds(bool nonuniform)
    {
        // #1729, #1737. Exercise the installed producer with a clamp Contact must never read.
        using var scene = new ContactTerrainScene(0, document: ContactTerrainScene.Document(0,
            profile: x => nonuniform && x > 1 ? 0.5f : 0.25f), clampXz: ThrowingClamp);
        var source = Assert.IsAssignableFrom<IMovementFlatAnalyticSupportSource>(scene.Source);
        var status = source.CertifyFlatRegion(new(0.5f, -1.75f), new(2.5f, -0.75f), out float height);
        if (nonuniform)
        {
            Assert.Equal(MovementAvailability.Unresolved, status);
            return;
        }
        Assert.Equal(MovementAvailability.Known, status);
        var random = new Random(172915);
        for (int sample = 0; sample < 128; sample++)
        {
            float x = 0.5f + random.NextSingle() * 2, z = -1.75f + random.NextSingle();
            LocomotionStateBits.AssertEqual(height, scene.Colliders.MovementGroundHeight(x, z),
                LocomotionStateBits.Policy.IncludeContinuityHandle);
            Assert.Equal(Vector3.UnitY, scene.Colliders.MovementGroundNormal(x, z));
        }
        MoveTuning tuning = ContactTerrainScene.Tuning;
        var body = new MoveState { Position = new(1, height + tuning.CapsuleHalfHeight, -1), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        using var preview = scene.Context.OpenGroundRangePreview(body, predicted, tuning, false, 0.1f);
        Assert.NotNull(preview);
        LocomotionStateBits.AssertEqual(predicted, preview.Step(Vector2.UnitX), LocomotionStateBits.Policy.IncludeContinuityHandle);
    }

    [Theory]
    [InlineData("interior")]
    [InlineData("rebase")]
    [InlineData("rim")]
    [InlineData("slope")]
    [InlineData("slope-rebase")]
    [InlineData("slope-seam")]
    [InlineData("slope-rim")]
    public void ContactArrivalWithClampMatchesExactSolverAndKeepsBoundaryFallback(string scenario)
    {
        // #1737, #1754. Slopes retain exact arrival decisions and the boundary still declines the rim.
        bool slope = scenario.StartsWith("slope", StringComparison.Ordinal);
        bool rim = scenario.EndsWith("rim", StringComparison.Ordinal);
        using var scene = new ContactTerrainScene(0, document: ContactTerrainScene.Document(0,
            profile: x => slope ? x * 0.125f : 0.25f), clampXz: ThrowingClamp);
        if (scenario.EndsWith("rebase", StringComparison.Ordinal)) scene.World.Rebase(new(2, 0.125f, -3));
        MoveTuning tuning = ContactTerrainScene.Tuning;
        float x = rim ? 4.99f : scenario == "slope-seam" ? 1.99f : 1;
        var body = new MoveState
        {
            Position = new(x, scene.Colliders.MovementGroundHeight(x, -1) + tuning.CapsuleHalfHeight, -1),
            Grounded = true,
        };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        Assert.True(predicted.SupportGranted);
        Assert.True(predicted.Position.X > body.Position.X);
        if (rim) Assert.Equal(5f, predicted.Position.X);
        var target = ReachTarget.Point(predicted.Position);
        static bool Admit(in MoveState from, in MoveState to, in MoveTuning tuning) => to.Grounded && !to.Swimming;
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Admit, out int count);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Admit);
        LocomotionStateBits.AssertEqual(expected, actual, LocomotionStateBits.Policy.IncludeContinuityHandle);
        LocomotionStateBits.AssertEqual(scene.Context.Step(body, expected, false, 0.1f, tuning),
            scene.Context.Step(body, actual, false, 0.1f, tuning), LocomotionStateBits.Policy.IncludeContinuityHandle);
        Assert.True(count > 0);
        output.WriteLine($"{scenario}: reference={count}, full={work.FullSteps}, certified={work.CertifiedTrials}");
        Assert.Equal(count, work.FullSteps + work.CertifiedTrials);
        Assert.Equal(rim ? count : 0, work.FullSteps);
        Assert.Equal(rim ? 0 : count, work.CertifiedTrials);
        using var preview = scene.Context.OpenGroundRangePreview(body, predicted, tuning, false, 0.1f);
        if (rim) Assert.Null(preview);
        else Assert.NotNull(preview);
    }

    static Vector2 ThrowingClamp(float x, float z) => throw new InvalidOperationException("Contact read the legacy clamp.");
}
