using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

/// <summary>Exact arrival trials under a live whole-region certificate. Intrinsically dry flat regions retain
/// their algebra. Other regions keep the shared kernel and real medium traces over captured ground.</summary>
internal sealed class GroundRangePreview(MovementQueryLease queries, MoveState local,
    MoveState predicted, MoveTuning tuning, bool run, float dt, float travel,
    CertifiedGroundRangeQueries? supported = null) : IDisposable
{
    readonly ExplicitContactModes? _modes = supported is null ? null : new();
    Vector3? _supportedPosition;
    internal bool Active { get; private set; } = true;
    internal bool TryStep(Vector2 command, out MoveState state)
    {
        state = default;
        if (!Active) return false;
        try { state = Step(command); return true; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal MoveState Step(Vector2 command)
    {
        queries.AssertCurrent();
        var (direction, fraction) = CharacterMovement.ResolveWorldDir(command, preserveSmallMagnitude: true);
        if (supported is not null)
        {
            // Retain the ordinary per-tick analytic consistency and capacity checks. Only immutable geometry
            // and coverage outlive a trial, never its mutable query workspace or medium history.
            queries.EndContactTick();
            queries.BeginContactTick();
            supported.Begin();
            _modes!.Initialize(supported, local, tuning, tuning.Water, false);
            MoveState result = ContactController.StepWithQueries(local, direction, fraction, run, false, dt, tuning,
                supported, out var continuation, settings: new(tuning.FootRadiusFraction), fluid: _modes);
            if (supported.Failed || !continuation.Completed || !result.Grounded || result.Swimming || !result.SupportGranted)
                throw new InvalidOperationException("The supported arrival trial left its certificate.");
            var framed = new FramedMovementState(result, queries.Frame, null);
            if (!MovementFrameRebinding.TryRebind(framed, framed.Frame, out _))
                throw new InvalidOperationException("The supported arrival trial left its frame.");
            queries.AssertCurrent();
            result.Position += queries.Frame.PhysicsOrigin;
            _supportedPosition = result.Position;
            return result;
        }
        float speedScale = 1f * local.SpeedScale * 1f;
        float speed = fraction > 0f ? (run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale * fraction : 0f;
        Vector2 velocity = direction * speed;
        Vector2 displacement = velocity * dt;
        Vector3 end = displacement == Vector2.Zero ? local.Position :
            new(local.Position.X + displacement.X, local.Position.Y, local.Position.Z + displacement.Y);
        // GroundCore.Result adds the exact zero recovery offset to the accepted plan. Keep that addition,
        // even for signed zero, before the ordinary carry clipping and world-frame publication.
        Vector2 achieved = Vector2.Zero + displacement;
        MoveState next = predicted with
        {
            Position = (end + queries.Frame.PhysicsOrigin) with { Y = predicted.Position.Y },
            FacingYaw = CharacterMovement.ResolveFacing(local.FacingYaw, direction, null, dt, tuning),
            CommandedVelocity = velocity,
            HorizontalVelocity = CharacterMovement.ClipCarryToAchieved(velocity, velocity, achieved, dt),
        };
        queries.AssertCurrent();
        return next;
    }

    internal bool ProvesDry(in MoveState body, in MoveTuning requested)
    {
        if (!Active || requested != tuning) return false;
        try { queries.AssertCurrent(); }
        catch (InvalidOperationException) { Dispose(); return false; }
        catch (ArgumentException) { Dispose(); return false; }
        // Direct-driver admission asks about the trial just returned. Its slope-dependent height was proved
        // by that exact kernel execution, including the final frame mapping, rather than the endpoint's Y.
        if (supported is not null) return _supportedPosition == body.Position;
        Vector3 position = body.Position - queries.Frame.PhysicsOrigin;
        float margin = MathF.BitIncrement(travel + 2 * MovementQueryLease.CoverageSkinMetres);
        return body.Position.Y == predicted.Position.Y && Math.Abs((double)position.X - local.Position.X) <= margin &&
            Math.Abs((double)position.Z - local.Position.Z) <= margin;
    }

    public void Dispose()
    {
        if (!Active) return;
        Active = false;
        try { queries.EndContactTick(); }
        finally { queries.Dispose(); }
    }
}
