using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropCorrectnessTests
{
    [Fact]
    public void FullTuningFallHoldsRemainRealWhileGroundHoldsAreReused()
    {
        using var fixture = new ContactDropFixture();
        var steps = new List<ContactDropStepObservation>();
        using var hooks = ContactNavigationBuildHooks.Install(new() { DropStep = steps.Add });
        using var measure = SupportQueryCapture.Begin("full-tuning-drop-holds");
        var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 }, new NavBakeExecution(1) { UseStableContactHoldReuse = true });
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        Assert.Equal(2, steps.Count(step => step.Hold && !step.Run));
        Assert.Equal(2, steps.Count(step => step.Hold && step.Run));
        Assert.Equal(3, steps.First(step => !step.Hold && !step.Run).CommandedVelocity.X);
        Assert.Equal(5, steps.First(step => !step.Hold && step.Run).CommandedVelocity.X);
        Assert.Equal(steps.Count, measure.Snapshot().Total[SupportQueryCounter.DropSteps]);
        Assert.True(measure.Snapshot().Total[SupportQueryCounter.InitialHoldReuseHits] > 0);
    }

    [Fact]
    public void RepresentableBrinkPhasesAllProveRealFalls()
    {
        foreach (float start in new[] { MathF.BitDecrement(-1.5f), -1.5f, MathF.BitIncrement(-1.5f) })
        {
            using var fixture = new ContactDropFixture(options: ContactDropFixture.Options with { MinX = start });
            var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default,
                new GroundProfileOptions { MaxDropMetres = 6 });
            Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        }
    }

    [Fact]
    public void DataOnlyMeasurementsDoNotRunMovementProofs()
    {
        using var fixture = new ContactDropFixture();
        var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        using var capture = SupportQueryCapture.Begin("data-only-drop-candidates");
        var measurements = ContactDropCandidates.Measure(nav.Space.Layers, nav.Graph.Layers,
            ContactDropFixture.Tuning, 6).ToArray();
        Assert.NotEmpty(measurements);
        Assert.All(measurements, row => { Assert.True(row.Walk); Assert.True(row.Run); Assert.Equal(4, row.ExpectedFallMetres); });
        Assert.Equal(0, capture.Snapshot().Total[SupportQueryCounter.Find]);
        Assert.Equal(0, capture.Snapshot().Total[SupportQueryCounter.DropSteps]);
    }

    [Fact]
    public void FallBindingNeverNormalizesActualFlightTuning()
    {
        using var fixture = new ContactDropFixture();
        using var source = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning with { AirMomentum = true });
        using var fall = source.CreateFallProofBinding();
        using var ground = source.CreateProofBinding();
        Assert.Equal(source.Tuning, fall.Tuning);
        Assert.Equal(1, ground.Tuning.WalkSpeed);
        Assert.False(ground.Tuning.AirMomentum);
        Assert.NotSame(source, fall);
    }

    [Fact]
    public void PreviewBudgetUsesTheStoredDurationWithoutRoundingUp()
    {
        var options = ContactDropFixture.Options;
        Assert.Equal(64, ContactDropControl.PreviewSteps(options, options.EdgeProbeSeconds));
        Assert.Equal(128, ContactDropControl.PreviewSteps(options, options.EdgeProbeSeconds / 2));
        Assert.Equal(0, ContactDropControl.PreviewSteps(options,
            MathF.BitIncrement(options.EdgeProbeSeconds * options.MaxEdgeProbeSteps)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactDropControl.PreviewSteps(options, MathF.BitIncrement(0)));
    }

    [Fact]
    public void PredictionPreservesRefusalOutcomesAndChecksTheActualFrame()
    {
        using var fixture = new ContactDropFixture();
        var frame = fixture.Context.NavigationFrame;
        var body = new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true };
        var state = new FramedMovementState(body, frame, null);
        var basis = new ContactDropPredictionBasis();
        var result = fixture.Context.PredictContactStep(state, Vector2.Zero, false,
            ContactDropFixture.Options.EdgeProbeSeconds, ContactDropFixture.Tuning, basis);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        var changed = new FramedMovementState(body, new(frame.Frame, frame.PhysicsOrigin, frame.Epoch + 1), null);
        Assert.Equal(MovementStepOutcome.FrameMismatch, fixture.Context.PredictContactStep(changed, Vector2.Zero, false,
            ContactDropFixture.Options.EdgeProbeSeconds, ContactDropFixture.Tuning, basis).Outcome);
    }

    [Fact]
    public void PreviewCannotMixPhysicsGenerationsBetweenSlices()
    {
        using var fixture = new ContactDropFixture();
        var state = fixture.Scene.State(new(-0.25f, 4.75f, 0));
        var basis = new ContactDropPredictionBasis();
        var first = fixture.Context.PredictContactStep(state, Vector2.Zero, false,
            ContactDropFixture.Options.EdgeProbeSeconds, ContactDropFixture.Tuning, basis);
        Assert.Equal(MovementStepOutcome.Advanced, first.Outcome);
        fixture.Scene.Physics.AddStatic(new BoxShape(new(0.1f, 0.1f, 0.1f)), Pose.At(new(10, 0, 10)));
        var refused = fixture.Context.PredictContactStep(first.State, Vector2.Zero, false,
            ContactDropFixture.Options.EdgeProbeSeconds, ContactDropFixture.Tuning, basis);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, refused.Outcome);
        Assert.Equal(first.State, refused.State);
    }

    [Fact]
    public void ControllerReportsUnwalkableContactEvenWhenTheTickEndsAirborne()
    {
        using var scene = new OrdinaryContactScene(ContactDropFixture.Tuning,
            (x, _) => -2 * x, (_, _) => Vector3.Normalize(new(2, 1, 0)),
            extent: new(new(-20, -20, -20), new(20, 20, 20), -10, 10));
        using var context = ContactNavigationFixture.Context(scene);
        var state = scene.State(new(0, 1.75f, 0), grounded: false);
        var basis = new ContactDropPredictionBasis();
        bool steep = false;
        for (int tick = 0; tick < 30; tick++)
        {
            MovementStepResult result = context.PredictContactStep(state, Vector2.Zero, false,
                ContactDropFixture.Options.EdgeProbeSeconds, ContactDropFixture.Tuning, basis);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            steep |= result.ContactUnwalkableSupport;
            state = result.State;
        }
        Assert.True(steep);
    }
}
