using System.Numerics;
using System.Linq;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPhysicalEvidenceTests(ITestOutputHelper output)
{
    // #1711. Area views retain the terrain policy through later source ownership installation and rebinding.
    [Theory]
    [InlineData("source")]
    [InlineData("view")]
    [InlineData("attach")]
    [InlineData("rebind")]
    [InlineData("view-attach")]
    [InlineData("view-rebind")]
    [InlineData("loaded")]
    public void TerrainDropPreviewAndRealFollowerShareTheCurrentSourceOwner(string ownership)
    {
        using var fixture = new ContactTerrainFixture(4, terrainEvidence: true);
        var tuning = ContactTerrainFixture.Tuning;
        var baked = fixture.Capture.BuildProfile(tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true });
        if (ownership == "loaded")
        {
            var sources = new NavBakeSources().Add("world", new byte[32]);
            var profiles = new[] { new NavBakeProfile("player", tuning, default) { MaxDropMetres = 6, AllowTerrainDescents = true } };
            var bake = GroundNavigationBake.Create(fixture.Capture, sources, profiles);
            using var stream = new MemoryStream();
            bake.WriteTo(stream);
            stream.Position = 0;
            var loaded = GroundNavigationBake.Load(stream, new(fixture.Capture.Options, sources, profiles));
            Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
            baked = loaded.Bake!.GetProfile("player");
        }
        var terrainLinks = baked.Graph.Links.Where(link => baked.Terrain!.Route(link) == ContactDropRoute.Terrain).ToArray();
        Assert.NotEmpty(terrainLinks);
        var source = ownership.Contains("attach")
            ? new GroundNavigation(baked.Graph, tuning, baked.Footprint, false, 6, true) : baked;
        var navigation = ownership == "source" ? source : source.ExcludingAreas(1).ExcludingAreas(2);
        if (ownership.Contains("attach"))
            (ownership == "view-attach" ? navigation : source).AttachTerrain(baked.Terrain!.Evidence, terrainLinks, baked.Terrain.SlopeCos);
        if (ownership.Contains("rebind"))
            (ownership == "view-rebind" ? navigation : source).RebindTerrainEvidence(baked.Terrain!.Evidence);
        Assert.NotNull(source.Terrain);
        Assert.Equal(0, source.Terrain.CertificateCountForTests);
        navigation.PrepareTerrainDescents();
        Assert.Equal(terrainLinks.Length, source.Terrain.CertificateCountForTests);

        var link = terrainLinks.Single(link => ContactDropProof.Feet(source.Space.Layers[link.FromLayer], link.FromX, link.FromZ) == fixture.From &&
            ContactDropProof.Feet(source.Space.Layers[link.ToLayer], link.ToX, link.ToZ) == fixture.To);
        var traversal = new NavDropTraversal(link, fixture.From, fixture.To);
        var body = new MoveState { Position = fixture.From + Vector3.UnitY * tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 };
        Assert.Equal(RangeMoveStatus.Dropping, ContactDropPreview.Prove(navigation, traversal, body, tuning, false,
            1f / 30, fixture.Context, new()));
        var move = new MoveToRange(navigation);
        var target = ReachTarget.Point(fixture.To + Vector3.UnitY * tuning.CapsuleHalfHeight);
        bool dropped = false, fringe = false;
        RangeSteering steering = default;
        for (int tick = 0; tick < 160; tick++)
        {
            steering = move.Tick(body, tuning, target, 0, false, 1f / 30, fixture.Context);
            Assert.Contains(steering.Status, new[] { RangeMoveStatus.Following, RangeMoveStatus.Dropping, RangeMoveStatus.InRange });
            dropped |= steering.Status == RangeMoveStatus.Dropping;
            fringe |= dropped && body.Grounded && !navigation.Footprint.Accepts(body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight);
            if (steering.Status == RangeMoveStatus.InRange) break;
            body = NpcGroundMovement.Step(body, steering, false, 1f / 30, tuning, fixture.Context);
        }
        Assert.True(dropped);
        Assert.True(fringe);
        Assert.Equal(RangeMoveStatus.InRange, steering.Status);
        Assert.True(body.Grounded);
    }

    // #1711. A terrain certificate's full shell must respect exclusions, including outside the planner's chord shell.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerrainDropExclusionRefusesTheInteriorAndTheCertificateFringe(bool fringe)
    {
        using var fixture = new ContactTerrainFixture(4, terrainEvidence: true,
            classify: p => p.X is > 0 and < 1 && (!fringe || p.Z is > 0.5f or < -0.5f) ? 1u : 0u);
        var tuning = ContactTerrainFixture.Tuning;
        var source = fixture.Capture.BuildProfile(tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true });
        var ordinary = source.Planner.FindPath(fixture.From, fixture.To, tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, ordinary.Status);
        Assert.Contains(ordinary.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);
        var navigation = source.ExcludingAreas(1);
        var control = source.ExcludingAreas(2).Planner.FindPath(fixture.From, fixture.To, tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, control.Status);
        var link = source.Graph.Links.Single(link => ContactDropProof.Feet(source.Space.Layers[link.FromLayer], link.FromX, link.FromZ) == fixture.From &&
            ContactDropProof.Feet(source.Space.Layers[link.ToLayer], link.ToX, link.ToZ) == fixture.To);
        var traversal = new NavDropTraversal(link, fixture.From, fixture.To);
        var body = new MoveState { Position = fixture.From + Vector3.UnitY * tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 };
        Assert.Equal(RangeMoveStatus.Dropping, ContactDropPreview.Prove(source, traversal, body, tuning, false, 1f / 30, fixture.Context, new()));
        Assert.Equal(RangeMoveStatus.Blocked, ContactDropPreview.Prove(navigation, traversal, body, tuning, false, 1f / 30, fixture.Context, new()));
        var path = navigation.Planner.FindPath(fixture.From, fixture.To, tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Unreachable, path.Status);
        var move = new MoveToRange(navigation);
        var steering = move.Tick(body, tuning, ReachTarget.Point(fixture.To + Vector3.UnitY * tuning.CapsuleHalfHeight),
            0, false, 1f / 30, fixture.Context);
        Assert.Equal(RangeMoveStatus.Unreachable, steering.Status);
        Assert.Equal(Vector2.Zero, steering.WorldDirection);
    }

    // #1697. Continuous terrain keeps the sharp recipe's local horizon and independent physical gates.
    [Theory]
    [InlineData(1.3f)]
    [InlineData(4f)]
    [InlineData(6f)]
    public void ContinuousTerrainDoesNotAcquireSharpDropCandidates(float height)
    {
        using var fixture = new ContactTerrainFixture(height);
        var nav = fixture.Capture.BuildProfile(ContactTerrainFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.True(nav.Footprint.Accepts(fixture.From));
        Assert.True(nav.Footprint.Accepts(fixture.To));
        Assert.Empty(ContactDropCandidates.Generate(nav.Space.Layers, nav.Graph.Layers, ContactTerrainFixture.Tuning, 6));
        var corridor = new NavDropFootprint(nav.Footprint, ContactTerrainFixture.Tuning, fixture.From, fixture.To, 6);
        Assert.False(corridor.Certify());
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == KhaozEngine.Navigation.NavLinkKind.Drop);
    }

    // #1697. Physical slide feasibility does not satisfy the released sharp Drop's contact and first-landing gates.
    [Theory]
    [InlineData(1.3f, false)]
    [InlineData(1.3f, true)]
    [InlineData(4f, false)]
    [InlineData(4f, true)]
    [InlineData(6f, false)]
    [InlineData(6f, true)]
    public void ContinuousTerrainSlidesAndFirstSupportsOutsideTheAdmittedLanding(float height, bool run)
    {
        using var fixture = new ContactTerrainFixture(height);
        using var source = fixture.Context.CreateNavigationBinding(ContactTerrainFixture.Tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = source.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState
        { Position = fixture.From + Vector3.UnitY * ContactTerrainFixture.Tuning.CapsuleHalfHeight, Grounded = true }, frame, null);
        var footprint = new NavAreaFootprint(fixture.Capture.Columns, ContactTerrainFixture.Options, ContactTerrainFixture.Tuning, default);
        bool steep = false, supported = false, arrived = false;
        for (int slice = 0; slice < ContactTerrainFixture.Options.MaxEdgeProbeSteps; slice++)
        {
            Vector2 input = slice == 0 || arrived ? Vector2.Zero : ContactDropControl.Input(state.State,
                fixture.To, ContactTerrainFixture.Tuning, run, 1, ContactTerrainFixture.Options.EdgeProbeSeconds);
            Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var queries));
            using (queries)
            {
                var result = source.StepTowards(new(state.State, frame, null), input, run,
                    ContactTerrainFixture.Options.EdgeProbeSeconds, queries!);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                Assert.False(result.State.State.Swimming || result.ContactEnteredSwimming);
                Assert.False(result.ContactAirLanded);
                Assert.Null(result.ContactFirstAirLandingFeet);
                steep |= result.ContactUnwalkableSupport;
                Vector3 feet = result.State.State.Position - Vector3.UnitY * ContactTerrainFixture.Tuning.CapsuleHalfHeight;
                if (steep && !supported && result.ContactWalkableSupport)
                {
                    supported = true;
                    Assert.False(footprint.Accepts(feet));
                    Assert.True(feet.X < fixture.To.X);
                    Assert.Equal(0, feet.Y);
                }
                if (supported && result.ContactWalkableSupport && GroundTraversalProbe.Near(feet, fixture.To))
                {
                    Assert.True(footprint.Accepts(feet));
                    if (arrived) return;
                    arrived = true;
                }
                state = result.State;
            }
        }
        Assert.Fail("The continuous terrain path did not reach lower support and Hold the anchor inside the existing budget.");
    }

    [Fact]
    public void ExactSixMetreFallHasAnAdvancedPhysicalPathWithoutTheNavigationBoundary()
    {
        using var fixture = new ContactDropFixture(6);
        using var source = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = source.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState { Position = new(-0.25f, 6.75f, 0), Grounded = true }, frame, null);
        Vector3 target = new(0.25f, 0, 0);
        bool flew = false, landed = false;
        for (int slice = 0; slice < ContactDropFixture.Options.MaxEdgeProbeSteps; slice++)
        {
            Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var queries));
            using (queries)
            {
                var unselected = new FramedMovementState(state.State, frame, null);
                Assert.Equal(MovementAvailability.Known,
                    source.SampleWaterForPacing(unselected, queries!, out _, out float landScale).Availability);
                Vector2 input = slice == 0 || landed ? Vector2.Zero : ContactDropControl.Input(state.State,
                    target, ContactDropFixture.Tuning, false, landScale, ContactDropFixture.Options.EdgeProbeSeconds);
                var result = source.StepTowards(unselected, input, false, ContactDropFixture.Options.EdgeProbeSeconds, queries!);
                Vector3 feet = result.State.State.Position - Vector3.UnitY * ContactDropFixture.Tuning.CapsuleHalfHeight;
                output.WriteLine($"slice={slice} {result.Outcome} feet={feet} grounded={result.State.State.Grounded}");
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                Assert.False(result.State.State.Swimming);
                flew |= !result.State.State.Grounded || result.ContactAirLanded;
                if (flew && result.State.State.Grounded && GroundTraversalProbe.Near(feet, target))
                {
                    if (landed) return;
                    landed = true;
                }
                state = result.State;
            }
        }
        Assert.Fail("The physical controller did not complete the fall and post-landing Hold within 64 slices.");
    }
}
