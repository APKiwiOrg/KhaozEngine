using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>Proves every trial axis, shell, support admission column and medium path in one immutable
    /// ordinary interval. A failed optional proof supplies nothing to the exact solver.</summary>
    internal bool CertifyGroundRange(in FramedMovementState state, in MoveTuning tuning, float travel,
        OrdinaryMovementRegistration registration, MovementBoundary boundary, out CertifiedGroundRangeQueries? supported)
    {
        supported = null;
        AssertCurrent();
        if (_view is not IPhysicsStaticEvidence evidence ||
            !ReferenceEquals(registration.SourceWorld, _physics.SourceWorld) ||
            registration.Authenticate(_physics) != MovementAvailability.Known ||
            boundary is not AnalyticBoundary || !float.IsFinite(travel)) return false;
        if (RebuildSelection(state, out var selection) != MovementAvailability.Known) return false;
        MoveState body = state.State;
        float feet = body.Position.Y - tuning.CapsuleHalfHeight;
        // Outward axes include float position addition and the rounding in substep/shell paths. This is proof
        // enclosure only. It never widens a movement, support, medium or reach decision.
        float margin = MathF.BitIncrement(travel + 2 * CoverageSkinMetres);
        Vector2 min = new(MathF.BitDecrement(body.Position.X - margin), MathF.BitDecrement(body.Position.Z - margin));
        Vector2 max = new(MathF.BitIncrement(body.Position.X + margin), MathF.BitIncrement(body.Position.Z + margin));
        float footRadius = tuning.FootRadiusFraction * tuning.CapsuleRadius;
        float cosine = MathF.Cos(CharacterMovement.TractionGate(true, tuning));
        bool flat = _pin is IMovementFlatRangeSupportPin flatPin &&
            flatPin.CertifyFlatRegion(min, max, out float height) == MovementAvailability.Known &&
            BitConverter.SingleToInt32Bits(height) == BitConverter.SingleToInt32Bits(feet);
        SupportedGroundRegion? region = null;
        float minimumFeet = feet, maximumFeet = feet;
        if (!flat)
        {
            if (_pin is not IMovementSupportedRangeSupportPin source ||
                source.CaptureSupportedRegion(min, max, out region) != MovementAvailability.Known || region is null ||
                region.MinimumNormalY < cosine) return false;
            minimumFeet = MathF.Min(feet, region.MinimumHeight);
            maximumFeet = MathF.Max(feet, region.MaximumHeight);
            // The whole region stays in the ordinary legs band. Larger changes, including steep or missing
            // support, retain the live solver rather than predicting which substeps will cross them.
            if ((double)maximumFeet - minimumFeet >= tuning.StepHeight) return false;
        }
        var transition = new MovementTransitionContext(selection.Space, new(body.Position.X, feet, body.Position.Z));
        var query = new FootSupportQuery(new(body.Position.X, body.Position.Z), feet, footRadius,
            tuning.StepHeight, tuning.StepHeight, cosine);
        var request = new MovementSupportRequest(new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight,
            selection.Space, selection.Support), query.ReachUp, query.ReachDown, MathF.Acos(cosine), transition);
        if (FindContactSupport(request, query, out var support, out var binding) != MovementAvailability.Known ||
            support.Status != SupportStatus.Walkable || support.Static.HasValue || !binding.HasValue ||
            flat && (support.Normal != Vector3.UnitY || support.Height != feet) || support.HeightError != 0) return false;

        float radius = MathF.Max(tuning.CapsuleRadius, footRadius);
        Vector3 lower = new(MathF.BitDecrement(min.X - radius),
            flat ? feet : MathF.BitDecrement(minimumFeet - 2 * CoverageSkinMetres), MathF.BitDecrement(min.Y - radius));
        Vector3 upper = new(MathF.BitIncrement(max.X + radius),
            MathF.BitIncrement(maximumFeet + 2 * tuning.CapsuleHalfHeight + tuning.StepHeight + 2 * CoverageSkinMetres),
            MathF.BitIncrement(max.Y + radius));
        // The complete support query includes proposal paths and the slope-dependent admission column.
        // Enclose it at the four extreme axes as well as every shell recovery/lift/seat along the interval.
        for (int corner = 0; corner < (flat ? 4 : 8); corner++)
        {
            Vector2 axis = new((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y);
            var at = new Vector3(axis.X, body.Position.Y, axis.Y);
            if (boundary.Classify(at, out bool inside) != MovementAvailability.Known || !inside) return false;
            float y = (corner & 4) == 0 ? minimumFeet : maximumFeet;
            if (!SupportFootprintBounds.TryCreate(FootSupport.DescribeFootprint(query with { Axis = axis, FeetY = y }), out var bounds))
                return false;
            lower = Vector3.Min(lower, bounds.Minimum);
            upper = Vector3.Max(upper, bounds.Maximum);
        }
        if (!Witness.Scope.ContainsBounds(lower, upper)) return false;
        if (!FlatRangeRoundoff.Fits(lower, upper, Frame.PhysicsOrigin, tuning)) return false;
        bool obstruction = false;
        var captured = evidence.CaptureStaticEvidence(_physics, lower, upper, SupportFootprintResult.MaximumElements,
            (in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices) =>
            {
                // Only the selected receiver can certify its own exclusions. Registration metadata alone
                // cannot establish which faces a caller-supplied view actually selected.
                obstruction = true;
                return false;
            });
        AssertCurrent();
        if (captured != PhysicsEvidenceStatus.Complete || obstruction) return false;

        // A stationary capsule expanded by the whole horizontal travel and lift encloses every physical body
        // path. Prove strictly dry coverage, including tangency. Wet, uncertain and refused results fall back.
        float expansion = GroundPlacement.NotBelow(Math.Sqrt(2d) * margin + tuning.StepHeight + 2d * CoverageSkinMetres +
            Math.Max((double)feet - minimumFeet, (double)maximumFeet - feet));
        var enclosure = new MovementBodyQuery(body.Position,
            GroundPlacement.NotBelow((double)tuning.CapsuleRadius + expansion),
            GroundPlacement.NotBelow((double)tuning.CapsuleHalfHeight + expansion), selection.Space, selection.Support);
        Span<MovementCoverageSpan> spans = stackalloc MovementCoverageSpan[MaxCoverageSpans];
        var coverage = TraceWater(new(enclosure, Vector3.Zero), spans, Span<MovementDomainContact>.Empty);
        AssertCurrent();
        if (coverage.Availability != MovementAvailability.Known || coverage.ContactsWritten != 0) return false;
        foreach (var span in spans[..coverage.SpansWritten])
            if (!span.HasDryCoverage) return false;
        // Refusal parity is independent of geometric coverage. This same certificate also authorizes
        // DirectMoveToRange's skipped pacing point reads, not only the copied movement trials.
        if (_pin is not IMovementDryTracePointPin points ||
            !points.CanClassifyDryTracePoints(new(enclosure, Vector3.Zero))) return false;
        if (region is not null) supported = new(this, region, tuning, selection, binding.Value, min, max, lower, upper);
        return true;
    }
}
