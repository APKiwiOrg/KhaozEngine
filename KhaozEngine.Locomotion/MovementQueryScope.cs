using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Bounded physics-frame query region, including the declared support search envelope.</summary>
public readonly record struct MovementQueryScope
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public float MaxRise { get; }
    public float MaxDrop { get; }
    public string WorldId { get; }
    public MovementSpaceKey? CurrentSpace { get; }
    public MovementQueryIdentity Identity { get; }
    public MovementFrameDescriptor Frame { get; }
    public Vector3 EnvelopeMin => Min - Vector3.UnitY * MaxDrop;
    public Vector3 EnvelopeMax => Max + Vector3.UnitY * MaxRise;
    public bool IsValid => MovementEnvironmentValidation.Finite(Min) && MovementEnvironmentValidation.Finite(Max) &&
        Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z &&
        MovementEnvironmentValidation.Nonnegative(MaxRise) && MovementEnvironmentValidation.Nonnegative(MaxDrop) &&
        MovementEnvironmentValidation.Finite(EnvelopeMin) && MovementEnvironmentValidation.Finite(EnvelopeMax) &&
        MovementEnvironmentValidation.Name(WorldId) && Identity.IsValid && Frame.IsValid &&
        (CurrentSpace is null || CurrentSpace.Value.IsValid &&
            string.Equals(CurrentSpace.Value.WorldId, WorldId, StringComparison.Ordinal));

    public MovementQueryScope(Vector3 min, Vector3 max, float maxRise, float maxDrop,
        MovementSpaceKey currentSpace, MovementQueryIdentity identity, MovementFrameDescriptor frame)
        : this(min, max, maxRise, maxDrop, currentSpace.WorldId, currentSpace, identity, frame) { }

    public MovementQueryScope(Vector3 min, Vector3 max, float maxRise, float maxDrop,
        string worldId, MovementSpaceKey? currentSpace, MovementQueryIdentity identity, MovementFrameDescriptor frame)
    {
        Min = min;
        Max = max;
        MaxRise = maxRise;
        MaxDrop = maxDrop;
        WorldId = worldId;
        CurrentSpace = currentSpace;
        Identity = identity;
        Frame = frame;
        MovementEnvironmentValidation.Require(IsValid, nameof(min));
    }

    /// <summary>Checks the full request and support envelope without changing its identity or frame.</summary>
    public bool Contains(in MovementQueryScope request) => IsValid && request.IsValid &&
        Identity == request.Identity && Frame == request.Frame && CurrentSpace == request.CurrentSpace &&
        string.Equals(WorldId, request.WorldId, StringComparison.Ordinal) &&
        MaxRise >= request.MaxRise && MaxDrop >= request.MaxDrop &&
        ContainsBounds(request.Min, request.Max) && ContainsBounds(request.EnvelopeMin, request.EnvelopeMax);

    /// <summary>Tests finite bounds against the complete certified region, including its support envelope.</summary>
    public bool ContainsBounds(Vector3 min, Vector3 max) => IsValid &&
        MovementEnvironmentValidation.Finite(min) && MovementEnvironmentValidation.Finite(max) &&
        min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z &&
        min.X >= EnvelopeMin.X && min.Y >= EnvelopeMin.Y && min.Z >= EnvelopeMin.Z &&
        max.X <= EnvelopeMax.X && max.Y <= EnvelopeMax.Y && max.Z <= EnvelopeMax.Z;
}
