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
        Assert.Throws<InvalidOperationException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 }, new NavBakeExecution(degree)));
        Assert.True(water.Generation > 0);
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
        var failure = Assert.Throws<InvalidOperationException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning,
            default, new GroundProfileOptions { MaxDropMetres = 6 }));
        Assert.True(water.Refused);
        Assert.Contains("Drop", failure.Message, StringComparison.Ordinal);
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
            DropCandidate = row => throw new InvalidOperationException($"drop-ordinal-{candidates[row.Link]}"),
        });
        foreach (int degree in new[] { 1, 2, 4 })
        {
            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning,
                default, new GroundProfileOptions { MaxDropMetres = 6 }, new NavBakeExecution(degree)));
            Assert.Equal("drop-ordinal-0", failure.Message);
        }
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
