using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Locomotion;

/// <summary>Pure conversion between recorded physics frames. It does not establish canonical membership.</summary>
public static class MovementFrameRebinding
{
    public const float MaxLocalVerticalMetres = 640f;

    /// <summary>Converts within the certified local envelopes. Failure returns the original state unchanged.</summary>
    public static bool TryRebind(in FramedMovementState state, in MovementFrameDescriptor target,
        out FramedMovementState rebound)
    {
        rebound = state;
        if (!state.IsValid || !target.IsValid || !InEnvelope(state.State.Position)) return false;
        if (state.Frame == target) return true;

        // Subtract recorded origins before adding the small local value. Forming a large float world
        // position first would destroy fractions that should survive a nearby rebase. Y uses the same
        // single origin conversion and retains the world's datum rather than applying WorldFrame twice.
        Vector3 oldOrigin = state.Frame.PhysicsOrigin;
        Vector3 newOrigin = target.PhysicsOrigin;
        double x = state.State.Position.X + ((double)oldOrigin.X - newOrigin.X);
        double y = state.State.Position.Y + ((double)oldOrigin.Y - newOrigin.Y);
        double z = state.State.Position.Z + ((double)oldOrigin.Z - newOrigin.Z);
        if (!InEnvelope(x, y, z)) return false;
        Vector3 position = new((float)x, (float)y, (float)z);
        if (!InEnvelope(position) || Math.Abs(x - position.X) > MovementQueryLease.CoverageSkinMetres ||
            Math.Abs(y - position.Y) > MovementQueryLease.CoverageSkinMetres ||
            Math.Abs(z - position.Z) > MovementQueryLease.CoverageSkinMetres) return false;

        MoveState converted = state.State;
        converted.Position = position;
        // Stable keys keep their meaning, but a changed frame/epoch requires canonical reconstruction
        // before the previous selection can be used again. No local handles survive this conversion.
        rebound = new FramedMovementState(converted, target, null);
        return true;
    }

    static bool InEnvelope(Vector3 position) => InEnvelope(position.X, position.Y, position.Z);
    static bool InEnvelope(double x, double y, double z) => double.IsFinite(x) && double.IsFinite(y) &&
        double.IsFinite(z) && Math.Abs(y) <= MaxLocalVerticalMetres &&
        x * x + z * z <= (double)WorldFrame.MaxLocalRadius * WorldFrame.MaxLocalRadius;
}
