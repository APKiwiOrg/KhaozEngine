using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal MovementAvailability CheckContactSupportQuery(in MovementSupportRequest request, in FootSupportQuery query)
    {
        AssertUsable();
        MovementAvailability allowed = ContactSupportScope(request);
        if (allowed != MovementAvailability.Known) return allowed;
        if (!float.IsFinite(query.FootRadius) || query.FootRadius <= 0 ||
            query.Axis.X != request.Body.Centre.X || query.Axis.Y != request.Body.Centre.Z ||
            !float.IsFinite(query.FeetY)) return MovementAvailability.Invalid;
        Vector3 min = Witness.Scope.EnvelopeMin, max = Witness.Scope.EnvelopeMax;
        double radius = Math.Max(query.FootRadius, FootSupport.AxisProbeRadius);
        double band = SupportCertification.ContactBand;
        double low = (double)query.FeetY - query.ReachDown - FootSupport.BandMargin;
        double high = (double)query.FeetY + query.ReachUp + FootSupport.BandMargin + 2 * radius + FootSupport.ProbeLength;
        // Guard both float probe paths and the rounded contact poses used for neighborhood queries.
        // The largest radius alone does not enclose the thin probe's rounding at a coarse float datum.
        float bottom = query.FeetY + query.ReachUp + FootSupport.BandMargin;
        float distance = query.ReachUp + query.ReachDown + 2 * FootSupport.BandMargin;
        for (int probe = 0; probe < 2; probe++)
        {
            float r = probe == 0 ? FootSupport.AxisProbeRadius : query.FootRadius;
            float centre = bottom + r + FootSupport.ProbeLength / 2;
            float end = centre - distance;
            low = Math.Min(low, Math.Min((double)centre - distance, end) - r - FootSupport.ProbeLength / 2);
            high = Math.Max(high, (double)centre + r + FootSupport.ProbeLength / 2);
        }
        if (!AxisInside(query.Axis.X, 0, radius + band, min.X, max.X) ||
            !AxisInside(query.Axis.Y, 0, radius + band, min.Z, max.Z) || low - band < min.Y || high + band > max.Y)
            return MovementAvailability.Unresolved;
        return MovementAvailability.Known;
    }

    internal MovementAvailability FindContactSupport(in MovementSupportRequest request, in FootSupportQuery query,
        out SupportSample sample, out MovementContactSupportBinding? binding)
    {
        sample = default;
        binding = null;
        MovementAvailability allowed = CheckContactSupportQuery(request, query);
        if (allowed != MovementAvailability.Known) return allowed;
        allowed = SampleAnalyticGround(request, out MovementAnalyticGround? ground);
        if (allowed != MovementAvailability.Known) return allowed;
        try
        {
            AssertCurrent();
            SupportSample found = FootSupport.Find(ground is { } field ? (x, z) => field.Height : null,
                ground is { } plane ? (x, z) => plane.Normal : null, _view, _physics, query);
            AssertCurrent();
            if (found.Status == SupportStatus.Refused) return MovementAvailability.Unresolved;
            if (found.Status is SupportStatus.Walkable or SupportStatus.Steep)
            {
                allowed = BindContactSupportAtKernel(request, found, query.FeetY, out var owner);
                if (allowed != MovementAvailability.Known) return allowed;
                binding = owner;
            }
            sample = found;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
        catch (NotSupportedException) { return MovementAvailability.Unresolved; }
    }

    internal MovementAvailability SweepContactShell(in MovementBodyQuery shell, CapsuleShape shape, Vector3 direction, float distance,
        QueryFilter filter, out bool blocked, out SweepHit hit)
    {
        blocked = false;
        hit = default;
        MovementAvailability allowed = CheckContactShellShape(shell, shape);
        if (allowed != MovementAvailability.Known) return allowed;
        if (filter != QueryFilter.StaticsOnly || !MovementEnvironmentValidation.Finite(direction) ||
            !float.IsFinite(distance) || distance < 0) return MovementAvailability.Invalid;
        Vector3 delta = direction * distance;
        if (!_selectionReady) return MovementAvailability.Unresolved;
        if (!shell.IsValid || !SameWorld(shell.CurrentSpace)) return MovementAvailability.Invalid;
        Vector3 min = Witness.Scope.EnvelopeMin, max = Witness.Scope.EnvelopeMax;
        double radial = (double)shell.Radius + 2d * CoverageSkinMetres;
        double vertical = (double)shell.HalfHeight + 2d * CoverageSkinMetres;
        if (!AxisInside(shell.Centre.X, (double)direction.X * distance, radial, min.X, max.X) ||
            !AxisInside(shell.Centre.Y, (double)direction.Y * distance, vertical, min.Y, max.Y) ||
            !AxisInside(shell.Centre.Z, (double)direction.Z * distance, radial, min.Z, max.Z))
            return MovementAvailability.Unresolved;
        if (!MovementCapsuleRounding.TryEnclose(shell, delta, out var enclosure, out _, out _))
            return MovementAvailability.Unresolved;
        allowed = QuerySolidSweepFiltered(enclosure, delta, out CapsuleSweepResult certified, filter);
        if (allowed != MovementAvailability.Known) return allowed;
        try
        {
            AssertCurrent();
            // Certification gates the complete selected query. The shared core retains its existing hit,
            // contact normal and arithmetic, including zero-offset parity with the dry primitive.
            bool found = _view.SweepCapsule(shape, Pose.At(shell.Centre), direction, distance, out SweepHit raw, filter);
            AssertCurrent();
            if (!found && certified.Status != CapsuleSweepStatus.Clear ||
                found && certified.Status != CapsuleSweepStatus.Hit ||
                found && (!float.IsFinite(raw.Distance) || raw.Distance < 0 || raw.Distance > distance ||
                    !MovementEnvironmentValidation.Finite(raw.Normal))) return MovementAvailability.Unresolved;
            double directionLength = Math.Sqrt((double)direction.X * direction.X +
                (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
            if (found && certified.Status == CapsuleSweepStatus.Hit &&
                Math.Max(0, (double)raw.Distance - ShellMotion.ContactSkin) * directionLength > certified.ClearThroughDistance)
                return MovementAvailability.Unresolved;
            blocked = found;
            hit = raw;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
        catch (NotSupportedException) { return MovementAvailability.Unresolved; }
    }

    internal MovementAvailability PenetrateContactShell(in MovementBodyQuery shell, CapsuleShape shape, out bool overlapping, out Vector3 mtv)
    {
        overlapping = false;
        mtv = default;
        MovementAvailability allowed = CheckContactShellShape(shell, shape);
        if (allowed != MovementAvailability.Known) return allowed;
        CapsuleContact[] storage = ArrayPool<CapsuleContact>.Shared.Rent(64);
        try
        {
            allowed = QuerySolidContactsFiltered(shell, 0, storage.AsSpan(0, 64), out _, QueryFilter.StaticsOnly);
            if (allowed == MovementAvailability.CapacityExceeded)
            {
                // Retry once at the fixed proof capacity, never use an incomplete contact prefix.
                CapsuleContact[] larger = ArrayPool<CapsuleContact>.Shared.Rent(MaxSolidContacts);
                ArrayPool<CapsuleContact>.Shared.Return(storage, clearArray: true);
                storage = larger;
                allowed = QuerySolidContactsFiltered(shell, 0, storage.AsSpan(0, MaxSolidContacts), out _, QueryFilter.StaticsOnly);
            }
            if (allowed != MovementAvailability.Known) return allowed;
            AssertCurrent();
            bool found = _view.ComputePenetration(shape, Pose.At(shell.Centre), out Vector3 raw);
            AssertCurrent();
            if (!MovementEnvironmentValidation.Finite(raw)) return MovementAvailability.Invalid;
            overlapping = found;
            mtv = raw;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
        catch (NotSupportedException) { return MovementAvailability.Unresolved; }
        finally { ArrayPool<CapsuleContact>.Shared.Return(storage, clearArray: true); }
    }

    internal MovementAvailability CheckContactBodyPath(in MovementBodyQuery body, Vector3 delta)
    {
        AssertUsable();
        if (!_selectionReady) return MovementAvailability.Unresolved;
        if (!body.IsValid || !SameWorld(body.CurrentSpace) || !MovementEnvironmentValidation.Finite(delta))
            return MovementAvailability.Invalid;
        Vector3 min = Witness.Scope.EnvelopeMin, max = Witness.Scope.EnvelopeMax;
        double radial = (double)body.Radius + CoverageSkinMetres;
        double vertical = (double)body.HalfHeight + CoverageSkinMetres;
        if (!AxisInside(body.Centre.X, delta.X, radial, min.X, max.X) ||
            !AxisInside(body.Centre.Y, delta.Y, vertical, min.Y, max.Y) ||
            !AxisInside(body.Centre.Z, delta.Z, radial, min.Z, max.Z)) return MovementAvailability.Unresolved;
        try { AssertCurrent(); return MovementAvailability.Known; }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
    }

    MovementAvailability CheckContactShellShape(in MovementBodyQuery shell, CapsuleShape shape)
    {
        AssertUsable();
        if (!_selectionReady) return MovementAvailability.Unresolved;
        if (!shell.IsValid || !SameWorld(shell.CurrentSpace) || shape is null ||
            !float.IsFinite(shape.Radius) || shape.Radius <= 0 || !float.IsFinite(shape.Length) || shape.Length < 0 ||
            shape.Radius > shell.Radius || (double)shape.Radius + (double)shape.Length / 2 > shell.HalfHeight)
            return MovementAvailability.Invalid;
        return MovementAvailability.Known;
    }
}
