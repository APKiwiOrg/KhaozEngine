using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Builds bounded candidate paths under one read lease. The solid stage does not commit
/// movement. Every returned segment still needs the combined resolver's water-policy proof.</summary>
internal static class MovementCapsuleResolver
{
    internal const uint PolicyVersion = 2;
    internal const int MaximumCorrections = 8;
    internal const int MaximumEndpoints = MaximumCorrections + 1;
    const float Skin = MovementQueryLease.CoverageSkinMetres;

    internal static MovementAvailability TryResolveSolids(in MovementBodyQuery body, Vector3 displacement,
        MovementQueryLease queries, Span<Vector3> destination, out int written, out bool blocked) =>
        ResolveCore(body, displacement, queries, false, WaterTraversalMode.Legacy, 0, destination, out written, out blocked);

    internal static MovementAvailability TryResolve(in MovementBodyQuery body, Vector3 displacement,
        MovementQueryLease queries, Span<Vector3> destination, out int written, out bool blocked) =>
        ResolveCore(body, displacement, queries, true, WaterTraversalMode.Legacy, 0, destination, out written, out blocked);

    internal static MovementAvailability TryResolvePolicy(in MovementBodyQuery body, Vector3 displacement,
        MovementQueryLease queries, WaterTraversalMode mode, float enterFraction, Span<Vector3> destination, out int written, out bool blocked) =>
        ResolveCore(body, displacement, queries, true, mode, enterFraction, destination, out written, out blocked);

    static MovementAvailability ResolveCore(in MovementBodyQuery body, Vector3 displacement,
        MovementQueryLease queries, bool proveMedium, WaterTraversalMode mode, float enterFraction,
        Span<Vector3> destination, out int written, out bool blocked)
    {
        written = 0;
        blocked = false;
        ArgumentNullException.ThrowIfNull(queries);
        if (!body.IsValid || !MovementEnvironmentValidation.Finite(displacement)) return MovementAvailability.Invalid;
        if (destination.IsEmpty) return MovementAvailability.CapacityExceeded;
        CapsuleContact[] contacts = ArrayPool<CapsuleContact>.Shared.Rent(MovementQueryLease.MaxSolidContacts);
        Vector3[] normals = ArrayPool<Vector3>.Shared.Rent(MovementConstraintProjection.MaximumInputNormals);
        try
        {
            Span<CapsuleContact> contactBuffer = contacts.AsSpan(0, MovementQueryLease.MaxSolidContacts);
            Span<Vector3> normalBuffer = normals.AsSpan(0, MovementConstraintProjection.MaximumInputNormals);
            Span<Vector3> waterNormals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
            Span<Vector3> path = stackalloc Vector3[MaximumEndpoints];
            MovementAvailability status = Trace(body, Vector3.Zero, queries, proveMedium, mode, enterFraction, waterNormals, out _, out MovementWaterBoundary.Hit initialWater,
                out CapsuleSweepResult initial, out _, out _);
            if (status != MovementAvailability.Known) return status;
            // A closed zero-time Hit is neither a safe starting pose nor a recovery certificate.
            if (initial.Status != CapsuleSweepStatus.Clear || initialWater.Blocked) return MovementAvailability.Unresolved;
            status = ClearPlacement(body, queries, contactBuffer);
            if (status != MovementAvailability.Known) return status;

            MovementBodyQuery current = body;
            Vector3 remaining = displacement;
            int count = 0;
            bool obstructed = false;
            for (int correction = 0; correction <= MaximumCorrections; correction++)
            {
                status = Trace(current, remaining, queries, proveMedium, mode, enterFraction, waterNormals, out int waterCount, out MovementWaterBoundary.Hit waterHit,
                    out CapsuleSweepResult sweep, out Vector3 requestedEnd, out double inflation);
                if (status != MovementAvailability.Known) return status;
                if (sweep.Status == CapsuleSweepStatus.Clear && !waterHit.Blocked)
                {
                    status = ClearPlacement(At(current, requestedEnd), queries, contactBuffer);
                    if (status != MovementAvailability.Known) return status;
                    path[count++] = requestedEnd;
                    return Publish(path[..count], obstructed, queries, destination, out written, out blocked);
                }
                if (correction == MaximumCorrections || remaining == Vector3.Zero)
                    return MovementAvailability.Unresolved;
                double length = Length(remaining);
                double solidDistance = sweep.Status == CapsuleSweepStatus.Clear ? double.PositiveInfinity : sweep.ImpactDistance!.Value;
                double waterDistance = waterHit.Blocked ? waterHit.Distance : double.PositiveInfinity;
                bool solidFirst = solidDistance <= waterDistance;
                double impactDistance = Math.Min(solidDistance, waterDistance);
                double clearDistance = solidFirst ? sweep.ClearThroughDistance : waterDistance;
                float margin = Skin + sweep.CertifiedErrorMetres;
                if (!MovementCapsuleRounding.TryImpact(current.Centre, remaining,
                    impactDistance, length, out Vector3 impact, out double poseError) ||
                    solidFirst && solidDistance - sweep.ClearThroughDistance + poseError + inflation > margin)
                    return MovementAvailability.Unresolved;
                contactBuffer.Clear();
                status = queries.QuerySolidContacts(At(current, impact), margin, contactBuffer, out CapsuleContactResult set);
                if (status != MovementAvailability.Known) return status;
                int normalCount = set.Written;
                for (int i = 0; i < set.Written; i++) normalBuffer[i] = contactBuffer[i].Normal;
                if (waterHit.Blocked && waterDistance - impactDistance <= margin)
                {
                    waterNormals[..waterCount].CopyTo(normalBuffer[normalCount..]);
                    normalCount += waterCount;
                }
                if (proveMedium && mode is WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly)
                {
                    var near = new MovementBodyQuery(impact, current.Radius + Skin, current.HalfHeight + Skin,
                        current.CurrentSpace, current.CurrentSupport);
                    status = MovementWaterBoundary.Find(near, Vector3.Zero, body.HalfHeight, enterFraction, mode,
                        queries, waterNormals, out int nearCount, out _, Skin);
                    if (status != MovementAvailability.Known) return status;
                    for (int i = 0; i < nearCount; i++)
                    {
                        bool duplicate = false;
                        for (int j = set.Written; j < normalCount; j++)
                            if (normalBuffer[j] == waterNormals[i]) { duplicate = true; break; }
                        if (duplicate) continue;
                        if (normalCount == normalBuffer.Length) return MovementAvailability.CapacityExceeded;
                        normalBuffer[normalCount++] = waterNormals[i];
                    }
                }
                if (normalCount == 0) return MovementAvailability.Unresolved;
                double retreat = 0;
                bool inward = false;
                for (int i = 0; i < normalCount; i++)
                {
                    Vector3 normal = normalBuffer[i];
                    double approach = -((double)normal.X * remaining.X + (double)normal.Y * remaining.Y +
                        (double)normal.Z * remaining.Z) / length;
                    if (approach <= 0) continue;
                    inward = true;
                    // A fixed distance along the path provides almost no normal clearance at a
                    // grazing floor. This is only a proposal, subsequently swept in full again.
                    retreat = Math.Max(retreat, 2d * Skin / approach);
                }
                if (!inward) return MovementAvailability.Unresolved;
                double advance = Math.Max(0, clearDistance - retreat);
                if (advance > 0)
                {
                    Vector3 prefix = remaining * (float)(advance / length);
                    status = Trace(current, prefix, queries, proveMedium, mode, enterFraction, waterNormals, out _, out MovementWaterBoundary.Hit prefixWater,
                        out CapsuleSweepResult prefixSweep, out Vector3 prefixEnd, out _);
                    if (status != MovementAvailability.Known) return status;
                    if (prefixSweep.Status != CapsuleSweepStatus.Clear || prefixWater.Blocked) return MovementAvailability.Unresolved;
                    status = ClearPlacement(At(current, prefixEnd), queries, contactBuffer);
                    if (status != MovementAvailability.Known) return status;
                    path[count++] = prefixEnd;
                    current = At(current, prefixEnd);
                }
                Vector3 desired = requestedEnd - current.Centre;
                status = MovementConstraintProjection.TryProject(desired, normalBuffer[..normalCount], out Vector3 projected);
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
        bool proveMedium, WaterTraversalMode mode, float enterFraction, Span<Vector3> waterNormals,
        out int waterCount, out MovementWaterBoundary.Hit waterHit, out CapsuleSweepResult result, out Vector3 end, out double inflation)
    {
        result = default;
        waterCount = 0;
        waterHit = default;
        if (!MovementCapsuleRounding.TryEnclose(body, delta, out MovementBodyQuery enclosure, out end, out inflation))
            return MovementAvailability.Unresolved;
        MovementAvailability status = queries.QuerySolidSweep(enclosure, delta, out result);
        if (status != MovementAvailability.Known || !proveMedium) return status;
        if (mode is WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly)
            return MovementWaterBoundary.Find(enclosure, delta, body.HalfHeight, enterFraction, mode, queries,
                waterNormals, out waterCount, out waterHit);
        return result.Status == CapsuleSweepStatus.Clear
            ? MovementWaterPathProof.Check(enclosure, delta, queries, mode) : status;
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
