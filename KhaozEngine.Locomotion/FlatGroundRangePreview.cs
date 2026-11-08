using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Exact flat-ground algebra under a live whole-region certificate. Only command-dependent fields
/// vary. The ordinary endpoint supplied by the caller owns the remaining constant state fields.</summary>
internal sealed class FlatGroundRangePreview(MovementQueryLease queries, MoveState local,
    MoveState predicted, MoveTuning tuning, bool run, float dt, float travel) : IDisposable
{
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
