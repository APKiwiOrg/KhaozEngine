using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    // Shared by support composition and the explicit mover. This limit belongs in policy identity.
    internal const int MaxSolidContacts = 16384;

    /// <summary>Private consumer scratch only. No result is usable without Known and complete coverage.</summary>
    internal MovementAvailability QuerySolidContacts(in MovementBodyQuery body, Span<CapsuleContact> scratch,
        out CapsuleContactResult result) => QuerySolidContacts(body, CoverageSkinMetres, scratch, out result);

    internal MovementAvailability QuerySolidContacts(in MovementBodyQuery body, float margin,
        Span<CapsuleContact> scratch, out CapsuleContactResult result)
    {
        result = default;
        AssertUsable();
        if (!_selectionReady) return MovementAvailability.Unresolved;
        if (!body.IsValid || !SameWorld(body.CurrentSpace) || !float.IsFinite(margin) ||
            margin < 0f || margin > 2f * CoverageSkinMetres) return MovementAvailability.Invalid;
        // The backend may include uncertain contacts by up to one additional skin of numerical error.
        double radial = (double)body.Radius + margin + CoverageSkinMetres;
        double vertical = (double)body.HalfHeight + margin + CoverageSkinMetres;
        Vector3 min = Witness.Scope.EnvelopeMin;
        Vector3 max = Witness.Scope.EnvelopeMax;
        if (!AxisInside(body.Centre.X, 0, radial, min.X, max.X) ||
            !AxisInside(body.Centre.Y, 0, vertical, min.Y, max.Y) ||
            !AxisInside(body.Centre.Z, 0, radial, min.Z, max.Z))
            return MovementAvailability.Unresolved;
        if (_view is not IPhysicsCapsuleContacts contacts) return MovementAvailability.Unresolved;
        try
        {
            AssertCurrent();
            var capsule = new CapsuleShape(body.Radius, 2f * (body.HalfHeight - body.Radius));
            CapsuleContactResult query = contacts.QueryCapsuleContacts(capsule, Pose.At(body.Centre),
                margin, scratch[..Math.Min(scratch.Length, MaxSolidContacts)]);
            AssertCurrent();
            if (!query.Complete)
                return query.RequiredCapacity > scratch.Length || query.RequiredCapacity > MaxSolidContacts
                    ? MovementAvailability.CapacityExceeded : MovementAvailability.Unresolved;
            if (query.Written < 0 || query.Written > scratch.Length || query.Written > MaxSolidContacts ||
                query.RequiredCapacity != query.Written || !float.IsFinite(query.CertifiedErrorMetres) ||
                query.CertifiedErrorMetres < 0f) return MovementAvailability.Invalid;
            if (query.CertifiedErrorMetres > CoverageSkinMetres) return MovementAvailability.Unresolved;
            foreach (CapsuleContact contact in scratch[..query.Written])
                if (!MovementEnvironmentValidation.Unit(contact.Normal) || !float.IsFinite(contact.Separation))
                    return MovementAvailability.Invalid;
            result = query;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
        catch (NotSupportedException) { return MovementAvailability.Unresolved; }
    }
}
