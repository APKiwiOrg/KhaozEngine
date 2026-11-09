using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    FlatGroundRangePreview? _rangePreview;

    internal FlatGroundRangePreview? OpenFlatRangePreview(in MoveState body, in MoveState predicted,
        in MoveTuning tuning, bool run, float dt)
    {
        if (tuning.Controller != MovementControllerKind.Contact ||
            MovementWaterField is not null and not IMovementDryTracePointProof ||
            MovementRegistration?.AnalyticSupport is not IMovementFlatAnalyticSupportSource ||
            !body.Grounded || body.Swimming || body.WaterExcursion != WaterExcursionState.None ||
            body.Commitment != default || body.JumpBufferRemaining != 0 || body.ClimbRate != 0 ||
            body.ClimbRateEwma != 0 || body.StepDeltaY != 0 || !predicted.Grounded || predicted.Swimming ||
            !predicted.SupportGranted ||
            !(body.SpeedScale > 0) || !ExplicitCharacterMovement.ValidCarriedState(body)) return null;
        if (_rangePreview is { Active: true }) throw new InvalidOperationException("Range previews cannot overlap.");
        _rangePreview = null;
        MovementQueryLease? queries = null;
        bool began = false;
        try
        {
            ValidateTuning(tuning);
            _origin = Physics!.Origin;
            EnsureOrigin();
            CaptureContactFrame();
            var binding = _binding!;
            if (binding.TryAcquire(binding.CreateOrdinaryScope(_contactFrame), out queries) != MovementAvailability.Known)
                return null;
            queries!.BeginContactTick();
            began = true;
            var local = body with { Position = body.Position - _origin };
            // Each normalized component is at most one and the speed fraction is at most one. Round each
            // product upward to bound every float command fraction, including subnormal commands.
            float speed = MathF.BitIncrement((run ? tuning.RunSpeed : tuning.WalkSpeed) * body.SpeedScale);
            float travel = MathF.BitIncrement(speed * dt);
            if (!queries.CertifyFlatRange(new(local, _contactFrame, null), tuning, travel,
                MovementRegistration!, binding.CaptureOrdinaryBoundary(_contactFrame))) return null;
            EnsureOrigin();
            _rangePreview = new(queries, local, predicted, tuning, run, dt, travel);
            return _rangePreview;
        }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        finally
        {
            if (_rangePreview is null && queries is not null)
            {
                if (began) queries.EndContactTick();
                queries.Dispose();
            }
        }
    }

    internal bool HasDryRangePreview(in MoveState body, in MoveTuning tuning) =>
        (MovementWaterField is null or IMovementDryTracePointProof) && _rangePreview?.ProvesDry(body, tuning) == true;
}
