using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropLandingClassificationTests
{
    [Fact]
    public void AdversarialNextHoldSwimmingClassificationCannotPublishTheFall()
    {
        var field = new LandingWater();
        var tuning = ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var fixture = new ContactDropFixture(tuning: tuning, water: field);
        bool landed = false;
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            DropStep = step =>
            {
                if (!step.Grounded || step.Feet.Y != 0) return;
                landed = true;
                field.Wet = true;
            },
        });
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.True(landed);
        Assert.True(field.WetReads > 0);
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
    }

    // Fault injection keeps the observed revision stable, so the actual next-Hold classifier must refuse the
    // swimming result. Ordinary revision changes have their own whole-build refusal tests.
    private sealed class LandingWater : IMovementWaterField
    {
        readonly IMovementWaterField _dry = MovementWaterField.Level("world", -9, -10);
        readonly IMovementWaterField _wet = MovementWaterField.Level("world", 2, -10);
        internal bool Wet;
        internal int WetReads;
        public string SemanticIdentity => "post-landing-classification-fault";
        public long Generation => 0;
        public MovementWaterExtent Extent => _wet.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
        {
            if (Wet) WetReads++;
            return (Wet ? _wet : _dry).SampleCentreWater(body, origin);
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
            => (Wet ? _wet : _dry).TraceWater(query, origin, identity, spans, contacts);
    }
}
