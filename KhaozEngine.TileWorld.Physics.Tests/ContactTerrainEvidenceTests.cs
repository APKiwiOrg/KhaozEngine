using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using KhaozEngine.Tests.Locomotion.Contacts;

namespace KhaozEngine.Tests.TileWorld.Physics;

public partial class ContactTerrainEvidenceTests
{
    // #1697. The selected support witness must report slide landings without changing motion or coarse flags.
    [Theory]
    [InlineData(1.3f, false)]
    [InlineData(1.3f, true)]
    [InlineData(4f, false)]
    [InlineData(4f, true)]
    [InlineData(6f, false)]
    [InlineData(6f, true)]
    public void AuthenticatedSlideAndFirstSupportPreserveOrdinaryMovement(float height, bool run)
    {
        using var scene = new ContactTerrainScene(height);
        using var binding = scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
        var frame = scene.Context.NavigationFrame;
        var scope = binding.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState
        { Position = scene.From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight, Grounded = true }, frame, null);
        var validator = new ContactTerrainEvidenceValidator(scene.Source, ContactTerrainTopology.Faces(scene.Source, 0, 0, 5, 3),
            scene.From, scene.To, ContactTerrainScene.Tuning.MaxSlopeRadians);
        ContactSupportEvidence? lastSupport = null;
        var evidence = new ContactDescentEvidence(Observe);
        bool Observe(in ContactSelectedSupport selected)
        {
            lastSupport = selected.Facts;
            return validator.Observe(selected);
        }
        bool steep = false, landed = false, arrived = false;
        for (int slice = 0; slice < 64; slice++)
        {
            Vector2 input = slice == 0 || arrived ? Vector2.Zero : ContactDropControl.Input(state.State,
                scene.To, ContactTerrainScene.Tuning, run, 1, ContactTerrainScene.Dt);
            lastSupport = null;
            MovementStepResult ordinary = Step(null), observed = Step(evidence);
            LocomotionStateBits.AssertEqual(ordinary, observed, LocomotionStateBits.Policy.IncludeContinuityHandle);
            validator.AcceptTick(observed, evidence.Summary, slice == 0 || arrived, ContactTerrainScene.Tuning.CapsuleHalfHeight);
            Assert.Equal(ContactTerrainEvidenceValidator.Refusal.None, validator.Reason);
            Assert.Equal(MovementStepOutcome.Advanced, observed.Outcome);
            if (observed.ContactWalkableSupport)
            {
                Assert.NotNull(lastSupport);
                Assert.Equal(observed.State.State.Position.X, lastSupport.Value.Feet.X);
                Assert.Equal(observed.State.State.Position.Z, lastSupport.Value.Feet.Z);
            }
            Assert.Equal(ContactDescentEvidenceStatus.Complete, evidence.Summary.Status);
            Assert.False(evidence.Summary.Water || evidence.Summary.Swimming || evidence.Summary.WaterExcursion);
            steep |= evidence.Summary.Steep;
            if (evidence.Summary.FirstWalkableSupport is { } first)
            {
                Assert.True(steep);
                Assert.Equal(MovementAvailability.Known, scene.Source.Describe(new(first.Feet.X, first.Feet.Z), out var feature));
                Assert.Equal(feature, first.Feature);
                Assert.True(first.Feet.X < scene.To.X);
                Assert.Equal(0, first.Height);
                landed = true;
            }
            Vector3 feet = observed.State.State.Position - Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight;
            if (landed && observed.ContactWalkableSupport && GroundTraversalProbe.Near(feet, scene.To))
            {
                if (arrived) { Assert.True(validator.Accepted); return; }
                arrived = true;
            }
            state = observed.State;
            MovementStepResult Step(ContactDescentEvidence? observer)
            {
                Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var lease));
                using (lease)
                {
                    lease!.DescentEvidence = observer;
                    return binding.StepTowards(new(state.State, frame, null), input, run, ContactTerrainScene.Dt, lease);
                }
            }
        }
        Assert.Fail("The witnessed slide did not reach and Hold the named lower anchor within the existing budget.");
    }
}
