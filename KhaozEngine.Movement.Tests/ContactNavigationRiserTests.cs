using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationRiserTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0.0425f, true)]
    [InlineData(0.1f, true)]
    [InlineData(0.15f, true)]
    [InlineData(0.2f, true)]
    [InlineData(0.3f, true)]
    [InlineData(0.5f, false)]
    public void RoutesAgreeWithLiveContactClimbing(float rise, bool climbable)
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f, WalkSpeed = 1, RunSpeed = 1 };
        using var scene = Scene(tuning, rise);
        Vector3 from = new(-0.875f, 0, 0.125f), to = new(0.875f, rise, 0.125f);
        foreach (float phase in new[] { -0.011f, 0f, 0.007f })
        {
            FramedMovementState state = scene.State(from + new Vector3(phase, tuning.CapsuleHalfHeight, 0));
            for (int tick = 0; tick < 180; tick++)
            {
                using var queries = scene.Acquire();
                MovementStepResult result = scene.Binding.StepTowards(new(state.State, state.Frame, null),
                    Vector2.UnitX, false, 1f / 60f, queries);
                Assert.Contains(result.Outcome, new[] { MovementStepOutcome.Advanced, MovementStepOutcome.Blocked });
                state = result.State;
                if (state.State.Position.X >= to.X || result.Outcome == MovementStepOutcome.Blocked) break;
            }
            bool climbed = state.State.Position.X >= to.X && state.State.Grounded &&
                MathF.Abs(state.State.Position.Y - tuning.CapsuleHalfHeight - rise) < 0.001f;
            output.WriteLine($"rise={rise}, phase={phase}, liveFeet={state.State.Position - Vector3.UnitY * tuning.CapsuleHalfHeight}, climbed={climbed}");
            Assert.Equal(climbable, climbed);
        }

        using var context = Context(scene);
        var options = Options with
        { MinX = -1.5f, MaxX = 1.5f, MinZ = -0.75f, MaxZ = 0.75f, MaxEdgeProbeSteps = 64 };
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        var footprint = new NavAreaFootprint(capture.Columns, options, tuning, default);
        using (var queries = scene.Acquire())
        {
            Assert.Equal(MovementAvailability.Known, scene.Binding.TryFindRestingAnchor(
                scene.State(new(-0.125f, tuning.CapsuleHalfHeight, 0.125f)), queries, out var anchor));
            output.WriteLine($"anchor={anchor.Feet}, support={anchor.Support.Status}, rawFootprint={footprint.Accepts(new(-0.125f, 0, 0.125f))}, anchorFootprint={footprint.Accepts(anchor.Feet)}");
        }
        foreach (PhysicsNavSurface surface in capture.Columns.GetColumn(6, 3))
            output.WriteLine($"columnAcrossRiser: {surface}");
        GroundNavigation nav = capture.BuildProfile(tuning, default);
        NavPath path = nav.Planner.FindPath(from, to, tuning.CapsuleRadius, PathQueryBudget.Default);
        output.WriteLine($"rise={rise}, route={path.Status}, besideRiser={Resting(nav, new(-0.125f, 0, 0.125f))}");
        Assert.Equal(climbable, path.Status == NavPathStatus.Complete);
        if (climbable) Assert.NotNull(Resting(nav, new(-0.125f, 0, 0.125f)));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    public void SlabKeepsForbiddenRawAndRestingFootprintAreas(float forbiddenHeight)
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f };
        using var scene = Scene(tuning, 0.2f);
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options with
        { MinX = -1.5f, MaxX = 1.5f, MinZ = -0.75f, MaxZ = 0.75f },
            sample => sample.X >= 0 && MathF.Abs(sample.Y - forbiddenHeight) < 0.001f ? 1u : 0u);
        GroundNavigation nav = capture.BuildProfile(tuning, new NavAreaFilter(0, 1));
        Assert.Null(Resting(nav, new(-0.125f, 0, 0.125f)));
        Assert.NotEqual(NavPathStatus.Complete, nav.Planner.FindPath(new Vector3(-0.875f, 0, 0.125f),
            new Vector3(0.875f, 0.2f, 0.125f), tuning.CapsuleRadius, PathQueryBudget.Default).Status);
    }

    static OrdinaryContactScene Scene(MoveTuning tuning, float rise) => new(tuning,
        extent: new(new(-4, -1, -4), new(4, 3, 4), 0, rise), install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-4, -0.25f, -4), new(4, 0, 4));
            // TileWorld walk surfaces are 0.1 m slabs, so taller risers leave captured ground below.
            Box(world, builder, "step", new(0, rise - 0.1f, -4), new(4, rise, 4));
        });
}
