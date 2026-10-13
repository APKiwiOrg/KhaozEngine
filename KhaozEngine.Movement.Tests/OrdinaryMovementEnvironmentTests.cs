using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementEnvironmentTests
{
    [Fact]
    public void OrdinaryPinAuthenticatesWorldIdentityFrameAndGeneration()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var field = new ObservedField(MovementWaterField.Level("world", 0, -4));
        var tuning = OrdinaryContactScene.DefaultTuning;
        OrdinaryMovementRegistration registration;
        using (var read = world.AcquireQueryReadLease()) registration = OrdinaryMovementRegistration.CreateBuilder(
            "world", world, new(new(-20, -4, -20), new(20, 4, 20), 0, 0)).Build(read);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var provider = new OrdinaryMovementEnvironment(view, registration, tuning, (_, _) => 0, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20), field);
        var recorder = new RecordingProvider(provider);
        var context = new MovementEnvironmentContext(view, recorder, provider.Identity);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        var scope = provider.CreateScope(frame);
        var wrongWorld = new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
            "other", null, scope.Identity, frame);
        var wrongIdentity = new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
            "world", null, new("other", 1, "other"), frame);
        var wrongFrame = provider.CreateScope(new(WorldFrame.Origin, new(8, 10, -16), 2));
        Assert.Equal(MovementAvailability.Invalid, context.TryAcquire(wrongWorld, out _));
        Assert.Equal(MovementAvailability.Invalid, context.TryAcquire(wrongIdentity, out _));
        Assert.Equal(MovementAvailability.Invalid, context.TryAcquire(wrongFrame, out _));
        Assert.Equal(MovementAvailability.Known, context.TryAcquire(scope, out var lease));
        using (lease)
        {
            Assert.NotNull(recorder.Pin);
            recorder.Pin.AssertCurrent();
            field.Generation++;
            Assert.Throws<InvalidOperationException>(recorder.Pin.AssertCurrent);
        }
        Assert.Throws<ObjectDisposedException>(recorder.Pin!.AssertCurrent);
    }

    [Fact]
    public void PinConvertsSurfaceBedAndContactColumnIncludingPhysicsYOrigin()
    {
        var field = new ObservedField(MovementWaterField.Level("world", 12, 8));
        using var scene = new OrdinaryContactScene(height: (_, _) => 8,
            extent: new(new(-20, 8, -20), new(20, 12, 20), 8, 8), water: field, origin: new(8, 10, -16));
        using var lease = scene.Acquire();
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(new(1, 0, 1)), out var selection));
        var body = new MovementBodyQuery(new(1, 0, 1), 0.25f, 0.75f, selection.Space, null);
        var localPoint = lease.SampleCentreWater(body);
        Assert.Equal(2f, localPoint.Interval!.Value.UpperY);
        Assert.Equal(-2f, localPoint.Interval!.Value.LowerY);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.True(result.ContactsWritten > 0);
        Assert.Equal(new Vector2(1, 1), contacts[0].IntervalColumnXZ);
        Assert.Equal(selection.Space, contacts[0].Space);
        Assert.Equal(new MovementMediumSweepQuery(new(new(9, 10, -15), 0.25f, 0.75f, selection.Space, null),
            Vector3.Zero), field.ObservedQuery);
    }

    [Fact]
    public void TraceAtOrigin256CertifiesItsConversionErrorWithinTheBudget()
    {
        var field = new ObservedField(MovementWaterField.Level("world", 0, -4));
        using var scene = new OrdinaryContactScene(height: (_, _) => -4, water: field, origin: new(256, 0, 0),
            extent: new(new(236, -5, -20), new(276, 4, 20), -4, -4),
            boundary: MovementBoundary.Rectangle(236, -20, 276, 20));
        using var lease = scene.Acquire();
        Vector3 centre = new(0.4f, -2.1f, 0.3f);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var body = new MovementBodyQuery(centre, 0.25f, 0.75f, selection.Space, null);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = lease.TraceWater(new(body, new(0.1f, 0, 0)), spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Vector3 observed = field.ObservedQuery.Body.Centre;
        double inputError = Math.Abs(observed.X - ((double)centre.X + 256));
        Assert.True(inputError > 0);
        var reference = MovementWaterField.Level("world", 0, -4).TraceWater(field.ObservedQuery, new("reference", 1, "reference"),
            new MovementCoverageSpan[64], new MovementDomainContact[256]);
        Assert.Equal(MovementAvailability.Known, reference.Availability);
        Assert.True(result.CertifiedErrorMetres >= reference.CertifiedErrorMetres + inputError);
        Assert.True(result.CertifiedErrorMetres <= MovementQueryLease.CoverageSkinMetres);
        Assert.True(result.ContactsWritten > 0);
        Assert.Equal(-4f, contacts[0].Interval.LowerY);
        Assert.Equal(0f, contacts[0].Interval.UpperY);
    }

    [Fact]
    public void BorrowedViewsSurviveDisposalAndOwnedReadsRefuseDisposal()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var tuning = OrdinaryContactScene.DefaultTuning;
        OrdinaryMovementRegistration registration;
        using (var read = world.AcquireQueryReadLease()) registration = OrdinaryMovementRegistration.CreateBuilder(
            "world", world, new(new(-20, -4, -20), new(20, 4, 20), 0, 0)).Build(read);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        using var borrower = EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => 0, null,
            boundary, movementQueries: view);
        using var survivor = EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => 0, null,
            boundary, movementQueries: view);
        borrower.Dispose();
        borrower.Dispose();
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, survivor.TryAcquire(survivor.CreateOrdinaryScope(frame), out var lease));
        using (lease)
        {
            var state = new FramedMovementState(new MoveState { Position = new(0, 0.75f, 0), Grounded = true }, frame, null);
            Assert.Equal(MovementStepOutcome.Advanced, survivor.StepTowards(state, Vector2.UnitX, false, 0.1f, lease!).Outcome);
        }
        using var owned = EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => 0, null, boundary);
        Assert.Equal(MovementAvailability.Known, owned.TryAcquire(owned.CreateOrdinaryScope(frame), out var ownedLease));
        using (ownedLease) Assert.Throws<InvalidOperationException>(owned.Dispose);
        owned.Dispose();
        owned.Dispose();
        using var readAfter = world.AcquireQueryReadLease();
        readAfter.AssertCurrent();
    }

    [Fact]
    public void EnumerationRefusesAndColdScopesAndGenerationsRemainTruthful()
    {
        using var scene = new OrdinaryContactScene();
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        var provider = new OrdinaryMovementEnvironment(view, scene.Registration, scene.Tuning, scene.Height, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20));
        var recording = new RecordingProvider(provider);
        var context = new MovementEnvironmentContext(view, recording, provider.Identity);
        var scope = provider.CreateScope(scene.Frame);
        var clipped = new MovementQueryScope(scope.Min + Vector3.One, scope.Max, scope.MaxRise, scope.MaxDrop,
            scope.WorldId, null, scope.Identity, scope.Frame);
        Assert.Equal(MovementAvailability.Unresolved, context.TryAcquire(clipped, out _));
        Assert.Equal(MovementAvailability.Known, context.TryAcquire(scope, out var lease));
        long generation;
        using (lease)
        {
            generation = recording.Pin!.EnvironmentGeneration;
            var space = new MovementSpaceKey("world", "ordinary");
            var body = new MovementBodyQuery(new(0, 0.75f, 0), 0.25f, 0.75f, space, null);
            var enumeration = recording.Pin.EnumerateSupport(new(body, 1, 1, 0, new(space, body.Feet)), []);
            Assert.Equal(MovementAvailability.Unresolved, enumeration.Availability);
            Assert.Equal(0, enumeration.Written);
            Assert.Equal(4, recording.Pin.Witness.ResourceIds.Count);
        }
        scene.Physics.Step(1f / 30f);
        Assert.Equal(scope, provider.CreateScope(scene.Frame));
        Assert.Equal(MovementAvailability.Known, context.TryAcquire(scope, out var later));
        using (later) Assert.True(recording.Pin!.EnvironmentGeneration > generation);
    }

    [Fact]
    public void PhysicalSupportUsesRegisteredProvenanceAndStaticChangesRefuseRebinding()
    {
        using var scene = new OrdinaryContactScene(install: (physics, builder) =>
        {
            var handle = physics.AddStatic(new BoxShape(new(4, 0.1f, 4)), Pose.At(new(0, 0.1f, 0)));
            builder.Add(handle, 0, int.MaxValue, new("world", "platform"), new(-4, 0, -4), new(4, 0.2f, 4));
        });
        using (var lease = scene.Acquire())
        {
            var result = scene.Binding.StepTowards(scene.State(new(0, 0.95f, 0)), Vector2.Zero, false, 0.1f, lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            Assert.Equal(new MovementSupportKey("world", "platform"), result.State.Selection!.Value.Support);
        }
        scene.Physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(10, 0, 0)));
        Assert.Equal(MovementAvailability.Unresolved, scene.Binding.TryAcquire(scene.Scope, out _));
    }

    [Fact]
    public void GenerationChangeDuringPinCaptureCannotPublishThePreviousTupleGeneration()
    {
        var field = new ObservedField(MovementWaterField.Level("world", 0, -4));
        using var scene = new OrdinaryContactScene(water: field);
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        var provider = new OrdinaryMovementEnvironment(view, scene.Registration, scene.Tuning, scene.Height, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20), field);
        var context = new MovementEnvironmentContext(view, provider, provider.Identity);
        var scope = provider.CreateScope(scene.Frame);
        int reads = 0;
        // Advance after both preparation checks and the tuple cache read, during pin capture.
        field.OnGenerationRead = () => { if (++reads == 4) field.Generation = 1; };
        var status = context.TryAcquire(scope, out var lease);
        using (lease) Assert.Equal(MovementAvailability.Unresolved, status);
        Assert.Null(lease);
        field.OnGenerationRead = null;
        Assert.Equal(MovementAvailability.Known, context.TryAcquire(scope, out var stable));
        using (stable) stable!.AssertCurrent();
    }

    internal sealed class ObservedField(IMovementWaterField inner) : IMovementWaterField
    {
        public string SemanticIdentity => inner.SemanticIdentity;
        long _generation;
        public Action? OnGenerationRead;
        public long Generation { get { OnGenerationRead?.Invoke(); return _generation; } set => _generation = value; }
        public MovementWaterExtent Extent => inner.Extent;
        public MovementMediumSweepQuery ObservedQuery;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body) => inner.SampleCentreWater(body);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        { ObservedQuery = query; return inner.TraceWater(query, identity, spans, contacts); }
    }

    sealed class RecordingProvider(OrdinaryMovementEnvironment inner) : IMovementEnvironmentProvider
    {
        public IMovementEnvironmentPin? Pin;
        public string WorldId => inner.WorldId;
        public MovementPreparationResult Prepare(in MovementQueryScope scope) => inner.Prepare(scope);
        public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physics,
            out IMovementEnvironmentPin? pin)
        { var status = inner.TryPinPrepared(scope, physics, out pin); Pin = pin; return status; }
    }
}
