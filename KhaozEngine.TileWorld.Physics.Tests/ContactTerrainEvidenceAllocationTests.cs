using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
public class ContactTerrainEvidenceAllocationTests
{
    [Fact]
    public void BorrowedSelectedTickAllocatesNothingWithEvidenceOffOrOn()
    {
        using var scene = new ContactTerrainScene(4);
        using var binding = scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
        var frame = scene.Context.NavigationFrame;
        var state = new FramedMovementState(new MoveState
        { Position = scene.From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight, Grounded = true }, frame, null);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease)
        {
            MovementStepResult result = default;
            Action tick = () => result = binding.StepTowards(state, Vector2.Zero, false, ContactTerrainScene.Dt, lease!);
            tick();
            long ordinaryBytes = AllocationMeasurement.Measure("ordinary selected tick", tick);
            var ordinary = result;
            var evidence = new ContactDescentEvidence();
            lease!.DescentEvidence = evidence;
            tick();
            long observedBytes = AllocationMeasurement.Measure("witnessed selected tick", tick);
            Assert.Equal(0, ordinaryBytes);
            Assert.Equal(0, observedBytes);
            Assert.Equal(ContactDescentEvidenceStatus.Complete, evidence.Summary.Status);
            LocomotionStateBits.AssertEqual(ordinary, result, LocomotionStateBits.Policy.IncludeContinuityHandle);
        }
    }
}
