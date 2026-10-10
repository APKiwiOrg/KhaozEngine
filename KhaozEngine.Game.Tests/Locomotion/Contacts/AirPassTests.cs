// Every expectation below is derived from the installed geometry and the closed form of gravity-first fixed steps,
// never from a stepper run. The default tuning gives a shell from feet + 0.4 to feet + 1.8 with radius 0.4, substeps
// of at most 0.2 over the whole displacement and a footprint radius of 0.2. A descending substep lands on walkable
// support from its lowest feet to StepHeight 0.4 above its highest, and slides on steep support within the span it
// travelled. From launch speed v0, tick k ends at v0 k dt - g dt^2 k (k + 1) / 2 with speed v0 - g dt k, until the
// MaxFallSpeed clamp.
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class AirPassTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new(FootRadiusFraction: 0.5f);
    const float Dt = 1f / 30f;
    const float Skin = ShellMotion.ContactSkin;
    const float HalfSkin = Skin / 2;
    static float Gravity => Tuning.Gravity;
    static float Radius => Tuning.CapsuleRadius;
    static float FootRadius => Settings.FootRadiusFraction * Tuning.CapsuleRadius;
    static float Height => 2 * Tuning.CapsuleHalfHeight;

    /// <summary>What the body moves through: installed statics, analytic terrain, or neither.</summary>
    sealed class Ground(FootSupportScene? scene, Func<float, float, float>? height = null,
        Func<float, float, Vector3>? normal = null) : IDisposable
    {
        internal StaticHandle? this[string name] => scene?[name];

        internal AirStepResult Step(Vector3 feet, Vector2 velocity, float verticalVelocity) =>
            AirPass.Step(feet, velocity, verticalVelocity, Dt, Gravity, Tuning, Settings, Tuning.MaxSlopeRadians,
                height, normal, scene?.World, scene is null ? null : scene.Lease);

        internal GroundStepResult Rest(Vector3 feet) =>
            GroundCore.Step(feet, Vector2.Zero, Dt, Tuning, Settings, height, normal, scene?.World,
                scene is null ? null : scene.Lease);

        // Steps until the body lands or slides, at most limit ticks, carrying the feet and vertical speed.
        internal List<AirStepResult> Run(Vector3 feet, Vector2 velocity, float verticalVelocity, int limit)
        {
            var ticks = new List<AirStepResult>();
            for (int i = 0; i < limit; i++)
            {
                AirStepResult result = Step(feet, velocity, verticalVelocity);
                ticks.Add(result);
                if (result.Outcome != AirOutcome.Airborne) break;
                feet = result.Feet;
                verticalVelocity = result.VerticalVelocity;
            }
            return ticks;
        }

        public void Dispose() => scene?.Dispose();
    }

    static Ground Make(string ground, Func<SceneVariant, FootSupportScene> scene,
        Func<float, float, float>? terrain = null, Func<float, float, Vector3>? normal = null) => ground switch
        {
            "box" => new Ground(scene(SceneVariant.Box)),
            "mesh" => new Ground(scene(SceneVariant.Mesh)),
            "terrain" => new Ground(null, terrain, normal),
            _ => new Ground(null),
        };

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

    static void AssertLanded(AirStepResult result, double height, StaticHandle? support)
    {
        Assert.True(result.Outcome == AirOutcome.Landed, $"{result}");
        Assert.Equal(SupportStatus.Walkable, result.Support.Status);
        Assert.Equal(support, result.Support.Static);
        AssertNear(height, result.Feet.Y, HalfSkin, result);
        Assert.Equal(0f, result.VerticalVelocity);
    }

    // A landing whose feet may sit below the support by a climb the ground core still owes.
    static void AssertLandedOn(AirStepResult result, StaticHandle? support)
    {
        Assert.True(result.Outcome == AirOutcome.Landed, $"{result}");
        Assert.Equal(SupportStatus.Walkable, result.Support.Status);
        Assert.Equal(support, result.Support.Static);
        Assert.Equal(0f, result.VerticalVelocity);
    }

    static float Budget => Tuning.MaxStepClimbSpeed * Dt;

    // The landing tick of a 4 m/s run from x 0.6 launched at the given speed: the first descending tick with the axis
    // over the ledge and its top between the tick's end feet and StepHeight above its start feet. Every tick there is
    // one substep.
    static int LedgeLandingTick(double launch)
    {
        int k = 1;
        while (!(FreeV(launch, k) <= 0 && 0.6 + 4.0 * Dt * k > 1 && FreeY(0, launch, k) <= LedgeTop &&
            LedgeTop <= FreeY(0, launch, k - 1) + Tuning.StepHeight)) k++;
        return k;
    }

    public static IEnumerable<object[]> Physical() => [["box"], ["mesh"]];
    public static IEnumerable<object[]> Grounds() => [["box"], ["mesh"], ["terrain"]];

    [Theory]
    [InlineData("none")]
    [InlineData("box")]
    [InlineData("mesh")]
    public void GravityReachesTerminalSpeed(string ground)
    {
        // Seventy ticks fall about 68 m, so the floor 100 m below is never reached.
        using Ground space = Make(ground, v => Floor(v));
        var feet = new Vector3(0.5f, 100, -0.25f);
        float vy = 0;
        for (int k = 1; k <= 70; k++)
        {
            AirStepResult result = space.Step(feet, Vector2.Zero, vy);

            AssertNear(Math.Max(FreeV(0, k), -Tuning.MaxFallSpeed), result.VerticalVelocity, 1e-3, result);
            Assert.Equal(feet.Y + result.VerticalVelocity * Dt, result.Feet.Y);
            Assert.Equal(AirOutcome.Airborne, result.Outcome);
            Assert.False(result.Blocked);
            feet = result.Feet;
            vy = result.VerticalVelocity;
        }
        Assert.Equal(-Tuning.MaxFallSpeed, vy);
    }

    [Theory]
    [InlineData("box", 0.9f, 0f, 0f)]
    [InlineData("mesh", 0.9f, 0f, 0f)]
    [InlineData("terrain", 0.9f, 0f, 0f)]
    [InlineData("box", 0.8f, 6f, 3f)]
    [InlineData("mesh", 0.8f, 6f, 3f)]
    [InlineData("terrain", 0.8f, 6f, 3f)]
    public void FallLandsOnFlatWithTheContactSpeed(string ground, float startY, float vx, float vz)
    {
        using Ground floor = Make(ground, v => Floor(v), (_, _) => 0);
        var velocity = new Vector2(vx, vz);
        int k = LandingTick(startY, 0, 0);

        List<AirStepResult> ticks = floor.Run(new Vector3(0, startY, 0), velocity, 0, k + 1);

        Assert.Equal(k, ticks.Count);
        AirStepResult landed = ticks[^1];
        AssertLanded(landed, 0, floor["floor"]);
        AssertNear(Gravity * Dt * k, landed.FallSpeedAtContact, 1e-3, landed);
        // The landing substep is the first whose end reaches the floor. The rest of the tick's horizontal
        // displacement is left for the ground core.
        double tickStart = FreeY(startY, 0, k - 1);
        var displacement = new Vector3(vx * Dt, (float)(FreeV(0, k) * Dt), vz * Dt);
        int substeps = (int)Math.Ceiling(displacement.Length() / (0.5 * Radius));
        int landing = 1;
        while (tickStart + displacement.Y * landing / substeps > 0) landing++;
        double fraction = (double)landing / substeps;
        AssertNear(vx * Dt * fraction, landed.Achieved.X, 1e-5, landed);
        AssertNear(vz * Dt * fraction, landed.Achieved.Y, 1e-5, landed);
        AssertNear(vx * Dt * (1 - fraction), landed.Remaining.X, 1e-5, landed);
        AssertNear(vz * Dt * (1 - fraction), landed.Remaining.Y, 1e-5, landed);
    }

    [Theory]
    [InlineData(SceneVariant.Box, 1f)]
    [InlineData(SceneVariant.Mesh, 1f)]
    [InlineData(SceneVariant.Box, 0.05f)]
    [InlineData(SceneVariant.Mesh, 0.05f)]
    public void TerminalFallLandsOnAThinSlab(SceneVariant variant, float startY)
    {
        // One 30 Hz tick at 50 m/s falls 1.67 m, through the whole 0.05 m slab.
        using Ground slab = new(ThinSlab(variant));

        AirStepResult result = slab.Step(new Vector3(0.25f, startY, -0.5f), Vector2.Zero, -Tuning.MaxFallSpeed);

        AssertLanded(result, 0, slab["slab"]);
        Assert.Equal(Tuning.MaxFallSpeed, result.FallSpeedAtContact);
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void JumpUnderASlabStopsRising(string ground)
    {
        // The head at feet + 1.8 meets the slab's underside when the feet reach 0.5.
        using Ground space = Make(ground,
            v => Floor(v).Ceiling("slab", new Vector3(0, EaveUnderside, 0), 2, 2));
        double clearance = EaveUnderside - Height;
        int contact = 1;
        while (FreeY(0, Tuning.JumpSpeed, contact) <= clearance) contact++;
        int fall = LandingTick(clearance - Skin, 0, 0);

        List<AirStepResult> ticks = space.Run(Vector3.Zero, Vector2.Zero, Tuning.JumpSpeed, contact + fall + 1);

        Assert.Equal(contact + fall, ticks.Count);
        for (int k = 1; k < contact; k++)
            AssertNear(FreeY(0, Tuning.JumpSpeed, k), ticks[k - 1].Feet.Y, HalfSkin, ticks[k - 1]);
        AirStepResult stopped = ticks[contact - 1];
        AssertNear(clearance - Skin, stopped.Feet.Y, HalfSkin, stopped);
        AssertNear(0, stopped.VerticalVelocity, 1e-3, stopped);
        Assert.True(stopped.Blocked, $"{stopped}");
        Assert.Equal(AirOutcome.Airborne, stopped.Outcome);
        Assert.All(ticks, t => Assert.True(t.Feet.Y <= clearance, $"{t}"));
        AirStepResult landed = ticks[^1];
        AssertLanded(landed, 0, space["floor"]);
        AssertNear(Gravity * Dt * fall, landed.FallSpeedAtContact, 1e-3, landed);
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void JumpUnderALowCeilingStaysDown(string ground)
    {
        // The underside is two skins above the head, so the jump has one skin of room. The ceiling removes the
        // rise, and the floor within the legs band below the stopped feet lands the body on the jump tick or the next.
        float underside = Height + 2 * Skin;
        using Ground space = Make(ground, v => Floor(v).Ceiling("ceiling", new Vector3(0, underside, 0), 2, 2));

        List<AirStepResult> ticks = space.Run(Vector3.Zero, Vector2.Zero, Tuning.JumpSpeed, 2);

        Assert.True(ticks[0].Blocked, $"{ticks[0]}");
        Assert.All(ticks, t => Assert.InRange(t.Feet.Y, -HalfSkin, 2 * Skin));
        AssertLanded(ticks[^1], 0, space["floor"]);
    }

    [Theory]
    [InlineData("box", 0f)]
    [InlineData("mesh", 0f)]
    [InlineData("box", 20f)]
    [InlineData("mesh", 20f)]
    [InlineData("box", -20f)]
    [InlineData("mesh", -20f)]
    public void RunJumpUnderAnEaveNeverWedges(string ground, float degrees)
    {
        using Ground space = Make(ground, v => Eave(v, degrees));
        // Ceilings only shorten the rise and the fall, so the unobstructed flight bounds the landing.
        int limit = (int)Math.Ceiling(2 * Tuning.JumpSpeed / Gravity / Dt) + 2;
        // The shell stays under the eave's highest underside point, half a length times the tilt above its centre.
        double headroom = EaveUnderside + MathF.Sin(Radians(MathF.Abs(degrees))) - Height;

        List<AirStepResult> ticks = space.Run(new Vector3(0.6f, 0, 0), new Vector2(6, 0), Tuning.JumpSpeed, limit);

        AssertLanded(ticks[^1], 0, space["floor"]);
        Assert.Contains(ticks, t => t.Blocked);
        Assert.All(ticks, t =>
        {
            Assert.True(t.Feet.X <= 2 - Radius, $"{t}");
            Assert.True(t.Feet.Y <= headroom, $"{t}");
        });
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void RunJumpIntoAWallSlides(string ground)
    {
        using Ground space = Make(ground, v => Floor(v).Wall("wall", 1, 0.5f, 12));
        float stop = 1 - Radius;

        // A wall face is vertical, so the run-jump keeps its whole vertical arc and its tangent.
        int k = LandingTick(0, Tuning.JumpSpeed, 0);
        List<AirStepResult> jump = space.Run(Vector3.Zero, new Vector2(6, 3), Tuning.JumpSpeed, k + 1);

        Assert.Equal(k, jump.Count);
        AssertLanded(jump[^1], 0, space["floor"]);
        Assert.Contains(jump, t => t.Blocked);
        Assert.InRange(jump[^1].Feet.X, stop - 2 * Skin, stop);
        for (int i = 0; i < jump.Count; i++)
        {
            Assert.True(jump[i].Feet.X <= stop, $"{jump[i]}");
            if (i == jump.Count - 1) continue;
            AssertNear(3 * Dt, jump[i].Achieved.Y, HalfSkin, jump[i]);
            AssertNear(FreeY(0, Tuning.JumpSpeed, i + 1), jump[i].Feet.Y, HalfSkin, jump[i]);
        }

        // A terminal fall beside the wall moves 1.67 m a tick and never passes it.
        List<AirStepResult> fall = space.Run(new Vector3(0.5f, 9.5f, 0), new Vector2(6, 0), -Tuning.MaxFallSpeed, 7);

        Assert.Equal(6, fall.Count);
        AssertLanded(fall[^1], 0, space["floor"]);
        Assert.Equal(Tuning.MaxFallSpeed, fall[^1].FallSpeedAtContact);
        Assert.All(fall, t => Assert.True(t.Feet.X <= stop, $"{t}"));
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void InnerCornerStopsBothComponents(string ground)
    {
        using Ground space = Make(ground, InnerCorner);
        float stop = 1 - Radius;
        int k = LandingTick(0.9, 0, 0);

        List<AirStepResult> ticks = space.Run(new Vector3(0, 0.9f, 0), new Vector2(6, 6), 0, k + 1);

        Assert.Equal(k, ticks.Count);
        AirStepResult landed = ticks[^1];
        AssertLanded(landed, 0, space["floor"]);
        Assert.True(landed.Blocked, $"{landed}");
        Assert.InRange(landed.Feet.X, stop - 2 * Skin, stop);
        Assert.InRange(landed.Feet.Z, stop - 2 * Skin, stop);
        // Both walls are vertical, so the fall is never slowed.
        for (int i = 0; i < ticks.Count - 1; i++)
            AssertNear(FreeY(0.9, 0, i + 1), ticks[i].Feet.Y, HalfSkin, ticks[i]);
    }

    [Theory]
    [InlineData(SceneVariant.Box, 0.5f)]
    [InlineData(SceneVariant.Mesh, 0.5f)]
    [InlineData(SceneVariant.Box, -0.1f)]
    [InlineData(SceneVariant.Mesh, -0.1f)]
    public void FallOntoACrateTopIsNotSunk(SceneVariant variant, float axisX)
    {
        // At x -0.1 the axis is off the crate but the footprint reaches 0.1 over it, so the crate top supports.
        using Ground crate = new(Crate(variant));
        int k = LandingTick(1.2, 0, CrateTop);

        List<AirStepResult> ticks = crate.Run(new Vector3(axisX, 1.2f, 0), Vector2.Zero, 0, k + 1);

        Assert.Equal(k, ticks.Count);
        AssertLanded(ticks[^1], CrateTop, crate["crate"]);
        Assert.Equal(axisX, ticks[^1].Feet.X);
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void JumpOntoALedgeLandsWhenDescending(string ground)
    {
        // From x 0.6 at 4 m/s the shell's lower cap stays more than its radius from the ledge's edge at (1, 0.6), and
        // on the second tick the feet are still below the top with the footprint already over it.
        using Ground space = Make(ground, Ledge, (x, _) => x < 1 ? 0 : LedgeTop);
        int k = LandingTick(0, Tuning.JumpSpeed, LedgeTop);

        List<AirStepResult> ticks = space.Run(new Vector3(0.6f, 0, 0), new Vector2(4, 0), Tuning.JumpSpeed, k + 1);

        Assert.Equal(k, ticks.Count);
        Assert.Contains(ticks, t => t.VerticalVelocity > 0 && t.Feet.Y < LedgeTop && t.Feet.X > 1 - FootRadius);
        Assert.All(ticks, t => Assert.False(t.Blocked, $"{t}"));
        AirStepResult landed = ticks[^1];
        AssertLanded(landed, LedgeTop, space["ledge"]);
        AssertNear(-FreeV(Tuning.JumpSpeed, k), landed.FallSpeedAtContact, 1e-3, landed);
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void LandingNeverRisesAboveTheReach(string ground)
    {
        // The feet start 0.45 below the ledge top, beyond the legs band. On statics the footprint reaches 0.005 over
        // the edge and the shell's lower cap starts just clear of it. On terrain the body runs at the step.
        bool terrain = ground == "terrain";
        using Ground space = Make(ground, Ledge, (x, _) => x < 1 ? 0 : LedgeTop);
        var feet = new Vector3(terrain ? 0.95f : 0.805f, LedgeTop - 0.45f, 0);
        Vector2 velocity = terrain ? new Vector2(2, 0) : Vector2.Zero;
        float vy = 0;

        // Ninety ticks is a guard only, far past the 0.11 s free fall from 0.15.
        List<AirStepResult> ticks = space.Run(feet, velocity, vy, 90);

        AssertLanded(ticks[^1], 0, space["floor"]);
        foreach (AirStepResult tick in ticks)
        {
            float rising = MathF.Max(vy - Gravity * Dt, -Tuning.MaxFallSpeed);
            Assert.True(tick.Feet.Y <= feet.Y + MathF.Max(0, rising * Dt) + HalfSkin, $"{tick} from {feet}");
            if (terrain) Assert.True(tick.Feet.X < 1, $"{tick}");
            feet = tick.Feet;
            vy = tick.VerticalVelocity;
        }
    }

    [Theory]
    [InlineData("box", 0.9f)]
    [InlineData("mesh", 0.9f)]
    [InlineData("terrain", 0.9f)]
    [InlineData("box", 0.85f)]
    [InlineData("mesh", 0.85f)]
    [InlineData("terrain", 0.85f)]
    public void DropOntoAThirtyDegreeSlopeLands(string ground, float startY)
    {
        // The foot probe meets a 30 degree plane 0.2 (1 / cos 30 - 1) = 0.031 above the plane at the axis, so it
        // needs the legs band above the feet whatever span the landing substep travelled.
        float grade = MathF.Tan(Radians(30));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using Ground slope = Make(ground, v => Slope(v, 30), (x, _) => grade * x, (_, _) => normal);
        int k = LandingTick(startY, 0, 0);

        List<AirStepResult> ticks = slope.Run(new Vector3(0, startY, 0), Vector2.Zero, 0, k + 1);

        Assert.Equal(k, ticks.Count);
        AssertLanded(ticks[^1], 0, slope["slope"]);
        AssertNear(Gravity * Dt * k, ticks[^1].FallSpeedAtContact, 1e-3, ticks[^1]);
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void JumpPeakingUnderALedgeLandsOnIt(string ground)
    {
        // A 4.9 m/s launch peaks at 0.4 below the 0.6 top. Every tick is one substep, and the shell's lower cap stays
        // more than its radius from the edge at (1, 0.6) until the landing. The first descending tick with the axis
        // over the ledge and its top within the legs band lands on it.
        const float launch = 4.9f;
        using Ground space = Make(ground, Ledge, (x, _) => x < 1 ? 0 : LedgeTop);
        int k = LedgeLandingTick(launch);

        List<AirStepResult> ticks = space.Run(new Vector3(0.6f, 0, 0), new Vector2(4, 0), launch, k + 1);

        Assert.Equal(k, ticks.Count);
        Assert.All(ticks, t => Assert.False(t.Blocked, $"{t}"));
        Assert.All(ticks[..^1], t => Assert.True(t.Feet.Y < LedgeTop, $"{t}"));
        AirStepResult landed = ticks[^1];
        AssertLandedOn(landed, space["ledge"]);
        AssertNear(-FreeV(launch, k), landed.FallSpeedAtContact, 1e-3, landed);
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void LedgeLandingIsPaced(string ground)
    {
        // The jump above lands from 0.4 onto the 0.6 top, a climb of 0.2 against a budget of 3.5 m/s for one tick.
        // The landing tick rises at most the budget, and resting ground ticks pay the rest.
        const float launch = 4.9f;
        using Ground space = Make(ground, Ledge, (x, _) => x < 1 ? 0 : LedgeTop);
        int k = LedgeLandingTick(launch);
        double anchor = FreeY(0, launch, k - 1);

        List<AirStepResult> ticks = space.Run(new Vector3(0.6f, 0, 0), new Vector2(4, 0), launch, k);

        AirStepResult landed = ticks[^1];
        AssertLandedOn(landed, space["ledge"]);
        Assert.True(landed.Feet.Y - anchor <= Budget + landed.Support.HeightError + 1e-5, $"{landed} from {anchor}");
        Assert.True(landed.Feet.Y < LedgeTop - HalfSkin, $"{landed}");
        Vector3 feet = landed.Feet;
        int owed = (int)Math.Ceiling((LedgeTop - feet.Y) / Budget);
        for (int i = 0; i < owed; i++)
        {
            GroundStepResult rest = space.Rest(feet);
            Assert.True(rest.Feet.Y - feet.Y <= Budget + HalfSkin, $"{rest} from {feet}");
            feet = rest.Feet;
        }
        AssertNear(LedgeTop, feet.Y, HalfSkin, feet);
    }

    [Theory]
    [InlineData("box", 0f)]
    [InlineData("mesh", 0f)]
    [InlineData("box", 0.5f)]
    [InlineData("mesh", 0.5f)]
    public void RunJumpAlongAWallKeepsVerticalSpeed(string ground, float leanDegrees)
    {
        // A wall face through (1, 6) leaning over the run by leanDegrees, so its normal's Y is -sin(lean). One
        // contact a tick removes the run's 3 m/s into it, which changes the vertical speed by at most
        // (3 + JumpSpeed sin(lean)) sin(lean).
        using Ground space = Make(ground,
            v => Floor(v).Slab("wall", new Vector3(1, 6, 0), MathF.PI / 2 + Radians(leanDegrees), 6, 5, 0.5f));
        double lean = Math.Sin(Radians(leanDegrees));
        double perTick = (3 + Tuning.JumpSpeed * lean) * lean;

        List<AirStepResult> ticks = space.Run(Vector3.Zero, new Vector2(3, 6), Tuning.JumpSpeed, 30);

        AssertLanded(ticks[^1], 0, space["floor"]);
        Assert.Contains(ticks, t => t.Blocked && t.VerticalVelocity > 0);
        for (int i = 0; i < ticks.Count - 1; i++)
            AssertNear(FreeV(Tuning.JumpSpeed, i + 1), ticks[i].VerticalVelocity, 1e-3 + (i + 1) * perTick, ticks[i]);
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void FallBesideASteepFaceLandsAtTheToe(string ground)
    {
        // A 75 degree face rising from its toe at the origin. The shell's lower cap meets it with the feet
        // (R / n.y - StepHeight - R) = 0.745 above the plane at the axis, so the footprint never reaches the face and
        // the body slides down it airborne to the floor.
        float angle = Radians(75);
        using Ground space = Make(ground, v => Floor(v).Slab("face",
            new Vector3(2 * MathF.Cos(angle), 2 * MathF.Sin(angle), 0), angle, 2, 2));

        // Ninety ticks is a guard only.
        List<AirStepResult> ticks = space.Run(new Vector3(0.4f, 3, 0), Vector2.Zero, 0, 90);

        AssertLanded(ticks[^1], 0, space["floor"]);
        Assert.All(ticks[..^1], t => Assert.Equal(AirOutcome.Airborne, t.Outcome));
        Assert.Contains(ticks, t => t.Blocked);
        Assert.True(ticks[^1].Feet.X < 0, $"{ticks[^1]}");
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void FallOntoSteepStartsASlide(string ground)
    {
        // At 50 degrees the shell's lower cap reaches the plane only with the feet 0.18 below it at the axis, so the
        // footprint meets the face first.
        float grade = MathF.Tan(Radians(50));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using Ground slope = Make(ground, v => Slope(v, 50), (x, _) => grade * x, (_, _) => normal);
        int k = LandingTick(0.9, 0, 0);

        List<AirStepResult> ticks = slope.Run(new Vector3(0, 0.9f, 0), Vector2.Zero, 0, k + 1);

        Assert.Equal(k, ticks.Count);
        AirStepResult slid = ticks[^1];
        Assert.True(slid.Outcome == AirOutcome.Sliding, $"{slid}");
        Assert.Equal(SupportStatus.Steep, slid.Support.Status);
        Assert.Equal(slope["slope"], slid.Support.Static);
        AssertNear(0, slid.Feet.Y, HalfSkin, slid);
        AssertNear(Gravity * Dt * k, slid.FallSpeedAtContact, 1e-3, slid);
        Assert.Equal(-slid.FallSpeedAtContact, slid.VerticalVelocity);
    }

    [Theory]
    [InlineData(SceneVariant.Box, false)]
    [InlineData(SceneVariant.Mesh, false)]
    [InlineData(SceneVariant.Box, true)]
    [InlineData(SceneVariant.Mesh, true)]
    public void PacedLandingStaysWithinTheGroundBand(SceneVariant variant, bool roof)
    {
        // The feet start two skins above StepHeight below the ledge top, the lowest start whose shell clears the top
        // and whose band reaches it, so the paced seat leaves the largest lag, StepHeight less the budget less two
        // skins. A roof 1.5 above the top pushes the seat down until the shell clears it, at most to the shell's
        // lowest clear height over the top, so the lag stays under StepHeight. Either way one resting ground tick
        // still finds the top in its band.
        FootSupportScene built = Ledge(variant);
        if (roof) built.Ceiling("roof", new Vector3(2.5f, LedgeTop + 1.5f, 0), 1.5f, 2);
        using Ground space = new(built);
        var feet = new Vector3(1.5f, LedgeTop - Tuning.StepHeight + 2 * Skin, 0);

        AirStepResult landed = space.Step(feet, Vector2.Zero, 0);

        AssertLandedOn(landed, space["ledge"]);
        double lag = LedgeTop - landed.Feet.Y;
        if (roof)
            AssertNear(LedgeTop + 1.5 - Height - Skin, landed.Feet.Y, HalfSkin, landed);
        else
            AssertNear(Tuning.StepHeight - Budget - 2 * Skin, lag, HalfSkin, landed);
        Assert.True(lag < Tuning.StepHeight, $"{landed}");
        GroundStepResult rest = space.Rest(landed.Feet);
        Assert.Equal(GroundFooting.Walkable, rest.Footing);
        Assert.Equal(space["ledge"], rest.Support.Static);
        // Without the roof the climb is paid from the budget. Under it the shell has no room to rise.
        AssertNear(roof ? 0 : Budget, rest.Rise, HalfSkin, rest);
    }

    // A 75 degree face rising from its toe at the origin, 2 m along its slope, over the floor.
    static FootSupportScene SteepFace(SceneVariant variant)
    {
        float angle = Radians(75);
        return Floor(variant).Slab("face", new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * 2, angle, 2, 2);
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void RunIntoASteepFaceNeverLifts(string ground)
    {
        // From x 0 at feet 1.5 the shell's lower cap meets the face within three ticks at 6 m/s. Removing the run's
        // component into the face would turn it into upward speed, so the vertical speed and the arc stay at most
        // the free fall's.
        using Ground space = Make(ground, SteepFace);

        List<AirStepResult> ticks = space.Run(new Vector3(0, 1.5f, 0), new Vector2(6, 0), 0, 6);

        Assert.Equal(6, ticks.Count);
        Assert.Contains(ticks, t => t.Blocked);
        for (int i = 0; i < ticks.Count; i++)
        {
            Assert.True(ticks[i].VerticalVelocity <= FreeV(0, i + 1) + 1e-4, $"{ticks[i]}");
            Assert.True(ticks[i].Feet.Y <= FreeY(1.5, 0, i + 1) + HalfSkin, $"{ticks[i]}");
        }
    }

    [Theory]
    [MemberData(nameof(Physical))]
    public void RisingOntoAWalkableRampKeepsMoving(string ground)
    {
        // The feet start 0.3 under a 30 degree plane at x -1, so the shell's lower cap is 0.433 from it and meets it on
        // the first tick. Rising at 3 m/s against the plane's 3.46 m/s climb, the run keeps at least its projection
        // on the plane, 6 cos^2(30) m/s, less a skin per contact.
        float grade = MathF.Tan(Radians(30));
        using Ground space = Make(ground, v => Slope(v, 30));

        List<AirStepResult> ticks = space.Run(new Vector3(-1, -grade - 0.3f, 0), new Vector2(6, 0), 3, 3);

        Assert.Equal(3, ticks.Count);
        Assert.Contains(ticks, t => t.Blocked);
        double along = 6 * Math.Pow(Math.Cos(Radians(30)), 2) * Dt;
        Assert.All(ticks, t =>
        {
            Assert.True(t.VerticalVelocity > 0, $"{t}");
            Assert.True(t.Achieved.X >= along - 4 * Skin, $"{t}");
        });
    }

    [Theory]
    [InlineData(2.0f, 3f, false)]
    [InlineData(2.05f, 3f, true)]
    [InlineData(2.2f, 0f, false)]
    [InlineData(2.2f, 3f, true)]
    public void FootprintMustFitTheStepHeight(float fraction, float bandDegrees, bool rejected)
    {
        // The foot probe meets the steepest walkable plane footRadius (1 / cos(gate) - 1) above the plane at the axis,
        // and the landing band reaches StepHeight 0.4 above the feet. At the banded 48 degree gate the largest
        // fraction of the 0.4 radius is 2.022, at the bare 45 degree gate 2.414.
        MoveTuning tuning = Tuning with { TractionHysteresisRadians = Radians(bandDegrees) };
        var settings = new GroundCoreSettings(fraction);
        AirStepResult Step() => AirPass.Step(Vector3.Zero, Vector2.Zero, 0, Dt, Gravity, tuning, settings,
            tuning.MaxSlopeRadians, null, null, null, null);

        if (rejected) Assert.Throws<ArgumentOutOfRangeException>(() => Step());
        else Assert.Equal(AirOutcome.Airborne, Step().Outcome);
    }

    [Fact]
    public void JumpIntoASteepTerrainFaceNeverSinksIntoIt()
    {
        // A 5:1 analytic face, 78.7 degrees, rises to +X from x 0 with its own normal. A jump at JumpSpeed running at
        // 6 m/s from 0.05 short of the toe rises along the face. Steep terrain above the feet is a wall, so no tick
        // ends with the face above the feet at the axis. The descent meets the face within its travel and slides.
        const float Grade = 5;
        Vector3 normal = Vector3.Normalize(new Vector3(-Grade, 1, 0));
        float Face(float x, float z) => x < 0 ? 0 : Grade * x;
        using Ground space = new(null, Face, (x, _) => x < 0 ? Vector3.UnitY : normal);

        List<AirStepResult> ticks = space.Run(new Vector3(-0.05f, 0, 0), new Vector2(6, 0), Tuning.JumpSpeed, 60);

        Assert.All(ticks, t => Assert.True(Face(t.Feet.X, t.Feet.Z) <= t.Feet.Y + HalfSkin, $"{t}"));
        Assert.Equal(AirOutcome.Sliding, ticks[^1].Outcome);
        Assert.True(ticks[^1].Feet.X > 0, $"{ticks[^1]}");
    }

    [Fact]
    public void DiagonalRunIntoATerrainCliffKeepsAlongSpeed()
    {
        // A 3 m analytic cliff at x 1 whose delegate reports the face normal -X beyond the edge. A run at (6, 6) from
        // feet 0.5 meets it on the third tick, keeps its whole Z speed and lands on the sixth on the ground below.
        using Ground space = new(null, (x, _) => x < 1 ? 0 : 3, (x, _) => x < 1 ? Vector3.UnitY : -Vector3.UnitX);
        int k = LandingTick(0.5, 0, 0);

        List<AirStepResult> ticks = space.Run(new Vector3(0.5f, 0.5f, 0), new Vector2(6, 6), 0, k + 1);

        Assert.Equal(k, ticks.Count);
        AssertLanded(ticks[^1], 0, null);
        Assert.Contains(ticks, t => t.Blocked);
        Assert.All(ticks, t => Assert.True(t.Feet.X < 1, $"{t}"));
        for (int i = 0; i < ticks.Count - 1; i++)
            AssertNear(6 * Dt, ticks[i].Achieved.Y, HalfSkin, ticks[i]);
    }
}
