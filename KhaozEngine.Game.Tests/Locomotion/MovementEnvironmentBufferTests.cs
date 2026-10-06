using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// These prove consumer buffer/lifetime validation, not native volumetric geometry or support selection.
public class MovementEnvironmentBufferTests
{
    static readonly MovementSpaceKey Space = new("world", "room");
    static readonly MovementBodyQuery Body = new(new Vector3(0f, 1f, 0f), 0.3f, 0.75f, Space, null);
    static readonly MovementSupportRequest Request = new(Body, 0.5f, 2f, 0.8f, new MovementTransitionContext(Space, Body.Feet));
    static readonly MovementMediumSweepQuery Sweep = new(Body, Vector3.UnitX);
    static readonly MovementSupportCandidate SupportSentinel = new(new MovementSupportKey("world", "sentinel"),
        Space, new Vector3(3f), Vector3.UnitY, null);
    static readonly MovementCoverageSpan SpanSentinel = new(0.9f, 1f, 0, 0, true);
    static readonly MovementDomainContact ContactSentinel = Contact("sentinel", 0.9f);

    [Fact]
    public void KnownSupportCopiesOnlyTheValidatedCompletePrefix()
    {
        using var scene = new Scene();
        scene.Environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> output) =>
        {
            output[0] = Candidate(0);
            output[1] = Candidate(1);
            return new MovementSupportSet(MovementAvailability.Known, 2, 2, scene.Identity);
        };
        var destination = Supports(3);
        MovementSupportSet result = Enumerate(scene.Lease, Request, destination);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(new[] { Candidate(0), Candidate(1), SupportSentinel }, destination);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("capacity")]
    [InlineData("identity")]
    [InlineData("over-count")]
    [InlineData("missing-entry")]
    [InlineData("stale")]
    public void RefusedSupportNeverChangesCallerSentinels(string fault)
    {
        using var scene = new Scene();
        scene.Environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> output) =>
        {
            output[0] = Candidate(0);
            if (fault == "stale") scene.Environment.Fault = "stale-pin";
            return fault switch
            {
                "unresolved" => new(MovementAvailability.Unresolved, 0, 0, scene.Identity),
                "capacity" => new(MovementAvailability.CapacityExceeded, 0, 3, scene.Identity),
                "identity" => new(MovementAvailability.Known, 1, 1, WrongIdentity),
                "over-count" => new(MovementAvailability.Known, 3, 3, scene.Identity),
                "missing-entry" => new(MovementAvailability.Known, 2, 2, scene.Identity),
                _ => new(MovementAvailability.Known, 1, 1, scene.Identity)
            };
        };
        var destination = Supports(2);
        MovementSupportSet result = Enumerate(scene.Lease, Request, destination);
        Assert.NotEqual(MovementAvailability.Known, result.Availability);
        Assert.Equal(0, result.Written);
        Assert.All(destination, candidate => Assert.Equal(SupportSentinel, candidate));
    }

    [Fact]
    public void ThrowingSupportProviderCannotLeakItsPartialWrite()
    {
        using var scene = new Scene();
        scene.Environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> output) =>
        {
            output[0] = Candidate(0);
            throw new EnvironmentAcquisitionFixture.FixtureException();
        };
        var destination = Supports(2);
        Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() => Enumerate(scene.Lease, Request, destination));
        Assert.All(destination, candidate => Assert.Equal(SupportSentinel, candidate));
    }

    [Fact]
    public void SupportOutsideTheCertifiedEnvelopeDoesNotReachTheProvider()
    {
        using var scene = new Scene();
        var request = new MovementSupportRequest(Body, 3f, 2f, 0.8f, Request.Transition);
        var destination = Supports(2);
        Assert.Equal(MovementAvailability.Unresolved, Enumerate(scene.Lease, request, destination).Availability);
        Assert.Equal(0, scene.Environment.SupportCalls);
        Assert.All(destination, candidate => Assert.Equal(SupportSentinel, candidate));
    }

    [Fact]
    public void ZeroSupportCapacityReturnsTheRequiredCountWithoutAPrefix()
    {
        using var scene = new Scene();
        scene.Environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> output) =>
            new(MovementAvailability.CapacityExceeded, 0, 2, scene.Identity);
        MovementSupportSet result = Enumerate(scene.Lease, Request, []);
        Assert.Equal(MovementAvailability.CapacityExceeded, result.Availability);
        Assert.Equal(2, result.RequiredCapacity);
        Assert.Equal(0, result.Written);
    }

    [Fact]
    public void LargeCallerBufferDoesNotExpandTheSupportWorkBudget()
    {
        using var scene = new Scene();
        scene.Environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> output) =>
        {
            Assert.Equal(256, output.Length);
            output[0] = Candidate(0);
            return new MovementSupportSet(MovementAvailability.Known, 1, 1, scene.Identity);
        };
        var destination = Supports(257);
        Assert.Equal(MovementAvailability.Known, Enumerate(scene.Lease, Request, destination).Availability);
        Assert.All(destination.Skip(1), value => Assert.Equal(SupportSentinel, value));
    }

    [Fact]
    public void KnownCoverageCopiesBothBuffersOnlyAfterValidation()
    {
        using var scene = new Scene();
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            spans[0] = new MovementCoverageSpan(0f, 1f, 0, 1, true);
            contacts[0] = Contact("river", 0f);
            return Complete(scene.Identity);
        };
        var spans = Spans(2);
        var contacts = Contacts(2);
        MovementCoverageResult result = Trace(scene.Lease, Sweep, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(new[] { new MovementCoverageSpan(0f, 1f, 0, 1, true), SpanSentinel }, spans);
        Assert.Equal(new[] { Contact("river", 0f), ContactSentinel }, contacts);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("capacity")]
    [InlineData("identity")]
    [InlineData("over-count")]
    [InlineData("missing-span")]
    [InlineData("missing-contact")]
    [InlineData("contact-range")]
    [InlineData("foreign-world")]
    [InlineData("error-budget")]
    [InlineData("stale")]
    public void RefusedCoverageNeverChangesEitherCallerBuffer(string fault)
    {
        using var scene = new Scene();
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            if (fault != "missing-span")
                spans[0] = new MovementCoverageSpan(0f, 1f, fault == "contact-range" ? 2 : 0, 1, true);
            if (fault != "missing-contact")
                contacts[0] = Contact("river", 0f, fault == "foreign-world" ? "other-world" : "world");
            if (fault == "stale") scene.Environment.Fault = "stale-pin";
            return fault switch
            {
                "unresolved" => new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0f, scene.Identity),
                "capacity" => new(MovementAvailability.CapacityExceeded, 0, 3, 0, 3, 0f, scene.Identity),
                "identity" => Complete(WrongIdentity),
                "over-count" => new(MovementAvailability.Known, 3, 3, 1, 1, 0.001f, scene.Identity),
                "error-budget" => new(MovementAvailability.Known, 1, 1, 1, 1, 0.01f, scene.Identity),
                _ => Complete(scene.Identity)
            };
        };
        var spans = Spans(2);
        var contacts = Contacts(2);
        MovementCoverageResult result = Trace(scene.Lease, Sweep, spans, contacts);
        Assert.NotEqual(MovementAvailability.Known, result.Availability);
        Assert.Equal(0, result.SpansWritten);
        Assert.Equal(0, result.ContactsWritten);
        Unchanged(spans, contacts);
    }

    [Theory]
    [InlineData(0.1f, 0.5f, 0.5f, 1f)]
    [InlineData(0f, 0.4f, 0.6f, 1f)]
    [InlineData(0f, 0.6f, 0.5f, 1f)]
    [InlineData(0f, 0.5f, 0.5f, 0.9f)]
    public void KnownCoverageCannotLeaveGapsOverlapOrOmitEndpoints(float a, float b, float c, float d)
    {
        using var scene = new Scene();
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            spans[0] = new MovementCoverageSpan(a, b, 0, 0, true);
            spans[1] = new MovementCoverageSpan(c, d, 0, 0, true);
            return new MovementCoverageResult(MovementAvailability.Known, 2, 2, 0, 0, 0.001f, scene.Identity);
        };
        var spans = Spans(2);
        var contacts = Contacts(2);
        Assert.Equal(MovementAvailability.Invalid, Trace(scene.Lease, Sweep, spans, contacts).Availability);
        Unchanged(spans, contacts);
    }

    [Fact]
    public void ThrowingCoverageProviderCannotPublishEitherPartialBuffer()
    {
        using var scene = new Scene();
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            spans[0] = new MovementCoverageSpan(0f, 1f, 0, 1, true);
            contacts[0] = Contact("river", 0f);
            throw new EnvironmentAcquisitionFixture.FixtureException();
        };
        var spans = Spans(2);
        var contacts = Contacts(2);
        Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() => Trace(scene.Lease, Sweep, spans, contacts));
        Unchanged(spans, contacts);
    }

    [Fact]
    public void WholeSweptCapsuleMustFitTheCertifiedScopeBeforeTracing()
    {
        using var scene = new Scene();
        var spans = Spans(2);
        var contacts = Contacts(2);
        var query = new MovementMediumSweepQuery(Body, new Vector3(100f, 0f, 0f));
        Assert.Equal(MovementAvailability.Unresolved, Trace(scene.Lease, query, spans, contacts).Availability);
        Assert.Equal(0, scene.Environment.CoverageCalls);
        Unchanged(spans, contacts);
    }

    [Fact]
    public void ZeroCoverageCapacityKeepsExplicitRequiredCounts()
    {
        using var scene = new Scene();
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
            new(MovementAvailability.CapacityExceeded, 0, 1, 0, 1, 0f, scene.Identity);
        MovementCoverageResult result = Trace(scene.Lease, Sweep, [], []);
        Assert.Equal(MovementAvailability.CapacityExceeded, result.Availability);
        Assert.Equal(1, result.RequiredSpanCapacity);
        Assert.Equal(1, result.RequiredContactCapacity);
    }

    [Fact]
    public void LargeCallerBuffersDoNotExpandCoverageWorkBudgets()
    {
        using var scene = new Scene();
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            Assert.Equal(64, spans.Length);
            Assert.Equal(256, contacts.Length);
            spans[0] = new MovementCoverageSpan(0f, 1f, 0, 1, true);
            contacts[0] = Contact("river", 0f);
            return Complete(scene.Identity);
        };
        var spans = Spans(65);
        var contacts = Contacts(257);
        Assert.Equal(MovementAvailability.Known, Trace(scene.Lease, Sweep, spans, contacts).Availability);
        Assert.All(spans.Skip(1), value => Assert.Equal(SpanSentinel, value));
        Assert.All(contacts.Skip(1), value => Assert.Equal(ContactSentinel, value));
    }

    static MovementSupportCandidate Candidate(int n) => new(new MovementSupportKey("world", "floor-" + n),
        Space, new Vector3(0f, 0.25f + n * 0.25f, 0f), Vector3.UnitY, null);
    static MovementDomainContact Contact(string name, float fraction, string world = "world") =>
        new(new MovementDomainKey(world, name), new MovementSpaceKey(world, "room"),
            new MovementWaterInterval(-4f, 2f, 2f, true, "bed", "surface"), new Vector2(fraction, 0f), Vector3.UnitZ, fraction, "bank", 0u);
    static MovementQueryIdentity WrongIdentity => new("closure", 1u, "wrong-scope");
    static MovementCoverageResult Complete(MovementQueryIdentity identity) =>
        new(MovementAvailability.Known, 1, 1, 1, 1, 0.001f, identity);
    static MovementSupportCandidate[] Supports(int count) => Enumerable.Repeat(SupportSentinel, count).ToArray();
    static MovementCoverageSpan[] Spans(int count) => Enumerable.Repeat(SpanSentinel, count).ToArray();
    static MovementDomainContact[] Contacts(int count) => Enumerable.Repeat(ContactSentinel, count).ToArray();
    static void Unchanged(MovementCoverageSpan[] spans, MovementDomainContact[] contacts)
    {
        Assert.All(spans, value => Assert.Equal(SpanSentinel, value));
        Assert.All(contacts, value => Assert.Equal(ContactSentinel, value));
    }

    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld _world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView _view;
        public EnvironmentAcquisitionFixture Environment { get; }
        public MovementQueryLease Lease { get; }
        public MovementQueryIdentity Identity => Environment.Identity;
        public Scene()
        {
            _view = _world.CreateQueryViewExcludingStatics([]);
            Environment = new EnvironmentAcquisitionFixture(_view);
            var result = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, result.Status);
            Lease = Assert.IsType<MovementQueryLease>(result.Lease);
        }
        public void Dispose()
        {
            try { Lease.Dispose(); }
            finally { try { _view.Dispose(); } finally { _world.Dispose(); } }
        }
    }

    static MovementSupportSet Enumerate(MovementQueryLease lease, in MovementSupportRequest request,
        Span<MovementSupportCandidate> output) => lease.EnumerateSupport(request, output);
    static MovementCoverageResult Trace(MovementQueryLease lease, in MovementMediumSweepQuery query,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) => lease.TraceWater(query, spans, contacts);
}
