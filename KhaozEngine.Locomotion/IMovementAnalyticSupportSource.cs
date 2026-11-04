using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>A source-defined address, qualified by immutable installed provenance. Neither component is a height
/// or a process-local physics handle.</summary>
public readonly record struct MovementSurfaceFeatureKey(string Source, long Address, int Part)
{
    public bool IsValid => MovementEnvironmentValidation.Name(Source) && Part >= 0;
}

/// <summary>One immutable authored triangle, in absolute world coordinates, with its source-certified normal.</summary>
public readonly record struct MovementSurfaceFeature(MovementSurfaceFeatureKey Key, MovementSupportKey Owner,
    Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)
{
    public bool IsValid => Key.IsValid && Owner.IsValid && MovementEnvironmentValidation.Finite(A) &&
        MovementEnvironmentValidation.Finite(B) && MovementEnvironmentValidation.Finite(C) &&
        MovementEnvironmentValidation.Unit(Normal);
}

/// <summary>Optional identity and topology for an immutable analytic field. This capability never changes movement
/// selection. Unknown support is unavailable evidence. Implementations must match the exact height/normal delegates,
/// select the same authored face as that field, and derive adjacency from source topology rather than height.</summary>
public interface IMovementAnalyticSupportSource
{
    bool Matches(Func<float, float, float> height, Func<float, float, Vector3>? normal, OrdinaryGroundSpace space);
    MovementAvailability Describe(Vector2 absoluteAxis, out MovementSurfaceFeature feature);
    MovementAvailability GetFeature(MovementSurfaceFeatureKey key, out MovementSurfaceFeature feature);
    bool AreAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second);
}
