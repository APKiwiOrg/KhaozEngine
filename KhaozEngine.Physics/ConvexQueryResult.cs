using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Unresolved has no usable evidence. Clear and Hit require whole-view certification.</summary>
public enum ConvexSweepStatus { Unresolved = 0, Clear = 1, Hit = 2 }

/// <summary>Raw source-local provenance. Coincident members with different identities remain distinct.</summary>
public readonly record struct ConvexContactIdentity(bool Dynamic, int BodyHandle, int ChildIndex, int FeatureId);

/// <summary>Structural sweep evidence in metres. Construction validates fields, not geometric proof.
/// ClearThroughDistance must cover the full request for Clear. For Hit it is the lower impact bound,
/// not an inclusive safe placement. The bracket and reported error cannot exceed the existing 1 mm budget.
/// Identity retains the exact raw target provenance on Hit and must be zero on Clear or Unresolved.</summary>
public readonly record struct ConvexSweepResult
{
    public ConvexSweepStatus Status { get; }
    public float ClearThroughDistance { get; }
    public float? ImpactDistance { get; }
    public float CertifiedErrorMetres { get; }
    public SweepHit? Hit { get; }
    public ConvexContactIdentity Identity { get; }

    public ConvexSweepResult(ConvexSweepStatus status, float clearThroughDistance,
        float? impactDistance, float certifiedErrorMetres, SweepHit? hit, ConvexContactIdentity identity)
    {
        bool valid = ConvexResultValidation.Nonnegative(clearThroughDistance) &&
            ConvexResultValidation.Error(certifiedErrorMetres) && (status switch
            {
                ConvexSweepStatus.Unresolved => clearThroughDistance == 0 && impactDistance is null &&
                    certifiedErrorMetres == 0 && hit is null && identity == default,
                ConvexSweepStatus.Clear => impactDistance is null && hit is null && identity == default,
                ConvexSweepStatus.Hit => impactDistance is float upper &&
                    ConvexResultValidation.Bracket(clearThroughDistance, upper, certifiedErrorMetres) &&
                    hit is SweepHit operational && float.IsFinite(operational.Distance) &&
                    operational.Distance >= clearThroughDistance && operational.Distance <= upper &&
                    ConvexResultValidation.Finite(operational.Point) && ConvexResultValidation.Direction(operational.Normal),
                _ => false
            });
        if (!valid) throw new ArgumentException("Invalid convex sweep result.");
        Status = status;
        ClearThroughDistance = clearThroughDistance;
        ImpactDistance = impactDistance;
        CertifiedErrorMetres = certifiedErrorMetres;
        Hit = hit;
        Identity = identity;
    }
}

/// <summary>Nearest nonnegative separation. Incomplete and empty results have no witness or identity.
/// Overlap has a zero distance bracket. Separated witnesses and a unit normal away from the target
/// must be certified by the backend. Value validation alone cannot establish witness membership.</summary>
public readonly record struct ConvexDistanceResult
{
    public bool Complete { get; }
    public bool HasTarget { get; }
    public float LowerDistance { get; }
    public float UpperDistance { get; }
    public Vector3 QueryWitness { get; }
    public Vector3 TargetWitness { get; }
    public Vector3 Normal { get; }
    public ConvexContactIdentity Identity { get; }

    public ConvexDistanceResult(bool complete, bool hasTarget, float lowerDistance, float upperDistance,
        Vector3 queryWitness, Vector3 targetWitness, Vector3 normal, ConvexContactIdentity identity)
    {
        bool valid = complete && hasTarget
            ? ConvexResultValidation.Bracket(lowerDistance, upperDistance) &&
                ConvexResultValidation.Finite(queryWitness) && ConvexResultValidation.Finite(targetWitness) &&
                ConvexResultValidation.Finite(normal) && (upperDistance == 0 || ConvexResultValidation.Direction(normal))
            : !hasTarget && lowerDistance == 0 && upperDistance == 0 && queryWitness == default &&
                targetWitness == default && normal == default && identity == default;
        if (!valid) throw new ArgumentException("Invalid convex distance result.");
        Complete = complete;
        HasTarget = hasTarget;
        LowerDistance = lowerDistance;
        UpperDistance = upperDistance;
        QueryWitness = queryWitness;
        TargetWitness = targetWitness;
        Normal = normal;
        Identity = identity;
    }
}

/// <summary>A raw constraint with signed separation bounds. Normal is a certified unit member of the
/// actual normal cone, pointing away from the target. Negative separation denotes penetration.</summary>
public readonly record struct ConvexContact
{
    public Vector3 Normal { get; }
    public float LowerSeparation { get; }
    public float UpperSeparation { get; }
    public ConvexContactIdentity Identity { get; }

    public ConvexContact(Vector3 normal, float lowerSeparation, float upperSeparation, ConvexContactIdentity identity)
    {
        if (!ConvexResultValidation.Direction(normal) || !float.IsFinite(lowerSeparation) ||
            !float.IsFinite(upperSeparation) || upperSeparation < lowerSeparation ||
            (double)upperSeparation - lowerSeparation > 0.001f)
            throw new ArgumentException("Invalid convex contact.");
        Normal = normal;
        LowerSeparation = lowerSeparation;
        UpperSeparation = upperSeparation;
        Identity = identity;
    }
}

/// <summary>A complete list or atomic refusal. Refusal writes zero, with known required capacity or zero.
/// Complete output writes every required member. CertifiedErrorMetres bounds all reported separations.</summary>
public readonly record struct ConvexContactResult
{
    public bool Complete { get; }
    public int Written { get; }
    public int RequiredCapacity { get; }
    public float CertifiedErrorMetres { get; }

    public ConvexContactResult(bool complete, int written, int requiredCapacity, float certifiedErrorMetres)
    {
        if (written < 0 || requiredCapacity < 0 || !ConvexResultValidation.Error(certifiedErrorMetres) ||
            (complete ? written != requiredCapacity : written != 0 || certifiedErrorMetres != 0))
            throw new ArgumentException("Invalid convex contact result.");
        Complete = complete;
        Written = written;
        RequiredCapacity = requiredCapacity;
        CertifiedErrorMetres = certifiedErrorMetres;
    }
}

/// <summary>The deepest raw member's certified minimum translation proposal and nonnegative depth bounds.
/// Incomplete or nonoverlapping results expose no translation, depth or identity. A proposal requires
/// whole-view placement validation before use. It is not a global-union minimum translation.</summary>
public readonly record struct ConvexPenetrationResult
{
    public bool Complete { get; }
    public bool Overlapping { get; }
    public Vector3 Translation { get; }
    public float LowerDepth { get; }
    public float UpperDepth { get; }
    public ConvexContactIdentity Identity { get; }

    public ConvexPenetrationResult(bool complete, bool overlapping, Vector3 translation,
        float lowerDepth, float upperDepth, ConvexContactIdentity identity)
    {
        bool valid = complete && overlapping
            ? ConvexResultValidation.Bracket(lowerDepth, upperDepth) && upperDepth > 0 &&
                ConvexResultValidation.Direction(translation)
            : !overlapping && translation == default && lowerDepth == 0 && upperDepth == 0 && identity == default;
        if (!valid) throw new ArgumentException("Invalid convex penetration result.");
        Complete = complete;
        Overlapping = overlapping;
        Translation = translation;
        LowerDepth = lowerDepth;
        UpperDepth = upperDepth;
        Identity = identity;
    }
}

// Structural checks only. No new geometric tolerance, normal repair or admission epsilon.
internal static class ConvexResultValidation
{
    internal static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
    internal static bool Error(float value) => Nonnegative(value) && value <= 0.001f;
    internal static bool Bracket(float lower, float upper, float error = 0.001f) =>
        Nonnegative(lower) && Nonnegative(upper) && upper >= lower && (double)upper - lower <= error;
    internal static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal static bool Direction(Vector3 value) => Finite(value) && value != Vector3.Zero;
}
