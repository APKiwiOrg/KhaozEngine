// KhaozEngine #1270 approach mechanism. Before the walkable clearance rule, the directed proof from x -0.375 to the
// resting bank column converged to feet (-0.13175464, 0.012019396) and stopped 6.75 mm short of its 1 mm arrival
// ball. There the moving body's pre-sweep push off the lip corner moved it 4.39 mm away from the lip edge, exactly
// what the swept advance regained. The measurement is in docs/verification/2026-10-08-low-lip-approach-mechanism.json.
// This control starts at that point and checks the push now only lifts, so the body keeps the swept advance.

using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public partial class LowLipTraversalTests
{
    // Mirrors the core's private SkinWidth, the clearance of the inflated slide probe and of a swept advance.
    private const float CoreSkin = 0.01f;

    [Fact]
    public void MovingBodyKeepsTheSweptAdvanceAgainstTheLipCorner()
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        Assert.Equal(Vector3.Zero, world.Origin);
        GroundMoveContext context = Context(world);
        MoveTuning probe = Tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false };
        Vector3 target = RestFeet(context, probe, LipFeet(-EdgeOffset, RestingLip));
        var stalled = new Vector3(-0.13175464f, 0.012019396f, Row);
        MoveState body = StandingAt(stalled);
        Vector2 command = ApproachCommand(stalled, target);

        // The corner contact the old rule pushed along: the inflated probe is touching and its MTV is walkable.
        CapsuleShape capsule = CharacterMovement.CapsuleFor(probe);
        var inflated = new CapsuleShape(capsule.Radius + CoreSkin, capsule.Length);
        bool touching = world.ComputePenetration(inflated, Pose.At(body.Position), out Vector3 push);
        float pushLength = push.Length();
        bool walkable = touching && push.Y >= MathF.Cos(probe.MaxSlopeRadians) * pushLength;

        MoveState next = context.DryContext.Step(body, command, false, Tick, probe);
        Vector3 nextFeet = FeetOf(next, probe);
        float advance = nextFeet.X - stalled.X;
        // No pushback: the walkable pass-through keeps the command less one skin along the sweep direction.
        var delta = new Vector3(command.X * Tick, -probe.Gravity * Tick * Tick, command.Y * Tick);
        float gain = delta.X * (1f - CoreSkin / delta.Length());
        float offset = -nextFeet.X;
        float onCorner = RestingLip - probe.CapsuleRadius +
            MathF.Sqrt(probe.CapsuleRadius * probe.CapsuleRadius - offset * offset);

        _restingOutput.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"stalled={stalled} target={target} push={push} walkable={walkable} command={command}"));
        _restingOutput.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"next feet={nextFeet} grounded={next.Grounded} advance={advance:R} gain={gain:R} " +
            $"oldPushbackX={push.X:R} cornerFeetY={onCorner:R}"));

        Assert.True(touching && walkable && push.X < 0f, $"pre-repair corner push {push}");
        Assert.True(next.Grounded);
        Assert.InRange(advance, gain - 0.00005f, gain + 0.00005f);
        Assert.InRange(nextFeet.Y, onCorner - 0.0001f, onCorner + 0.0001f);
        Assert.InRange(MathF.Abs(nextFeet.Z - Row), 0f, 0.000001f);
    }

    private static Vector3 RestFeet(GroundMoveContext context, in MoveTuning tuning, Vector3 raw)
        => NpcGroundMovement.Hold(StandingAt(raw), Tick, tuning, context).Position - Vector3.UnitY * tuning.CapsuleHalfHeight;

    private static Vector3 FeetOf(in MoveState body, in MoveTuning tuning)
        => body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;

    // TryEdge's command: toward the target at unit pace, never past it within one tick.
    private static Vector2 ApproachCommand(Vector3 feet, Vector3 target)
    {
        Vector2 delta = new(target.X - feet.X, target.Z - feet.Z);
        float distance = delta.Length();
        return distance > 0f ? delta / distance * MathF.Min(1f, distance / Tick) : Vector2.Zero;
    }
}
