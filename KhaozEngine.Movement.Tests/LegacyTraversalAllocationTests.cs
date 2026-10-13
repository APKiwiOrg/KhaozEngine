using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public sealed class LegacyTraversalAllocationTests
{
    [Fact]
    public void LegacyValidationAlternatesTraversalTuningsWithoutAllocating()
    {
        using var context = new GroundMoveContext((_, _) => 0);
        var source = MoveTuning.Default;
        var probe = source with { WalkSpeed = 1, RunSpeed = 1, AirMomentum = false };
        context.ValidateTuning(source);
        context.ValidateTuning(probe);
        using var region = SupportAllocationRegion.Start();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        context.ValidateTuning(source);
        context.ValidateTuning(probe);
        long bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes);
        var basis = new MoveState { Position = new(0, source.CapsuleHalfHeight, 0), Grounded = true };
        var sourceStep = context.Step(basis, Vector2.UnitX, false, 0.1f, source);
        var probeStep = context.Step(basis, Vector2.UnitX, false, 0.1f, probe);
        Assert.Equal(source.WalkSpeed * 0.1f, sourceStep.Position.X);
        Assert.Equal(0.1f, probeStep.Position.X);
    }
}
