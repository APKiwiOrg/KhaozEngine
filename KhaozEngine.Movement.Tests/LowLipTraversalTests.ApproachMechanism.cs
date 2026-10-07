// KhaozEngine #1270 approach discriminator. It characterizes the motion that leaves the directed proof from x -0.375
// to the resting bank column short of its 1 mm arrival ball. It replays the unchanged TryEdge call order and proves
// the replay matches every predicate input exactly. It then separates the carried state from the position and
// shadows one core step through the same public world queries, stage by stage. It is a measurement of the current
// core, not a repair, and it changes no tolerance, budget or pace.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    public void RestingBankApproachStallsOnTheMovingCornerDepenetration()
    {
        using BepuPhysicsWorld world = LipWorld(RestingLip);
        Assert.Equal(Vector3.Zero, world.Origin);
        GroundMoveContext context = Context(world);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        var footprint = new NavAreaFootprint(bake.Columns, Options, Tuning, default);
        MoveTuning probe = Tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false };
        Vector3 fromRest = RestFeet(context, probe, CapturedPoint(bake, -0.375f).Feet);
        Vector3 toRest = RestFeet(context, probe, CapturedPoint(bake, -EdgeOffset).Feet);

        var inputs = new List<Vector3>(66);
        bool Record(Vector3 feet)
        {
            inputs.Add(feet);
            return footprint.Accepts(feet);
        }
        bool proved = GroundTraversalProbe.TryEdge(context, Tuning, fromRest, toRest, Record, Tick, 64);

        // The replay uses TryEdge's tuning, dry context and command. Predicate inputs 3 to 66 are its 64 step outputs.
        GroundMoveContext dry = context.DryContext;
        var states = new List<MoveState>(64);
        var body = new MoveState
        {
            Position = fromRest + Vector3.UnitY * probe.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1f,
        };
        body = dry.Step(body, Vector2.Zero, false, Tick, probe);
        states.Add(body);
        for (int step = 1; step < 64; step++)
        {
            body = dry.Step(body, ApproachCommand(FeetOf(body, probe), toRest), false, Tick, probe);
            states.Add(body);
        }
        bool replayExact = inputs.Count == 66 &&
            Enumerable.Range(0, 64).All(i => inputs[i + 2] == FeetOf(states[i], probe));

        // Carried state against a fresh grounded state at the same position, under the same next command.
        MoveState carried = states[^1];
        Vector3 carriedFeet = FeetOf(carried, probe);
        Vector2 command = ApproachCommand(carriedFeet, toRest);
        var fresh = new MoveState { Position = carried.Position, Grounded = true, SpeedScale = 1f };
        MoveState carriedNext = dry.Step(carried, command, false, Tick, probe);
        MoveState freshNext = dry.Step(fresh, command, false, Tick, probe);

        // Shadow of one core step through the same queries: the inflated pre-sweep depenetration, the swept advance,
        // the settle overlap check and the downward support sweep. The terrain is analytic at Y 0.
        CapsuleShape capsule = CharacterMovement.CapsuleFor(probe);
        var inflated = new CapsuleShape(capsule.Radius + CoreSkin, capsule.Length);
        Vector3 pushed = carried.Position, push = Vector3.Zero;
        int pushes = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!world.ComputePenetration(inflated, Pose.At(pushed), out Vector3 mtv) || mtv.LengthSquared() <= 1e-12f)
                break;
            pushed += mtv;
            push += mtv;
            pushes++;
        }
        float vVel = MathF.Max(-probe.MaxFallSpeed, carried.VerticalVelocity - probe.Gravity * Tick);
        var delta = new Vector3(command.X * probe.WalkSpeed * Tick, vVel * Tick, command.Y * probe.WalkSpeed * Tick);
        float length = delta.Length();
        Vector3 direction = delta / length;
        bool swept = world.SweepCapsule(capsule, Pose.At(pushed), direction, length, out SweepHit sweep);
        Vector3 contactNormal = swept && sweep.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(sweep.Normal) : default;
        bool walkable = swept && contactNormal.Y >= MathF.Cos(probe.MaxSlopeRadians);
        Vector3 advanced = pushed + delta - (swept ? direction * MathF.Min(CoreSkin, sweep.Distance) : Vector3.Zero);
        bool settleOverlap = world.ComputePenetration(capsule, Pose.At(advanced), out Vector3 settleMtv);
        float halfH = probe.CapsuleHalfHeight;
        float probeStart = advanced.Y + 2f * halfH;
        float maxProbe = MathF.Max(CoreSkin, probeStart - halfH + CoreSkin);
        bool supported = world.SweepCapsule(capsule, Pose.At(new Vector3(advanced.X, probeStart, advanced.Z)),
            -Vector3.UnitY, maxProbe, out SweepHit floor);
        var shadow = new Vector3(advanced.X, probeStart - floor.Distance, advanced.Z);
        float shadowError = Vector3.Distance(shadow, carriedNext.Position);

        // The fixed point: the pushback's horizontal part equals the swept advance's horizontal gain. The lip edge is
        // at x 0, so the body's offset from it is -x, and a touching corner normal has horizontal part offset / radius.
        float offset = -carried.Position.X;
        float bottomCentreY = carriedFeet.Y + probe.CapsuleRadius;
        float cornerDistance = new Vector2(offset, bottomCentreY - RestingLip).Length();
        float pushback = CoreSkin * offset / probe.CapsuleRadius;
        float gain = delta.X * (1f - CoreSkin / length);

        string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        string V(Vector3 value) => string.Create(CultureInfo.InvariantCulture, $"({value.X:R}, {value.Y:R}, {value.Z:R})");
        _restingOutput.WriteLine($"proof={proved} inputs={inputs.Count} replayExact={replayExact} from={V(fromRest)} " +
            $"to={V(toRest)}");
        _restingOutput.WriteLine("feet x by step: " + string.Join(" ", states.Select(s => F(FeetOf(s, probe).X))));
        _restingOutput.WriteLine($"carried feet={V(carriedFeet)} grounded={carried.Grounded} vVel={F(carried.VerticalVelocity)} " +
            $"climbRate={F(carried.ClimbRate)} climbEwma={F(carried.ClimbRateEwma)} stepDeltaY={F(carried.StepDeltaY)} " +
            $"sinceGround={F(carried.TimeSinceGrounded)} support={carried.SupportGranted} " +
            string.Create(CultureInfo.InvariantCulture,
                $"horizontal=({carried.HorizontalVelocity.X:R}, {carried.HorizontalVelocity.Y:R}) ") +
            string.Create(CultureInfo.InvariantCulture,
                $"commanded=({carried.CommandedVelocity.X:R}, {carried.CommandedVelocity.Y:R})"));
        _restingOutput.WriteLine($"next carried={V(carriedNext.Position)} fresh={V(freshNext.Position)} " +
            $"same={carriedNext.Position == freshNext.Position} groundedNext={carriedNext.Grounded}/{freshNext.Grounded} " +
            string.Create(CultureInfo.InvariantCulture, $"command=({command.X:R}, {command.Y:R})"));
        _restingOutput.WriteLine($"shadow pushes={pushes} push={V(push)} pushed={V(pushed)} delta={V(delta)} " +
            $"swept={swept} hitDistance={F(sweep.Distance)} normal={V(contactNormal)} walkable={walkable}");
        _restingOutput.WriteLine($"shadow advanced={V(advanced)} settleOverlap={settleOverlap} " +
            $"settleDepth={F(settleMtv.Length())} supported={supported} supportNormal={V(floor.Normal)} " +
            $"shadow={V(shadow)} error={F(shadowError)}");
        _restingOutput.WriteLine($"fixed point cornerDistance={F(cornerDistance)} pushbackX={F(pushback)} " +
            $"gainX={F(gain)} netX={F(carriedNext.Position.X - carried.Position.X)} " +
            $"residual={F(Vector3.Distance(carriedFeet, toRest))}");

        Assert.False(proved);
        Assert.True(replayExact, "the replay diverged from the TryEdge predicate inputs");
        Assert.Equal(carriedNext.Position, freshNext.Position);
        Assert.True(carriedNext.Grounded && freshNext.Grounded);
        Assert.True(push.X < 0f && push.Y > 0f, $"pushback {V(push)}");
        Assert.True(swept && walkable, "the swept advance did not meet the walkable corner");
        Assert.True(!settleOverlap || settleMtv.Length() <= CoreSkin, "the settle pass would correct the advance");
        Assert.True(supported);
        Assert.InRange(shadowError, 0f, 0.000001f);
        Assert.InRange(MathF.Abs(gain + push.X), 0f, 0.000001f);
        Assert.InRange(Vector3.Distance(carriedFeet, toRest), GroundTraversalProbe.ArrivalTolerance, 0.01f);
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
