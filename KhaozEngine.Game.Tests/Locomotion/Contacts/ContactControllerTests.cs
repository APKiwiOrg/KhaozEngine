// Every expectation below is derived from the installed geometry and the closed form of gravity-first fixed steps,
// never from a stepper run. The default tuning walks at 6 m/s, so a tick at 30 Hz moves 0.2. From launch speed v0, tick
// k of a free flight ends at v0 k dt - g dt^2 k (k + 1) / 2 with speed v0 - g dt k. MoveState.Position is the capsule
// centre, CapsuleHalfHeight above the feet.
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new(FootRadiusFraction: 0.5f);
    const float Dt = 1f / 30f;
    const float Skin = ShellMotion.ContactSkin;
    const float HalfSkin = Skin / 2;
    static float HalfHeight => Tuning.CapsuleHalfHeight;
    static float Gravity => Tuning.Gravity;
    static float Banded => Tuning.MaxSlopeRadians + Tuning.TractionHysteresisRadians;

    static float Abyss(float x, float z) => -1000;
    static float Flat(float x, float z) => 0;

    /// <summary>What the body moves on: installed statics over a terrain far below, or analytic terrain.</summary>
    sealed class Ground(FootSupportScene? scene, Func<float, float, float> height,
        Func<float, float, Vector3>? normal = null) : IDisposable
    {
        internal FootSupportScene? Scene => scene;
        internal IPhysicsWorld? World => scene?.World;
        internal IPhysicsQueryLease? Lease => scene is null ? null : scene.Lease;
        internal Func<float, float, float> Height => height;
        internal Func<float, float, Vector3>? Normal => normal;

        internal MoveState Step(in MoveState state, Vector2 moveDir, float fraction, bool jump, in MoveTuning tuning,
            float dt = Dt, bool run = false, float? faceYaw = null,
            Func<float, float, float, MovementMedium>? medium = null) =>
            ContactController.Step(state, moveDir, fraction, run, jump, dt, height, tuning, normal, scene?.World,
                null, medium, faceYaw);

        // The ground core's own answer for a grounded body, under the scene's lease once the controller is done.
        internal GroundStepResult Core(Vector3 feet, Vector2 displacement, in MoveTuning tuning, float gate) =>
            GroundCore.Step(feet, displacement, Dt, tuning, Settings, height, normal, scene?.World,
                scene is null ? null : scene.Lease, gate);

        public void Dispose() => scene?.Dispose();
    }

    static Ground Make(string ground, Func<SceneVariant, FootSupportScene> scene, Func<float, float, float> terrain,
        Func<float, float, Vector3>? normal = null) => ground switch
        {
            "box" => new Ground(scene(SceneVariant.Box), Abyss),
            "mesh" => new Ground(scene(SceneVariant.Mesh), Abyss),
            _ => new Ground(null, terrain, normal),
        };

    static FootSupportScene Floor(SceneVariant variant) => FootSupportScenes.Floor(variant);

    public static IEnumerable<object[]> Grounds() => [["box"], ["mesh"], ["terrain"]];
    public static IEnumerable<object[]> Physical() => [["box"], ["mesh"]];

    static MoveState Standing(Vector3 feet) => new() { Position = feet + HalfHeight * Vector3.UnitY, Grounded = true };

    static MoveState Falling(Vector3 feet) =>
        new() { Position = feet + HalfHeight * Vector3.UnitY, TimeSinceGrounded = 1 };

    static Vector3 Feet(in MoveState state) => state.Position - HalfHeight * Vector3.UnitY;

    static double FreeY(double y0, double v0, int k) => y0 + v0 * k * Dt - Gravity * (double)Dt * Dt * k * (k + 1) / 2;

    static double FreeV(double v0, int k) => v0 - Gravity * (double)Dt * k;

    // The first tick, counted from 1, that ends descending at or below h.
    static int LandingTick(double y0, double v0, double h)
    {
        for (int k = 1; ; k++)
            if (FreeV(v0, k) <= 0 && FreeY(y0, v0, k) <= h) return k;
    }

    static void AssertNear(double expected, double actual, double tolerance, object context) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"Expected {expected:R}, got {actual:R}: {context}");

    static string Show(in MoveState s) =>
        $"pos {s.Position} vy {s.VerticalVelocity} grounded {s.Grounded} t {s.TimeSinceGrounded} " +
        $"buffer {s.JumpBufferRemaining} h {s.HorizontalVelocity} impact {s.LandingImpactSpeed} " +
        $"granted {s.SupportGranted} commitment {s.Commitment.Phase}/{s.Commitment.EndReason}";

    [Theory]
    [MemberData(nameof(Grounds))]
    public void JumpStampsJumpSpeedAndRisesNextTick(string ground)
    {
        using Ground g = Make(ground, Floor, Flat);
        float v0 = Tuning.JumpSpeed;

        MoveState jumped = g.Step(Standing(Vector3.Zero), Vector2.Zero, 0, true, Tuning);

        Assert.Equal(v0, jumped.VerticalVelocity);
        Assert.False(jumped.Grounded, Show(jumped));
        Assert.True(jumped.SupportGranted, Show(jumped));
        Assert.Equal(0f, jumped.LandingImpactSpeed);
        Assert.Equal(Tuning.CoyoteTime + Dt, jumped.TimeSinceGrounded);
        Assert.Equal(0f, jumped.JumpBufferRemaining);
        AssertNear(0, Feet(jumped).Y, HalfSkin, Show(jumped));

        // The rise starts on the next tick and follows the closed form to the sampled apex.
        MoveState s = jumped;
        for (int k = 1; ; k++)
        {
            s = g.Step(s, Vector2.Zero, 0, false, Tuning);
            Assert.False(s.Grounded, $"tick {k}: {Show(s)}");
            AssertNear(FreeY(0, v0, k), Feet(s).Y, HalfSkin, $"tick {k}: {Show(s)}");
            AssertNear(FreeV(v0, k), s.VerticalVelocity, 1e-3, $"tick {k}: {Show(s)}");
            if (FreeV(v0, k) <= 0) break;
        }
    }

    // A press on the apex tick finds no footing and a coyote window spent by the jump, so it only arms the buffer,
    // which expires long before the 1.9 m fall ends. The body falls to the floor and lands without a relaunch.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void NoDoubleJumpAtTheApex(string ground)
    {
        using Ground g = Make(ground, Floor, Flat);
        float v0 = Tuning.JumpSpeed;
        int apex = 1;
        while (FreeV(v0, apex) > 0) apex++;

        MoveState s = g.Step(Standing(Vector3.Zero), Vector2.Zero, 0, true, Tuning);
        for (int k = 1; k < apex; k++) s = g.Step(s, Vector2.Zero, 0, false, Tuning);
        s = g.Step(s, Vector2.Zero, 0, true, Tuning);

        Assert.False(s.Grounded, Show(s));
        AssertNear(FreeV(v0, apex), s.VerticalVelocity, 1e-3, Show(s));
        Assert.Equal(Tuning.JumpBuffer, s.JumpBufferRemaining);

        for (int tick = 0; tick < 60 && !s.Grounded; tick++)
        {
            MoveState next = g.Step(s, Vector2.Zero, 0, false, Tuning);
            Assert.True(next.Grounded || next.VerticalVelocity <= s.VerticalVelocity,
                $"relaunched at tick {tick}: {Show(next)}");
            s = next;
        }
        Assert.True(s.Grounded, Show(s));
        Assert.Equal(0f, s.VerticalVelocity);
        AssertNear(0, Feet(s).Y, HalfSkin, Show(s));
    }

    // A 2 m ledge over x at most 0. Walking +X at 0.2 a tick from x -0.5, the footprint of radius 0.2 leaves a prop
    // ledge on the tick that ends at x 0.3. Analytic terrain is sampled at the axis, so that walk leaves it at x 0.
    // Either tick flies the rest of the tick from the ledge height and ends airborne with TimeSinceGrounded dt, at most
    // a tick's free fall from rest, g dt^2, below the ledge. A press one tick later is inside the 0.1 s window. A
    // press three ticks later, at 4 dt, is outside it.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void CoyoteJumpInsideTheWindowOnly(string ground)
    {
        const float Top = 2;
        using Ground g = Make(ground, v => FootSupportScenes.Floor(v).Flat("ledge", -4, 0, -2, 2, Top),
            (x, _) => x <= 0 ? Top : 0);
        var east = Vector2.UnitX;

        MoveState s = Standing(new Vector3(-0.5f, Top, 0));
        for (int tick = 0; tick < 10 && s.Grounded; tick++) s = g.Step(s, east, 1, false, Tuning);
        Assert.False(s.Grounded, Show(s));
        Assert.Equal(Dt, s.TimeSinceGrounded);
        Assert.True(s.VerticalVelocity <= 0, Show(s));
        Assert.InRange(Feet(s).Y, Top - Gravity * Dt * Dt, Top + HalfSkin);
        Assert.True(Feet(s).X > (ground == "terrain" ? -HalfSkin : FootRadiusOf(Tuning)), Show(s));

        MoveState inside = g.Step(s, east, 1, true, Tuning);
        Assert.Equal(Tuning.JumpSpeed, inside.VerticalVelocity);
        Assert.False(inside.Grounded);
        Assert.False(inside.SupportGranted);
        Assert.Equal(Tuning.CoyoteTime + Dt, inside.TimeSinceGrounded);
        Assert.Equal(0f, inside.JumpBufferRemaining);

        MoveState outside = s;
        for (int tick = 0; tick < 2; tick++) outside = g.Step(outside, east, 1, false, Tuning);
        outside = g.Step(outside, east, 1, true, Tuning);
        Assert.True(outside.TimeSinceGrounded > Tuning.CoyoteTime, Show(outside));
        Assert.True(outside.VerticalVelocity < 0, Show(outside));
        Assert.False(outside.Grounded);
        Assert.Equal(Tuning.JumpBuffer, outside.JumpBufferRemaining);
    }

    static float FootRadiusOf(in MoveTuning tuning) => Settings.FootRadiusFraction * tuning.CapsuleRadius;

    // From rest at feet 1.1, tick 8 ends at 0.1 and tick 9 at -0.15, so tick 9 lands at 9 g dt = 7.5 m/s. A press on
    // tick 8 arms the buffer, outside any coyote window. Tick 9 lands, reports the impact and the grant, and relaunches.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void BufferedJumpFiresOnLandingAndKeepsTheImpact(string ground)
    {
        const float Y0 = 1.1f;
        using Ground g = Make(ground, Floor, Flat);
        int landing = LandingTick(Y0, 0, 0);
        Assert.True(FreeY(Y0, 0, landing - 1) >= 0.05, "the tick before the landing must end clear of the floor");

        MoveState s = Falling(new Vector3(0, Y0, 0));
        for (int k = 1; k < landing; k++)
        {
            s = g.Step(s, Vector2.Zero, 0, k == landing - 1, Tuning);
            Assert.False(s.Grounded, $"tick {k}: {Show(s)}");
        }
        Assert.Equal(Tuning.JumpBuffer, s.JumpBufferRemaining);
        Assert.True(s.VerticalVelocity < 0, Show(s));

        MoveState landed = g.Step(s, Vector2.Zero, 0, false, Tuning);
        Assert.True(landed.SupportGranted, Show(landed));
        AssertNear(Gravity * (double)Dt * landing, landed.LandingImpactSpeed, 1e-3, Show(landed));
        Assert.Equal(Tuning.JumpSpeed, landed.VerticalVelocity);
        Assert.False(landed.Grounded);
        Assert.Equal(0f, landed.JumpBufferRemaining);
        AssertNear(0, Feet(landed).Y, HalfSkin, Show(landed));

        MoveState rising = g.Step(landed, Vector2.Zero, 0, false, Tuning);
        AssertNear(FreeY(0, Tuning.JumpSpeed, 1), Feet(rising).Y, HalfSkin, Show(rising));
        Assert.Equal(0f, rising.LandingImpactSpeed);
    }

    // Ten airborne ticks from feet 20 never reach the floor at 0. Without momentum the air speed is WalkSpeed times
    // AirControl, so a half control travels half as far.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void AirControlHalvesAirTravel(string ground)
    {
        using Ground g = Make(ground, Floor, Flat);
        double Travel(float control)
        {
            MoveTuning tuning = Tuning with { AirControl = control };
            MoveState s = Falling(new Vector3(-1, 20, 0));
            for (int k = 1; k <= 10; k++)
            {
                s = g.Step(s, Vector2.UnitX, 1, false, tuning);
                Assert.False(s.Grounded, Show(s));
                AssertNear(Tuning.WalkSpeed * control, s.CommandedVelocity.X, 1e-4, Show(s));
            }
            return Feet(s).X + 1;
        }

        double full = Travel(1), half = Travel(0.5f);
        AssertNear(10 * Tuning.WalkSpeed * Dt, full, HalfSkin, full);
        AssertNear(5 * Tuning.WalkSpeed * Dt, half, HalfSkin, half);
        AssertNear(full / 2, half, HalfSkin, half);
    }

    // A running jump carries the ground tick's 6 m/s. With momentum on, releasing the stick keeps that speed in the
    // air. With momentum off, the released air tick commands nothing and the body stops moving horizontally.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void MomentumKeepsSpeedOnRelease(string ground)
    {
        using Ground g = Make(ground, Floor, Flat);
        foreach (bool momentum in new[] { true, false })
        {
            MoveTuning tuning = Tuning with { AirMomentum = momentum };
            MoveState s = g.Step(Standing(new Vector3(-1, 0, 0)), Vector2.UnitX, 1, true, tuning);
            Assert.False(s.Grounded, Show(s));
            AssertNear(Tuning.WalkSpeed, s.HorizontalVelocity.X, 1e-4, Show(s));
            for (int k = 1; k <= 5; k++)
            {
                MoveState next = g.Step(s, Vector2.Zero, 0, false, tuning);
                Assert.False(next.Grounded, Show(next));
                double expected = momentum ? Tuning.WalkSpeed * Dt : 0;
                AssertNear(expected, Feet(next).X - Feet(s).X, HalfSkin, $"momentum {momentum}: {Show(next)}");
                if (momentum) AssertNear(Tuning.WalkSpeed, next.HorizontalVelocity.Length(), 1e-4, Show(next));
                s = next;
            }
        }
    }

    // AirControl 0 under momentum gives the command no authority, so a full run input across the arc changes nothing:
    // the arc keeps the launch's 6 m/s along X until it lands.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void MomentumWithAirControlZeroIsBallistic(string ground)
    {
        using Ground g = Make(ground, Floor, Flat);
        MoveTuning tuning = Tuning with { AirMomentum = true, AirControl = 0 };
        MoveState s = g.Step(Standing(new Vector3(-1, 0, 0)), Vector2.UnitX, 1, true, tuning);
        int airborne = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            MoveState next = g.Step(s, Vector2.UnitY, 1, false, tuning, run: true);
            if (next.Grounded) break;
            airborne++;
            AssertNear(Tuning.WalkSpeed * Dt, Feet(next).X - Feet(s).X, HalfSkin, Show(next));
            AssertNear(0, Feet(next).Z - Feet(s).Z, HalfSkin, Show(next));
            AssertNear(Tuning.WalkSpeed, next.HorizontalVelocity.X, 1e-4, Show(next));
            AssertNear(0, next.HorizontalVelocity.Y, 1e-4, Show(next));
            s = next;
        }
        Assert.True(airborne >= LandingTick(0, Tuning.JumpSpeed, 0) - 1, $"only {airborne} airborne ticks");
    }

    // A body falling from feet 2 with a 6 m/s carry toward a wall at x 1. The shell of radius 0.4 stops at
    // x 1 - 0.4 - skin on tick 3. Analytic terrain rising to 5 past x 1.1 is a wall for the axis itself, met on
    // tick 6. Either way the clip leaves no more carry than half a skin per tick, never grows the carry, and the
    // fall keeps its closed-form vertical speed. Tick 9 ends at 0.75, still airborne.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void WallClipsTheCarry(string ground)
    {
        using Ground g = Make(ground, v => FootSupportScenes.Floor(v).Wall("wall", 1, 0.3f, 4),
            (x, _) => x > 1.1f ? 5 : 0);
        float limit = ground == "terrain" ? 1.1f : 1 - Tuning.CapsuleRadius - Skin + HalfSkin;
        MoveTuning tuning = Tuning with { AirMomentum = true };
        MoveState s = Falling(new Vector3(0, 2, 0)) with { HorizontalVelocity = new Vector2(Tuning.WalkSpeed, 0) };
        MoveState previous = s;
        for (int k = 1; k <= 9; k++)
        {
            previous = s;
            s = g.Step(s, Vector2.Zero, 0, false, tuning);
            Assert.False(s.Grounded, $"tick {k}: {Show(s)}");
            Assert.True(Feet(s).X <= limit, $"tick {k} passed the wall: {Show(s)}");
            Assert.True(s.HorizontalVelocity.Length() <= previous.HorizontalVelocity.Length(), $"tick {k}: {Show(s)}");
            AssertNear(0, s.HorizontalVelocity.Y, 1e-4, Show(s));
            AssertNear(FreeV(0, k), s.VerticalVelocity, 1e-3, $"tick {k}: {Show(s)}");
        }
        Assert.True(s.HorizontalVelocity.Length() <= HalfSkin / Dt, Show(s));
        AssertNear(Feet(previous).X, Feet(s).X, HalfSkin, Show(s));
    }

    // Falling from feet 1.1 at 6 m/s, tick 9 starts at 0.1 and plans (0.2, -0.25), two substeps. The first ends at
    // -0.025, so the body lands half way through the tick and the ground core walks the other 0.1.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void LandingMidTickKeepsTheHorizontalMove(string ground)
    {
        const float Y0 = 1.1f;
        using Ground g = Make(ground, Floor, Flat);
        int landing = LandingTick(Y0, 0, 0);
        MoveState s = Falling(new Vector3(-1, Y0, 0));
        for (int k = 1; k <= landing; k++)
        {
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning);
            Assert.Equal(k == landing, s.Grounded);
            AssertNear(-1 + Tuning.WalkSpeed * Dt * k, Feet(s).X, HalfSkin, $"tick {k}: {Show(s)}");
        }
        AssertNear(0, Feet(s).Y, HalfSkin, Show(s));
        AssertNear(Gravity * (double)Dt * landing, s.LandingImpactSpeed, 1e-3, Show(s));
        Assert.True(s.SupportGranted);
        AssertNear(Tuning.WalkSpeed, s.HorizontalVelocity.X, 1e-4, Show(s));
    }

    // Every GroundCoreTests scene, start and move. Each row's WalkSpeed makes one tick's commanded move the row's
    // move.
    public static IEnumerable<object[]> ParityRows()
    {
        string[] physical = ["free-prop", "free-prop-diagonal", "free-slope", "still", "lip", "crate", "tall",
            "step-0.32", "step-0.40", "step-0.41", "step-0.60", "steep-rise", "steep-off", "bank", "lagging",
            "nosing"];
        foreach (string row in physical)
            foreach (SceneVariant variant in new[] { SceneVariant.Box, SceneVariant.Mesh })
                yield return [row, variant];
        foreach (string row in new[] { "free-terrain", "flat-terrain", "bank-terrain", "cliff", "refused-target",
            "refused-start", "ceiling", "incline" })
            yield return [row, SceneVariant.Box];
    }

    static (Ground Ground, Vector3 Feet, Vector2 Move) ParityScene(string row, SceneVariant v)
    {
        Ground On(FootSupportScene scene) => new(scene, Abyss);
        Ground Terrain(float degrees)
        {
            float grade = MathF.Tan(Radians(degrees));
            Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
            return new Ground(null, (x, _) => grade * x, (_, _) => normal);
        }
        (Ground, Vector3, Vector2) Seated(FootSupportScene scene, string top, float x, float z, Vector2 move) =>
            (On(scene), new Vector3(x, (float)scene.TopHeightAt(top, x, z), z), move);
        (Ground, Vector3, Vector2) StepDown(float drop) =>
            (On(FootSupportScenes.Floor(v).Flat("ledge", -2, 0, -2, 2, drop)), new Vector3(-0.1f, drop, 0),
                new Vector2(0.4f, 0));
        return row switch
        {
            "free-prop" => Seated(PropFloor(v), "floor", 0, 0, new Vector2(0.3f, 0)),
            "free-prop-diagonal" => Seated(PropFloor(v), "floor", 0, 0, new Vector2(0.21f, -0.17f)),
            "free-slope" => Seated(Slope(v, 20), "slope", 0, 0, new Vector2(-0.3f, 0)),
            "still" => Seated(PropFloor(v), "floor", 0.5f, 0.25f, Vector2.Zero),
            "lip" => (On(Lip(v)), new Vector3(-0.3f, 0, 0), new Vector2(0.2f, 0)),
            "crate" => (On(Crate(v)), new Vector3(-0.3f, 0, 0), new Vector2(0.2f, 0)),
            "tall" => (On(FootSupportScenes.Floor(v).Flat("crate", 0, 1, -1, 1, Tuning.StepHeight + 0.01f)),
                new Vector3(-0.3f, 0, 0), new Vector2(0.2f, 0)),
            "step-0.32" => StepDown(0.32f),
            "step-0.40" => StepDown(0.40f),
            "step-0.41" => StepDown(0.41f),
            "step-0.60" => StepDown(0.60f),
            "steep-rise" => (On(Ramp(v, 2f * MathF.Tan(Radians(60f)))), new Vector3(-0.15f, 0, 0),
                new Vector2(0.2f, 0.1f)),
            "steep-off" => (On(Ramp(v, -2f * MathF.Tan(Radians(60f)))), new Vector3(0.01f, 0, 0),
                new Vector2(0.2f, 0)),
            "bank" => Seated(Slope(v, 46), "slope", 0, 0, new Vector2(0.3f, 0)),
            "lagging" => (On(Stairs(v, 0.35f, 0.30f, 3)), new Vector3(0.1f, 0.3f - 0.4f / 3f, 0),
                new Vector2(0.15f, 0)),
            "nosing" => (On(Stairs(v, 0.35f, 0.30f, 6)), new Vector3(0.65f, 0.6f, 0), new Vector2(0.19f, 0)),
            "free-terrain" => (Terrain(20), Vector3.Zero, new Vector2(0, 0.3f)),
            "flat-terrain" => (Terrain(0), Vector3.Zero, new Vector2(0.21f, -0.17f)),
            "bank-terrain" => (Terrain(46), Vector3.Zero, new Vector2(0.3f, 0)),
            "cliff" => (new Ground(null, (x, _) => MathF.Max(0, 5 * (x - 0.1f)),
                (x, _) => x > 0.1f ? Vector3.Normalize(new Vector3(-5, 1, 0)) : Vector3.UnitY),
                Vector3.Zero, new Vector2(0.2f, 0.2f)),
            "refused-target" => (On(OverCapacityFan()), new Vector3(-0.2f, 0, 0), new Vector2(0.2f, 0)),
            "refused-start" => (On(OverCapacityFan()), Vector3.Zero, new Vector2(0.2f, 0)),
            "ceiling" => (On(FootSupportScenes.Floor(SceneVariant.Box)
                .Slab("ceiling", new Vector3(2.5f, 1.7f, 0), 0, 1.5f, 3)), new Vector3(0.39f, 0, 0),
                new Vector2(0.3f, 0)),
            "incline" => (On(Incline(40)), new Vector3(-3.6000018f, -3.0207605f, 0), new Vector2(6 * Dt, 0)),
            _ => throw new ArgumentException(row),
        };
    }

    // A grounded tick is the ground core's tick at the banded gate, with the default settings' half-radius foot.
    // Both run in this process, so the feet must agree to the bit.
    [Theory]
    [MemberData(nameof(ParityRows))]
    public void GroundParityWithTheGroundCore(string row, SceneVariant variant)
    {
        (Ground ground, Vector3 feet, Vector2 move) = ParityScene(row, variant);
        using Ground g = ground;
        bool moving = move != Vector2.Zero;
        MoveTuning tuning = moving ? Tuning with { WalkSpeed = move.Length() / Dt } : Tuning;
        MoveState state = Standing(feet);

        MoveState result = ContactController.Step(state, moving ? Vector2.Normalize(move) : Vector2.Zero,
            moving ? 1 : 0, false, false, Dt, g.Height, tuning, g.Normal, g.World, null, null);

        AssertNear(move.Length() / Dt, result.CommandedVelocity.Length(), 1e-4, Show(result));
        var start = new Vector3(state.Position.X, state.Position.Y - HalfHeight, state.Position.Z);
        Vector2 velocity = result.CommandedVelocity;
        GroundStepResult core = g.Core(start, velocity * Dt, tuning, Banded);
        bool grounded = core.Footing is GroundFooting.Walkable or GroundFooting.Held;
        (Vector3 end, float verticalVelocity) = (core.Feet, 0f);
        if (!grounded && core.RemainingTime > 0)
            (end, verticalVelocity) = Continued(g, core, velocity, tuning);
        var expected = new Vector3(end.X, end.Y + HalfHeight, end.Z);
        Assert.True(BitsEqual(expected, result.Position), $"{row}: core {core}, controller {Show(result)}");
        Assert.Equal(grounded, result.Grounded);
        Assert.Equal(grounded, result.SupportGranted);
        Assert.Equal(verticalVelocity, result.VerticalVelocity);
        Assert.Equal(0f, result.LandingImpactSpeed);
        Assert.Equal(grounded ? 0 : Dt, result.TimeSinceGrounded);
    }

    static bool BitsEqual(Vector3 a, Vector3 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) &&
        BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) &&
        BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    // The commitment rows mine MovementCommitmentTests: 60 Hz and a 0.35 m capsule radius.
    const float CommitDt = 1f / 60f;
    static readonly MoveTuning CommitTuning = Tuning with { CapsuleRadius = 0.35f };

    static FootSupportScene WideFloor(SceneVariant variant) =>
        new FootSupportScene(variant).Flat("floor", -6, 12, -6, 6, 0);

    // Three ticks of preparation: the first two hold the body where it stands against hostile input. The third
    // launches along the latched -Z at 6 m/s with the half-step seeded vertical speed 5 + g dt / 2, less one tick of
    // gravity, and the facing never turns.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void PreparationRootsThenLaunches(string ground)
    {
        using Ground g = Make(ground, WideFloor, Flat);
        float dt = CommitDt;
        MoveState state = Standing(new Vector3(3, 0, -4)) with
        {
            FacingYaw = 0.7f,
            Commitment = new MovementCommitment(3u, -Vector2.UnitY, 6, 5, 3 * dt, 4),
        };
        MoveState Hostile(in MoveState s) =>
            g.Step(s, Vector2.UnitX, 1, true, CommitTuning, dt, run: true, faceYaw: -1.5f);

        MoveState first = Hostile(state);
        MoveState second = Hostile(first);
        foreach (MoveState held in new[] { first, second })
        {
            Assert.True(Vector3.Distance(state.Position, held.Position) <= HalfSkin, Show(held));
            Assert.True(held.Grounded, Show(held));
            Assert.Equal(0f, held.VerticalVelocity);
            Assert.Equal(0.7f, held.FacingYaw);
        }
        Assert.Equal(MovementCommitmentPhase.Preparing, second.Commitment.Phase);

        MoveState launched = Hostile(second);
        Assert.Equal(MovementCommitmentPhase.Airborne, launched.Commitment.Phase);
        Assert.False(launched.Grounded);
        double vy = 5 - CommitTuning.Gravity * (double)dt / 2;
        AssertNear(vy, launched.VerticalVelocity, 1e-4, Show(launched));
        AssertNear(vy * dt, launched.Position.Y - state.Position.Y, HalfSkin, Show(launched));
        AssertNear(-6 * dt, launched.Position.Z - state.Position.Z, HalfSkin, Show(launched));
        AssertNear(0, launched.Position.X - state.Position.X, HalfSkin, Show(launched));
        Assert.Equal(0.7f, launched.FacingYaw);
    }

    // A 22 m/s leap meets a wall whose face stands at x 1.25. The shell never passes the face, to half a skin, the
    // carry is clipped, and the body falls to the floor, where the commitment completes exactly once.
    [Theory]
    [MemberData(nameof(Physical))]
    public void WallBlocksThenLands(string ground)
    {
        using Ground g = Make(ground, v => WideFloor(v).Wall("wall", 1.25f, 0.3f, 5), Flat);
        float limit = 1.25f - CommitTuning.CapsuleRadius + HalfSkin;
        MoveState s = Standing(Vector3.Zero) with
        {
            Commitment = new MovementCommitment(9u, Vector2.UnitX, 22, 7, 0, 5),
        };
        bool ended = false;
        for (int tick = 0; tick < 300 && !ended; tick++)
        {
            s = g.Step(s, Vector2.Zero, 0, false, CommitTuning, CommitDt);
            Assert.True(Feet(s).X <= limit, $"tick {tick} passed the wall: {Show(s)}");
            ended = s.Commitment.Phase is MovementCommitmentPhase.Completed or MovementCommitmentPhase.Aborted;
        }
        Assert.True(ended, Show(s));
        Assert.Equal(MovementCommitmentPhase.Completed, s.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.Landed, s.Commitment.EndReason);
        Assert.True(s.Grounded, Show(s));
        AssertNear(0, Feet(s).Y, HalfSkin, Show(s));
    }

    // A wall 0.36 from the axis leaves the 0.35 shell 0.01 of travel, far under a tenth of the 1 / 3 m launch tick.
    [Theory]
    [MemberData(nameof(Physical))]
    public void BlockedLaunchAborts(string ground)
    {
        using Ground g = Make(ground, v => WideFloor(v).Wall("wall", 0.36f, 0.3f, 4), Flat);
        MoveState s = Standing(Vector3.Zero) with
        {
            Commitment = new MovementCommitment(21u, Vector2.UnitX, 20, 7, 0, 3),
        };

        s = g.Step(s, Vector2.Zero, 0, false, CommitTuning, CommitDt);

        Assert.Equal(MovementCommitmentPhase.Aborted, s.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.Blocked, s.Commitment.EndReason);
        Assert.True(Feet(s).X <= 0.36f - CommitTuning.CapsuleRadius - Skin + HalfSkin, Show(s));
    }

    [Fact]
    public void TimeoutAborts()
    {
        using var g = new Ground(null, Abyss);
        MoveState s = Falling(new Vector3(0, 50, 0)) with
        {
            Commitment = new MovementCommitment(22u, Vector2.UnitX, 5, 4, 0, 3 * CommitDt),
        };
        for (int i = 0; i < 4 && s.Commitment.Phase != MovementCommitmentPhase.Aborted; i++)
            s = g.Step(s, Vector2.Zero, 0, false, CommitTuning, CommitDt);

        Assert.Equal(MovementCommitmentPhase.Aborted, s.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.TimedOut, s.Commitment.EndReason);
    }

    // An 8 m arc with apex 2 over 0.54 s: horizontal 8 / 0.54, vertical 4 * 2 / 0.54, gravity 8 * 2 / 0.54^2. The
    // half-step seed puts every tick boundary on the authored parabola, so the landing tick ends within one tick of
    // travel of x 8.
    [Theory]
    [InlineData("box", 30)]
    [InlineData("mesh", 30)]
    [InlineData("terrain", 30)]
    [InlineData("box", 60)]
    [InlineData("mesh", 60)]
    [InlineData("terrain", 60)]
    public void AuthoredArcLandsOnTime(string ground, int tickRate)
    {
        const float Distance = 8, Apex = 2, Duration = 0.54f;
        float dt = 1f / tickRate;
        float horizontal = Distance / Duration;
        using Ground g = Make(ground, WideFloor, Flat);
        MoveState s = Standing(Vector3.Zero) with
        {
            Commitment = new MovementCommitment(24u, Vector2.UnitX, horizontal, 4 * Apex / Duration,
                8 * Apex / (Duration * Duration), preparationSeconds: 0, recoverySeconds: 0, timeoutSeconds: 3),
        };
        for (int i = 0; i < 180 && s.Commitment.Phase != MovementCommitmentPhase.Completed; i++)
            s = g.Step(s, Vector2.Zero, 0, false, CommitTuning, dt);

        Assert.Equal(MovementCommitmentPhase.Completed, s.Commitment.Phase);
        AssertNear(Distance, Feet(s).X, horizontal * dt + HalfSkin, Show(s));
        AssertNear(0, Feet(s).Y, HalfSkin, Show(s));
    }

    // 256 scripted ticks over a floor at 0 for x at most 0, a 50 degree face falling from x 0 to (2, -2 tan 50) and a
    // pit floor at the toe: a walk and a jump, the walk off onto the face, the slide and its landing, a commitment,
    // runs and jumps back into the face, and a figure of eight. A replay from a copy of the first state, with the
    // same inputs, reaches every state to the bit.
    [Fact]
    public void ReplayIsBitIdentical()
    {
        float toe = -2 * MathF.Tan(Radians(50));
        using var g = new Ground(Ramp(SceneVariant.Box, toe).Flat("pit", 2, 6, -2, 2, toe), Abyss);
        MoveState initial = Standing(new Vector3(-1.5f, 0, 0));

        (Vector2 Dir, float Fraction, bool Jump) Input(int i)
        {
            if (i < 40) return (Vector2.Normalize(new Vector2(1, i < 5 ? 0 : 0.2f)), 0.5f, i == 4);
            if (i < 80) return (Vector2.Zero, 0, false);
            if (i < 130) return (-Vector2.UnitX, 1, i % 12 == 0);
            float a = 0.1f * i;
            return (new Vector2(MathF.Cos(a), MathF.Sin(a)), 0.5f, i % 25 == 0);
        }
        MoveState Commit(MoveState s, int i) => i == 60
            ? s with { Commitment = new MovementCommitment(5u, Vector2.UnitX, 2, 4, 25, 2 * Dt, 0.1f, 2) }
            : s;

        var states = new List<MoveState>();
        int grounded = 0, air = 0, slides = 0, committed = 0;
        MoveState live = initial;
        for (int i = 0; i < 256; i++)
        {
            live = Commit(live, i);
            if (live.Grounded) grounded++;
            else if (InContact(g, live)) slides++;
            else air++;
            if (live.Commitment.Phase == MovementCommitmentPhase.Airborne) committed++;
            (Vector2 dir, float fraction, bool jump) = Input(i);
            live = g.Step(live, dir, fraction, jump, Tuning);
            states.Add(live);
        }
        Assert.True(grounded > 0 && air > 0 && slides > 0 && committed > 0,
            $"ground {grounded}, air {air}, slide {slides}, committed {committed}");

        MoveState replay = initial;
        for (int i = 0; i < 256; i++)
        {
            replay = Commit(replay, i);
            (Vector2 dir, float fraction, bool jump) = Input(i);
            replay = g.Step(replay, dir, fraction, jump, Tuning);
            AssertSameBits(states[i], replay, i);
        }
    }

    static bool InContact(Ground g, in MoveState s)
    {
        using IPhysicsQueryLease lease = g.Scene!.World.AcquireQueryReadLease();
        return SlideCore.InContact(Feet(s), Tuning, Settings, Tuning.MaxSlopeRadians, g.Height, g.Normal, g.World,
            lease, out _);
    }

    static void AssertSameBits(in MoveState expected, in MoveState actual, int tick)
    {
        static int B(float f) => BitConverter.SingleToInt32Bits(f);
        float[] e = Fields(expected), a = Fields(actual);
        for (int i = 0; i < e.Length; i++)
            Assert.True(B(e[i]) == B(a[i]), $"tick {tick} field {i}: {Show(expected)} against {Show(actual)}");
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.SupportGranted, actual.SupportGranted);
        Assert.Equal(expected.Swimming, actual.Swimming);
        Assert.Equal(expected.Commitment, actual.Commitment);
    }

    static float[] Fields(in MoveState s) =>
    [
        s.Position.X, s.Position.Y, s.Position.Z, s.VerticalVelocity, s.TimeSinceGrounded, s.JumpBufferRemaining,
        s.SpeedScale, s.HorizontalVelocity.X, s.HorizontalVelocity.Y, s.CommandedVelocity.X, s.CommandedVelocity.Y,
        s.ClimbRate, s.ClimbRateEwma, s.StepDeltaY, s.LandingImpactSpeed, s.FacingYaw,
    ];

    // Water belongs to phase 4. A dry medium changes nothing.
    [Fact]
    public void WaterMediumIsRejectedUntilPhase4()
    {
        using var g = new Ground(null, Flat);
        MoveState s = Standing(Vector3.Zero);
        Assert.Throws<NotSupportedException>(() =>
            g.Step(s, Vector2.UnitX, 1, false, Tuning, medium: (_, _, _) => new MovementMedium(4, inWater: true)));

        MoveState dry = g.Step(s, Vector2.UnitX, 1, false, Tuning, medium: (_, _, _) => MovementMedium.Dry);
        MoveState none = g.Step(s, Vector2.UnitX, 1, false, Tuning);
        AssertSameBits(none, dry, 0);
    }
}
