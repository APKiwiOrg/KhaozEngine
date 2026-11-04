using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public partial class ContactTerrainEvidenceTests
{
    // #1697. Each refusal differs from a passing physical control in exactly the feature or rule it exercises.
    [Theory]
    [InlineData("shelf", ContactTerrainEvidenceValidator.Refusal.FirstSupportOutsideLandingPatch)]
    [InlineData("equal-height-deck", ContactTerrainEvidenceValidator.Refusal.SupportOwnerChanged)]
    [InlineData("no-identity", ContactTerrainEvidenceValidator.Refusal.AnalyticIdentityUnavailable)]
    [InlineData("outside-chain", ContactTerrainEvidenceValidator.Refusal.SteepOutsideChain)]
    public void ValidatorSeparatesRealFirstSupportAndSourceIdentity(string change, ContactTerrainEvidenceValidator.Refusal expected)
    {
        Assert.True(Run(false).Accepted);
        var refused = Run(true);
        Assert.False(refused.Accepted);
        Assert.Equal(expected, refused.Reason);
        if (change == "equal-height-deck")
        {
            Assert.NotNull(refused.FirstSupport);
            Assert.Equal(0, refused.FirstSupport.Value.Height);
            Assert.Equal(new MovementSupportKey("world", "deck"), refused.FirstSupport.Value.Binding.Owner);
        }
        if (change == "shelf")
        {
            Assert.NotNull(refused.FirstSupport);
            Assert.Equal(2, refused.FirstSupport.Value.Height);
            Assert.NotNull(refused.FirstSupport.Value.Feature);
        }

        ContactTerrainEvidenceValidator Run(bool changed)
        {
            var doc = ContactTerrainScene.Document(4);
            bool deckCase = change == "equal-height-deck", shelf = changed && change == "shelf";
            for (int z = 0; z <= 3; z++)
            {
                if (shelf) { doc.SetCornerHeightCm(2, z, 0, 200); doc.SetCornerHeightCm(3, z, 0, 200); }
                if (deckCase) doc.SetCornerHeightCm(2, z, 0, -100);
            }
            using var scene = new ContactTerrainScene(4, identity: !(changed && change == "no-identity"), document: doc,
                extra: (world, builder) =>
                {
                    if (!(changed && deckCase)) return;
                    var handle = world.AddStatic(new BoxShape(new(0.5f, 0.1f, 1)), Pose.At(new(2.4f, -0.1f, -1.5f)));
                    builder.Add(handle, 0, int.MaxValue, new("world", "deck"), new(1.9f, -0.2f, -2.5f), new(2.9f, 0, -0.5f));
                });
            Vector3 target = change is "shelf" or "equal-height-deck" ? new(4.375f, 0, -1.375f) : scene.To;
            Assert.Equal(MovementAvailability.Known, scene.Source.Describe(new(1.05f, -1.375f), out var selectedSteep));
            var validator = new ContactTerrainEvidenceValidator(scene.Source,
                ContactTerrainTopology.Faces(scene.Source, 0, 0, 5, 3), scene.From, target, ContactTerrainScene.Tuning.MaxSlopeRadians,
                excludedSteep: changed && change == "outside-chain" ? selectedSteep.Key : null);
            var evidence = new ContactDescentEvidence(validator.Observe);
            using var binding = scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
            var frame = scene.Context.NavigationFrame;
            var scope = binding.CreateOrdinaryScope(frame);
            var state = new FramedMovementState(new MoveState
            { Position = scene.From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight, Grounded = true }, frame, null);
            bool arrived = false;
            for (int slice = 0; slice < 64; slice++)
            {
                bool hold = slice == 0 || arrived;
                Vector2 input = hold ? Vector2.Zero : ContactDropControl.Input(state.State,
                    target, ContactTerrainScene.Tuning, true, 1, ContactTerrainScene.Dt);
                Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var lease));
                using (lease)
                {
                    lease!.DescentEvidence = evidence;
                    var result = binding.StepTowards(new(state.State, frame, null), input, true, ContactTerrainScene.Dt, lease);
                    validator.AcceptTick(result, evidence.Summary, hold, ContactTerrainScene.Tuning.CapsuleHalfHeight);
                    if (validator.Reason != ContactTerrainEvidenceValidator.Refusal.None || validator.Accepted) return validator;
                    arrived = result.ContactWalkableSupport && GroundTraversalProbe.Near(
                        result.State.State.Position - Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight, target);
                    state = result.State;
                }
            }
            Assert.Fail("The physical control did not complete the unchanged bounded path.");
            return validator;
        }
    }
}
