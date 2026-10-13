using System;
using System.Buffers;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Non-owning environment evidence within the caller's live physical read interval.</summary>
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
        if (!OrdinaryMovementEnvironment.TryTranslateExact(body.Centre, Frame.PhysicsOrigin, out Vector3 absolute))
            return NoPoint(MovementAvailability.Unresolved);
        var point = _owner.WaterField.SampleCentreWater(new(absolute, body.Radius, body.HalfHeight, body.CurrentSpace, body.CurrentSupport));
        AssertCurrent();
        if (!point.IsValid || point.Availability == MovementAvailability.Known && point.Space != body.CurrentSpace)
            return NoPoint(MovementAvailability.Invalid);
        if (point.Availability != MovementAvailability.Known || point.Interval is null) return point;
        if (!OrdinaryMovementEnvironment.TryTranslateExact(point.Interval.Value, -Frame.PhysicsOrigin.Y, out MovementWaterInterval interval))
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
        Vector3 absolute = OrdinaryMovementEnvironment.Translate(query.Body.Centre, Frame.PhysicsOrigin, out double inputError);
        if (inputError > MovementQueryLease.CoverageSkinMetres) return NoTrace(MovementAvailability.Unresolved);
        var body = new MovementBodyQuery(absolute, query.Body.Radius, query.Body.HalfHeight, query.Body.CurrentSpace, query.Body.CurrentSupport);
        var spanStorage = ArrayPool<MovementCoverageSpan>.Shared.Rent(Math.Max(1, spans.Length));
        var contactStorage = ArrayPool<MovementDomainContact>.Shared.Rent(Math.Max(1, contacts.Length));
        try
        {
            Span<MovementCoverageSpan> sourceSpans = spanStorage.AsSpan(0, spans.Length);
            Span<MovementDomainContact> sourceContacts = contactStorage.AsSpan(0, contacts.Length);
            sourceSpans.Clear();
            sourceContacts.Clear();
            var result = _owner.WaterField.TraceWater(new(body, query.Delta), _owner.Identity, sourceSpans, sourceContacts);
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
                MovementWaterInterval interval = OrdinaryMovementEnvironment.Translate(contact.Interval, -Frame.PhysicsOrigin.Y,
                    out double intervalError);
                Vector3 column = OrdinaryMovementEnvironment.Translate(new(contact.IntervalColumnXZ.X, 0, contact.IntervalColumnXZ.Y),
                    new(-Frame.PhysicsOrigin.X, 0, -Frame.PhysicsOrigin.Z), out double columnError);
                conversionError = Math.Max(conversionError, intervalError + columnError);
                sourceContacts[i] = new(contact.Domain, contact.Space, interval, new(column.X, column.Z), contact.Normal,
                    contact.Fraction, contact.BoundaryId, contact.CoverageRegionHandle, contact.Overlap, contact.SpanOverlap);
            }
            float error = OrdinaryServedDomain.Upper(result.CertifiedErrorMetres + inputError + conversionError);
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
        Vector3 absolute = OrdinaryMovementEnvironment.Translate(new(axis.X, 0, axis.Y),
            new(Frame.PhysicsOrigin.X, 0, Frame.PhysicsOrigin.Z), out double axisError);
        if (axisError != 0) return MovementAvailability.Unresolved;
        float height = _owner.GroundHeight(absolute.X, absolute.Z);
        double local = (double)height - Frame.PhysicsOrigin.Y;
        if (!float.IsFinite(height) || local > Witness.Scope.EnvelopeMax.Y) return MovementAvailability.Unresolved;
        if (local < Witness.Scope.EnvelopeMin.Y) return MovementAvailability.Known;
        OrdinaryMovementExtent extent = _owner.Registration.Extent;
        if (extent.AnalyticMinimumY is not { } lower || extent.AnalyticMaximumY is not { } upper || height < lower || height > upper)
            return MovementAvailability.Unresolved;
        Vector3 normal = _owner.GroundNormal?.Invoke(absolute.X, absolute.Z) ?? Vector3.UnitY;
        if (!MovementEnvironmentValidation.Unit(normal)) return MovementAvailability.Unresolved;
        ground = new(axis, (float)local, normal, _owner.AnalyticOwner, _owner.Space, null, _owner.Identity);
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
