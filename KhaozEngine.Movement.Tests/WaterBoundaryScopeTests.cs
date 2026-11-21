using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

/// <summary>Navigation bake proofs answer every water boundary question with the full coverage solve, while runtime
/// movement may take the capture certificate. A bake that refuses today must still refuse.</summary>
public class WaterBoundaryScopeTests
{
    static MoveTuning Swimmer => Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
    static MoveTuning DropSwimmer => ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };

    // A shallow free surface over the lower floor of each Drop world, so every Drop lands in water.
    static IMovementWaterField Shallow => MovementWaterField.Level("world", 0.1f, -1);

    // The serial build, the parallel build's own worker sources, and the sharp and terrain Drop proofs each trace the
    // field and never consult the certificate.
    [Theory]
    [InlineData("hold-and-edge", 1)]
    [InlineData("hold-and-edge", 2)]
    [InlineData("sharp-drop", 1)]
    [InlineData("sharp-drop", 2)]
    [InlineData("terrain-drop", 1)]
    [InlineData("terrain-drop", 2)]
    public void ABakeTracesTheFieldAndNeverConsultsTheCertificate(string profile, int degree)
    {
        NavBakeExecution? execution = degree > 1 ? new NavBakeExecution(degree) : null;
        if (profile == "hold-and-edge")
        {
            using var world = SwimTraversalProbeTests.PoolWorld();
            var field = new ConsultedWaterField(MovementWaterField.Level("world", 0, -4));
            using var context = Context(world, ContactNavigationWaterTests.PoolHeight, field);
            using (var capture = PhysicsNavBake.Capture(context, ContactNavigationWaterTests.PoolOptions, _ => 0))
                _ = capture.BuildProfile(Swimmer, default, GroundProfileOptions.Default, execution);
            AssertFullSolve(field);
            return;
        }
        if (profile == "sharp-drop")
        {
            var field = new ConsultedWaterField(Shallow);
            using var fixture = new ContactDropFixture(tuning: DropSwimmer, water: field);
            GroundNavigation nav = fixture.Capture.BuildProfile(DropSwimmer, default,
                new GroundProfileOptions { MaxDropMetres = 6 }, execution);
            Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
            AssertFullSolve(field);
            return;
        }
        var terrainField = new ConsultedWaterField(Shallow);
        using var terrain = new ContactTerrainFixture(4, terrainEvidence: true, water: terrainField);
        GroundNavigation terrainNav = terrain.Capture.BuildProfile(ContactTerrainFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true }, execution);
        Assert.Contains(terrainNav.Graph.Links, link => terrainNav.Terrain!.Route(link) == ContactDropRoute.Terrain);
        AssertFullSolve(terrainField);
    }

    // Runtime movement and runtime Drop prediction lease under the runtime scope, so each consults the certificate.
    [Theory]
    [InlineData("ordinary-step")]
    [InlineData("sharp-drop")]
    [InlineData("terrain-drop")]
    public void ARuntimeStepAndRuntimeDropPredictionConsultTheCertificate(string runtime)
    {
        if (runtime == "ordinary-step")
        {
            using var world = SwimTraversalProbeTests.PoolWorld();
            var field = new ConsultedWaterField(MovementWaterField.Level("world", 0, -4));
            using var context = Context(world, ContactNavigationWaterTests.PoolHeight, field);
            context.ValidateTuning(Swimmer);
            EffectiveMovementBinding binding = context.ResolvedBinding;
            MovementFrameDescriptor frame = context.NavigationFrame;
            Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
            using (queries)
            {
                var state = new FramedMovementState(new MoveState
                {
                    Position = new(-2.5f, Swimmer.CapsuleHalfHeight, 0.25f),
                    Grounded = true,
                    SpeedScale = 1,
                }, frame, null);
                _ = binding.StepTowards(state, Vector2.UnitX, false, 1f / 30, queries!);
            }
            Assert.True(field.Consulted > 0);
            return;
        }
        bool terrain = runtime == "terrain-drop";
        var dropField = new ConsultedWaterField(Shallow);
        using var dropFixture = terrain ? null : new ContactDropFixture(tuning: DropSwimmer, water: dropField);
        using var terrainFixture = terrain ? new ContactTerrainFixture(4, terrainEvidence: true, water: dropField) : null;
        PhysicsNavBake capture = dropFixture?.Capture ?? terrainFixture!.Capture;
        GroundMoveContext dropContext = dropFixture?.Context ?? terrainFixture!.Context;
        MoveTuning tuning = terrain ? ContactTerrainFixture.Tuning : DropSwimmer;
        GroundNavigation nav = capture.BuildProfile(tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = terrain }, null);
        Assert.Equal(0, dropField.Consulted);
        NavLink link = nav.Graph.Links.First(candidate => candidate.Kind == NavLinkKind.Drop &&
            (!terrain || nav.Terrain!.Route(candidate) == ContactDropRoute.Terrain));
        Vector3 from = ContactDropProof.Feet(nav.Space.Layers[link.FromLayer], link.FromX, link.FromZ);
        Vector3 to = ContactDropProof.Feet(nav.Space.Layers[link.ToLayer], link.ToX, link.ToZ);
        if (terrain) nav.PrepareTerrainDescents();
        var body = new MoveState { Position = from + Vector3.UnitY * tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 };
        _ = ContactDropPreview.Prove(nav, new NavDropTraversal(link, from, to), body, tuning, false, 1f / 30,
            dropContext, new());
        Assert.True(dropField.Consulted > 0);
    }

    [Fact]
    public void BakeBindingsAndTheirProofBindingsLeaseTheFullSolve()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, ContactNavigationWaterTests.PoolHeight, MovementWaterField.Level("world", 0, -4));
        MovementFrameDescriptor frame = context.NavigationFrame;
        using var bake = context.CreateNavigationBakeBinding(Swimmer);
        using var proof = bake.CreateProofBinding();
        using var fall = bake.CreateFallProofBinding();
        using var navigation = context.CreateNavigationBinding(Swimmer);
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, Scope(bake, frame));
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, Scope(proof, frame));
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, Scope(fall, frame));
        Assert.Equal(MovementWaterBoundaryScope.Runtime, Scope(navigation, frame));
        context.ValidateTuning(Swimmer);
        Assert.Equal(MovementWaterBoundaryScope.Runtime, Scope(context.ResolvedBinding, frame));
    }

    // A bake cannot forget its scope: every navigation proof constructor refuses a runtime source, beside the same
    // construction from a bake source.
    [Theory]
    [InlineData("serial")]
    [InlineData("worker")]
    [InlineData("drop")]
    [InlineData("terrain-drop")]
    public void ANavigationProofRefusesARuntimeBindingBesideItsBakeControl(string proof)
    {
        using var fixture = new ContactDropFixture();
        using var bake = fixture.Context.CreateNavigationBakeBinding(ContactDropFixture.Tuning);
        using var runtime = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning);
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, bake.WaterBoundaryScope);
        Assert.Equal(MovementWaterBoundaryScope.Runtime, runtime.WaterBoundaryScope);
        Create(fixture, bake, proof).Dispose();
        Assert.Throws<InvalidOperationException>(() => Create(fixture, runtime, proof));
    }

    static IDisposable Create(ContactDropFixture fixture, EffectiveMovementBinding source, string proof)
    {
        var footprint = new NavAreaFootprint(fixture.Capture.Columns, ContactDropFixture.Options, ContactDropFixture.Tuning, default);
        MovementFrameDescriptor frame = fixture.Context.NavigationFrame;
        Func<Vector3, MovementQueryLease> acquire = _ => throw new UnreachableException("Construction acquires no lease.");
        return proof switch
        {
            "serial" => new ContactNavigationProof(fixture.Capture, fixture.Context, source, footprint, maxDropMetres: 6),
            "worker" => new ContactNavigationProof(fixture.Capture, ContactNavigationSnapshot.Capture(fixture.Capture, fixture.Context),
                source, footprint, ContactNavigationReusePolicy.Capture(), 6),
            "drop" => new ContactDropProof(ContactDropFixture.Options, source, frame, acquire, footprint, 6, false),
            "terrain-drop" => new ContactTerrainDropProof(ContactDropFixture.Options, source, frame, acquire, footprint, 6, false,
                null!, null!, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(proof)),
        };
    }

    static void AssertFullSolve(ConsultedWaterField field)
    {
        Assert.True(field.Traced > 0);
        Assert.Equal(0, field.Consulted);
    }

    static MovementWaterBoundaryScope Scope(EffectiveMovementBinding binding, MovementFrameDescriptor frame)
    {
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease) return lease!.WaterBoundaryScope;
    }

    // Records whether the boundary reach certificate was consulted, from any worker thread. It always declines, so
    // every answer the movement sees is the full solve's.
    sealed class ConsultedWaterField(IMovementWaterField inner) : IMovementWaterBoundaryReachProof
    {
        int _consulted, _traced;
        internal int Consulted => Volatile.Read(ref _consulted);
        internal int Traced => Volatile.Read(ref _traced);
        public string SemanticIdentity => inner.SemanticIdentity;
        public long Generation => inner.Generation;
        public MovementWaterExtent Extent => inner.Extent;

        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            inner.SampleCentreWater(body, physicsOrigin);

        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            Interlocked.Increment(ref _traced);
            return inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
        }

        public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity)
        {
            Interlocked.Increment(ref _consulted);
            return MovementWaterReach.Declined;
        }
    }
}
