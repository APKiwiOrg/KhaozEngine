// KhaozEngine #1270 discriminators. A 4.25 cm lip models the measured bridge deck-to-bank height difference. With the 0.3 m capsule centred
// 0.125 m off the lip's west edge, the bottom sphere rests on the lip corner, so a grounded hold leaves the feet
// lip - r + sqrt(r^2 - d^2) = 15.2 mm above the captured bank height. The 2.5 cm lip of #1253 stays clear of the
// corner.
//
// The facts named Accepts with a hypothesis comment state that a physically supported resting offset keeps its bank
// column, edges and route. They are expected to fail at the starting commit. The Refuses facts guard any repair against the deck layer
// on the bank column, an unsupported height and a resting pose a roof does not fit. Prior art is the SlopeArrivalTests
// header (#1265): a generic upward arrival band plus a grounded vertical push-out broke 7 stair tests and was reverted.

using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public partial class LowLipTraversalTests
{
    private const float RestingLip = 0.0425f;
    private const float EdgeOffset = 0.125f;
    private const float Tick = 1f / 30f;

    [Theory]
    [InlineData(RestingLip)]
    [InlineData(DeckTop)]
    public void HoldRestsTheBankBodyOnTheLipCorner(float lip)
    {
        using BepuPhysicsWorld world = LipWorld(lip);
        GroundMoveContext context = Context(world);
        Vector3 start = LipFeet(-EdgeOffset, lip);
        float rise = CornerRise(lip);

        MoveState body = NpcGroundMovement.Hold(StandingAt(start), Tick, Tuning, context);
        Vector3 rest = body.Position;

        Assert.True(body.Grounded, $"ungrounded at centre {rest}");
        Assert.InRange(new Vector2(rest.X - start.X, rest.Z - start.Z).Length(), 0f, 0.00001f);
        Assert.InRange(rest.Y - Tuning.CapsuleHalfHeight - start.Y, rise - 0.00025f, rise + 0.00025f);
        for (int tick = 0; tick < 30; tick++)
        {
            body = NpcGroundMovement.Hold(body, Tick, Tuning, context);
            Assert.True(body.Grounded, $"ungrounded at tick {tick}, centre {body.Position}");
            Assert.InRange(Vector3.Distance(body.Position, rest), 0f, 0.0001f);
        }
    }

    [Fact]
    public void ProbeHoldsTheBankBodyAtItsRestingHeight()
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        Vector3 resting = LipFeet(-EdgeOffset, RestingLip) + Vector3.UnitY * CornerRise(RestingLip);

        Assert.True(GroundTraversalProbeTests.Probe(Context(world), Tuning, resting, resting));
    }

    [Theory]
    [InlineData(-0.375f)]
    [InlineData(EdgeOffset)]
    public void ProbeAcceptsColumnsClearOfARestingLipCorner(float x)
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        Vector3 feet = LipFeet(x, RestingLip);

        Assert.True(GroundTraversalProbeTests.Probe(Context(world), Tuning, feet, feet));
    }

    // Hypothesis, expected red at the starting commit: every proof that starts or ends on the bank column expects the feet
    // within the 1 mm arrival ball of the captured height, and the hold rests them on the lip corner instead.
    [Theory]
    [InlineData(-EdgeOffset, -EdgeOffset)]
    [InlineData(-0.375f, -EdgeOffset)]
    [InlineData(-EdgeOffset, EdgeOffset)]
    [InlineData(EdgeOffset, -EdgeOffset)]
    public void ProbeAcceptsTheBankColumnBesideARestingLip(float fromX, float toX)
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        GroundMoveContext context = Context(world);
        Vector3 from = LipFeet(fromX, RestingLip), to = LipFeet(toX, RestingLip);

        Assert.True(GroundTraversalProbeTests.Probe(context, Tuning, from, to),
            $"bank hold rests the feet at {HoldFeet(context, LipFeet(-EdgeOffset, RestingLip))}");
    }

    [Fact]
    public void BakeKeepsTheBankColumnOnTheGroundLayerBesideARestingLip()
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(Context(world), Options, _ => 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        float tolerance = GroundTraversalProbe.ArrivalTolerance;
        float[] clear = NodeHeights(nav, -0.375f);
        float[] deck = NodeHeights(nav, EdgeOffset);
        float[] bank = NodeHeights(nav, -EdgeOffset);

        Assert.True(clear.Length == 1 && MathF.Abs(clear[0]) <= tolerance,
            $"clear column nodes [{string.Join(", ", clear)}]");
        Assert.True(deck.Length == 1 && MathF.Abs(deck[0] - RestingLip) <= tolerance,
            $"deck column nodes [{string.Join(", ", deck)}]");
        // Hypothesis, expected red at the starting commit: the candidate hold drops the bank column. A repair keeps one node
        // there between the captured ground and the corner rest, never at the deck height.
        Assert.True(bank.Length == 1 && bank[0] >= -tolerance && bank[0] <= CornerRise(RestingLip) + tolerance,
            $"bank column nodes [{string.Join(", ", bank)}]");
    }

    // Hypothesis, expected red at the starting commit: with the bank column dropped, no edge or route reaches the deck.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BakeRoutesAcrossARestingLip(bool reverse)
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(Context(world), Options, _ => 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3[] chain =
            [LipFeet(-0.375f, RestingLip), LipFeet(-EdgeOffset, RestingLip), LipFeet(EdgeOffset, RestingLip)];
        if (reverse) Array.Reverse(chain);

        bool onto = nav.AllowsSegment(chain[0], chain[1]), across = nav.AllowsSegment(chain[1], chain[2]);
        NavPath route = nav.Planner.FindPath(LipFeet(reverse ? 1.125f : -1.125f, RestingLip),
            LipFeet(reverse ? -1.125f : 1.125f, RestingLip), nav.AgentRadius, PathQueryBudget.Default);

        Assert.True(onto && across && route.Status == NavPathStatus.Complete,
            $"segments {onto} then {across}, route {route.Status}");
    }

    // The bank column must not take the deck height. Its body drops to the corner rest, 27 mm below that target.
    [Fact]
    public void ProbeRefusesTheBankColumnAtTheDeckHeight()
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        Vector3 deckHeight = new(-EdgeOffset, RestingLip, Row);

        Assert.False(GroundTraversalProbeTests.Probe(Context(world), Tuning, deckHeight, deckHeight));
    }

    // The corner rest height at a column the corner cannot reach, and a height above the lip beyond the step.
    [Theory]
    [InlineData(-0.375f, 0.0152f)]
    [InlineData(-EdgeOffset, 0.5f)]
    public void ProbeRefusesUnsupportedHeightsBesideARestingLip(float x, float feetY)
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        Vector3 feet = new(x, feetY, Row);

        Assert.False(GroundTraversalProbeTests.Probe(Context(world), Tuning, feet, feet));
    }

    // The roof clears a body at the captured bank height by 5 mm, so the corner rest pushes 10 mm into it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProbeRefusesTheBankColumnUnderARoofItsRestDoesNotFit(bool resting)
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        world.AddStatic(new BoxShape(new Vector3(0.5f, 0.25f, 2f)),
            Pose.At(new Vector3(-EdgeOffset, 2f * Tuning.CapsuleHalfHeight + 0.005f + 0.25f, 0f)));
        Vector3 feet = LipFeet(-EdgeOffset, RestingLip) + Vector3.UnitY * (resting ? CornerRise(RestingLip) : 0f);

        Assert.False(GroundTraversalProbeTests.Probe(Context(world), Tuning, feet, feet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveBodyCrossesARestingLipBothWays(bool reverse)
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        GroundMoveContext context = Context(world);

        MoveState body = reverse ? WalkOffTheDeck(context, RestingLip) : WalkOntoTheDeck(context, 1f, RestingLip);
        float landing = reverse ? 0f : RestingLip;

        Assert.InRange(body.Position.Y - Tuning.CapsuleHalfHeight, landing - 0.001f, landing + 0.001f);
        Assert.True(reverse ? body.Position.X < -0.2f : body.Position.X > 0.2f, $"stopped at centre {body.Position}");
    }

    // The mirror of WalkOntoTheDeck: steers from the deck toward (-1.125, Row) at 1 m/s for 60 ticks.
    private static MoveState WalkOffTheDeck(GroundMoveContext context, float lip)
    {
        MoveTuning tuning = Tuning with { WalkSpeed = 1f };
        var target = new Vector2(-1.125f, Row);
        MoveState body = StandingAt(LipFeet(1.125f, lip));

        for (int tick = 0; tick < 60; tick++)
        {
            Vector2 delta = target - new Vector2(body.Position.X, body.Position.Z);
            float distance = delta.Length();
            Vector2 direction = distance > 0f
                ? delta / distance * MathF.Min(1f, distance / (tuning.WalkSpeed * Tick))
                : Vector2.Zero;
            body = NpcGroundMovement.Step(body, new RangeSteering(direction, RangeMoveStatus.Following),
                false, Tick, tuning, context);
            Assert.True(body.Grounded, $"airborne at tick {tick}, centre {body.Position}, lip {lip}");
        }
        return body;
    }

    // Feet rise when the bottom sphere rests on the lip corner EdgeOffset from the capsule axis, zero when it clears
    // the corner.
    private static float CornerRise(float lip)
    {
        float r = Tuning.CapsuleRadius;
        return MathF.Max(0f, lip - r + MathF.Sqrt(r * r - EdgeOffset * EdgeOffset));
    }

    private static Vector3 HoldFeet(GroundMoveContext context, Vector3 feet)
        => NpcGroundMovement.Hold(StandingAt(feet), Tick, Tuning, context).Position
            - Vector3.UnitY * Tuning.CapsuleHalfHeight;

    private static MoveState StandingAt(Vector3 feet) => new()
    {
        Position = feet + Vector3.UnitY * Tuning.CapsuleHalfHeight,
        Grounded = true,
        SpeedScale = 1f,
    };

    // Heights of the accepted nodes on every layer at (x, Row).
    private static float[] NodeHeights(GroundNavigation nav, float x)
    {
        var heights = new List<float>();
        foreach (NavGrid grid in nav.Space.Layers)
        {
            (int cx, int cz) = grid.CellOf(x, Row);
            if (grid.SurfaceHeightAt(cx, cz) is float y && grid.IsPassable(cx, cz, 0f)) heights.Add(y);
        }
        return heights.ToArray();
    }

    private static Vector3 LipFeet(float x, float lip) => new(x, x > 0f ? lip : 0f, Row);
}
