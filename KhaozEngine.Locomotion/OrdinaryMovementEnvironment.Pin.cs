using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Non-owning evidence for one physical read and runtime registration publication.</summary>
internal sealed class OrdinaryMovementEnvironmentPin : IMovementEnvironmentPin, IMovementContactSupportPin
{
    readonly OrdinaryMovementEnvironment _owner;
    readonly IPhysicsQueryLease _physics;
    readonly (long Physics, long Registration, long Water) _tuple;
    bool _disposed;
    public IPhysicsWorldQueryView PhysicsView => _owner.Physics;
    public long GeometryGeneration { get; }
    public long EnvironmentGeneration { get; }
    public MovementFrameDescriptor Frame { get; }
    public MovementScopeWitness Witness { get; }

    internal OrdinaryMovementEnvironmentPin(OrdinaryMovementEnvironment owner, IPhysicsQueryLease physicsLease,
        in MovementQueryScope scope, MovementScopeWitness witness, long environmentGeneration)
    {
        _owner = owner;
        _physics = physicsLease;
        _tuple = owner.CaptureTuple(physicsLease);
        GeometryGeneration = physicsLease.GeometryGeneration;
        EnvironmentGeneration = environmentGeneration;
        Frame = scope.Frame;
        Witness = witness;
    }

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)
    {
        AssertCurrent();
        if (!ValidSpace(body.CurrentSpace)) return NoPoint(MovementAvailability.Invalid);
        if (_owner.WaterField is null) return new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
        float originY = Frame.PhysicsOrigin.Y;
        // The producer classifies the exact sum of the local centre and the origin.
        var point = _owner.WaterField.SampleCentreWater(body, Frame.PhysicsOrigin);
        AssertCurrent();
        if (!point.IsValid || point.Availability == MovementAvailability.Known && point.Space != body.CurrentSpace)
            return NoPoint(MovementAvailability.Invalid);
        if (point.Availability != MovementAvailability.Known || point.Interval is not { } wet) return point;
        // The outward local interval contains the local feet whenever the exact absolute feet are inside.
        if (!OrdinaryMovementEnvironment.TryToLocal(wet, originY, out MovementWaterInterval interval, out _) ||
            OrdinaryMovementEnvironment.ExactSign(body.Centre.Y, -body.HalfHeight, -interval.LowerY) < 0 ||
            OrdinaryMovementEnvironment.ExactSign(body.Centre.Y, -body.HalfHeight, -interval.UpperY) >= 0)
            return NoPoint(MovementAvailability.Unresolved);
        return new(point.Availability, point.Space, point.Domain, point.InWater, point.SpeedScale, interval);
    }

    public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates)
    {
        AssertCurrent();
        return new(MovementAvailability.Unresolved, 0, 0, _owner.Identity);
    }

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        AssertCurrent();
        if (!query.IsValid || !ValidSpace(query.Body.CurrentSpace)) return NoTrace(MovementAvailability.Invalid);
        if (_owner.WaterField is null)
        {
            if (spans.Length == 0) return new(MovementAvailability.CapacityExceeded, 0, 1, 0, 0, 0, _owner.Identity);
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, _owner.Identity);
        }
        Vector3 origin = Frame.PhysicsOrigin;
        var spanStorage = ArrayPool<MovementCoverageSpan>.Shared.Rent(Math.Max(1, spans.Length));
        var contactStorage = ArrayPool<MovementDomainContact>.Shared.Rent(Math.Max(1, contacts.Length));
        try
        {
            Span<MovementCoverageSpan> sourceSpans = spanStorage.AsSpan(0, spans.Length);
            Span<MovementDomainContact> sourceContacts = contactStorage.AsSpan(0, contacts.Length);
            sourceSpans.Clear();
            sourceContacts.Clear();
            // The producer traces the exact absolute sweep. Only reported local contact positions round here.
            var result = _owner.WaterField.TraceWater(query, origin, _owner.Identity, sourceSpans, sourceContacts);
            AssertCurrent();
            if (!result.IsValid || result.Identity.IsValid && result.Identity != _owner.Identity ||
                result.SpansWritten > spans.Length || result.ContactsWritten > contacts.Length)
                return NoTrace(MovementAvailability.Invalid);
            if (result.Availability != MovementAvailability.Known) return result;
            double conversionError = 0;
            for (int i = 0; i < result.ContactsWritten; i++)
            {
                MovementDomainContact contact = sourceContacts[i];
                if (!contact.IsValid || contact.Space != query.Body.CurrentSpace) return NoTrace(MovementAvailability.Invalid);
                if (!OrdinaryMovementEnvironment.TryToLocal(contact.Interval, origin.Y, out MovementWaterInterval interval,
                    out double intervalError))
                    return NoTrace(MovementAvailability.Unresolved);
                Vector3 column = OrdinaryMovementEnvironment.Translate(new(contact.IntervalColumnXZ.X, 0, contact.IntervalColumnXZ.Y),
                    new(-origin.X, 0, -origin.Z), out double columnError);
                conversionError = Math.Max(conversionError, intervalError + columnError);
                sourceContacts[i] = new(contact.Domain, contact.Space, interval, new(column.X, column.Z), contact.Normal,
                    contact.Fraction, contact.BoundaryId, contact.CoverageRegionHandle, contact.Overlap, contact.SpanOverlap);
            }
            float error = OrdinaryServedDomain.Upper(result.CertifiedErrorMetres + conversionError);
            if (error > MovementQueryLease.CoverageSkinMetres) return NoTrace(MovementAvailability.Unresolved);
            sourceSpans[..result.SpansWritten].CopyTo(spans);
            sourceContacts[..result.ContactsWritten].CopyTo(contacts);
            return new(result.Availability, result.SpansWritten, result.RequiredSpanCapacity, result.ContactsWritten,
                result.RequiredContactCapacity, error, result.Identity);
        }
        finally
        {
            ArrayPool<MovementCoverageSpan>.Shared.Return(spanStorage);
            ArrayPool<MovementDomainContact>.Shared.Return(contactStorage, clearArray: true);
        }
    }

    public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection)
    {
        AssertCurrent();
        selection = default;
        if (!state.IsValid || state.Frame != Frame) return MovementAvailability.Invalid;
        if (!Witness.Scope.ContainsBounds(state.State.Position, state.State.Position)) return MovementAvailability.Unresolved;
        selection = new(_owner.Space, null, _owner.Identity);
        return MovementAvailability.Known;
    }

    public MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground)
    {
        AssertCurrent();
        ground = null;
        if (!request.IsValid || !ValidSpace(request.Body.CurrentSpace)) return MovementAvailability.Invalid;
        Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
        Vector3 origin = Frame.PhysicsOrigin;
        bool localGround = _owner.GroundSpace == OrdinaryGroundSpace.Frame;
        // World horizontal delegates keep the dry adapter's rounded translation. Frame delegates keep the original operands.
        float x = localGround ? axis.X : axis.X + origin.X;
        float z = localGround ? axis.Y : axis.Y + origin.Z;
        float height = _owner.GroundHeight(x, z);
        if (!float.IsFinite(height)) return MovementAvailability.Unresolved;
        float local = localGround ? height : Contacts.SupportedCapsulePose.LocalSupport(height, origin.Y);
        if (local > Witness.Scope.EnvelopeMax.Y) return MovementAvailability.Unresolved;
        if (local < Witness.Scope.EnvelopeMin.Y) return MovementAvailability.Known;
        OrdinaryMovementExtent extent = _owner.Registration.Extent;
        double absoluteHeight = localGround ? (double)height + origin.Y : height;
        if (extent.AnalyticMinimumY is not { } lower || extent.AnalyticMaximumY is not { } upper || absoluteHeight < lower || absoluteHeight > upper)
            return MovementAvailability.Unresolved;
        Vector3 normal = _owner.GroundNormal?.Invoke(x, z) ?? Vector3.UnitY;
        if (!MovementEnvironmentValidation.Unit(normal)) return MovementAvailability.Unresolved;
        ground = new(axis, local, normal, _owner.AnalyticOwner, _owner.Space, null, _owner.Identity);
        return MovementAvailability.Known;
    }

    public MovementAvailability BindContactSupport(in MovementContactSupportRequest request, out MovementContactSupportBinding binding)
    {
        AssertCurrent();
        binding = default;
        if (!request.IsValid || !ValidSpace(request.Body.CurrentSpace) || request.Transition.OriginSpace != _owner.Space)
            return MovementAvailability.Invalid;
        MovementSupportKey owner;
        if (request.Static is { } handle)
        {
            MovementAvailability result = _owner.Registration.TryBind(_physics, handle, request.FeatureId, out owner);
            if (result != MovementAvailability.Known) return result;
        }
        else
        {
            var support = new MovementSupportRequest(request.Body, 0, 0, 0, request.Transition);
            MovementAvailability result = SampleAnalyticGround(support, out var ground);
            if (result != MovementAvailability.Known) return result;
            if (ground is null || ground.Value.Height != request.Height || ground.Value.Normal != request.Normal)
                return MovementAvailability.Unresolved;
            owner = _owner.AnalyticOwner;
        }
        binding = new(owner, _owner.Space, null, _owner.Identity);
        return MovementAvailability.Known;
    }

    public void AssertCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _physics.AssertCurrent();
        if (_physics.Origin != Frame.PhysicsOrigin || _tuple != _owner.CaptureTuple(_physics) ||
            !_owner.WaterConfigurationCurrent() || _owner.Registration.Authenticate(_physics) != MovementAvailability.Known)
            throw new InvalidOperationException("The ordinary environment pin is stale.");
    }

    public void Dispose() { if (_disposed) return; _physics.AssertCurrent(); _disposed = true; }
    bool ValidSpace(MovementSpaceKey space) => space == _owner.Space;
    static MovementWaterPoint NoPoint(MovementAvailability status) => new(status, default, null, false, 0, null);
    MovementCoverageResult NoTrace(MovementAvailability status) => new(status, 0, 0, 0, 0, 0, _owner.Identity);
}
