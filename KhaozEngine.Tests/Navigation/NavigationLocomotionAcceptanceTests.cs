using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// The feature-level acceptance bar: the "wolf behind the rock" scenario, end to end. A
/// <see cref="PathFollower"/> planning over a real <see cref="NavGrid"/> feeds its per-tick
/// <see cref="PathFollowOutput.WorldDir"/> into the REAL <see cref="CharacterMovement.StepTowards"/>,
/// resolved against a REAL <see cref="BepuPhysicsWorld"/> holding a rock cylinder between the agent and
/// its goal. Straight-line steering pins the agent on the rock (the control below), so the whole
/// navigation stack (grid bake, clearance, A*, follower) has to earn the detour that gets it around.
/// </summary>
public class NavigationLocomotionAcceptanceTests
{
    // Nav grid: 40x40 cells of 0.5 m anchored at (-2, -10), so it covers X in [-2, 18], Z in [-10, 10].
    const int GridW = 40;
    const int GridH = 40;
    const float CellSize = 0.5f;
    const float OriginX = -2f;
    const float OriginZ = -10f;

    // The rock, in the XZ plane. Nav blocks a disc of radius 2.0 around it (the 1.5 physics collider plus
    // a conservative margin, so the planned corridor stays clear of the real cylinder), physics is a solid
    // cylinder of radius 1.5 at the same spot.
    static readonly Vector2 Rock = new(4f, 0f);
    const float NavBlockRadius = 2.0f;
    const float RockColliderRadius = 1.5f;
    const float RockColliderHeight = 3f;   // base-aligned, spans [0, 3] from the flat ground up

    static readonly MoveTuning Tuning = MoveTuning.Default;   // walk 6, half-height 0.9, capsule radius 0.4
    static readonly Func<float, float, float> FlatGround = (x, z) => 0f;
    const float Dt = 1f / 30f;             // 30 Hz AI loop
    const int TickBudget = 300;            // 10 s of sim, the detour needs about 2

    // World-space centre of grid cell (cx, cz), matching NavGrid's own convention exactly.
    static Vector2 CellCenter(int cx, int cz)
        => new(OriginX + (cx + 0.5f) * CellSize, OriginZ + (cz + 0.5f) * CellSize);

    static PathFollower BuildFollower()
    {
        var grid = NavGrid.FromWalkable(GridW, GridH, CellSize, OriginX, OriginZ,
            (x, z) => Vector2.Distance(CellCenter(x, z), Rock) > NavBlockRadius);
        var planner = new GridPathPlanner(NavSpace.Single(grid));
        return new PathFollower(planner);
    }

    // A real static cylinder at the rock, added to a real Bepu world (base at ground, mirroring the pose
    // idiom in CharacterMovementStepTowardsTests: shape at Pose.At(worldPos), one Step to commit it).
    static BepuPhysicsWorld BuildRockWorld()
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new CylinderShape(RockColliderRadius, RockColliderHeight),
            Pose.At(new Vector3(Rock.X, 0f, Rock.Y)));
        world.Step(Dt);
        return world;
    }

    static MoveState StartState()
        => new() { Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f), Grounded = true };

    [Fact]
    public void Follower_SteersAgentAroundRock_ToGoalBehindIt()
    {
        var follower = BuildFollower();
        using var physics = BuildRockWorld();
        var goal = new Vector3(8f, 0f, 0f);

        MoveState state = StartState();
        float maxAbsZ = 0f;
        bool arrived = false;

        for (int i = 0; i < TickBudget; i++)
        {
            PathFollowOutput output = follower.Tick(state.Position, goal, Tuning.CapsuleRadius, Dt);
            Assert.NotEqual(PathFollowState.Unreachable, output.State);
            if (output.State == PathFollowState.Arrived)
            {
                arrived = true;
                break;
            }

            state = CharacterMovement.StepTowards(state, output.WorldDir, run: false, Dt,
                FlatGround, Tuning, world: physics);
            maxAbsZ = MathF.Max(maxAbsZ, MathF.Abs(state.Position.Z));
        }

        float finalDist = Vector2.Distance(new Vector2(state.Position.X, state.Position.Z),
            new Vector2(goal.X, goal.Z));

        Assert.True(arrived, $"follower never reported Arrived within {TickBudget} ticks (final XZ dist {finalDist:F2})");
        Assert.True(finalDist < 0.8f, $"agent did not reach the goal, final XZ dist {finalDist:F2}");
        // It detoured rather than boring through: the straight line to the goal is Z = 0, so any real
        // route around a disc of radius 2 has to swing well off that axis.
        Assert.True(maxAbsZ > 1.5f, $"agent did not detour around the rock, max |Z| was {maxAbsZ:F2}");
    }

    [Fact]
    public void StraightLineSteering_PinsAgentOnTheRock_NeverReachingTheGoal()
    {
        // The control: same 30 Hz loop and same physics rock, but steering hardcoded straight at the goal
        // instead of through the follower. Aimed dead centre at the cylinder, collide-and-slide stops the
        // agent on the near side and it never gets around, proving the scenario genuinely needs pathfinding.
        using var physics = BuildRockWorld();
        var goal = new Vector3(8f, 0f, 0f);

        MoveState state = StartState();
        float maxAbsZ = 0f;

        for (int i = 0; i < TickBudget; i++)
        {
            var toGoal = new Vector2(goal.X - state.Position.X, goal.Z - state.Position.Z);
            Vector2 straight = toGoal.LengthSquared() > 1e-6f ? Vector2.Normalize(toGoal) : Vector2.Zero;
            state = CharacterMovement.StepTowards(state, straight, run: false, Dt, FlatGround, Tuning, world: physics);
            maxAbsZ = MathF.Max(maxAbsZ, MathF.Abs(state.Position.Z));
        }

        float finalDist = Vector2.Distance(new Vector2(state.Position.X, state.Position.Z),
            new Vector2(goal.X, goal.Z));

        Assert.True(finalDist > 0.8f,
            $"straight-line steering reached the goal (final XZ dist {finalDist:F2}); the rock is not blocking as intended");
        Assert.True(state.Position.X < Rock.X,
            $"straight-line steering pushed the agent past the rock centre to x={state.Position.X:F2}; it should stall on the near side");
    }
}
