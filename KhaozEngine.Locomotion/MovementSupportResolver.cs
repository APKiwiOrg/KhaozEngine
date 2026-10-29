using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

/// <summary>Canonical support provenance and the separately cleared contact-shell pose.</summary>
public readonly record struct MovementSupportPlacement(MovementSupportCandidate Candidate, Vector3 Centre);

/// <summary>Composes canonical candidates with shared footprint and shell placement under one lease.</summary>
public static class MovementSupportResolver
{
    /// <summary>Filters legal candidates before choosing the highest. Known with null means none are eligible.
    /// Placement does not prove the swept path to it. That requires the shared movement resolver.</summary>
    public static MovementAvailability Select(in MovementSupportRequest request, MovementQueryLease queries,
        out MovementSupportPlacement? placement)
    {
        MoveTuning tuning = MoveTuning.Default with
        {
            CapsuleRadius = request.Body.Radius,
            CapsuleHalfHeight = request.Body.HalfHeight,
            MaxSlopeRadians = request.MaxSlopeRadians,
        };
        return Select(request, tuning, queries, out placement);
    }

    /// <summary>Selects using the caller's shell profile, including its step height and traction band.
    /// Body dimensions and slope must match the support request. A supported pose outside the admitted
    /// precision budget returns Unresolved with no placement.</summary>
    public static MovementAvailability Select(in MovementSupportRequest request, in MoveTuning tuning,
        MovementQueryLease queries, out MovementSupportPlacement? placement)
    {
        placement = null;
        ArgumentNullException.ThrowIfNull(queries);
        if (tuning.CapsuleRadius != request.Body.Radius || tuning.CapsuleHalfHeight != request.Body.HalfHeight ||
            tuning.MaxSlopeRadians != request.MaxSlopeRadians) return MovementAvailability.Invalid;
        ContactComposition composition = ContactComposition.PublicExplicit(tuning);
        MovementSupportCandidate[] candidates = ArrayPool<MovementSupportCandidate>.Shared.Rent(MovementQueryLease.MaxSupportCandidates);
        try
        {
            Span<MovementSupportCandidate> buffer = candidates.AsSpan(0, MovementQueryLease.MaxSupportCandidates);
            buffer.Clear();
            MovementSupportSet set = queries.EnumerateSupport(request, buffer);
            if (set.Availability != MovementAvailability.Known) return set.Availability;
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

                MovementAvailability status = ClearPlacement(request, candidate, normal, tuning, queries, composition,
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
        catch (ContactQueryFailure failure) { return failure.Availability; }
        catch (ObjectDisposedException) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
        finally
        {
            ArrayPool<MovementSupportCandidate>.Shared.Return(candidates, clearArray: true);
        }
    }

    static MovementAvailability ClearPlacement(in MovementSupportRequest request, in MovementSupportCandidate candidate,
        Vector3 normal, in MoveTuning tuning, MovementQueryLease queries, in ContactComposition composition,
        out MovementSupportPlacement? placement)
    {
        placement = null;
        ShellGeometry.Validate(tuning, composition.Foot);
        var contact = new LeasedContactQueryContext(queries, tuning,
            new(candidate.Space, candidate.Owner, queries.Identity), request.Transition, composition);
        SupportSample support = ContactPlacement.Find(contact, candidate.Feet, tuning, composition,
            tuning.StepHeight, tuning.GroundedEpsilon, out var binding);
        if (support.Status != SupportStatus.Walkable || binding is null) return MovementAvailability.Unresolved;
        // A higher footprint in the legs band makes this lower candidate ineligible. Its binding
        // belongs to that higher source, so it is not a contradiction of this candidate's owner.
        if (Math.Abs((double)support.Height - candidate.Feet.Y) > support.HeightError)
            return MovementAvailability.Known;
        if (binding.Value.Owner != candidate.Owner || binding.Value.Space != candidate.Space ||
            binding.Value.TraversedLinkId != candidate.TraversedLinkId && candidate.Space != request.Body.CurrentSpace)
            return MovementAvailability.Invalid;
        if (Vector3.Dot(support.Normal, normal) < 0.9999f) return MovementAvailability.Invalid;
        Vector3 feet = candidate.Feet with { Y = support.Height };
        if (!ContactPlacement.Clear(contact, feet, tuning, composition)) return MovementAvailability.Known;
        placement = new MovementSupportPlacement(candidate, contact.SupportedBodyCentre(feet, support));
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
