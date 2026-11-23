using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Plain triangle geometry: positions and a triangle list of indices into them, three per triangle.</summary>
public readonly record struct CoverageClaimMesh(ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices)
{
    readonly ReadOnlyMemory<bool> _swinging;

    /// <summary>The definition part this mesh represents. Zero leaves the part unstated. Required for footprint
    /// measurement. Coverage and standoff checks require it for pieces that declare motion. Ignored for body meshes.</summary>
    public uint PartId { get; init; }

    /// <summary>One flag per position. Required for a piece part with motion. Otherwise, empty means all vertices
    /// are fixed. Only piece flags affect coverage.</summary>
    /// <remarks>An empty mask is stored as the default, so a mesh given an empty mask equals the same mesh given none,
    /// as meshes compared before masks existed.</remarks>
    public ReadOnlyMemory<bool> Swinging
    {
        get => _swinging;
        init => _swinging = value.IsEmpty ? default : value;
    }

    internal void ValidateSwingingMask(string parameterName, int meshIndex)
    {
        if (!Swinging.IsEmpty && Swinging.Length != Positions.Length)
            throw new ArgumentException(
                $"Mesh {meshIndex} has {Positions.Length} positions and {Swinging.Length} swinging flags. A nonempty mask must have one flag per position.",
                parameterName);
    }
}
