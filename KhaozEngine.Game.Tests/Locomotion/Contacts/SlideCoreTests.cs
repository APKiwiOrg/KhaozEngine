// Every expectation below is derived from the installed geometry and the closed form of a slide on one plane, never
// from a stepper run. The default tuning slides at the bare 45 degree gate with an 8 degree friction ramp, so a face
// at theta degrees pulls with scale s = clamp((theta - 45) / 8, 0, 1). From rest, tick k ends with fall-line speed
// g sin(theta) s dt k, horizontal speed that times cos(theta) and vertical speed that times -sin(theta), and the axis
// has travelled g sin(theta) cos(theta) s dt^2 k (k + 1) / 2 down the fall line.
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class SlideCoreTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new(FootRadiusFraction: 0.5f);
    const float Dt = 1f / 30f;
    const float HalfSkin = ShellMotion.ContactSkin / 2;
    static float Gate => Tuning.MaxSlopeRadians;
    static float Gravity => Tuning.Gravity;
    static float FootRadius => Settings.FootRadiusFraction * Tuning.CapsuleRadius;

    /// <summary>What the body slides on: installed statics or analytic terrain.</summary>
    sealed class Ground(FootSupportScene? scene, Func<float, float, float>? height = null,
        Func<float, float, Vector3>? normal = null) : IDisposable
    {
        IPhysicsWorld? World => scene?.World;
        IPhysicsQueryLease? Lease => scene is null ? null : scene.Lease;

        internal StaticHandle? this[string name] => scene?[name];

        // The installed top of static name, or the terrain, at (x, 0).
        internal double TopAt(string name, double x) =>
            scene is null ? height!((float)x, 0) : scene.TopHeightAt(name, x, 0);

        internal Vector3 Seated(string name, float x) => new(x, (float)TopAt(name, x), 0);

        internal bool Contact(Vector3 feet, out SupportSample support) =>
            SlideCore.InContact(feet, Tuning, Settings, Gate, height, normal, World, Lease, out support);

        internal SlideStepResult Step(Vector3 feet, Vector2 carry, float verticalVelocity, Vector2 steer,
            in SupportSample support) =>
            SlideCore.Step(feet, carry, verticalVelocity, steer, Dt, Tuning, Settings, Gate, support, height, normal,
                World, Lease);

        internal AirStepResult Air(Vector3 feet, Vector2 velocity, float verticalVelocity) =>
            AirPass.Step(feet, velocity, verticalVelocity, Dt, Gravity, Tuning, Settings, Gate, height, normal, World,
                Lease);

        // Slides from rest at feet until a tick does not end sliding, at most limit ticks. Every tick starts in
        // contact and carries the last tick's velocity, as the controller does.
        internal List<SlideStepResult> Run(Vector3 feet, int limit)
        {
            var ticks = new List<SlideStepResult>();
            Vector2 carry = Vector2.Zero;
            float verticalVelocity = 0;
            for (int i = 0; i < limit; i++)
            {
                Assert.True(Contact(feet, out SupportSample support), $"tick {i + 1} at {feet} not in contact: {support}");
                SlideStepResult result = Step(feet, carry, verticalVelocity, Vector2.Zero, support);
                ticks.Add(result);
                if (result.Outcome != SlideOutcome.Sliding) break;
                feet = result.Feet;
                carry = result.HorizontalVelocity;
                verticalVelocity = result.VerticalVelocity;
            }
            return ticks;
        }

        public void Dispose() => scene?.Dispose();
    }

    // A plane through the origin rising to +X by degrees, as installed statics or as analytic terrain.
    static Ground Plane(string ground, float degrees)
    {
        float grade = MathF.Tan(Radians(degrees));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        return ground switch
        {
            "box" => new Ground(Slope(SceneVariant.Box, degrees)),
            "mesh" => new Ground(Slope(SceneVariant.Mesh, degrees)),
            _ => new Ground(null, (x, _) => grade * x, (_, _) => normal),
        };
    }

    static double Scale(double degrees) =>
        Math.Clamp((degrees - 45) / (Tuning.SlideFrictionRampRadians * 180 / Math.PI), 0, 1);

    static double Rad(double degrees) => degrees * Math.PI / 180;

    // The fall-line speed after k ticks from rest.
    static double Fall(double degrees, int k) => Gravity * Math.Sin(Rad(degrees)) * Scale(degrees) * Dt * k;

    // The horizontal distance travelled down the fall line after k ticks from rest.
    static double Travel(double degrees, int k) =>
        Gravity * Math.Sin(Rad(degrees)) * Math.Cos(Rad(degrees)) * Scale(degrees) * (double)Dt * Dt * k * (k + 1) / 2;

    // The first tick whose travel reaches distance.
    static int TravelTick(double degrees, double distance)
    {
        int k = 1;
        while (Travel(degrees, k) < distance) k++;
        return k;
    }

    static void AssertNear(double expected, double actual, double tolerance, object context) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"Expected {expected:R}, got {actual:R}: {context}");

    public static IEnumerable<object[]> Grounds() => [["terrain"], ["box"], ["mesh"]];
    public static IEnumerable<object[]> Physical() => [["box"], ["mesh"]];

    [Fact]
    public void SixtyDegreeFaceSlidesToTheToeWithOneImpact()
    {
        // Analytic terrain: a plateau at 3 m, a 60 degree face falling to +X and a floor from the toe at x 0. The
        // body starts on the face 1.5 m from the toe, reaches it on tick K and lands there with the slide's vertical
        // speed. The shell is not involved, because analytic terrain has none to meet.
        float grade = MathF.Tan(Radians(60));
        float edge = -3 / grade;
        Vector3 face = Vector3.Normalize(new Vector3(grade, 1, 0));
        using var ground = new Ground(null,
            (x, _) => x >= 0 ? 0 : x <= edge ? 3 : -grade * x,
            (x, _) => x < 0 && x > edge ? face : Vector3.UnitY);
        int landing = TravelTick(60, 1.5);

        List<SlideStepResult> ticks = ground.Run(new Vector3(-1.5f, 1.5f * grade, 0), landing + 1);

        Assert.Equal(landing, ticks.Count);
        for (int k = 1; k < landing; k++)
        {
            SlideStepResult tick = ticks[k - 1];
            Assert.True(tick.Outcome == SlideOutcome.Sliding, $"tick {k}: {tick}");
            Assert.Equal(0f, tick.ImpactSpeed);
            AssertNear(-1.5 + Travel(60, k), tick.Feet.X, HalfSkin, tick);
            AssertNear(-grade * (double)tick.Feet.X, tick.Feet.Y, HalfSkin, tick);
        }
        SlideStepResult landed = ticks[^1];
        Assert.True(landed.Outcome == SlideOutcome.Landed, $"{landed}");
        Assert.Equal(SupportStatus.Walkable, landed.Support.Status);
        AssertNear(0, landed.Feet.Y, HalfSkin, landed);
        Assert.Equal(0f, landed.VerticalVelocity);
        AssertNear(Fall(60, landing) * Math.Sin(Rad(60)), landed.ImpactSpeed, 1e-3, landed);
    }

    [Theory]
    [InlineData("terrain", 46, 0.125)]
    [InlineData("terrain", 49, 0.5)]
    [InlineData("terrain", 53, 1.0)]
    [InlineData("terrain", 75, 1.0)]
    [InlineData("box", 46, 0.125)]
    [InlineData("box", 49, 0.5)]
    [InlineData("box", 53, 1.0)]
    [InlineData("mesh", 46, 0.125)]
    [InlineData("mesh", 49, 0.5)]
    [InlineData("mesh", 53, 1.0)]
    public void FrictionRampSetsTheSlideRate(string ground, float degrees, double scale)
    {
        // One tick from rest on the face gives the fall-line speed g sin(theta) s dt, which the ramp's closed form
        // fixes. The horizontal velocity is its cos(theta) part, down the fall line to -X.
        using Ground face = Plane(ground, degrees);
        Vector3 feet = face.Seated("slope", 0.5f);
        Assert.True(face.Contact(feet, out SupportSample support), $"{support}");
        Assert.Equal(SupportStatus.Steep, support.Status);

        SlideStepResult result = face.Step(feet, Vector2.Zero, 0, Vector2.Zero, support);

        Assert.True(result.Outcome == SlideOutcome.Sliding, $"{result}");
        double unit = Gravity * Math.Sin(Rad(degrees)) * Math.Cos(Rad(degrees)) * Dt;
        AssertNear(scale, -result.HorizontalVelocity.X / unit, 1e-3, result);
        AssertNear(0, result.HorizontalVelocity.Y, 1e-6, result);
        AssertNear(result.HorizontalVelocity.X * Dt, result.Achieved.X, HalfSkin, result);
        AssertNear(face.TopAt("slope", result.Feet.X), result.Feet.Y, HalfSkin, result);
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void SteerMovesAlongTheContourOnly(string ground)
    {
        // On a 50 degree face rising to +X the contour is Z. A steer of (5, 4) adds 4 dt along Z and nothing down the
        // fall line, and never enters the carried velocity.
        using Ground face = Plane(ground, 50);
        Vector3 feet = face.Seated("slope", 0.5f);
        Assert.True(face.Contact(feet, out SupportSample support), $"{support}");

        SlideStepResult free = face.Step(feet, Vector2.Zero, 0, Vector2.Zero, support);
        SlideStepResult steered = face.Step(feet, Vector2.Zero, 0, new Vector2(5, 4), support);

        Assert.True(steered.Outcome == SlideOutcome.Sliding, $"{steered}");
        Assert.Equal(free.HorizontalVelocity, steered.HorizontalVelocity);
        AssertNear(free.Achieved.X, steered.Achieved.X, HalfSkin, steered);
        AssertNear(free.Achieved.Y + 4 * Dt, steered.Achieved.Y, HalfSkin, steered);
        AssertNear(free.Feet.Y, steered.Feet.Y, HalfSkin, steered);
    }

    [Theory]
    [MemberData(nameof(Grounds))]
    public void VGullyWedges(string ground)
    {
        // A symmetric V with its crease along Z at x 0: 60 degree terrain faces, or 55 degree slabs, because a seated
        // shell overlaps a face steeper than acos((R + skin) / (StepHeight + R)), 59.9 degrees. The body slides
        // from x -0.6 into the crease, where the fall lines oppose. A tick that ends no lower than it started there
        // is wedged. It stalls only when the next substep, at most half the capsule radius, seats on the far face
        // above its feet, so it wedges within that distance of the crease.
        float degrees = ground == "terrain" ? 60 : 55;
        float angle = Radians(degrees), grade = MathF.Tan(angle);
        Vector3 left = Vector3.Normalize(new Vector3(grade, 1, 0)), right = Vector3.Normalize(new Vector3(-grade, 1, 0));
        FootSupportScene? scene = ground == "terrain" ? null :
            new FootSupportScene(ground == "box" ? SceneVariant.Box : SceneVariant.Mesh)
                .Slab("left", new Vector3(-MathF.Cos(angle), MathF.Sin(angle), 0), -angle, 1, 2)
                .Slab("right", new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0), angle, 1, 2);
        using var gully = scene is null
            ? new Ground(null, (x, _) => MathF.Abs(x) * grade, (x, _) => x <= 0 ? left : right)
            : new Ground(scene);

        // Sixty ticks is a guard only.
        List<SlideStepResult> ticks = gully.Run(gully.Seated("left", -0.6f), 60);

        SlideStepResult wedged = ticks[^1];
        Assert.True(wedged.Outcome == SlideOutcome.Wedged, $"{wedged}");
        Assert.Equal(SupportStatus.Steep, wedged.Support.Status);
        double reach = 0.5 * Tuning.CapsuleRadius;
        // It cannot wedge before it is within reach of the crease, so it has come down at least (0.6 - reach) tan
        // from its seated start.
        Vector3 start = gully.Seated("left", -0.6f);
        Assert.True(start.Y - wedged.Feet.Y >= (0.6 - reach) * grade - HalfSkin, $"{wedged} from {start}");
        Assert.True(Math.Abs(wedged.Feet.X) <= reach + HalfSkin, $"{wedged}");
        Assert.True(wedged.Feet.Y <= reach * grade + HalfSkin, $"{wedged}");
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SlideOnAPropMatchesTerrain(SceneVariant variant)
    {
        // A 50 degree slab and analytic terrain of the same plane, 8 ticks from x 1 (travel 0.31 m, inside the
        // slab): the same velocity within float error and the same feet within half the skin.
        using Ground prop = Plane(variant == SceneVariant.Box ? "box" : "mesh", 50);
        using Ground terrain = Plane("terrain", 50);

        List<SlideStepResult> onProp = prop.Run(prop.Seated("slope", 1), 8);
        List<SlideStepResult> onTerrain = terrain.Run(terrain.Seated("slope", 1), 8);

        Assert.Equal(8, onProp.Count);
        Assert.Equal(8, onTerrain.Count);
        for (int k = 0; k < 8; k++)
        {
            Assert.True(onProp[k].Outcome == SlideOutcome.Sliding, $"{onProp[k]}");
            Assert.True(onTerrain[k].Outcome == SlideOutcome.Sliding, $"{onTerrain[k]}");
            Assert.Equal(prop["slope"], onProp[k].Support.Static);
            AssertNear(onTerrain[k].HorizontalVelocity.X, onProp[k].HorizontalVelocity.X, 1e-4, onProp[k]);
            AssertNear(onTerrain[k].HorizontalVelocity.Y, onProp[k].HorizontalVelocity.Y, 1e-4, onProp[k]);
            AssertNear(onTerrain[k].VerticalVelocity, onProp[k].VerticalVelocity, 1e-3, onProp[k]);
            Assert.True(Vector3.Distance(onTerrain[k].Feet, onProp[k].Feet) <= HalfSkin,
                $"{onProp[k]} against {onTerrain[k]}");
        }
        AssertNear(-Fall(50, 8) * Math.Cos(Rad(50)), onProp[^1].HorizontalVelocity.X, 1e-4, onProp[^1]);
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SlideOntoAPropTopGrounds(SceneVariant variant)
    {
        // A crate top at 0.3 over x in [-1.5, 0] and a 50 degree face rising from its edge. The start at x 0.52 sits
        // between the travel of tick K - 1 (0.47) and tick K (0.56), so the body lands on the crate on tick K with
        // the slide's vertical speed, and no earlier tick reports an impact.
        float angle = Radians(50);
        using Ground ground = CrateBelowAFace(variant);
        int landing = TravelTick(50, 0.52);
        Assert.True(Travel(50, landing - 1) < 0.52 - 0.03 && Travel(50, landing) > 0.52 + 0.03);

        List<SlideStepResult> ticks = ground.Run(ground.Seated("face", 0.52f), landing + 1);

        Assert.Equal(landing, ticks.Count);
        Assert.All(ticks[..^1], t =>
        {
            Assert.True(t.Outcome == SlideOutcome.Sliding, $"{t}");
            Assert.Equal(0f, t.ImpactSpeed);
        });
        SlideStepResult landed = ticks[^1];
        Assert.True(landed.Outcome == SlideOutcome.Landed, $"{landed}");
        Assert.Equal(SupportStatus.Walkable, landed.Support.Status);
        Assert.Equal(ground["crate"], landed.Support.Static);
        AssertNear(CrateTop, landed.Feet.Y, HalfSkin, landed);
        Assert.Equal(0f, landed.VerticalVelocity);
        AssertNear(Fall(50, landing) * Math.Sin(angle), landed.ImpactSpeed, 1e-3, landed);
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void BlockedSlideKeepsOnlyAchievedSpeed(SceneVariant variant)
    {
        // A 50 degree face rising to +X and a crate whose vertical uphill side stands at x -0.3 over z in [-0.3, 0.3].
        // From x 1 the shell (radius 0.4) meets that side once the axis reaches x 0.1, after about 14 ticks of
        // closed-form fall (5.6 m/s down the fall line). A steer along +Z then carries it past the side's edge, and it
        // slides on. Every tick keeps a carry no faster than the move it achieved, so a blocked tick sheds the speed
        // it could not spend and the body leaves the crate slower than it met it.
        float angle = Radians(50);
        FootSupportScene scene = Slope(variant, 50);
        if (variant == SceneVariant.Box)
            scene.Slab("crate", new Vector3(-0.6f, 2, 0), 0, 0.3f, 0.3f, 4);
        else
        {
            Vector3 a = new(-0.3f, -2, -0.3f), b = new(-0.3f, -2, 0.3f), up = new(0, 4, 0);
            scene.Mesh("crate", [.. FootSupportScene.Facing(a, b, a + up, Vector3.UnitX),
                .. FootSupportScene.Facing(b, b + up, a + up, Vector3.UnitX)]);
        }
        using var ground = new Ground(scene);
        Vector3 feet = ground.Seated("slope", 1);
        Vector2 carry = Vector2.Zero;
        float verticalVelocity = 0;
        double before = 0;
        int blockedTicks = 0, freeAfter = 0;
        var trace = new System.Text.StringBuilder();
        // Ninety ticks is a guard only.
        for (int tick = 1; tick <= 90 && freeAfter < 3; tick++)
        {
            Assert.True(ground.Contact(feet, out SupportSample support), $"tick {tick}: {support}\n{trace}");
            // A tick starting with the shell against the side, within two skins of x 0.1 and short of its edge at
            // z 0.3 + R, is pressed, and steers from then on. Once past the edge the body is free again.
            bool pressed = feet.X < 0.1f + 2 * ShellMotion.ContactSkin && feet.Z < 0.3f + Tuning.CapsuleRadius;
            Vector2 steer = pressed || blockedTicks > 0 ? new Vector2(0, 4) : Vector2.Zero;
            SlideStepResult result = ground.Step(feet, carry, verticalVelocity, steer, support);
            trace.AppendLine($"tick {tick}: {result}");
            Assert.True(result.Outcome == SlideOutcome.Sliding, $"{trace}");

            double fallLine = -result.HorizontalVelocity.X / Math.Cos(angle);
            double contour = result.HorizontalVelocity.Y;
            double kept = Math.Sqrt(fallLine * fallLine + contour * contour);
            double rise = (double)result.Feet.Y - feet.Y;
            double achieved = Math.Sqrt((double)result.Achieved.X * result.Achieved.X +
                (double)result.Achieved.Y * result.Achieved.Y + rise * rise) / Dt;
            Assert.True(kept <= achieved + 1e-3, $"kept {kept:R} over achieved {achieved:R}\n{trace}");

            if (blockedTicks == 0 && !pressed) before = Math.Max(before, fallLine);
            if (pressed) blockedTicks++;
            else if (blockedTicks > 0) freeAfter++;
            feet = result.Feet;
            carry = result.HorizontalVelocity;
            verticalVelocity = result.VerticalVelocity;
        }
        Assert.True(blockedTicks > 0 && freeAfter == 3, $"{trace}");
        Assert.True(feet.Z > 0.7f, $"{trace}");
        double leaving = -carry.X / Math.Cos(angle);
        Assert.True(before > 5 && leaving < before, $"leaving {leaving:R} against {before:R}\n{trace}");
    }

    [Theory]
    [InlineData("feet")]
    [InlineData("carry")]
    [InlineData("verticalVelocity")]
    [InlineData("steer")]
    [InlineData("dt")]
    [InlineData("tractionSlopeRadians")]
    public void NonFiniteInputsNameTheirParameter(string name)
    {
        using Ground face = Plane("terrain", 50);
        Vector3 feet = face.Seated("slope", 0.5f);
        Assert.True(face.Contact(feet, out SupportSample support), $"{support}");
        float grade = MathF.Tan(Radians(50));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        float nan = float.NaN;

        ArgumentException thrown = Assert.ThrowsAny<ArgumentException>(() => SlideCore.Step(
            name == "feet" ? feet with { Y = nan } : feet,
            name == "carry" ? new Vector2(nan, 0) : Vector2.Zero,
            name == "verticalVelocity" ? nan : 0,
            name == "steer" ? new Vector2(0, nan) : Vector2.Zero,
            name == "dt" ? nan : Dt, Tuning, Settings,
            name == "tractionSlopeRadians" ? nan : Gate, support, (x, _) => grade * x, (_, _) => normal, null, null));

        Assert.Equal(name, thrown.ParamName);
    }

    [Fact]
    public void ZeroStepHeightIsRejected()
    {
        // A slide's substeps keep the drop along the face within half the step height, which needs a positive step
        // height. A zero step height is rejected before any substep is counted, because the shell could not clear
        // its walkable plane: (0 + R) cos(45) is under R.
        MoveTuning flat = Tuning with { StepHeight = 0 };
        float grade = MathF.Tan(Radians(50));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));

        Assert.Throws<ArgumentException>(() => GroundCore.Slide(new Vector3(0.5f, 0.5f * grade, 0),
            new Vector2(-0.1f, 0), Dt, flat, Settings, (x, _) => grade * x, (_, _) => normal, null, null, Gate));
    }

    // A crate top at 0.3 over x in [-1.5, 0] and a 50 degree face rising from its edge.
    static Ground CrateBelowAFace(SceneVariant variant)
    {
        float angle = Radians(50);
        return new Ground(new FootSupportScene(variant)
            .Flat("crate", -1.5f, 0, -2, 2, CrateTop)
            .Slab("face", new Vector3(MathF.Cos(angle), CrateTop + MathF.Sin(angle), 0), angle, 1, 2));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void FastSlideLandsOnAPropTopUnsunk(SceneVariant variant)
    {
        // From x 0.03 at 7.3 m/s down the fall line, one substep of the tick (under 0.168, the substep that keeps
        // the drop along the face within half the step height) ends more than budget / tan(50) past the crate's
        // edge. The face's plane extended there lies more than the climb budget below the crate top, so a landing
        // that charged the crate against the face's plane would leave the feet sunk. A slide drops onto the crate,
        // so nothing is charged and the feet seat on the top.
        float angle = Radians(50);
        using Ground ground = CrateBelowAFace(variant);
        const float start = 0.03f, speed = 7.3f;
        double fall = speed + Gravity * Math.Sin(angle) * Scale(50) * Dt;
        double travel = fall * Math.Cos(angle) * Dt;
        double budget = Tuning.MaxStepClimbSpeed * Dt;
        Assert.True(travel < 0.168 && (travel - start) * Math.Tan(angle) > budget + 0.03, $"{travel}");
        Vector3 feet = ground.Seated("face", start);
        Assert.True(ground.Contact(feet, out SupportSample support), $"{support}");

        SlideStepResult landed = ground.Step(feet, new Vector2(-speed * MathF.Cos(angle), 0),
            -speed * MathF.Sin(angle), Vector2.Zero, support);

        Assert.True(landed.Outcome == SlideOutcome.Landed, $"{landed}");
        Assert.Equal(ground["crate"], landed.Support.Static);
        AssertNear(CrateTop, landed.Feet.Y, HalfSkin, landed);
        AssertNear(-travel, landed.Achieved.X, HalfSkin, landed);
        AssertNear(fall * Math.Sin(angle), landed.ImpactSpeed, 1e-3, landed);
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SlideOffAnEdgeGoesAirborne(SceneVariant variant)
    {
        // A 50 degree face whose low edge is at (0, 2) with nothing below. The footprint keeps the face until the
        // axis is a foot radius past the edge, and the substep that leaves it is at most half the capsule radius. The
        // body leaves airborne with the slide's velocity, down the fall line.
        float angle = Radians(50);
        using var ground = new Ground(new FootSupportScene(variant)
            .Slab("face", new Vector3(MathF.Cos(angle), 2 + MathF.Sin(angle), 0), angle, 1, 2));

        // Forty ticks is a guard only.
        List<SlideStepResult> ticks = ground.Run(ground.Seated("face", 0.5f), 40);

        Assert.All(ticks[..^1], t => Assert.True(t.Outcome == SlideOutcome.Sliding, $"{t}"));
        SlideStepResult left = ticks[^1];
        Assert.True(left.Outcome == SlideOutcome.Airborne, $"{left}");
        Assert.True(left.Feet.X < 0 && left.Feet.X >= -(FootRadius + 0.5 * Tuning.CapsuleRadius) - HalfSkin,
            $"{left}");
        Assert.Equal(0f, left.ImpactSpeed);
        AssertNear(-Fall(50, ticks.Count) * Math.Cos(angle), left.HorizontalVelocity.X, 1e-3, left);
        AssertNear(0, left.HorizontalVelocity.Y, 1e-6, left);
        AssertNear(-Fall(50, ticks.Count) * Math.Sin(angle), left.VerticalVelocity, 1e-3, left);
        Assert.False(ground.Contact(left.Feet, out SupportSample after), $"{after}");
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SteepPropFaceHandsToTheAirPass(SceneVariant variant)
    {
        // A 70 degree face rising from its toe at the origin over a floor. A shell seated on it overlaps it, because
        // (StepHeight + R) cos(70) = 0.27 is under R, so the slide seat fails clearance and hands the body to the air
        // pass. From then on the shell keeps the feet at least R / cos(70) - StepHeight - R = 0.37 above the face at
        // the axis, and the body falls along it to the floor past the toe, never seated mid-face.
        float angle = Radians(70);
        using var ground = new Ground(Floor(variant)
            .Slab("face", new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * 2, angle, 2, 2));
        Vector3 feet = ground.Seated("face", 0.3f);
        Assert.True(ground.Contact(feet, out SupportSample support), $"{support}");
        double clear = Tuning.CapsuleRadius / Math.Cos(angle) - Tuning.StepHeight - Tuning.CapsuleRadius - HalfSkin;

        SlideStepResult handed = ground.Step(feet, Vector2.Zero, 0, Vector2.Zero, support);

        Assert.True(handed.Outcome == SlideOutcome.Airborne, $"{handed}");
        Assert.True(handed.Feet.Y - ground.TopAt("face", handed.Feet.X) >= clear, $"{handed}");
        feet = handed.Feet;
        float vy = handed.VerticalVelocity;
        AirStepResult air = default;
        // Sixty ticks is a guard only.
        for (int i = 0; i < 60; i++)
        {
            air = ground.Air(feet, handed.HorizontalVelocity, vy);
            Assert.True(air.Outcome != AirOutcome.Sliding, $"tick {i + 1}: {air}");
            if (air.Outcome == AirOutcome.Landed) break;
            if (air.Feet.X > 0)
                Assert.True(air.Feet.Y - ground.TopAt("face", air.Feet.X) >= clear, $"tick {i + 1}: {air}");
            feet = air.Feet;
            vy = air.VerticalVelocity;
        }
        Assert.True(air.Outcome == AirOutcome.Landed, $"{air}");
        Assert.Equal(ground["floor"], air.Support.Static);
        AssertNear(0, air.Feet.Y, HalfSkin, air);
        Assert.True(air.Feet.X < 0, $"{air}");
    }
}
