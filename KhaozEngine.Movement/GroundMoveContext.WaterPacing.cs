using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    // The current step may still be grounded when its landed feet would start the next step swimming.
    internal bool CanLandWithoutSwimming(in MoveState landed, in MoveTuning tuning) =>
        HasDryRangePreview(landed, tuning) || !SampleWaterForPacing(landed, tuning).Swimming;

    internal (MovementMedium Medium, bool Swimming, float FeetY) SampleWaterForPacing(in MoveState body, in MoveTuning tuning)
        => SampleWaterForPacing(body, tuning, out _);

    internal (MovementMedium Medium, bool Swimming, float FeetY) SampleWaterForPacing(in MoveState body, in MoveTuning tuning,
        out float landSpeedScale)
    {
        landSpeedScale = 1f;
        ValidateTuning(tuning);
        _origin = Physics?.Origin ?? Vector3.Zero;
        if (!float.IsFinite(_origin.X) || !float.IsFinite(_origin.Y) || !float.IsFinite(_origin.Z))
            throw new InvalidOperationException("The physics origin must be finite.");
        _stepping = true;
        try
        {
            EnsureOrigin();
            Vector3 position = body.Position - _origin;
            if (tuning.Controller == MovementControllerKind.Legacy)
            {
                // StepTowards computes feet height after rebasing the centre. Taking the difference in
                // world coordinates can round a landing to the other side of the swim-entry threshold.
                float feetY = position.Y - tuning.CapsuleHalfHeight;
                var medium = _localMedium?.Invoke(position.X, position.Z, feetY) ?? MovementMedium.Dry;
                return (medium, CharacterMovement.ResolveSwimming(body.Swimming, medium, feetY, tuning), feetY);
            }
            CaptureContactFrame();
            var binding = _binding!;
            var scope = binding.CreateOrdinaryScope(_contactFrame);
            if (binding.TryAcquire(scope, out var queries) != MovementAvailability.Known)
                throw new InvalidOperationException("The contact pacing water lease is unresolved.");
            using (queries)
            {
                var local = body with { Position = position };
                var point = binding.SampleWaterForPacing(new(local, _contactFrame, null), queries!, out bool swimming, out landSpeedScale);
                EnsureOrigin();
                if (point.Availability != MovementAvailability.Known)
                    throw new InvalidOperationException("The contact pacing water point is unresolved.");
                var medium = point.InWater ? new MovementMedium(point.Interval!.Value.UpperY, true, point.SpeedScale) : MovementMedium.Dry;
                return (medium, swimming, local.Position.Y - tuning.CapsuleHalfHeight);
            }
        }
        finally { _stepping = false; }
    }

    void CaptureContactFrame()
    {
        _contactFrame = ContactFrameAt(_origin);
        _frameCaptured = true;
    }

    // A new origin starts the next epoch. The current frame is reused while the origin holds.
    MovementFrameDescriptor ContactFrameAt(Vector3 origin) =>
        _frameCaptured && _contactFrame.PhysicsOrigin == origin ? _contactFrame
            : new(default, origin, _frameCaptured ? checked(_contactFrame.Epoch + 1) : 1);
}
