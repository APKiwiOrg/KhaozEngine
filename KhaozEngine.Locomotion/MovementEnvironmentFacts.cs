using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Default means unresolved. Only Known permits consuming geometry or medium facts.</summary>
public enum MovementAvailability
{
    Unresolved = 0,
    Known,
    Stale,
    Invalid,
    CapacityExceeded
}

/// <summary>An upright body in the leased physics frame, with canonical selection context.</summary>
public readonly record struct MovementBodyQuery
{
    public Vector3 Centre { get; }
    public float Radius { get; }
    public float HalfHeight { get; }
    public MovementSpaceKey CurrentSpace { get; }
    public MovementSupportKey? CurrentSupport { get; }
    public Vector3 Feet => Centre - Vector3.UnitY * HalfHeight;
    public bool IsValid => MovementEnvironmentValidation.Finite(Centre) && float.IsFinite(Radius) && Radius > 0f &&
        float.IsFinite(HalfHeight) && HalfHeight >= Radius && CurrentSpace.IsValid &&
        (CurrentSupport is null || CurrentSupport.Value.IsValid &&
            string.Equals(CurrentSupport.Value.WorldId, CurrentSpace.WorldId, StringComparison.Ordinal));

    public MovementBodyQuery(Vector3 centre, float radius, float halfHeight,
        MovementSpaceKey currentSpace, MovementSupportKey? currentSupport)
    {
        Centre = centre;
        Radius = radius;
        HalfHeight = halfHeight;
        CurrentSpace = currentSpace;
        CurrentSupport = currentSupport;
        MovementEnvironmentValidation.Require(IsValid, nameof(centre));
    }
}

/// <summary>Actual vertical containment, distinct from a connected body's nominal free surface.</summary>
public readonly record struct MovementWaterInterval
{
    public float LowerY { get; }
    public float UpperY { get; }
    public float NominalSurfaceY { get; }
    public bool UpperIsFreeSurface { get; }
    public string LowerBoundaryId { get; }
    public string UpperBoundaryId { get; }
    public bool IsValid => float.IsFinite(LowerY) && float.IsFinite(UpperY) && float.IsFinite(NominalSurfaceY) &&
        LowerY < UpperY && UpperY <= NominalSurfaceY && (!UpperIsFreeSurface || UpperY == NominalSurfaceY) &&
        MovementEnvironmentValidation.Name(LowerBoundaryId) && MovementEnvironmentValidation.Name(UpperBoundaryId);

    public MovementWaterInterval(float lowerY, float upperY, float nominalSurfaceY, bool upperIsFreeSurface,
        string lowerBoundaryId, string upperBoundaryId)
    {
        LowerY = lowerY;
        UpperY = upperY;
        NominalSurfaceY = nominalSurfaceY;
        UpperIsFreeSurface = upperIsFreeSurface;
        LowerBoundaryId = lowerBoundaryId;
        UpperBoundaryId = upperBoundaryId;
        MovementEnvironmentValidation.Require(IsValid, nameof(lowerY));
    }
}

/// <summary>Canonical centre-feet membership. This does not certify the whole capsule's wet footprint.</summary>
public readonly record struct MovementWaterPoint
{
    public MovementAvailability Availability { get; }
    public MovementSpaceKey Space { get; }
    public MovementDomainKey? Domain { get; }
    public bool InWater { get; }
    public float SpeedScale { get; }
    public MovementWaterInterval? Interval { get; }
    public bool IsValid => MovementEnvironmentValidation.Availability(Availability) &&
        MovementEnvironmentValidation.Nonnegative(SpeedScale) &&
        (Availability != MovementAvailability.Known
            ? !InWater && Domain is null && Interval is null
            : Space.IsValid && (InWater
                ? Domain is { IsValid: true } domain && Interval is { IsValid: true } &&
                    string.Equals(domain.WorldId, Space.WorldId, StringComparison.Ordinal)
                : Domain is null && Interval is null));

    public MovementWaterPoint(MovementAvailability availability, MovementSpaceKey space,
        MovementDomainKey? domain, bool inWater, float speedScale, MovementWaterInterval? interval)
    {
        Availability = availability;
        Space = space;
        Domain = domain;
        InWater = inWater;
        SpeedScale = speedScale;
        Interval = interval;
        MovementEnvironmentValidation.Require(IsValid, nameof(availability));
    }
}
