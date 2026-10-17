using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class ContactLifetimeAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvisionedFindAllocatesNothingAfterPhysicsAdvances(bool changeDynamic)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        var dynamic = world.AddDynamic(new BoxShape(Vector3.One), Pose.At(new(20, 0, 0)), new(1));
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        SupportSample cold;
        long generation;
        using (var lease = world.AcquireQueryReadLease())
        {
            generation = lease.GeometryGeneration;
            cold = FootSupport.Find(null, null, world, lease, query);
            FootSupport.Find(null, null, world, lease, query);
        }
        world.Step(1f / 30);
        if (changeDynamic) world.SetDynamicVelocity(dynamic, Vector3.UnitX, Vector3.Zero);
        using var next = world.AcquireQueryReadLease();
        Assert.True(next.GeometryGeneration > generation);
        SupportSample warm = default;
        long bytes = AllocationMeasurement.Measure(nameof(ProvisionedFindAllocatesNothingAfterPhysicsAdvances), () =>
            warm = FootSupport.Find(null, null, world, next, query));
        output.WriteLine($"Post-step Find with dynamic change {changeDynamic}. {bytes} B.");
        Assert.Equal(SupportStatus.Walkable, warm.Status);
        Assert.Equal(cold.Height, warm.Height);
        Assert.Equal(cold.HeightError, warm.HeightError);
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData("DryTick", 64)]
    [InlineData("ExplicitTick", 312)]
    public void AcquiredTickAfterPhysicsAdvancesAllocatesOnlyItsLifetimeTokens(string operation, long expectedBytes)
    {
        var row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == "flat box")
            with
        { Explicit = operation == "ExplicitTick" };
        using var execution = new SupportCostExecution(row, operation);
        var cold = execution.Invoke();
        execution.Invoke();
        execution.World.Step(1f / 30);
        SupportCostOutput warm = default;
        long bytes = AllocationMeasurement.Measure($"{nameof(AcquiredTickAfterPhysicsAdvancesAllocatesOnlyItsLifetimeTokens)} {operation}",
            () => warm = execution.Invoke());
        output.WriteLine($"Post-step acquired {operation}. {bytes} B.");
        Assert.False(warm.Refused);
        Assert.True(warm.GeometryPassed);
        Assert.Equal(cold, warm);
        Assert.Equal(expectedBytes, bytes);
    }
}
