using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed class SupportCostRoofOracleTests(ITestOutputHelper output)
{
    static readonly MoveTuning Tuning = SupportGateCostScenes.Tuning;

    // A paced climb along +X from the floor toward a 0.25 tread at x 0.3, beside a ceiling over z 0.1 and beyond, its
    // underside at 1.85. The roof point that stops the climb is the ceiling's edge, off the axis, where the capsule's
    // rounded top lies below its apex.
    [Fact]
    public void AnOffAxisRoofLimitIsWhereThePacedBodyStops()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
        scene.Install(new BoxShape(new(1.35f, 0.125f, 2)), Pose.At(new(1.65f, 0.125f, 0)));
        scene.Install(new BoxShape(new(2, 0.1f, 1)), Pose.At(new(0, 1.95f, 1.1f)));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, lease, Tuning);
        const float dt = 1f / 30f;
        double budget = (double)Tuning.MaxStepClimbSpeed * dt;
        GroundStepResult result = GroundCore.Step(Vector3.Zero, new Vector2(Tuning.WalkSpeed * dt, 0), dt, Tuning,
            new GroundCoreSettings(), queries);
        var axis = new Vector2(result.Feet.X, result.Feet.Z);
        double? roof = SupportCostRoofOracle.Limit(scene, axis, 0.2, 0.25, Tuning);
        double rho = 0.1 - axis.Y, flatTop = 1.85 - 2 * (double)Tuning.CapsuleHalfHeight - ShellMotion.ContactSkin;
        output.WriteLine($"end {result.Feet}, footing {result.Footing}, support {result.Support.Height:R}, " +
            $"limit {roof:R}, flat top {flatTop:R}");
        Assert.NotNull(roof);
        double limit = roof.Value;
        Assert.Equal(GroundFooting.Walkable, result.Footing);
        Assert.InRange(Math.Abs(result.Support.Height - 0.25), 0, result.Support.HeightError + 1e-6);
        Assert.True(result.Feet.X > 0.1f, $"The climb moved {result.Feet}");
        Assert.InRange(rho, 0.001, 0.2);
        // The roof binds below the budgeted height and the tread, and the rounded top lifts it above a flat top.
        Assert.True(limit < budget && limit > flatTop + 0.001, $"Limit {limit:R}");
        Assert.InRange(Math.Abs(result.Feet.Y - limit), 0, 0.001);
        Assert.True(SupportCostExecution.PacedEndReaches(result.Feet.Y, 0.25, 0, budget, limit));
        // A lag beyond the roof limit fails, though a flat top model would have excused it.
        Assert.True(limit - 0.005 > flatTop - 0.001);
        Assert.False(SupportCostExecution.PacedEndReaches(limit - 0.005, 0.25, 0, budget, limit));
    }
}
