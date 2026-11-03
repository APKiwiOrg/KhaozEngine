using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ContactDropAdversarialGeometryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsufficientLandingRoomAndNarrowPropEdgeRefuseRealFall(bool narrowEdge)
    {
        using var fixture = new ContactDropFixture(install: (world, builder) =>
        {
            if (narrowEdge)
                ContactNavigationFixture.Box(world, builder, "thin-prop-edge", new(0.05f, 0, 0.08f), new(0.45f, 2, 0.12f));
            else
            {
                ContactNavigationFixture.Box(world, builder, "pocket-left", new(0, 0, -1), new(0.5f, 1.5f, -0.08f));
                ContactNavigationFixture.Box(world, builder, "pocket-right", new(0, 0, 0.08f), new(0.5f, 1.5f, 1));
            }
        });
        GroundNavigation ground = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        Assert.Contains(fixture.Capture.Columns.GetColumn(3, 1).ToArray(), surface => surface.Height == 0);
        AssertRefused(fixture.Capture, fixture.Context, ground.Footprint, CandidateSpace(fixture.Capture, 0), 0);
    }

    [Fact]
    public void UnwalkableLandingCannotPublishDropEvenWhenTheTickEndsAirborne()
    {
        var tuning = ContactDropFixture.Tuning;
        using var scene = new OrdinaryContactScene(tuning, (x, _) => x < 0 ? 4 : -2 * x,
            (x, _) => x < 0 ? Vector3.UnitY : Vector3.Normalize(new(2, 1, 0)),
            extent: new(new(-20), new(20), -10, 10), install: (world, builder) =>
            {
                ContactNavigationFixture.Box(world, builder, "ledge", new(-8, 0, -8), new(0, 4, 8));
                var handle = world.AddStatic(new BoxShape(new(8, 0.1f, 8)),
                    new(new(0, -MathF.Sqrt(5) * 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.Atan(2))));
                builder.Add(handle, 0, int.MaxValue, new("world", "steep"), new(-20), new(20));
            });
        using var context = ContactNavigationFixture.Context(scene);
        var options = ContactDropFixture.Options with { MaxSlopeRadians = 1.4f };
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        float lower = Assert.Single(capture.Columns.GetColumn(3, 1).ToArray()).Height;
        Assert.True(lower < 0 && lower > -1);
        int cells = capture.Columns.Width * capture.Columns.Height;
        var heights = new float[cells];
        var blocked = Enumerable.Repeat(true, cells).ToArray();
        int from = 1 * capture.Columns.Width + 2, to = from + 1;
        heights[from] = 4;
        heights[to] = lower;
        blocked[from] = blocked[to] = false;
        var grid = NavGrid.FromBlockedSurfaces(capture.Columns.Width, capture.Columns.Height,
            options.CellSize, options.MinX, options.MinZ, blocked, heights);
        var footprint = new NavAreaFootprint(capture.Columns, options, tuning, default);
        AssertRefused(capture, context, footprint, NavSpace.Single(grid), lower, expectSteep: true);
    }

    static NavSpace CandidateSpace(PhysicsNavBake capture, float lower)
    {
        var options = capture.Options;
        int cells = capture.Columns.Width * capture.Columns.Height;
        var heights = new float[cells];
        var blocked = Enumerable.Repeat(true, cells).ToArray();
        int from = capture.Columns.Width + 2, to = from + 1;
        heights[from] = 4;
        heights[to] = lower;
        blocked[from] = blocked[to] = false;
        return NavSpace.Single(NavGrid.FromBlockedSurfaces(capture.Columns.Width, capture.Columns.Height,
            options.CellSize, options.MinX, options.MinZ, blocked, heights));
    }

    void AssertRefused(PhysicsNavBake capture, GroundMoveContext context, NavAreaFootprint footprint,
        NavSpace space, float lower, bool expectSteep = false)
    {
        int fromLayer = Enumerable.Range(0, space.Layers.Count).First(layer => space.Layers[layer].RestingHeightAt(2, 1) == 4);
        int toLayer = Enumerable.Range(0, space.Layers.Count).First(layer => space.Layers[layer].RestingHeightAt(3, 1) == lower);
        var link = new NavLink(fromLayer, 2, 1, toLayer, 3, 1) { Kind = NavLinkKind.Drop };
        Vector3 from = ContactDropProof.Feet(space.Layers[fromLayer], 2, 1), to = ContactDropProof.Feet(space.Layers[toLayer], 3, 1);
        Assert.True(footprint.Accepts(from));
        Assert.True(footprint.Accepts(to));
        Assert.True(new NavDropFootprint(footprint, ContactDropFixture.Tuning, from, to, 6).Certify());
        int candidates = 0, steps = 0;
        bool flew = false, steep = false;
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            DropCandidate = _ => candidates++,
            DropStep = step => { steps++; flew |= !step.Grounded; steep |= step.UnwalkableSupport; output.WriteLine($"{step.Outcome} feet={step.Feet} ground={step.Grounded}"); },
        });
        using var source = context.CreateNavigationBinding(ContactDropFixture.Tuning);
        using var proof = new ContactNavigationProof(capture, context, source, footprint, maxDropMetres: 6);
        Assert.False(proof.TryDrop(space, link));
        Assert.Equal(1, candidates);
        Assert.True(steps > 1);
        Assert.True(flew);
        if (expectSteep) Assert.True(steep);
    }
}
