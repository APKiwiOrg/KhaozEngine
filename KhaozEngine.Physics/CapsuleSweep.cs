using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Structural outcome of an optional certified sweep. Unresolved exposes no usable prefix.</summary>
public enum CapsuleSweepStatus { Unresolved = 0, Clear = 1, Hit = 2 }

/// <summary>Distances in metres along the requested displacement, with a declared spatial error.
/// Valid structure does not prove the backend's certificate or authorize a placement.</summary>
public readonly record struct CapsuleSweepResult
{
    public CapsuleSweepStatus Status { get; }
    /// <summary>Lower impact bound for Hit, with an empty forward-clear interval at zero.
    /// It is not an inclusive safe placement. For Clear this must cover the entire request.</summary>
    public float ClearThroughDistance { get; }
    /// <summary>Upper impact bound for Hit. This is a query distance, not an accepted placement.</summary>
    public float? ImpactDistance { get; }
    public float CertifiedErrorMetres { get; }
    /// <summary>Structural Clear/Hit status only. Consumers must validate the request extent,
    /// accepted error, scope and read lifetime independently of this property.</summary>
    public bool IsComplete => IsValid && Status != CapsuleSweepStatus.Unresolved;
    public bool IsValid => Valid(Status, ClearThroughDistance, ImpactDistance, CertifiedErrorMetres);

    public CapsuleSweepResult(CapsuleSweepStatus status, float clearThroughDistance,
        float? impactDistance, float certifiedErrorMetres)
    {
        if (!Valid(status, clearThroughDistance, impactDistance, certifiedErrorMetres))
            throw new ArgumentException("Invalid capsule sweep result.");
        Status = status;
        ClearThroughDistance = clearThroughDistance;
        ImpactDistance = impactDistance;
        CertifiedErrorMetres = certifiedErrorMetres;
    }

    static bool Valid(CapsuleSweepStatus status, float clearThroughDistance, float? impactDistance,
        float certifiedErrorMetres)
    {
        if (!float.IsFinite(clearThroughDistance) || clearThroughDistance < 0f ||
            !float.IsFinite(certifiedErrorMetres) || certifiedErrorMetres < 0f ||
            (impactDistance is float impact && (!float.IsFinite(impact) || impact < 0f)))
            return false;
        return status switch
        {
            CapsuleSweepStatus.Unresolved => clearThroughDistance == 0f &&
                impactDistance is null && certifiedErrorMetres == 0f,
            CapsuleSweepStatus.Clear => impactDistance is null,
            CapsuleSweepStatus.Hit => impactDistance is float upper && upper >= clearThroughDistance,
            _ => false
        };
    }
}

/// <summary>Optional complete capsule sweeps through the exact selected physics query view.</summary>
public interface IPhysicsCapsuleSweep
{
    /// <summary>Clear certifies the entire closed path, including a stationary query for zero
    /// displacement. Hit does not authorize placement, including at zero or closed tangency.
    /// Unproved geometry, unsupported pose orientation, filters or numerical/work limits must return
    /// Unresolved without a usable prefix. Query-view restrictions and read lifetime remain binding.</summary>
    CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default);
}
