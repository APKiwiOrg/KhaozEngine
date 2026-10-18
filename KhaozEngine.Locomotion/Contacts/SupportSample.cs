using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>What the footprint found. <see cref="Refused"/> means a proposal could not be certified above the
/// best certified support, so no height may be trusted.</summary>
internal enum SupportStatus : byte { None, Walkable, Steep, Refused }

/// <summary>Certified support under a body's axis. The true support height lies within
/// <see cref="HeightError"/> of <see cref="Height"/>. <see cref="Static"/> null means analytic terrain.
/// <see cref="Witness"/> is the touched point inside the footprint, never the feet. Without support,
/// <see cref="Height"/> and <see cref="HeightError"/> are NaN.</summary>
/// <remarks>A supported physics sample read inside an open support interval also carries a 16-byte
/// <see cref="Continuity"/> handle to its row in that interval's arena: the source footprint, the exact query and the
/// selected member's direct joined row (CF-4). Analytic, None and Refused samples carry none. Evidence is internal to
/// its interval, so equality, hashing, wire and replay state read only the seven geometric fields.</remarks>
internal readonly record struct SupportSample(SupportStatus Status, float Height, float HeightError,
    Vector3 Normal, StaticHandle? Static, int FeatureId, Vector3 Witness)
{
    /// <summary>The sample's row in its interval arena, or default.</summary>
    internal SupportContinuityHandle Continuity { get; init; }

    /// <summary>The complete footprint and exact query this sample was certified from, or default. Authenticates the
    /// handle first, so a stale handle throws.</summary>
    internal SupportContinuitySource Source => Continuity.Arena is { } arena ? arena.Resolve(Continuity) : default;

    /// <summary>The selected raw member and its directly joined walkable members, or default. Authenticates the
    /// handle first, so a stale handle throws.</summary>
    internal SupportJoinedMembers Joined => Continuity.Arena is { } arena ? arena.Joined(Continuity) : default;

    /// <summary>Geometric equality over the seven original fields. Interval evidence is excluded.</summary>
    public bool Equals(SupportSample other) =>
        Status == other.Status && Height.Equals(other.Height) && HeightError.Equals(other.HeightError) &&
        Normal.Equals(other.Normal) && EqualityComparer<StaticHandle?>.Default.Equals(Static, other.Static) &&
        FeatureId == other.FeatureId && Witness.Equals(other.Witness);

    /// <summary>Hashes the seven geometric fields only.</summary>
    public override int GetHashCode() =>
        HashCode.Combine(Status, Height, HeightError, Normal, Static, FeatureId, Witness);
}
