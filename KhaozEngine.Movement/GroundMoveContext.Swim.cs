using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    /// <summary>Static clearance of a swimming body, which the core does not collide. The profile capsule at the body's
    /// pose is clear when it overlaps nothing, or when the separating translation points up within the walkable slope
    /// and is no longer than <see cref="MoveTuning.StepHeight"/>, a float capsule grazing the bed near a shore. A deck
    /// above, a post beside or a steep bank refuses it. True without physics.</summary>
    /// <remarks>The penetration query reports the deepest contact only, so a shallow side contact under a deeper bed
    /// contact passes. Deep water has no bed contact, so this applies only within about one step of the bed.</remarks>
    internal bool SwimClear(in MoveState body, in MoveTuning tuning)
    {
        if (Physics is null) return true;
        IPhysicsWorld world = (IPhysicsWorld?)MovementQueries ?? Physics;
        Vector3 local = body.Position - Physics.Origin;
        if (!world.ComputePenetration(CharacterMovement.CapsuleFor(tuning), Pose.At(local), out Vector3 mtv)) return true;
        float depth = mtv.Length();
        return mtv.Y >= depth * MathF.Cos(tuning.MaxSlopeRadians) && depth <= tuning.StepHeight;
    }
}
