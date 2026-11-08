using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests(ITestOutputHelper output)
{
    // #1730. Removing first-step reuse repeats the stationary water certificate.
    [Fact]
    public void IdenticalFirstStepUsesOnePlacementValidationAndReplayRevalidates()
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator();
        var basis = scene.Initial();
        using var work = new PlacementWork();
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            var first = simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
            Assert.Equal(MovementStepOutcome.Advanced, simulator.LastExplicitOutcome);
            Assert.Equal(1, work.Validations);
            Assert.Equal(1, work.Reuses);
            Assert.Equal(first, simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt));
            Assert.Equal(2, work.Validations);
            Assert.Equal(1, work.Reuses);
        }
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            Assert.Equal(3, work.Validations);
            simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
            Assert.Equal(3, work.Validations);
            Assert.Equal(2, work.Reuses);
        }
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void BoundedColdReadAndStepProfile()
    {
        using var field = new OrdinaryShoreField();
        using var scene = new OrdinaryPlayerWorld(field: field);
        using var simulator = scene.Simulator();
        using var work = new PlacementWork();
        // Fixed engine fixture, dry approach, exact shore tangent and wet basis. No timing assertion.
        for (int i = 0; i < 2048; i++)
        {
            var basis = scene.Initial((i % 3) switch { 0 => -2f, 1 => 0.75f, _ => 1.25f });
            using var read = simulator.BeginExplicitRead(basis);
            Assert.True(read!.BasisValid);
            _ = simulator.Step(basis, new MoveCommand(Vector2.UnitX, false, 0), 1f / 30);
        }
        output.WriteLine($"2048 read/step pairs, validations {work.Validations}, reuses {work.Reuses}");
    }

    sealed class PlacementWork : IDisposable
    {
        readonly bool enabled = MovementPlacementDiagnostics.Enabled;
        readonly long validations = MovementPlacementDiagnostics.Validations;
        readonly long reuses = MovementPlacementDiagnostics.Reuses;
        internal long Validations => MovementPlacementDiagnostics.Validations;
        internal long Reuses => MovementPlacementDiagnostics.Reuses;
        internal PlacementWork()
        {
            MovementPlacementDiagnostics.Enabled = true;
            MovementPlacementDiagnostics.Validations = MovementPlacementDiagnostics.Reuses = 0;
        }
        public void Dispose()
        {
            MovementPlacementDiagnostics.Enabled = enabled;
            MovementPlacementDiagnostics.Validations = validations;
            MovementPlacementDiagnostics.Reuses = reuses;
        }
    }
}
