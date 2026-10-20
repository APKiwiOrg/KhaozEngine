using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class CapturedStairExplicitSweepTests
{
    [Theory]
    [InlineData(false, "remove")]
    [InlineData(true, "remove")]
    [InlineData(false, "reuse")]
    [InlineData(true, "reuse")]
    [InlineData(false, "registration")]
    [InlineData(true, "registration")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "disposed")]
    [InlineData(true, "disposed")]
    [InlineData(false, "scope")]
    [InlineData(true, "scope")]
    public void CapturedStairRegistrationAndLeaseFailuresHoldAtomically(bool compound, string fault)
    {
        using var fixture = Fixture(compound);
        fixture.RecordBindings = true;
        var input = fixture.StateAtFeet(new(0, 0.5f, -0.25f));
        var capture = new TickCapture(fixture);
        var control = capture.Step(input, Vector2.UnitY);
        AssertAccepted(fixture, input, control, capture.Reads);
        Assert.NotEmpty(fixture.Bindings);
        StaticHandle selected = fixture.Bindings[^1].Request.Static!.Value;

        if (fault is "remove" or "reuse")
        {
            var oldNative = CertifiedGeneralSweepFixture.Static(fixture.World, selected);
            long generation = fixture.World.StaticGeometryGeneration;
            fixture.World.RemoveStatic(selected);
            if (fault == "reuse")
            {
                using var data = CapturedStairSweepFixture.ReadCapture();
                var part = data.RootElement.GetProperty("parts")[3];
                var points = new Vector3[8];
                for (int i = 0; i < points.Length; i++) points[i] = CapturedStairSweepFixture.Point(part.GetProperty("points")[i]);
                StaticHandle replacement = fixture.World.AddStatic(new ConvexHullShape(points), Pose.Identity);
                var reused = CertifiedGeneralSweepFixture.Static(fixture.World, replacement);
                Assert.Equal(oldNative.Handle.Value, reused.Handle.Value);
                Assert.NotEqual(selected, replacement);
            }
            Assert.True(fixture.World.StaticGeometryGeneration > generation);
            fixture.View.Reset();
            int analytic = fixture.Producer.AnalyticGroundCalls, bindings = fixture.Producer.ContactSupportCalls;
            var stale = fixture.Producer.Acquire();
            stale.Lease?.Dispose();
            Assert.NotEqual(MovementAvailability.Known, stale.Status);
            Assert.Equal(default, fixture.View.Counts);
            Assert.Equal(analytic, fixture.Producer.AnalyticGroundCalls);
            Assert.Equal(bindings, fixture.Producer.ContactSupportCalls);
            // The previously published interval cannot be reused after its disposal or the mutation.
            AssertHeld(input, fixture.StepWithLease(input, Vector2.UnitY, Dt, fixture.LastLease!));
            Assert.Equal(input, fixture.StateAtFeet(new(0, 0.5f, -0.25f)));
            return;
        }

        var producer = fault == "scope" ? fixture.CreateProducer(new(new(-0.1f), new(0.1f),
            0.1f, 0.1f, CapturedStairSweepFixture.Room, CapturedStairSweepFixture.QueryIdentity, fixture.Scope.Frame)) : fixture.Producer;
        var acquired = producer.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var lease = (MovementQueryLease)acquired.Lease!;
        if (fault == "registration") fixture.InvalidateRegistration();
        if (fault == "expired") producer.Fault = "stale-pin";
        if (fault == "disposed") lease.Dispose();
        fixture.View.Reset();
        int analyticBefore = producer.AnalyticGroundCalls, bindingsBefore = producer.ContactSupportCalls;
        AssertHeld(input, fixture.StepWithLease(input, Vector2.UnitY, Dt, lease));
        if (fault != "registration")
        {
            Assert.Equal(default, fixture.View.Counts);
            Assert.Equal(analyticBefore, producer.AnalyticGroundCalls);
            Assert.Equal(bindingsBefore, producer.ContactSupportCalls);
        }
        Assert.Equal(input, fixture.StateAtFeet(new(0, 0.5f, -0.25f)));
    }

    static void AssertHeld(FramedMovementState input, MovementStepResult result)
    {
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(MovementStepResult.HoldState(input.State) with { ClimbRateEwma = 0 }, result.State.State);
        Assert.Equal(input.Frame, result.State.Frame);
        Assert.Null(result.State.Selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedStairWarmAcquisitionRetainsOnlyNamedTokenBytes(bool compound)
    {
        var tokens = ContactLifetimeAllocationTests.Inventory();
        long ceiling = tokens.Movement + tokens.Physics + tokens.Pin;
        using var fixture = Fixture(compound);
        var input = fixture.StateAtFeet(new(0, 0.5f, -0.25f));
        var cold = fixture.StepTowards(input, Vector2.UnitY, Dt);
        var prime = fixture.StepTowards(input, Vector2.UnitY, Dt);
        MovementStepResult measured = default;
        long bytes = AllocationMeasurement.Measure("captured stairs acquired", () =>
            measured = fixture.StepTowards(input, Vector2.UnitY, Dt));
        var capture = new TickCapture(fixture);
        fixture.RecordBindings = true;
        var checkedResult = capture.Step(input, Vector2.UnitY);
        AssertAccepted(fixture, input, checkedResult, capture.Reads);
        Assert.Equal(cold, prime);
        Assert.Equal(prime, measured);
        Assert.Equal(measured, checkedResult);
        Assert.True(fixture.Producer.Disposed);
        Assert.True(capture.Counts.GroundSubsteps > 0);
        Assert.Equal(ceiling, bytes);

        fixture.RecordBindings = false;
        var acquired = fixture.Producer.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var lease = (MovementQueryLease)acquired.Lease!;
        var borrowedCold = fixture.StepWithLease(input, Vector2.UnitY, Dt, lease);
        var borrowedPrime = fixture.StepWithLease(input, Vector2.UnitY, Dt, lease);
        MovementStepResult borrowed = default;
        long borrowedBytes = AllocationMeasurement.Measure("captured stairs borrowed", () =>
            borrowed = fixture.StepWithLease(input, Vector2.UnitY, Dt, lease));
        Assert.Equal(borrowedCold, borrowedPrime);
        Assert.Equal(borrowedPrime, borrowed);
        Assert.Equal(measured, borrowed);
        Assert.Equal(0, borrowedBytes);
        output.WriteLine($"Compound {compound}. Movement/release {tokens.Movement} B, physics {tokens.Physics} B, " +
            $"producer pin {tokens.Pin} B, acquired {bytes} B, borrowed {borrowedBytes} B.");
    }
}
