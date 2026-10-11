using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    Dictionary<(MovementSpaceKey Space, Vector2 Axis), MovementAnalyticGround?>? _analyticGround;
    Dictionary<(StaticHandle Static, int Feature), MovementContactSupportBinding>? _contactOwners;

    internal MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground)
    {
        ground = null;
        AssertUsable();
        MovementAvailability allowed = ContactSupportScope(request);
        if (allowed != MovementAvailability.Known) return allowed;
        if (_pin is not IMovementContactSupportPin support) return MovementAvailability.Unresolved;
        try
        {
            AssertCurrent();
            MovementAvailability availability = support.SampleAnalyticGround(request, out MovementAnalyticGround? fact);
            AssertCurrent();
            if (!MovementEnvironmentValidation.Availability(availability)) return MovementAvailability.Invalid;
            if (availability != MovementAvailability.Known) return availability;
            Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
            if (fact is { } value && (!value.IsValid || value.Axis != axis || value.Identity != Identity ||
                !LegalContactSpace(value.Space, value.TraversedLinkId, request.Transition) ||
                !Witness.Scope.ContainsBounds(new(axis.X, value.Height, axis.Y), new(axis.X, value.Height, axis.Y))))
                return MovementAvailability.Invalid;
            var key = (request.Body.CurrentSpace, axis);
            if (_analyticGround is { } previous && previous.TryGetValue(key, out var remembered))
            {
                if (!SameAnalyticField(remembered, fact)) return MovementAvailability.Invalid;
            }
            else
            {
                if (_analyticGround?.Count == MaxSupportCandidates) return MovementAvailability.CapacityExceeded;
                (_analyticGround ??= new()).Add(key, fact);
            }
            ground = fact;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
    }

    internal MovementAvailability BindContactSupport(in MovementSupportRequest request, in SupportSample sample,
        out MovementContactSupportBinding binding)
    {
        binding = default;
        AssertUsable();
        MovementAvailability allowed = ContactSupportScope(request);
        if (allowed != MovementAvailability.Known) return allowed;
        if (_pin is not IMovementContactSupportPin support) return MovementAvailability.Unresolved;
        if (sample.Status is not (SupportStatus.Walkable or SupportStatus.Steep)) return MovementAvailability.Invalid;
        try
        {
            var boundRequest = new MovementContactSupportRequest(request.Body, request.Transition,
                new(request.Body.Centre.X, request.Body.Centre.Z), sample.Height, sample.HeightError,
                sample.Normal, sample.Witness, sample.Static, sample.FeatureId);
            double feet = (double)request.Body.Centre.Y - request.Body.HalfHeight;
            if ((double)sample.Height - sample.HeightError > feet + request.MaxRise + CoverageSkinMetres ||
                (double)sample.Height + sample.HeightError < feet - request.MaxDrop - CoverageSkinMetres)
                return MovementAvailability.Invalid;
            if (!Witness.Scope.ContainsBounds(sample.Witness, sample.Witness) ||
                !AxisInside(sample.Height, 0, sample.HeightError, Witness.Scope.EnvelopeMin.Y, Witness.Scope.EnvelopeMax.Y))
                return MovementAvailability.Unresolved;
            AssertCurrent();
            MovementAvailability availability = support.BindContactSupport(boundRequest, out MovementContactSupportBinding result);
            AssertCurrent();
            if (!MovementEnvironmentValidation.Availability(availability)) return MovementAvailability.Invalid;
            if (availability != MovementAvailability.Known) return availability;
            if (!result.IsValid || result.Identity != Identity ||
                !LegalContactSpace(result.Space, result.TraversedLinkId, request.Transition))
                return MovementAvailability.Invalid;
            if (sample.Static is { } installed)
            {
                var key = (installed, sample.FeatureId);
                if (_contactOwners is { } previous && previous.TryGetValue(key, out var remembered))
                {
                    if (remembered.Owner != result.Owner || remembered.Space != result.Space)
                        return MovementAvailability.Invalid;
                }
                else
                {
                    if (_contactOwners?.Count == MaxSupportCandidates) return MovementAvailability.CapacityExceeded;
                    (_contactOwners ??= new()).Add(key, result);
                }
            }
            else
            {
                var key = (request.Body.CurrentSpace, boundRequest.Axis);
                if (_analyticGround is null || !_analyticGround.TryGetValue(key, out var fact) || fact is null)
                    return MovementAvailability.Unresolved;
                MovementAnalyticGround value = fact.Value;
                if (value.Owner != result.Owner || value.Space != result.Space || value.Normal != sample.Normal ||
                    Math.Abs((double)value.Height - sample.Height) > sample.HeightError)
                    return MovementAvailability.Invalid;
            }
            binding = result;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
    }

    bool LegalContactSpace(MovementSpaceKey space, string? link, in MovementTransitionContext transition) =>
        SameWorld(space) && (space == transition.OriginSpace || link is not null);

    static bool SameAnalyticField(MovementAnalyticGround? first, MovementAnalyticGround? second) =>
        first is { } a && second is { } b
            ? a.Axis == b.Axis && a.Height == b.Height && a.Normal == b.Normal && a.Owner == b.Owner &&
                a.Space == b.Space && a.Identity == b.Identity
            : first.HasValue == second.HasValue;

    MovementAvailability ContactSupportScope(in MovementSupportRequest request)
    {
        if (!_selectionReady) return MovementAvailability.Unresolved;
        if (!request.IsValid || !SameWorld(request.Body.CurrentSpace)) return MovementAvailability.Invalid;
        MovementQueryScope scope = Witness.Scope;
        if (request.MaxRise > scope.MaxRise || request.MaxDrop > scope.MaxDrop ||
            !scope.ContainsBounds(request.Transition.StartFeet, request.Transition.StartFeet))
            return MovementAvailability.Unresolved;
        double radial = (double)request.Body.Radius + CoverageSkinMetres;
        double vertical = (double)request.Body.HalfHeight + CoverageSkinMetres;
        Vector3 centre = request.Body.Centre, min = scope.EnvelopeMin, max = scope.EnvelopeMax;
        if (!AxisInside(centre.X, 0, radial, min.X, max.X) || !AxisInside(centre.Z, 0, radial, min.Z, max.Z) ||
            (double)centre.Y - vertical - request.MaxDrop < min.Y ||
            (double)centre.Y + vertical + request.MaxRise > max.Y)
            return MovementAvailability.Unresolved;

        // Cover both proposal capsules, their vertical overshoot and the neighborhood contact band.
        // A context using a larger custom footprint must additionally guard its exact probe shape.
        double probe = Math.Max(request.Body.Radius, FootSupport.AxisProbeRadius);
        double band = SupportCertification.ContactBand;
        double feet = (double)centre.Y - request.Body.HalfHeight;
        if (!AxisInside(centre.X, 0, probe + band, min.X, max.X) ||
            !AxisInside(centre.Z, 0, probe + band, min.Z, max.Z) ||
            feet - request.MaxDrop - FootSupport.BandMargin - band < min.Y ||
            feet + request.MaxRise + FootSupport.BandMargin + 2 * probe + FootSupport.ProbeLength + band > max.Y)
            return MovementAvailability.Unresolved;
        return MovementAvailability.Known;
    }
}
