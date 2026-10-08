using System;
using System.Buffers;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    // These operational bounds affect query results and belong in the producer/profile policy identity.
    public const int MaxSupportCandidates = 256;
    public const int MaxCoverageSpans = 64;
    public const int MaxDomainContacts = 256;
    public const float CoverageSkinMetres = 0.001f;

    /// <summary>Enumerates into private bounded scratch. Caller storage changes only for a validated complete result.</summary>
    public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates)
    {
        AssertUsable();
        if (!_selectionReady) return NoSupport(MovementAvailability.Unresolved);
        if (!request.IsValid || !SameWorld(request.Body.CurrentSpace)) return NoSupport(MovementAvailability.Invalid);
        Vector3 extent = BodyExtent(request.Body);
        if (request.MaxRise > Witness.Scope.MaxRise || request.MaxDrop > Witness.Scope.MaxDrop ||
            !Witness.Scope.ContainsBounds(request.Body.Centre - extent - Vector3.UnitY * request.MaxDrop,
                request.Body.Centre + extent + Vector3.UnitY * request.MaxRise) ||
            !Witness.Scope.ContainsBounds(request.Transition.StartFeet, request.Transition.StartFeet))
            return NoSupport(MovementAvailability.Unresolved);

        int capacity = Math.Min(candidates.Length, MaxSupportCandidates);
        MovementSupportCandidate[] rented = ArrayPool<MovementSupportCandidate>.Shared.Rent(Math.Max(1, capacity));
        try
        {
            Span<MovementSupportCandidate> scratch = rented.AsSpan(0, capacity);
            scratch.Clear();
            AssertCurrent();
            MovementSupportSet result = _pin.EnumerateSupport(request, scratch);
            AssertCurrent();
            if (!result.IsValid || ResultIdentityMismatch(result.Identity, result.Availability))
                return NoSupport(MovementAvailability.Invalid);
            if (result.Availability != MovementAvailability.Known)
                return NoSupport(result.Availability, result.RequiredCapacity);
            if (result.Written > capacity) return NoSupport(MovementAvailability.Invalid);
            for (int i = 0; i < result.Written; i++)
            {
                MovementSupportCandidate candidate = scratch[i];
                if (!candidate.IsValid || !SameWorld(candidate.Space) ||
                    !Witness.Scope.ContainsBounds(candidate.Feet, candidate.Feet))
                    return NoSupport(MovementAvailability.Invalid);
            }
            scratch[..result.Written].CopyTo(candidates);
            return result;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return NoSupport(MovementAvailability.Stale); }
        catch (ArgumentException) { return NoSupport(MovementAvailability.Invalid); }
        finally { ArrayPool<MovementSupportCandidate>.Shared.Return(rented, clearArray: true); }
    }

    /// <summary>Validates complete ordered coverage and both buffers before publishing either one.</summary>
    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        AssertUsable();
        if (!_selectionReady) return NoCoverage(MovementAvailability.Unresolved);
        if (!query.IsValid || !SameWorld(query.Body.CurrentSpace)) return NoCoverage(MovementAvailability.Invalid);
        Vector3 min = Witness.Scope.EnvelopeMin, max = Witness.Scope.EnvelopeMax;
        double radial = (double)query.Body.Radius + CoverageSkinMetres;
        double vertical = (double)query.Body.HalfHeight + CoverageSkinMetres;
        // The producer traces the affine path, not its rounded float endpoint. An inward-rounded
        // endpoint or skin addition cannot widen the scope certified by the immutable witness.
        if (!AxisInside(query.Body.Centre.X, query.Delta.X, radial, min.X, max.X) ||
            !AxisInside(query.Body.Centre.Y, query.Delta.Y, vertical, min.Y, max.Y) ||
            !AxisInside(query.Body.Centre.Z, query.Delta.Z, radial, min.Z, max.Z))
            return NoCoverage(MovementAvailability.Unresolved);

        int spanCapacity = Math.Min(spans.Length, MaxCoverageSpans);
        int contactCapacity = Math.Min(contacts.Length, MaxDomainContacts);
        MovementCoverageSpan[] spanStorage = ArrayPool<MovementCoverageSpan>.Shared.Rent(Math.Max(1, spanCapacity));
        MovementDomainContact[]? contactStorage = null;
        try
        {
            contactStorage = ArrayPool<MovementDomainContact>.Shared.Rent(Math.Max(1, contactCapacity));
            Span<MovementCoverageSpan> scratchSpans = spanStorage.AsSpan(0, spanCapacity);
            Span<MovementDomainContact> scratchContacts = contactStorage.AsSpan(0, contactCapacity);
            scratchSpans.Clear();
            scratchContacts.Clear();
            AssertCurrent();
            MovementCoverageResult result = _pin.TraceWater(query, scratchSpans, scratchContacts);
            AssertCurrent();
            if (!result.IsValid || ResultIdentityMismatch(result.Identity, result.Availability))
                return NoCoverage(MovementAvailability.Invalid);
            if (result.Availability != MovementAvailability.Known)
                return NoCoverage(result.Availability, result.RequiredSpanCapacity, result.RequiredContactCapacity);
            if (result.SpansWritten > spanCapacity || result.ContactsWritten > contactCapacity)
                return NoCoverage(MovementAvailability.Invalid);
            if (result.CertifiedErrorMetres > CoverageSkinMetres) return NoCoverage(MovementAvailability.Unresolved);
            float next = 0f;
            Span<bool> owned = stackalloc bool[MaxDomainContacts];
            owned.Clear();
            for (int i = 0; i < result.SpansWritten; i++)
            {
                MovementCoverageSpan span = scratchSpans[i];
                if (!span.IsValid || span.EnterFraction != next ||
                    span.ContactStart + span.ContactCount > result.ContactsWritten)
                    return NoCoverage(MovementAvailability.Invalid);
                for (int j = span.ContactStart; j < span.ContactStart + span.ContactCount; j++)
                {
                    MovementDomainContact contact = scratchContacts[j];
                    if (owned[j] || contact.Fraction < span.EnterFraction || contact.Fraction > span.ExitFraction ||
                        contact.SpanOverlap == MovementContactOverlap.Tangent && contact.Overlap != MovementContactOverlap.Tangent ||
                        span.EnterFraction == span.ExitFraction && contact.SpanOverlap != contact.Overlap)
                        return NoCoverage(MovementAvailability.Invalid);
                    owned[j] = true;
                }
                next = span.ExitFraction;
            }
            if (next != 1f) return NoCoverage(MovementAvailability.Invalid);
            for (int i = 0; i < result.ContactsWritten; i++)
                if (!owned[i] || !scratchContacts[i].IsValid || !SameWorld(scratchContacts[i].Space) ||
                    !ColumnOverlapsCapsule(query, scratchContacts[i], result.CertifiedErrorMetres) ||
                    !TangentCoherent(query, scratchContacts[i], result.CertifiedErrorMetres))
                    return NoCoverage(MovementAvailability.Invalid);

            MovementAvailability facts = RememberWaterContacts(scratchContacts[..result.ContactsWritten]);
            if (facts != MovementAvailability.Known) return NoCoverage(facts);
            // No producer callback or fallible validation occurs between the two copies.
            scratchSpans[..result.SpansWritten].CopyTo(spans);
            scratchContacts[..result.ContactsWritten].CopyTo(contacts);
            return result;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return NoCoverage(MovementAvailability.Stale); }
        catch (ArgumentException) { return NoCoverage(MovementAvailability.Invalid); }
        finally
        {
            if (contactStorage is not null) ArrayPool<MovementDomainContact>.Shared.Return(contactStorage, clearArray: true);
            ArrayPool<MovementCoverageSpan>.Shared.Return(spanStorage, clearArray: true);
        }
    }

    bool ColumnOverlapsCapsule(in MovementMediumSweepQuery query, in MovementDomainContact contact, float error)
    {
        Vector2 column = contact.IntervalColumnXZ;
        Vector3 min = Witness.Scope.EnvelopeMin, max = Witness.Scope.EnvelopeMax;
        if (column.X < min.X || column.X > max.X || column.Y < min.Z || column.Y > max.Z) return false;

        double fraction = contact.Fraction;
        double centreX = query.Body.Centre.X + fraction * query.Delta.X;
        double centreY = query.Body.Centre.Y + fraction * query.Delta.Y;
        double centreZ = query.Body.Centre.Z + fraction * query.Delta.Z;
        double radius = query.Body.Radius + (double)CoverageSkinMetres + error;
        double dx = column.X - centreX, dz = column.Y - centreZ;
        double capSquared = radius * radius - dx * dx - dz * dz;
        if (capSquared < 0d) return false;

        // Isotropic skin/error inflation enlarges the caps, not the cylindrical core. A disc or
        // vertical prism would accept columns that miss the actual rounded capsule vertically.
        double cap = Math.Sqrt(capSquared);
        double core = (double)query.Body.HalfHeight - query.Body.Radius;
        double overlapMin = Math.Max(centreY - core - cap, contact.Interval.LowerY);
        double overlapMax = Math.Min(centreY + core + cap, contact.Interval.UpperY);
        // Only the relevant overlap needs certified query Y. The actual deep column can extend far
        // beyond it. Canonical membership and geometry dependencies remain the producer's proof.
        return overlapMin <= overlapMax && overlapMax >= min.Y && overlapMin <= max.Y;
    }

    bool SameWorld(MovementSpaceKey space) =>
        string.Equals(space.WorldId, Witness.Scope.WorldId, StringComparison.Ordinal);

    bool ResultIdentityMismatch(MovementQueryIdentity identity, MovementAvailability availability) =>
        availability == MovementAvailability.Known ? identity != Identity : identity.IsValid && identity != Identity;

    static Vector3 BodyExtent(in MovementBodyQuery body) =>
        new Vector3(body.Radius, body.HalfHeight, body.Radius) + new Vector3(CoverageSkinMetres);

    MovementSupportSet NoSupport(MovementAvailability availability, int requiredCapacity = 0) =>
        new(availability, 0, requiredCapacity, Identity);

    MovementCoverageResult NoCoverage(MovementAvailability availability, int spans = 0, int contacts = 0) =>
        new(availability, 0, spans, 0, contacts, 0f, Identity);
}
