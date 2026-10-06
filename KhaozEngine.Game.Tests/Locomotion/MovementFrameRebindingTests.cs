using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementFrameRebindingTests
{
    [Fact]
    public void RebindingUsesRecordedPhysicsOriginsIncludingYExactlyOnce()
    {
        var source = new MovementFrameDescriptor(new WorldFrame(1, -1), new Vector3(128f, 32f, -128f), 1ul);
        var target = new MovementFrameDescriptor(new WorldFrame(2, -2), new Vector3(256f, -16f, -256f), 2ul);
        FramedMovementState original = State(new Vector3(4f, 3f, -5f), source);
        Assert.True(Rebind(original, target, out FramedMovementState rebound));
        Assert.Equal(new Vector3(-124f, 51f, 123f), rebound.State.Position);
        Assert.Equal(original.State.VerticalVelocity, rebound.State.VerticalVelocity);
        Assert.Equal(original.State.JumpBufferRemaining, rebound.State.JumpBufferRemaining);
        Assert.Equal(original.State.Grounded, rebound.State.Grounded);
        Assert.Equal(target, rebound.Frame);
        Assert.Null(rebound.Selection);
        Assert.NotNull(original.Selection);
    }

    [Fact]
    public void AnUnchangedFramePreservesTheWholeRecordedState()
    {
        FramedMovementState original = State(new Vector3(1f, 2f, 3f));
        Assert.True(Rebind(original, original.Frame, out FramedMovementState rebound));
        Assert.Equal(original, rebound);
    }

    [Fact]
    public void LargeOriginsAreSubtractedBeforeAddingLocalFractionalPosition()
    {
        Vector3 oldOrigin = new(1_000_000f, 32f, -1_000_000f);
        Vector3 newOrigin = oldOrigin + new Vector3(128f, -16f, -128f);
        var source = new MovementFrameDescriptor(WorldFrame.Nearest(oldOrigin), oldOrigin, 1ul);
        var target = new MovementFrameDescriptor(WorldFrame.Nearest(newOrigin), newOrigin, 2ul);
        FramedMovementState original = State(new Vector3(0.125f, 0.25f, -0.375f), source);
        Assert.True(Rebind(original, target, out FramedMovementState rebound));
        Assert.Equal(new Vector3(-127.875f, 16.25f, 127.625f), rebound.State.Position);
    }

    [Theory]
    [InlineData(512f, 0f, 0f, true)]
    [InlineData(0f, 0f, 512f, true)]
    [InlineData(384f, 0f, 384f, false)]
    [InlineData(0f, 640f, 0f, true)]
    [InlineData(0f, -640f, 0f, true)]
    [InlineData(0f, 640.125f, 0f, false)]
    public void LocalEnvelopeUsesPlanarMagnitudeAndTheDeclaredVerticalBound(float x, float y, float z, bool accepted)
    {
        FramedMovementState original = State(new Vector3(x, y, z));
        Assert.Equal(accepted, Rebind(original, original.Frame, out FramedMovementState rebound));
        Assert.Equal(original, rebound);
    }

    [Fact]
    public void UnrepresentableTargetReturnsTheOriginalFrameAndSelection()
    {
        FramedMovementState original = State(Vector3.Zero);
        var target = new MovementFrameDescriptor(new WorldFrame(8, 0), new Vector3(1024f, 0f, 0f), 2ul);
        Assert.False(Rebind(original, target, out FramedMovementState rebound));
        Assert.Equal(original, rebound);
        Assert.NotEqual(target, rebound.Frame);
    }

    [Fact]
    public void AnUncertifiedSourceIsNotRepairedByRelabellingItsCoordinates()
    {
        FramedMovementState original = State(new Vector3(600f, 0f, 0f));
        var target = new MovementFrameDescriptor(new WorldFrame(4, 0), new Vector3(512f, 0f, 0f), 2ul);
        Assert.False(Rebind(original, target, out FramedMovementState rebound));
        Assert.Equal(original, rebound);
    }

    [Fact]
    public void AChangedEpochInvalidatesSelectionEvenWhenTheOriginDoesNotMove()
    {
        FramedMovementState original = State(new Vector3(1f, 2f, 3f));
        var target = new MovementFrameDescriptor(original.Frame.Frame, original.Frame.PhysicsOrigin, 2ul);
        Assert.True(Rebind(original, target, out FramedMovementState rebound));
        Assert.Equal(original.State.Position, rebound.State.Position);
        Assert.Equal(target, rebound.Frame);
        Assert.Null(rebound.Selection);
    }

    static FramedMovementState State(Vector3 position, MovementFrameDescriptor? frame = null)
    {
        var state = new MoveState { Position = position, VerticalVelocity = 3.5f, Grounded = true, JumpBufferRemaining = 0.1f };
        var selection = new MovementSelection(new MovementSpaceKey("world", "room"),
            new MovementSupportKey("world", "floor"), new MovementQueryIdentity("closure", 1u, "scope"));
        return new FramedMovementState(state, frame ?? new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1ul), selection);
    }

    static bool Rebind(in FramedMovementState state, in MovementFrameDescriptor target, out FramedMovementState rebound) =>
        MovementFrameRebinding.TryRebind(state, target, out rebound);
}
