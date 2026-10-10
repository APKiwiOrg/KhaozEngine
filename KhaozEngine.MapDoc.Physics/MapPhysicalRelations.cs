using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>One physical relation: its certainty, the owner of the static that blocks it and the distance along the
/// tested path to the block, both null unless blocked, and the identity of the built world it was evaluated over.
/// <see cref="BlockingOwner"/> is a placement id or a terrain chunk id. It is null for a block found by penetration at
/// the first feet point, which names no static, and for a static this registration did not add.</summary>
public sealed record MapPhysicalResult(MapPhysicalCertainty Certainty, string? BlockingOwner, float? BlockDistance,
    string BuildHash, string TerrainWitnessDigest);

/// <summary>Physical line of sight and walk clearance over a registration's statics, evaluated under a held read lease
/// of its physics world. Queries see statics only, so dynamic bodies never block. Interaction envelopes are never
/// installed, so they never block either, and a portal is an opening with no faces. Terrain chunks are one-sided, so a
/// ray or sweep that meets a face from its back passes through. Geometric relation facts stay with
/// <see cref="MapSpaceRelations"/>: these results add only physical certainty.</summary>
public sealed class MapPhysicalRelations
{
    /// <summary>The most feet points one clearance path takes.</summary>
    public const int MaxClearancePoints = 64;

    /// <summary>A ray hit this close to the segment's end is <see cref="MapPhysicalCertainty.Unknown"/>, and a shell
    /// penetrating no deeper than this is clear, in metres.</summary>
    public const float Tolerance = 0.001f;

    readonly MapPhysicsRegistration _registration;

    /// <summary>Relations over the statics <paramref name="registration"/> installed.</summary>
    public MapPhysicalRelations(MapPhysicsRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registration = registration;
    }

    /// <summary>Whether the straight segment from <paramref name="from"/> to <paramref name="to"/> is clear of every
    /// static. A hit nearer than the segment length minus <see cref="Tolerance"/> is
    /// <see cref="MapPhysicalCertainty.Blocked"/>, a hit within it of the end is <see cref="MapPhysicalCertainty.Unknown"/>
    /// and a miss is <see cref="MapPhysicalCertainty.Clear"/>. An end outside the document's playable bounds is
    /// Unknown, and two equal ends are Clear. Throws <see cref="ArgumentException"/> for a non-finite point or a lease
    /// over another physics world, <see cref="ObjectDisposedException"/> for a disposed registration, and whatever
    /// <see cref="IPhysicsQueryLease.AssertCurrent"/> throws for a stale lease.</summary>
    public MapPhysicalResult LineOfSight(IPhysicsQueryLease lease, MapFramePoint from, MapFramePoint to)
    {
        RequireFinite(from, nameof(from));
        RequireFinite(to, nameof(to));
        RequireLease(lease);
        if (!Playable(from) || !Playable(to)) return Result(MapPhysicalCertainty.Unknown);

        Vector3 start = Local(lease, from);
        Vector3 delta = Local(lease, to) - start;
        float length = delta.Length();
        if (length == 0f) return Result(MapPhysicalCertainty.Clear);
        if (!_registration.Physics.Raycast(start, delta / length, length, out RayHit hit, QueryFilter.StaticsOnly))
            return Result(MapPhysicalCertainty.Clear);
        return hit.Distance < length - Tolerance
            ? Result(MapPhysicalCertainty.Blocked, Owner(hit.Body), hit.Distance)
            : Result(MapPhysicalCertainty.Unknown);
    }

    /// <summary>Whether the contact shell of <paramref name="tuning"/> can stand at the first feet point and move along
    /// <paramref name="feetPath"/>. The shell is tested in place at the first point, then swept along each segment
    /// between consecutive points. Penetration deeper than <see cref="Tolerance"/>, or a sweep hit before a segment's
    /// end, is <see cref="MapPhysicalCertainty.Blocked"/>, with the distance along the path. A shell that starts in
    /// contact no deeper than <see cref="Tolerance"/> sweeps its first segment that moves from a start pushed out along
    /// the minimum translation vector to <see cref="Tolerance"/> clear of the contact, as the contact controller
    /// recovers. Leading zero-length segments keep the push-out. The backend reports a sweep that starts overlapping
    /// as a hit at distance 0 with no normal, so the push-out, not the hit normal, decides: a path moving away from the
    /// touched surface is clear and a path moving into it is blocked. A start contact of exactly zero depth has no
    /// push-out direction, so a sweep starting exactly tangent is reported as the backend reports it. Segments after
    /// the first that moves start from the path points themselves. A point outside the document's
    /// playable bounds is <see cref="MapPhysicalCertainty.Unknown"/>. Throws <see cref="ArgumentOutOfRangeException"/>
    /// for a path of other than 1 to <see cref="MaxClearancePoints"/> points, <see cref="ArgumentException"/> for an
    /// invalid tuning, a non-finite point or a lease over another physics world, <see cref="ObjectDisposedException"/>
    /// for a disposed registration, and whatever <see cref="IPhysicsQueryLease.AssertCurrent"/> throws for a stale
    /// lease.</summary>
    public MapPhysicalResult Clearance(IPhysicsQueryLease lease, IReadOnlyList<MapFramePoint> feetPath, in MoveTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(feetPath);
        if (feetPath.Count is < 1 or > MaxClearancePoints)
            throw new ArgumentOutOfRangeException(nameof(feetPath), feetPath.Count,
                $"A clearance path takes 1 to {MaxClearancePoints} feet points.");
        ContactShell.Validate(tuning);
        for (int i = 0; i < feetPath.Count; i++) RequireFinite(feetPath[i], nameof(feetPath));
        RequireLease(lease);
        for (int i = 0; i < feetPath.Count; i++)
            if (!Playable(feetPath[i])) return Result(MapPhysicalCertainty.Unknown);

        IPhysicsWorld physics = _registration.Physics;
        CapsuleShape shell = ContactShell.Shape(tuning);
        Vector3 feet = Local(lease, feetPath[0]);
        // The first sweep that moves starts here: the shell pushed out of a tolerated contact to Tolerance clear of it.
        Vector3 pushOut = Vector3.Zero;
        if (physics.ComputePenetration(shell, Pose.At(ContactShell.Centre(feet, tuning)), out Vector3 mtv))
        {
            float depth = mtv.Length();
            if (depth > Tolerance) return Result(MapPhysicalCertainty.Blocked, null, 0f);
            if (depth > 0f) pushOut = mtv * ((depth + Tolerance) / depth);
        }

        float travelled = 0f;
        for (int i = 1; i < feetPath.Count; i++)
        {
            Vector3 next = Local(lease, feetPath[i]);
            Vector3 delta = next - feet;
            float length = delta.Length();
            if (length > 0f &&
                physics.SweepCapsule(shell, Pose.At(ContactShell.Centre(feet, tuning) + pushOut), delta / length, length,
                    out SweepHit hit, QueryFilter.StaticsOnly) &&
                hit.Distance < length)
                return Result(MapPhysicalCertainty.Blocked, Owner(hit.Body), travelled + hit.Distance);
            travelled += length;
            feet = next;
            if (length > 0f) pushOut = Vector3.Zero;
        }
        return Result(MapPhysicalCertainty.Clear);
    }

    void RequireLease(IPhysicsQueryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _registration.ThrowIfDisposed();
        if (!ReferenceEquals(lease.SourceWorld, _registration.Physics))
            throw new ArgumentException("The lease is over another physics world than the registration's.", nameof(lease));
        lease.AssertCurrent();
    }

    static void RequireFinite(MapFramePoint point, string name)
    {
        if (!float.IsFinite(point.Local.X) || !float.IsFinite(point.Local.Y) || !float.IsFinite(point.Local.Z))
            throw new ArgumentException("A relation point must be finite.", name);
    }

    // The point's world XZ in double, from its frame anchor and local offset.
    static (double X, double Z) WorldXz(MapFramePoint point) =>
        ((double)point.Frame.X * WorldFrame.Grid + point.Local.X, (double)point.Frame.Z * WorldFrame.Grid + point.Local.Z);

    bool Playable(MapFramePoint point)
    {
        (double x, double z) = WorldXz(point);
        MapResolvedBounds playable = _registration.World.Document.PlayableBounds;
        return x >= playable.MinX && x <= playable.MaxX && z >= playable.MinZ && z <= playable.MaxZ;
    }

    // The point in the leased physics world's coordinates: world double less the lease's captured origin.
    static Vector3 Local(IPhysicsQueryLease lease, MapFramePoint point)
    {
        (double x, double z) = WorldXz(point);
        Vector3 origin = lease.Origin;
        return new Vector3((float)(x - origin.X), (float)((double)point.Local.Y - origin.Y), (float)(z - origin.Z));
    }

    string? Owner(StaticHandle? handle) =>
        handle is { } h && _registration.TryOwner(h, out MapStaticOwner? owner) ? owner.OwnerId : null;

    MapPhysicalResult Result(MapPhysicalCertainty certainty, string? owner = null, float? distance = null) =>
        new(certainty, owner, distance, _registration.World.BuildHash, _registration.World.Terrain.Witness.ScopedDigest);
}
