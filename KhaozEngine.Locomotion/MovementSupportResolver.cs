using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Canonical support provenance and the separately cleared upright-capsule pose.</summary>
public readonly record struct MovementSupportPlacement(MovementSupportCandidate Candidate, Vector3 Centre);

/// <summary>Composes canonical support candidates with complete leased physics clearance.</summary>
public static class MovementSupportResolver
{
    /// <summary>Filters legal candidates before choosing the highest. Known with null means none are eligible.
    /// Placement does not prove the swept path to it. That requires the shared movement resolver.</summary>
    public static MovementAvailability Select(in MovementSupportRequest request, MovementQueryLease queries,
        out MovementSupportPlacement? placement)
    {
        placement = null;
        ArgumentNullException.ThrowIfNull(queries);
        MovementSupportCandidate[] candidates = ArrayPool<MovementSupportCandidate>.Shared.Rent(MovementQueryLease.MaxSupportCandidates);
        CapsuleContact[]? contacts = null;
        try
        {
            Span<MovementSupportCandidate> buffer = candidates.AsSpan(0, MovementQueryLease.MaxSupportCandidates);
            buffer.Clear();
            MovementSupportSet set = queries.EnumerateSupport(request, buffer);
            if (set.Availability != MovementAvailability.Known) return set.Availability;
            contacts = ArrayPool<CapsuleContact>.Shared.Rent(MovementQueryLease.MaxSolidContacts);
            Span<CapsuleContact> scratch = contacts.AsSpan(0, MovementQueryLease.MaxSolidContacts);
            MovementSupportPlacement? best = null;
            bool ambiguous = false;
            float minimumY = request.Body.Feet.Y - request.MaxDrop;
            float maximumY = request.Body.Feet.Y + request.MaxRise;
            float minimumNormalY = MathF.Cos(request.MaxSlopeRadians);
            foreach (MovementSupportCandidate candidate in buffer[..set.Written])
            {
                // Link legality is certified by the producer, not reconstructed from opaque IDs here.
                if (candidate.Space != request.Body.CurrentSpace && candidate.TraversedLinkId is null)
                    return MovementAvailability.Invalid;
                if (candidate.Feet.X != request.Body.Feet.X || candidate.Feet.Z != request.Body.Feet.Z)
                    return MovementAvailability.Invalid;
                Vector3 normal = Vector3.Normalize(candidate.Normal);
                if (candidate.Feet.Y < minimumY || candidate.Feet.Y > maximumY ||
                    normal.Y <= 0f || normal.Y < minimumNormalY) continue;

                MovementAvailability status = ClearPlacement(request.Body, candidate, normal, queries, scratch,
                    out MovementSupportPlacement? cleared);
                if (status != MovementAvailability.Known) return status;
                if (cleared is not { } eligible) continue;
                if (best is not { } previous || candidate.Feet.Y > previous.Candidate.Feet.Y)
                {
                    best = eligible;
                    ambiguous = false;
                }
                else if (candidate.Feet.Y == previous.Candidate.Feet.Y)
                {
                    if (candidate.Owner != previous.Candidate.Owner) ambiguous = true;
                    else if (CompareAliases(eligible, previous) < 0) best = eligible;
                }
            }
            if (ambiguous) return MovementAvailability.Invalid;
            queries.AssertCurrent();
            placement = best;
            return MovementAvailability.Known;
        }
        finally
        {
            if (contacts is not null) ArrayPool<CapsuleContact>.Shared.Return(contacts, clearArray: true);
            ArrayPool<MovementSupportCandidate>.Shared.Return(candidates, clearArray: true);
        }
    }

    static MovementAvailability ClearPlacement(in MovementBodyQuery profile, in MovementSupportCandidate candidate,
        Vector3 normal, MovementQueryLease queries, Span<CapsuleContact> scratch,
        out MovementSupportPlacement? placement)
    {
        placement = null;
        // A slope's canonical point is not the bottom of the upright capsule. Retain that point and
        // offset the lower sphere centre to the tangent plane, then verify against every real contact.
        float offset = profile.HalfHeight - profile.Radius +
            (profile.Radius + MovementQueryLease.CoverageSkinMetres) / normal.Y;
        Vector3 centre = candidate.Feet + Vector3.UnitY * offset;
        if (!MovementEnvironmentValidation.Finite(centre)) return MovementAvailability.Unresolved;
        var body = new MovementBodyQuery(centre, profile.Radius, profile.HalfHeight, candidate.Space, candidate.Owner);
        scratch.Clear();
        MovementAvailability status = queries.QuerySolidContacts(body, scratch, out CapsuleContactResult result);
        if (status != MovementAvailability.Known) return status;
        bool supported = false;
        bool uncertainClearance = false;
        foreach (CapsuleContact contact in scratch[..result.Written])
        {
            // A definite penetration excludes this placement. An uncertain sign cannot prove clearance.
            if (contact.Separation + result.CertifiedErrorMetres < 0f) return MovementAvailability.Known;
            if (contact.Separation - result.CertifiedErrorMetres < 0f) uncertainClearance = true;
            if (Vector3.Dot(contact.Normal, normal) >= 0.9999f &&
                contact.Separation <= MovementQueryLease.CoverageSkinMetres + result.CertifiedErrorMetres)
                supported = true;
        }
        if (!supported || uncertainClearance) return MovementAvailability.Unresolved;
        placement = new MovementSupportPlacement(candidate, centre);
        return MovementAvailability.Known;
    }

    static int CompareAliases(in MovementSupportPlacement a, in MovementSupportPlacement b)
    {
        int result = string.CompareOrdinal(a.Candidate.Space.LocalId, b.Candidate.Space.LocalId);
        if (result != 0) return result;
        result = a.Centre.Y.CompareTo(b.Centre.Y);
        if (result != 0) return result;
        result = a.Candidate.Normal.X.CompareTo(b.Candidate.Normal.X);
        if (result != 0) return result;
        result = a.Candidate.Normal.Z.CompareTo(b.Candidate.Normal.Z);
        return result != 0 ? result : string.CompareOrdinal(a.Candidate.TraversedLinkId, b.Candidate.TraversedLinkId);
    }
}
