using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Builds bounded candidate paths under one read lease. The solid stage does not commit
/// movement. Every returned segment still needs the combined resolver's water-policy proof.</summary>
internal static class MovementCapsuleResolver
{
    internal const uint PolicyVersion = 1;
    internal const int MaximumCorrections = 8;
    internal const int MaximumEndpoints = MaximumCorrections + 1;
    const float Skin = MovementQueryLease.CoverageSkinMetres;

    internal static MovementAvailability TryResolveSolids(in MovementBodyQuery body, Vector3 displacement,
        MovementQueryLease queries, Span<Vector3> destination, out int written, out bool blocked)
    {
        written = 0;
        blocked = false;
        ArgumentNullException.ThrowIfNull(queries);
        if (!body.IsValid || !MovementEnvironmentValidation.Finite(displacement)) return MovementAvailability.Invalid;
        if (destination.IsEmpty) return MovementAvailability.CapacityExceeded;
        CapsuleContact[] contacts = ArrayPool<CapsuleContact>.Shared.Rent(MovementQueryLease.MaxSolidContacts);
        Vector3[] normals = ArrayPool<Vector3>.Shared.Rent(MovementQueryLease.MaxSolidContacts);
        try
        {
            Span<CapsuleContact> contactBuffer = contacts.AsSpan(0, MovementQueryLease.MaxSolidContacts);
            Span<Vector3> normalBuffer = normals.AsSpan(0, MovementQueryLease.MaxSolidContacts);
            Span<Vector3> path = stackalloc Vector3[MaximumEndpoints];
            MovementAvailability status = Trace(body, Vector3.Zero, queries, out CapsuleSweepResult initial,
                out _, out _);
            if (status != MovementAvailability.Known) return status;
            // A closed zero-time Hit is neither a safe starting pose nor a recovery certificate.
            if (initial.Status != CapsuleSweepStatus.Clear) return MovementAvailability.Unresolved;
            status = ClearPlacement(body, queries, contactBuffer);
            if (status != MovementAvailability.Known) return status;

            MovementBodyQuery current = body;
            Vector3 remaining = displacement;
            int count = 0;
            bool obstructed = false;
            for (int correction = 0; correction <= MaximumCorrections; correction++)
            {
                status = Trace(current, remaining, queries, out CapsuleSweepResult sweep,
                    out Vector3 requestedEnd, out double inflation);
                if (status != MovementAvailability.Known) return status;
                if (sweep.Status == CapsuleSweepStatus.Clear)
                {
                    status = ClearPlacement(At(current, requestedEnd), queries, contactBuffer);
                    if (status != MovementAvailability.Known) return status;
                    path[count++] = requestedEnd;
                    return Publish(path[..count], obstructed, queries, destination, out written, out blocked);
                }
                if (correction == MaximumCorrections || remaining == Vector3.Zero)
                    return MovementAvailability.Unresolved;
                double length = Length(remaining);
                float margin = Skin + sweep.CertifiedErrorMetres;
                if (!MovementCapsuleRounding.TryImpact(current.Centre, remaining,
                    sweep.ImpactDistance!.Value, length, out Vector3 impact, out double poseError) ||
                    (double)sweep.ImpactDistance.Value - sweep.ClearThroughDistance + poseError + inflation > margin)
                    return MovementAvailability.Unresolved;
                contactBuffer.Clear();
                status = queries.QuerySolidContacts(At(current, impact), margin, contactBuffer, out CapsuleContactResult set);
                if (status != MovementAvailability.Known) return status;
                if (set.Written == 0) return MovementAvailability.Unresolved;
                double retreat = 0;
                bool inward = false;
                for (int i = 0; i < set.Written; i++)
                {
                    Vector3 normal = contactBuffer[i].Normal;
                    normalBuffer[i] = normal;
                    double approach = -((double)normal.X * remaining.X + (double)normal.Y * remaining.Y +
                        (double)normal.Z * remaining.Z) / length;
                    if (approach <= 0) continue;
                    inward = true;
                    // A fixed distance along the path provides almost no normal clearance at a
                    // grazing floor. This is only a proposal, subsequently swept in full again.
                    retreat = Math.Max(retreat, 2d * Skin / approach);
                }
                if (!inward) return MovementAvailability.Unresolved;
                double advance = Math.Max(0, sweep.ClearThroughDistance - retreat);
                if (advance > 0)
                {
                    Vector3 prefix = remaining * (float)(advance / length);
                    status = Trace(current, prefix, queries, out CapsuleSweepResult prefixSweep,
                        out Vector3 prefixEnd, out _);
                    if (status != MovementAvailability.Known) return status;
                    if (prefixSweep.Status != CapsuleSweepStatus.Clear) return MovementAvailability.Unresolved;
                    status = ClearPlacement(At(current, prefixEnd), queries, contactBuffer);
                    if (status != MovementAvailability.Known) return status;
                    path[count++] = prefixEnd;
                    current = At(current, prefixEnd);
                }
                Vector3 desired = requestedEnd - current.Centre;
                status = MovementConstraintProjection.TryProject(desired, normalBuffer[..set.Written], out Vector3 projected);
                if (status != MovementAvailability.Known) return status;
                if (projected == desired) return MovementAvailability.Unresolved;
                obstructed = true;
                if (projected == Vector3.Zero)
                {
                    if (count == 0) path[count++] = current.Centre;
                    return Publish(path[..count], true, queries, destination, out written, out blocked);
                }
                remaining = projected;
            }
            return MovementAvailability.Unresolved;
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(normals, clearArray: true);
            ArrayPool<CapsuleContact>.Shared.Return(contacts, clearArray: true);
        }
    }

    static MovementAvailability Trace(in MovementBodyQuery body, Vector3 delta, MovementQueryLease queries,
        out CapsuleSweepResult result, out Vector3 end, out double inflation)
    {
        result = default;
        if (!MovementCapsuleRounding.TryEnclose(body, delta, out MovementBodyQuery enclosure, out end, out inflation))
            return MovementAvailability.Unresolved;
        return queries.QuerySolidSweep(enclosure, delta, out result);
    }

    static MovementAvailability ClearPlacement(in MovementBodyQuery body, MovementQueryLease queries,
        Span<CapsuleContact> contacts)
    {
        contacts.Clear();
        MovementAvailability status = queries.QuerySolidContacts(body, contacts, out CapsuleContactResult result);
        if (status != MovementAvailability.Known) return status;
        foreach (CapsuleContact contact in contacts[..result.Written])
            if (contact.Separation - result.CertifiedErrorMetres < 0) return MovementAvailability.Unresolved;
        return MovementAvailability.Known;
    }

    static MovementAvailability Publish(ReadOnlySpan<Vector3> path, bool obstructed, MovementQueryLease queries,
        Span<Vector3> destination, out int written, out bool blocked)
    {
        written = 0;
        blocked = false;
        if (path.Length > destination.Length) return MovementAvailability.CapacityExceeded;
        try { queries.AssertCurrent(); }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        path.CopyTo(destination);
        written = path.Length;
        blocked = obstructed;
        return MovementAvailability.Known;
    }

    static MovementBodyQuery At(in MovementBodyQuery body, Vector3 centre) =>
        new(centre, body.Radius, body.HalfHeight, body.CurrentSpace, body.CurrentSupport);
    static double Length(Vector3 value) => Math.Sqrt((double)value.X * value.X +
        (double)value.Y * value.Y + (double)value.Z * value.Z);
}
