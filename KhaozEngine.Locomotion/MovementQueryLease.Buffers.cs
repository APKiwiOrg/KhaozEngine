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
        Vector3 end = query.Body.Centre + query.Delta;
        Vector3 extent = BodyExtent(query.Body);
        if (!Witness.Scope.ContainsBounds(Vector3.Min(query.Body.Centre, end) - extent,
            Vector3.Max(query.Body.Centre, end) + extent)) return NoCoverage(MovementAvailability.Unresolved);

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
            for (int i = 0; i < result.SpansWritten; i++)
            {
                MovementCoverageSpan span = scratchSpans[i];
                if (!span.IsValid || span.EnterFraction != next ||
                    span.ContactStart + span.ContactCount > result.ContactsWritten)
                    return NoCoverage(MovementAvailability.Invalid);
                next = span.ExitFraction;
            }
            if (next != 1f) return NoCoverage(MovementAvailability.Invalid);
            for (int i = 0; i < result.ContactsWritten; i++)
                if (!scratchContacts[i].IsValid || !SameWorld(scratchContacts[i].Space))
                    return NoCoverage(MovementAvailability.Invalid);

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
