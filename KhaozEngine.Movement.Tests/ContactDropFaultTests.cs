using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropFaultTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EnvironmentRevisionDuringTheFallRefusesTheWholeBuild(int degree)
    {
        var water = new FaultField();
        using var fixture = new ContactDropFixture(water: water);
        using var hooks = ContactNavigationBuildHooks.Install(new() { DropStep = _ => water.Advance() });
        var failure = Assert.Throws<NavigationBuildException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 }, new NavBakeExecution(degree)));
        Assert.Equal(NavigationBuildRefusal.MovementLeaseIdentityChanged, failure.Reason);
        Assert.True(failure.ActualLease!.Value.EnvironmentGeneration > failure.ExpectedLease!.Value.EnvironmentGeneration);
        Assert.True(water.Generation > 0);
        hooks.Dispose();
        Assert.NotEmpty(fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 }, new NavBakeExecution(degree)).Space.Layers);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("slice")]
    [InlineData("landing")]
    public void RefusedHoldFlightAndPostLandingNeverPublish(string fault)
    {
        var water = new FaultField();
        using var fixture = new ContactDropFixture(water: water);
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            DropCandidate = _ => { if (fault == "initial") water.Armed = true; },
            DropStep = step =>
            {
                if (fault == "slice" || fault == "landing" && step.Grounded && step.Feet.Y == 0)
                    water.Armed = true;
            },
        });
        var failure = Assert.Throws<ContactDropProofException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning,
            default, new GroundProfileOptions { MaxDropMetres = 6 }));
        Assert.True(water.Refused);
        Assert.Equal(ContactDropProofFailure.StepUnavailable, failure.Reason);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, failure.Outcome);
    }

    [Fact]
    public void LowestCanonicalDropFailureWinsAcrossWorkerDegrees()
    {
        using var fixture = new ContactDropFixture();
        var ground = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        var candidates = ContactDropCandidates.Enumerate(ground.Space.Layers, ground.Graph.Layers,
            ContactDropFixture.Tuning, 6).Select((link, index) => (link, index)).ToDictionary(row => row.link, row => row.index);
        Assert.NotEmpty(candidates);
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            DropCandidate = row => throw new DropCandidateFailure(candidates[row.Link]),
        });
        foreach (int degree in new[] { 1, 2, 4 })
        {
            var failure = Assert.Throws<DropCandidateFailure>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning,
                default, new GroundProfileOptions { MaxDropMetres = 6 }, new NavBakeExecution(degree)));
            Assert.Equal(0, failure.Ordinal);
        }
    }

    private sealed class DropCandidateFailure(int ordinal) : InvalidOperationException
    {
        internal int Ordinal { get; } = ordinal;
    }

    private sealed class FaultField : IMovementWaterField
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -9, -10);
        long _revision;
        internal bool Armed, Refused;
        internal void Advance() => Interlocked.Increment(ref _revision);
        public string SemanticIdentity => "drop-refusal-field";
        public long Generation => Volatile.Read(ref _revision);
        public MovementWaterExtent Extent => _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin) => _inner.SampleCentreWater(body, origin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            if (!Armed) return _inner.TraceWater(query, origin, identity, spans, contacts);
            Refused = true;
            return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, identity);
        }
    }
}
