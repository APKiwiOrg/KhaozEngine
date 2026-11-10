using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterGrazingBankTests
{
    [Theory]
    [InlineData(110.375f)]
    [InlineData(110.5f)]
    public void LegalBankLiftCertifiesTheWholeCapsuleAcrossTheInternalBedDiagonal(float x)
    {
        // #1761. A finite bed-face grazing root must not refuse a continuous walkable bank lift.
        var colliders = GrazingWaterBank.Capture();
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = colliders.Medium.TraceWater(GrazingWaterBank.Lift(x), Vector3.Zero,
            GrazingWaterBank.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.InRange(result.CertifiedErrorMetres, 0, MovementQueryLease.CoverageSkinMetres);
        Assert.True(result.ContactsWritten > 0);
        Assert.Equal(0, spans[0].EnterFraction);
        Assert.Equal(1, spans[result.SpansWritten - 1].ExitFraction);
        Assert.All(contacts.Take(result.ContactsWritten), contact => Assert.Equal(GrazingWaterBank.Space, contact.Space));
    }

    [Theory]
    [InlineData(110.375f, -0.45409524f, -236.89905f)]
    [InlineData(110.5f, -0.45409524f, -236.89905f)]
    [InlineData(110.375f, -0.5200002f, -237.09906f)]
    [InlineData(110.375f, -0.5200002f, -237.07406f)]
    [InlineData(110.375f, -0.5200002f, -237.04906f)]
    [InlineData(110.375f, -0.5200002f, -237.02406f)]
    public void SurfaceSwimmerHoldingTowardTheCapturedBankReachesSupportedDryGround(float x, float y, float z)
    {
        var colliders = GrazingWaterBank.Capture();
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var builder = OrdinaryMovementRegistration.CreateBuilder(GrazingWaterBank.Space.WorldId, physics,
            new(new(109, -4, -240), new(113, 4, -233), -1.3f, 0));
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease()) registration = builder.Build(read);
        using var binding = EffectiveMovementBinding.Ordinary(GrazingWaterBank.Tuning, physics, registration,
            colliders.MovementGroundHeight, colliders.MovementGroundNormal,
            MovementBoundary.Rectangle(109, -240, 113, -233), colliders.Medium);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease)
        {
            var state = new FramedMovementState(new MoveState
            {
                Position = new(x, y, z),
                HorizontalVelocity = new(0, 3),
                Swimming = true,
                WaterExcursion = WaterExcursionState.Surface,
            }, frame, null);
            for (int tick = 0; tick < 30; tick++)
            {
                var result = binding.StepTowards(state, Vector2.UnitY, false, 1f / 30, lease!);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                state = result.State;
                if (state.State.Grounded && !state.State.Swimming && state.State.Position.Z > -236) break;
            }
            Assert.True(state.State.Grounded);
            Assert.False(state.State.Swimming);
            Assert.True(state.State.Position.Z > -236);
            Assert.True(state.State.SupportGranted);
            Assert.NotNull(state.Selection!.Value.Support);
        }
    }
}
