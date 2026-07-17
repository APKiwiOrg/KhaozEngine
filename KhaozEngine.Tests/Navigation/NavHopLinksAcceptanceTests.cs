using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// Feature-level acceptance for same-grid vertical hop links, end to end the way a game composes the
/// pieces: bake a <see cref="NavSpace"/> over a scripted surface with an isolated standable top
/// (<see cref="NavGridBaker.BakeOverworldHops"/>), plan a route onto it (<see cref="GridPathPlanner"/>),
/// and drive a <see cref="PathFollower"/> tick by tick along that real path. The top is a raised mesa
/// whose rim erodes to blocked under the step bake, so the interior is reachable only by crossing a
/// bake-generated <see cref="NavLinkKind.Hop"/> link. The main test proves the route completes across
/// exactly one hop landing and the follower surfaces the traversal seam (Following, then Hopping with the
/// correct takeoff and landing, then resume and Arrive). A negative control (jump below the rise) and a
/// determinism check bound it.
/// </summary>
public class NavHopLinksAcceptanceTests
{
    const float StepHeight = 0.5f;
    const float AgentHeight = 0f;
    const float JumpHeight = 1.2f;
    const float AgentRadius = 0.2f;
    const float MesaTop = 1.0f;

    // Region and cell size shared by every scenario here (a 12x12 grid of unit cells anchored at origin).
    const float MinX = 0f, MinZ = 0f, MaxX = 12f, MaxZ = 12f, CellSize = 1f;

    // A 5x5 raised mesa (world x, z in [4, 9)) at MesaTop, flat ground at height 0 elsewhere. The step bake
    // erodes the mesa rim (cells 4 and 8 border a height-0 neighbor and drop more than StepHeight), leaving a
    // standable 3x3 interior (cells 5..7) whose center cell (6, 6) is an isolated island reachable ONLY by a
    // hop across the one-cell blocked rim. Same shape family as the BakeOverworldHops and GridPathPlannerHop
    // tests, widened from 3x3 so a walk approach remains on the top after the landing.
    static bool IsMesa(float x, float z) => x >= 4f && x < 9f && z >= 4f && z < 9f;

    static DelegateSurfaceProvider MesaProvider()
        => new((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            h = IsMesa(x, z) ? MesaTop : 0f;
            return true;
        });

    static NavSpace MesaSpace(float jumpHeight = JumpHeight)
        => NavGridBaker.BakeOverworldHops(
            MesaProvider(), MinX, MinZ, MaxX, MaxZ,
            cellSize: CellSize, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: jumpHeight);

    // The ground approach start, in a clear low corner well away from the rim.
    static readonly Vector3 GroundStart = new(1.5f, 0f, 1.5f);

    // Kinematic drive: a small per-tick step (well under the follower's 0.6 accept radius, so the agent
    // never overshoots a waypoint) and a bounded tick count so a regression that never surfaces Hopping or
    // never arrives fails loudly instead of spinning.
    const float MoveStep = 0.15f;
    const float Dt = 1f / 60f;
    const int TickBudget = 600;

    [Fact]
    public void HopChain_BakeToPlanToFollow_CrossesIsolatedTop()
    {
        // 1. Bake the space. The isolated top yields hop links, and every generated link is a Hop.
        NavSpace space = MesaSpace();
        Assert.Single(space.Layers);
        Assert.NotEmpty(space.Links);
        foreach (NavLink link in space.Links)
            Assert.Equal(NavLinkKind.Hop, link.Kind);

        NavGrid grid = space.Layers[0];

        // 2. Resolve the mesa's passable interior center cell and confirm it stands at the mesa top.
        (int centerCx, int centerCz) = grid.CellOf(6.5f, 6.5f);
        float? centerHeight = grid.SurfaceHeightAt(centerCx, centerCz);
        Assert.NotNull(centerHeight);
        Assert.Equal(MesaTop, centerHeight!.Value);

        Vector2 goalXz = grid.CellCenter(centerCx, centerCz);
        var goal = new Vector3(goalXz.X, MesaTop, goalXz.Y);

        // 3. Plan the route. It must complete by crossing exactly one hop landing onto the top, and the hop
        // landing must be its own waypoint distinct from a preceding Walk takeoff.
        var planner = new GridPathPlanner(space);
        NavPath path = planner.FindPath(GroundStart, goal, AgentRadius);
        Assert.Equal(NavPathStatus.Complete, path.Status);

        int hopIdx = -1;
        int hopCount = 0;
        for (int i = 0; i < path.Waypoints.Count; i++)
        {
            if (path.Waypoints[i].Kind == NavWaypointKind.Hop)
            {
                hopCount++;
                if (hopIdx < 0) hopIdx = i;
            }
        }
        Assert.Equal(1, hopCount);
        Assert.True(hopIdx >= 1, "the hop landing must follow a distinct Walk takeoff waypoint");
        Assert.Equal(NavWaypointKind.Walk, path.Waypoints[hopIdx - 1].Kind);

        Vector2 expectedTakeoff = path.Waypoints[hopIdx - 1].Position;
        Vector2 expectedLanding = path.Waypoints[hopIdx].Position;
        Assert.NotEqual(expectedTakeoff, expectedLanding);

        // The crossing genuinely spans the rim: the takeoff cell stands on the low ground (height 0) and the
        // landing cell stands on the elevated interior (the mesa top).
        (int takeoffCx, int takeoffCz) = grid.CellOf(expectedTakeoff.X, expectedTakeoff.Y);
        (int landingCx, int landingCz) = grid.CellOf(expectedLanding.X, expectedLanding.Y);
        float? takeoffHeight = grid.SurfaceHeightAt(takeoffCx, takeoffCz);
        float? landingHeight = grid.SurfaceHeightAt(landingCx, landingCz);
        Assert.NotNull(takeoffHeight);
        Assert.NotNull(landingHeight);
        Assert.Equal(0f, takeoffHeight!.Value);
        Assert.Equal(MesaTop, landingHeight!.Value);

        // 4. Drive a follower over the same planner. While Following, step the agent toward the active
        // waypoint until the follower surfaces the hop.
        var follower = new PathFollower(planner);
        Vector2 posXz = new Vector2(GroundStart.X, GroundStart.Z);

        bool observedHop = false;
        Vector2 seenHopStart = Vector2.Zero;
        Vector2 seenLanding = Vector2.Zero;
        for (int tick = 0; tick < TickBudget; tick++)
        {
            var position = new Vector3(posXz.X, 0f, posXz.Y);
            PathFollowOutput output = follower.Tick(position, goal, AgentRadius, Dt);
            Assert.NotEqual(PathFollowState.Unreachable, output.State);
            Assert.NotEqual(PathFollowState.Arrived, output.State); // must hop before it can arrive

            if (output.State == PathFollowState.Hopping)
            {
                // Ground steering is suspended while the consumer drives the lunge: a leaked nonzero
                // steering vector here is a production regression.
                Assert.Equal(Vector2.Zero, output.WorldDir);
                observedHop = true;
                seenHopStart = output.HopStart;
                seenLanding = output.ActiveWaypoint;
                break;
            }

            // Following: WorldDir is a real steer direction, and the agent walks along it toward the active
            // waypoint, capped so it never overshoots.
            Assert.Equal(PathFollowState.Following, output.State);
            Assert.True(output.WorldDir.LengthSquared() > 0.9f, "Following must steer with a unit direction");
            Vector2 toWaypoint = output.ActiveWaypoint - posXz;
            float distance = toWaypoint.Length();
            if (distance > 1e-6f)
                posXz += toWaypoint / distance * MathF.Min(MoveStep, distance);
        }

        Assert.True(observedHop, "the follower never surfaced Hopping while crossing onto the isolated top");

        // The seam pins the traversal by value, not just by state name: both lunge ends are the planned
        // takeoff and landing cell centers (steering suspension was asserted inside the Hopping branch).
        Assert.Equal(expectedTakeoff, seenHopStart);
        Assert.Equal(expectedLanding, seenLanding);

        // 5. The consumer completes the lunge: place the agent at the landing. The follower must advance past
        // it, resuming Following (toward the remaining top approach) or Arrived, never stuck in Hopping.
        posXz = seenLanding;
        bool checkedResume = false;
        bool arrived = false;
        for (int tick = 0; tick < TickBudget; tick++)
        {
            var position = new Vector3(posXz.X, MesaTop, posXz.Y);
            PathFollowOutput output = follower.Tick(position, goal, AgentRadius, Dt);
            Assert.NotEqual(PathFollowState.Unreachable, output.State);

            if (!checkedResume)
            {
                checkedResume = true;
                Assert.NotEqual(PathFollowState.Hopping, output.State);
                Assert.True(
                    output.State is PathFollowState.Following or PathFollowState.Arrived,
                    $"after the lunge the follower reported {output.State}, expected Following or Arrived");
            }

            if (output.State == PathFollowState.Arrived)
            {
                arrived = true;
                break;
            }

            // Exactly one hop link is crossed on this route, so no later tick may re-enter Hopping.
            Assert.Equal(PathFollowState.Following, output.State);
            Vector2 toWaypoint = output.ActiveWaypoint - posXz;
            float distance = toWaypoint.Length();
            if (distance > 1e-6f)
                posXz += toWaypoint / distance * MathF.Min(MoveStep, distance);
        }

        // 6. The top was actually reached: the agent's final position is within the accept radius of the goal.
        Assert.True(arrived, "the follower never reported Arrived on the mesa top");
        float finalDist = Vector2.Distance(posXz, goalXz);
        Assert.True(
            finalDist <= PathFollowConfig.Default.AcceptRadius,
            $"agent ended {finalDist:F3} from the goal, beyond the {PathFollowConfig.Default.AcceptRadius} accept radius");
    }

    [Fact]
    public void NoHopLinks_WhenJumpBelowRise_TopStaysUnreachable()
    {
        // The negative control. jumpHeight 0.9 is above StepHeight (so the bake is legal) but below the 1.0
        // rim rise, so NavHopLinks generates no link across the rim. Without the hop the isolated top cannot
        // be routed onto: the same query never completes and no waypoint is a hop.
        const float LowJump = 0.9f;
        NavSpace space = MesaSpace(LowJump);
        Assert.Empty(space.Links);

        NavGrid grid = space.Layers[0];
        (int cx, int cz) = grid.CellOf(6.5f, 6.5f);
        float? centerHeight = grid.SurfaceHeightAt(cx, cz);
        Assert.NotNull(centerHeight);
        Assert.Equal(MesaTop, centerHeight!.Value); // the top is still standable, just cut off

        var planner = new GridPathPlanner(space);
        var goal = new Vector3(6.5f, MesaTop, 6.5f);
        NavPath path = planner.FindPath(GroundStart, goal, AgentRadius);

        Assert.NotEqual(NavPathStatus.Complete, path.Status);
        Assert.DoesNotContain(path.Waypoints, w => w.Kind == NavWaypointKind.Hop);
    }

    [Fact]
    public void Pipeline_IsDeterministic_SameBakeAndPlanTwice()
    {
        var goal = new Vector3(6.5f, MesaTop, 6.5f);

        NavSpace first = MesaSpace();
        NavSpace second = MesaSpace();

        // Same links from the same bake, in the same order.
        Assert.Equal(first.Links, second.Links);

        NavPath firstPath = new GridPathPlanner(first).FindPath(GroundStart, goal, AgentRadius);
        NavPath secondPath = new GridPathPlanner(second).FindPath(GroundStart, goal, AgentRadius);

        Assert.Equal(firstPath.Status, secondPath.Status);
        // NavWaypoint record-struct equality includes Position, Layer, and Kind, so this pins the geometry
        // and the hop marking together.
        Assert.Equal(firstPath.Waypoints, secondPath.Waypoints);
    }
}
