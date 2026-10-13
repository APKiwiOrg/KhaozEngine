using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    internal (MovementMedium Medium, bool Swimming, float FeetY) SampleWaterForPacing(in MoveState body, in MoveTuning tuning)
        => SampleWaterForPacing(body, tuning, out _);

    internal (MovementMedium Medium, bool Swimming, float FeetY) SampleWaterForPacing(in MoveState body, in MoveTuning tuning,
        out float landSpeedScale)
    {
        landSpeedScale = 1f;
        if (tuning.Controller == MovementControllerKind.Legacy)
        {
            float feetY = body.Position.Y - tuning.CapsuleHalfHeight;
            var medium = Medium?.Invoke(body.Position.X, body.Position.Z, feetY) ?? MovementMedium.Dry;
            return (medium, CharacterMovement.ResolveSwimming(body.Swimming, medium, feetY, tuning), feetY);
        }
        ValidateTuning(tuning);
        _origin = Physics!.Origin;
        if (!float.IsFinite(_origin.X) || !float.IsFinite(_origin.Y) || !float.IsFinite(_origin.Z))
            throw new InvalidOperationException("The physics origin must be finite.");
        _stepping = true;
        try
        {
            EnsureOrigin();
            CaptureContactFrame();
            var binding = _binding!;
            var scope = binding.CreateOrdinaryScope(_contactFrame);
            if (binding.TryAcquire(scope, out var queries) != MovementAvailability.Known)
                throw new InvalidOperationException("The contact pacing water lease is unresolved.");
            using (queries)
            {
                var local = body with { Position = body.Position - _origin };
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
        if (_frameCaptured && _contactFrame.PhysicsOrigin == _origin) return;
        _contactFrame = new(default, _origin, _frameCaptured ? checked(_contactFrame.Epoch + 1) : 1);
        _frameCaptured = true;
    }
}
