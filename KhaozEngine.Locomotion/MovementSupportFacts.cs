using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Canonical origin from which the producer proves a legal space/portal transition.</summary>
public readonly record struct MovementTransitionContext
{
    public MovementSpaceKey OriginSpace { get; }
    public Vector3 StartFeet { get; }
    public bool IsValid => OriginSpace.IsValid && MovementEnvironmentValidation.Finite(StartFeet);
    public MovementTransitionContext(MovementSpaceKey originSpace, Vector3 startFeet)
    {
        OriginSpace = originSpace;
        StartFeet = startFeet;
        MovementEnvironmentValidation.Require(IsValid, nameof(originSpace));
    }
}

/// <summary>Bounded support search. The provider owns legal membership, the generic resolver owns clearance.</summary>
public readonly record struct MovementSupportRequest
{
    public MovementBodyQuery Body { get; }
    public float MaxRise { get; }
    public float MaxDrop { get; }
    public float MaxSlopeRadians { get; }
    public MovementTransitionContext Transition { get; }
    public bool IsValid => Body.IsValid && MovementEnvironmentValidation.Nonnegative(MaxRise) &&
        MovementEnvironmentValidation.Nonnegative(MaxDrop) && MovementEnvironmentValidation.Nonnegative(MaxSlopeRadians) &&
        MaxSlopeRadians <= MathF.PI * 0.5f && Transition.IsValid &&
        string.Equals(Transition.OriginSpace.WorldId, Body.CurrentSpace.WorldId, StringComparison.Ordinal);
    public MovementSupportRequest(MovementBodyQuery body, float maxRise, float maxDrop, float maxSlopeRadians,
        MovementTransitionContext transition)
    {
        Body = body;
        MaxRise = maxRise;
        MaxDrop = maxDrop;
        MaxSlopeRadians = maxSlopeRadians;
        Transition = transition;
        MovementEnvironmentValidation.Require(IsValid, nameof(body));
    }
}

/// <summary>Producer-selected support geometry with canonical owner and optional legal-link provenance.</summary>
public readonly record struct MovementSupportCandidate
{
    public MovementSupportKey Owner { get; }
    public MovementSpaceKey Space { get; }
    public Vector3 Feet { get; }
    public Vector3 Normal { get; }
    public string? TraversedLinkId { get; }
    public bool IsValid => Owner.IsValid && Space.IsValid &&
        string.Equals(Owner.WorldId, Space.WorldId, StringComparison.Ordinal) &&
        MovementEnvironmentValidation.Finite(Feet) && MovementEnvironmentValidation.Unit(Normal) &&
        (TraversedLinkId is null || MovementEnvironmentValidation.Name(TraversedLinkId));
    public MovementSupportCandidate(MovementSupportKey owner, MovementSpaceKey space, Vector3 feet,
        Vector3 normal, string? traversedLinkId)
    {
        Owner = owner;
        Space = space;
        Feet = feet;
        Normal = normal;
        TraversedLinkId = traversedLinkId;
        MovementEnvironmentValidation.Require(IsValid, nameof(owner));
    }
}

/// <summary>Known means the complete candidate set. Refusals cannot expose a usable prefix.</summary>
public readonly record struct MovementSupportSet
{
    public MovementAvailability Availability { get; }
    public int Written { get; }
    public int RequiredCapacity { get; }
    public MovementQueryIdentity Identity { get; }
    public bool IsValid => MovementEnvironmentValidation.Availability(Availability) && Written >= 0 &&
        RequiredCapacity >= Written && (Availability == MovementAvailability.Known
            ? Written == RequiredCapacity && Identity.IsValid : Written == 0);
    public MovementSupportSet(MovementAvailability availability, int written, int requiredCapacity,
        MovementQueryIdentity identity)
    {
        Availability = availability;
        Written = written;
        RequiredCapacity = requiredCapacity;
        Identity = identity;
        MovementEnvironmentValidation.Require(IsValid, nameof(availability));
    }
}
