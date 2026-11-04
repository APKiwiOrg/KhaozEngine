using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.TileWorld;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using KhaozEngine.Tests.Locomotion.Contacts;

namespace KhaozEngine.Tests.TileWorld.Physics;

public partial class ContactTerrainEvidenceTests
{
    // #1697. Slide's internal ground substeps can touch the lower patch and leave it before returning to Air.
    [Fact]
    public void SlideRetainsFirstWalkableSupportEvenWhenTheSameTickLeavesTheShelf()
    {
        var doc = ContactTerrainScene.Document(4);
        for (int z = 0; z <= 3; z++)
        {
            if (z < 3) { doc.SetSettings(3, z, 0, TileSettings.NoDraw); doc.SetSettings(4, z, 0, TileSettings.NoDraw); }
            doc.SetCornerHeightCm(4, z, 0, -10000);
            doc.SetCornerHeightCm(5, z, 0, -10000);
        }
        using var scene = new ContactTerrainScene(4, document: doc);
        using var binding = scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
        var frame = scene.Context.NavigationFrame;
        var state = new FramedMovementState(new MoveState
        {
            Position = new(1.9f, 1.15f, -1.375f), Grounded = false,
            HorizontalVelocity = new(15, 0), VerticalVelocity = -40,
        }, frame, null);
        var evidence = new ContactDescentEvidence();
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease)
        {
            var ordinary = binding.StepTowards(state, Vector2.Zero, false, 0.2f, lease!);
            lease!.DescentEvidence = evidence;
            var observed = binding.StepTowards(state, Vector2.Zero, false, 0.2f, lease);
            LocomotionStateBits.AssertEqual(ordinary, observed, LocomotionStateBits.Policy.IncludeContinuityHandle);
            Assert.Equal(MovementStepOutcome.Advanced, observed.Outcome);
            Assert.False(observed.State.State.Grounded);
            Assert.False(observed.ContactAirLanded);
            var first = Assert.IsType<ContactSupportEvidence>(evidence.Summary.FirstWalkableSupport);
            Assert.Equal(0, first.Height);
            Assert.NotNull(first.Feature);
            Assert.True(first.Feet.X >= 2 && first.Feet.X < 3);
        }
    }
}
