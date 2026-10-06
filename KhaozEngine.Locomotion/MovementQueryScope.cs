using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Bounded physics-frame query region, including the declared support search envelope.</summary>
public readonly record struct MovementQueryScope
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public float MaxRise { get; }
    public float MaxDrop { get; }
    public MovementSpaceKey CurrentSpace { get; }
    public MovementQueryIdentity Identity { get; }
    public MovementFrameDescriptor Frame { get; }
    public Vector3 EnvelopeMin => Min - Vector3.UnitY * MaxDrop;
    public Vector3 EnvelopeMax => Max + Vector3.UnitY * MaxRise;
    public bool IsValid => MovementEnvironmentValidation.Finite(Min) && MovementEnvironmentValidation.Finite(Max) &&
        Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z &&
        MovementEnvironmentValidation.Nonnegative(MaxRise) && MovementEnvironmentValidation.Nonnegative(MaxDrop) &&
        MovementEnvironmentValidation.Finite(EnvelopeMin) && MovementEnvironmentValidation.Finite(EnvelopeMax) &&
        CurrentSpace.IsValid && Identity.IsValid && Frame.IsValid;

    public MovementQueryScope(Vector3 min, Vector3 max, float maxRise, float maxDrop,
        MovementSpaceKey currentSpace, MovementQueryIdentity identity, MovementFrameDescriptor frame)
    {
        Min = min;
        Max = max;
        MaxRise = maxRise;
        MaxDrop = maxDrop;
        CurrentSpace = currentSpace;
        Identity = identity;
        Frame = frame;
        MovementEnvironmentValidation.Require(IsValid, nameof(min));
    }

    /// <summary>Checks the full request and support envelope without changing its identity or frame.</summary>
    public bool Contains(in MovementQueryScope request) => IsValid && request.IsValid &&
        Identity == request.Identity && Frame == request.Frame && CurrentSpace == request.CurrentSpace &&
        MaxRise >= request.MaxRise && MaxDrop >= request.MaxDrop &&
        ContainsBounds(request.Min, request.Max) && ContainsBounds(request.EnvelopeMin, request.EnvelopeMax);

    /// <summary>Tests finite bounds against the complete certified region, including its support envelope.</summary>
    public bool ContainsBounds(Vector3 min, Vector3 max) => IsValid &&
        MovementEnvironmentValidation.Finite(min) && MovementEnvironmentValidation.Finite(max) &&
        min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z &&
        min.X >= EnvelopeMin.X && min.Y >= EnvelopeMin.Y && min.Z >= EnvelopeMin.Z &&
        max.X <= EnvelopeMax.X && max.Y <= EnvelopeMax.Y && max.Z <= EnvelopeMax.Z;
}
