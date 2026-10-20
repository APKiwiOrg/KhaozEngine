using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Physics;

/// <summary>Immutable profile and original raw-source tokens. Equality uses object identity, never token values.</summary>
public readonly struct SupportFootprintReuseDomain : IEquatable<SupportFootprintReuseDomain>
{
    /// <summary>Creates a domain from two non-null, immutable identity tokens owned by the caller.</summary>
    public SupportFootprintReuseDomain(object ProfileIdentity, object SourceIdentity)
    {
        ArgumentNullException.ThrowIfNull(ProfileIdentity);
        ArgumentNullException.ThrowIfNull(SourceIdentity);
        this.ProfileIdentity = ProfileIdentity;
        this.SourceIdentity = SourceIdentity;
    }

    /// <summary>The effective profile's immutable identity token.</summary>
    public object ProfileIdentity { get; }
    /// <summary>The original raw source's immutable identity token.</summary>
    public object SourceIdentity { get; }

    /// <inheritdoc/>
    public bool Equals(SupportFootprintReuseDomain other) =>
        ReferenceEquals(ProfileIdentity, other.ProfileIdentity) && ReferenceEquals(SourceIdentity, other.SourceIdentity);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SupportFootprintReuseDomain other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ProfileIdentity is null ? 0 : RuntimeHelpers.GetHashCode(ProfileIdentity),
        SourceIdentity is null ? 0 : RuntimeHelpers.GetHashCode(SourceIdentity));
    /// <summary>Compares both token identities.</summary>
    public static bool operator ==(SupportFootprintReuseDomain left, SupportFootprintReuseDomain right) => left.Equals(right);
    /// <summary>Compares both token identities.</summary>
    public static bool operator !=(SupportFootprintReuseDomain left, SupportFootprintReuseDomain right) => !left.Equals(right);
}

/// <summary>Optional exact completed-footprint reuse on the selected receiver. Unsupported receivers keep their
/// ordinary query path. A session borrows its opening lease only to authenticate immutable view metadata.</summary>
public interface IPhysicsSupportFootprintReuseSource
{
    /// <summary>Opens a thread-affine, non-owning session against a current lease. A nested attempt returns false
    /// and null without replacing the active session. The caller remains responsible for every lease.</summary>
    bool TryOpenSupportFootprintReuse(IPhysicsQueryLease lease, out IPhysicsSupportFootprintReuseSession? session);
}

/// <summary>A single selected view's source-local reuse lifetime. Payloads contain geometry, never old lease
/// metadata. Every query still requires a current authenticated lease and publishes its own fresh result.</summary>
public interface IPhysicsSupportFootprintReuseSession : IDisposable
{
    /// <summary>Authenticates this thread and unchanged view, discards every retained payload, and selects a new
    /// domain. Even resetting to the same pair of tokens discards all entries. Default domains are invalid.</summary>
    void ResetSource(in SupportFootprintReuseDomain domain);

    /// <summary>Rejects a wrong thread, disposed session, foreign or stale lease, or changed selected view.</summary>
    void AssertCurrent(IPhysicsQueryLease lease);
}
