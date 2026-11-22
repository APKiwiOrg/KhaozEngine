using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Plain triangle geometry: positions and a triangle list of indices into them, three per triangle.</summary>
public readonly record struct CoverageClaimMesh(ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices)
{
    /// <summary>One flag per position. Empty means all vertices are fixed. Only piece flags affect coverage.</summary>
    public ReadOnlyMemory<bool> Swinging { get; init; }

    internal void ValidateSwingingMask(string parameterName, int meshIndex)
    {
        if (!Swinging.IsEmpty && Swinging.Length != Positions.Length)
            throw new ArgumentException(
                $"Mesh {meshIndex} has {Positions.Length} positions and {Swinging.Length} swinging flags. A nonempty mask must have one flag per position.",
                parameterName);
    }
}
