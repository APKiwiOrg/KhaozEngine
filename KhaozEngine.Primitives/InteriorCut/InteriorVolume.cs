using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>A simple world-XZ interior ring with a floor and ceiling.</summary>
public sealed record InteriorVolume
{
    public required IReadOnlyList<Vector2> Outline { get; init; }
    public required float FloorY { get; init; }
    public required float CeilingY { get; init; }
    public IReadOnlyList<InteriorRoof> Roofs { get; init; } = [];
}

/// <summary>Roof surface samples and their mesh-local to world transform.</summary>
public readonly record struct InteriorRoof(ClearanceSamples Samples, Matrix4x4 LocalToWorld);
