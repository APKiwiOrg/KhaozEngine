using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal void BeginContactTick()
    {
        AssertUsable();
        AssertCurrent();
        Workspace.BeginTick(this);
    }

    internal void EndContactTick()
    {
        if (Workspace.Owns(this)) Workspace.EndTick();
    }

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
            SupportQueryDiagnostics.Record(SupportQueryCounter.AnalyticSamples);
            MovementAvailability availability = support.SampleAnalyticGround(request, out MovementAnalyticGround? fact);
            AssertCurrent();
            if (!MovementEnvironmentValidation.Availability(availability)) return MovementAvailability.Invalid;
            if (availability != MovementAvailability.Known) return availability;
            Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
            if (fact is { } value && (!value.IsValid || value.Axis != axis || value.Identity != Identity ||
                !LegalContactSpace(value.Space, value.TraversedLinkId, request.Transition) ||
                !Witness.Scope.ContainsBounds(new(axis.X, value.Height, axis.Y), new(axis.X, value.Height, axis.Y))))
                return MovementAvailability.Invalid;
            MovementAvailability consistency = Workspace.RememberAnalytic(request.Body.CurrentSpace, axis, fact);
            if (consistency != MovementAvailability.Known) return consistency;
            ground = fact;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
    }

    internal MovementAvailability BindContactSupport(in MovementSupportRequest request, in SupportSample sample,
        out MovementContactSupportBinding binding) => BindContactSupportCore(request, sample, out binding, null);

    internal MovementAvailability BindContactSupportAtKernel(in MovementSupportRequest request, in SupportSample sample,
        float kernelFeetY, out MovementContactSupportBinding binding) =>
        BindContactSupportCore(request, sample, out binding, kernelFeetY);

    MovementAvailability BindContactSupportCore(in MovementSupportRequest request, in SupportSample sample,
        out MovementContactSupportBinding binding, float? kernelFeetY)
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
            double actualFeet = (double)request.Body.Centre.Y - request.Body.HalfHeight;
            double feet = kernelFeetY is { } reference ? reference : actualFeet;
            // The explicit body's published feet include its vertical skin and float conversion. Its geometric
            // support band still belongs to the unchanged kernel feet, while the producer receives the real body.
            if (!double.IsFinite(feet) || Math.Abs(actualFeet - feet) > 2d * CoverageSkinMetres)
                return MovementAvailability.Invalid;
            if ((double)sample.Height - sample.HeightError > feet + request.MaxRise + CoverageSkinMetres ||
                (double)sample.Height + sample.HeightError < feet - request.MaxDrop - CoverageSkinMetres)
                return MovementAvailability.Invalid;
            if (!Witness.Scope.ContainsBounds(sample.Witness, sample.Witness) ||
                !AxisInside(sample.Height, 0, sample.HeightError, Witness.Scope.EnvelopeMin.Y, Witness.Scope.EnvelopeMax.Y))
                return MovementAvailability.Unresolved;
            AssertCurrent();
            SupportQueryDiagnostics.Record(SupportQueryCounter.Bindings);
            MovementAvailability availability = support.BindContactSupport(boundRequest, out MovementContactSupportBinding result);
            AssertCurrent();
            if (!MovementEnvironmentValidation.Availability(availability)) return MovementAvailability.Invalid;
            if (availability != MovementAvailability.Known) return availability;
            if (!result.IsValid || result.Identity != Identity ||
                !LegalContactSpace(result.Space, result.TraversedLinkId, request.Transition))
                return MovementAvailability.Invalid;
            if (sample.Static is { } installed)
            {
                MovementAvailability consistency = Workspace.RememberOwner(installed, sample.FeatureId, result);
                if (consistency != MovementAvailability.Known) return consistency;
            }
            else
            {
                if (!Workspace.TryGetAnalytic(request.Body.CurrentSpace, boundRequest.Axis, out var fact) || fact is null)
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

        // Preserve the body and reach envelope independently of the footprint's configuration.
        // CheckContactSupportQuery additionally encloses the complete admission column and float paths.
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
