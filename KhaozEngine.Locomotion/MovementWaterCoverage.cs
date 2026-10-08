using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>A finite capsule path whose full volumetric water coverage must be certified.</summary>
public readonly record struct MovementMediumSweepQuery
{
    public MovementBodyQuery Body { get; }
    public Vector3 Delta { get; }
    public bool IsValid => Body.IsValid && MovementEnvironmentValidation.Finite(Delta) &&
        MovementEnvironmentValidation.Finite(Body.Centre + Delta);
    public MovementMediumSweepQuery(MovementBodyQuery body, Vector3 delta)
    {
        Body = body;
        Delta = delta;
        MovementEnvironmentValidation.Require(IsValid, nameof(delta));
    }
}

/// <summary>One ordered path interval with simultaneous wet-domain and dry-footprint coverage.</summary>
public readonly record struct MovementCoverageSpan
{
    public float EnterFraction { get; }
    public float ExitFraction { get; }
    public int ContactStart { get; }
    public int ContactCount { get; }
    public bool HasDryCoverage { get; }
    public bool IsValid => float.IsFinite(EnterFraction) && float.IsFinite(ExitFraction) &&
        EnterFraction >= 0f && ExitFraction >= EnterFraction && ExitFraction <= 1f &&
        ContactStart >= 0 && ContactCount >= 0 && ContactStart <= int.MaxValue - ContactCount &&
        (ContactCount > 0 || HasDryCoverage);
    public MovementCoverageSpan(float enterFraction, float exitFraction, int contactStart, int contactCount,
        bool hasDryCoverage)
    {
        EnterFraction = enterFraction;
        ExitFraction = exitFraction;
        ContactStart = contactStart;
        ContactCount = contactCount;
        HasDryCoverage = hasDryCoverage;
        MovementEnvironmentValidation.Require(IsValid, nameof(enterFraction));
    }
}

/// <summary>Whether the uninflated capsule overlaps water at the contact fraction.
/// Uncertain intersection is Overlapping. Zero is not a valid producer classification.</summary>
public enum MovementContactOverlap { Unspecified = 0, Tangent = 1, Overlapping = 2 }

/// <summary>Clipped water-region provenance. NominalSurfaceY and UpperIsFreeSurface certify the
/// whole covered subregion. LowerY, a ceiling UpperY and boundary IDs remain column-local.
/// Producers split every free/closed transition. CoverageRegionHandle is pin-local.</summary>
public readonly record struct MovementDomainContact
{
    public MovementDomainKey Domain { get; }
    public MovementSpaceKey Space { get; }
    public MovementWaterInterval Interval { get; }
    public Vector2 IntervalColumnXZ { get; }
    public Vector3 Normal { get; }
    public float Fraction { get; }
    public string BoundaryId { get; }
    public uint CoverageRegionHandle { get; }
    public MovementContactOverlap Overlap { get; }
    /// <summary>No-positive-overlap certificate over the closed interval of the single owning span.
    /// Tangent is a whole-path fact. Positive or uncertain overlap is Overlapping.</summary>
    public MovementContactOverlap SpanOverlap { get; }
    public bool IsValid => Domain.IsValid && Space.IsValid && Interval.IsValid &&
        float.IsFinite(IntervalColumnXZ.X) && float.IsFinite(IntervalColumnXZ.Y) &&
        string.Equals(Domain.WorldId, Space.WorldId, StringComparison.Ordinal) &&
        MovementEnvironmentValidation.Unit(Normal) && float.IsFinite(Fraction) && Fraction >= 0f && Fraction <= 1f &&
        MovementEnvironmentValidation.Name(BoundaryId) &&
        Overlap is MovementContactOverlap.Tangent or MovementContactOverlap.Overlapping &&
        SpanOverlap is MovementContactOverlap.Tangent or MovementContactOverlap.Overlapping;
    public MovementDomainContact(MovementDomainKey domain, MovementSpaceKey space, MovementWaterInterval interval,
        Vector2 intervalColumnXZ, Vector3 normal, float fraction, string boundaryId, uint coverageRegionHandle, MovementContactOverlap overlap, MovementContactOverlap spanOverlap)
    {
        Domain = domain;
        Space = space;
        Interval = interval;
        IntervalColumnXZ = intervalColumnXZ;
        Normal = normal;
        Fraction = fraction;
        BoundaryId = boundaryId;
        CoverageRegionHandle = coverageRegionHandle;
        Overlap = overlap;
        SpanOverlap = spanOverlap;
        MovementEnvironmentValidation.Require(IsValid, nameof(domain));
    }
}

/// <summary>Whole-path coverage result. No spans or contacts are usable on a refusal.</summary>
public readonly record struct MovementCoverageResult
{
    public MovementAvailability Availability { get; }
    public int SpansWritten { get; }
    public int RequiredSpanCapacity { get; }
    public int ContactsWritten { get; }
    public int RequiredContactCapacity { get; }
    public float CertifiedErrorMetres { get; }
    public MovementQueryIdentity Identity { get; }
    public bool IsValid => MovementEnvironmentValidation.Availability(Availability) &&
        SpansWritten >= 0 && RequiredSpanCapacity >= SpansWritten && ContactsWritten >= 0 &&
        RequiredContactCapacity >= ContactsWritten && MovementEnvironmentValidation.Nonnegative(CertifiedErrorMetres) &&
        (Availability == MovementAvailability.Known
            ? SpansWritten > 0 && SpansWritten == RequiredSpanCapacity && ContactsWritten == RequiredContactCapacity && Identity.IsValid
            : SpansWritten == 0 && ContactsWritten == 0);
    public MovementCoverageResult(MovementAvailability availability, int spansWritten, int requiredSpanCapacity,
        int contactsWritten, int requiredContactCapacity, float certifiedErrorMetres, MovementQueryIdentity identity)
    {
        Availability = availability;
        SpansWritten = spansWritten;
        RequiredSpanCapacity = requiredSpanCapacity;
        ContactsWritten = contactsWritten;
        RequiredContactCapacity = requiredContactCapacity;
        CertifiedErrorMetres = certifiedErrorMetres;
        Identity = identity;
        MovementEnvironmentValidation.Require(IsValid, nameof(availability));
    }
}
