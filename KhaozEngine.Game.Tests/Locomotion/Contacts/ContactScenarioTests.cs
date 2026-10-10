// The steep-terrain scenarios of the legacy suites, mined for their geometry only and run through the whole tick,
// ContactController.Step, at 30 and 60 Hz: #440 (SteepSlopeSlideTests), #470 and #475 (TractionHysteresisTests),
// #468 (ClampRatchetTests and ClampRatchetSweepTests) and #486 (CliffToeWallTests). Every bound comes from the
// geometry, the closed forms or the spec's invariants, never from a run, and none is finer than half the contact skin.
// Analytic terrain runs every scenario. Props run where the shell can occupy the scenario: a face of 60 degrees or
// steeper meets the shell before the footprint, so a prop body falls along such a face instead of sliding on it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class ContactScenarioTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    const float HalfSkin = ShellMotion.ContactSkin / 2;
    static float HalfHeight => Tuning.CapsuleHalfHeight;
    // The default settings' foot disc: half the capsule radius.
    static float FootRadius => 0.5f * Tuning.CapsuleRadius;

    static float Abyss(float x, float z) => -1000;

    /// <summary>What the body moves on: installed statics over a terrain far below, or analytic terrain.</summary>
    sealed class Ground(FootSupportScene? scene, Func<float, float, float> height,
        Func<float, float, Vector3>? normal = null) : IDisposable
    {
        internal bool IsTerrain => scene is null;

        internal MoveState Step(in MoveState state, Vector2 moveDir, bool run, bool jump, float dt,
            in MoveTuning tuning) =>
            ContactController.Step(state, moveDir, moveDir == Vector2.Zero ? 0 : 1, run, jump, dt, height, tuning,
                normal, scene?.World, null, null);

        public void Dispose() => scene?.Dispose();
    }

    static Ground Make(string ground, Func<SceneVariant, FootSupportScene> scene, Func<float, float, float> terrain,
        Func<float, float, Vector3> normal) => ground switch
        {
            "box" => new Ground(scene(SceneVariant.Box), Abyss),
            "mesh" => new Ground(scene(SceneVariant.Mesh), Abyss),
            _ => new Ground(null, terrain, normal),
        };

    static MoveState Standing(Vector3 feet) => new() { Position = feet + HalfHeight * Vector3.UnitY, Grounded = true };

    static MoveState Falling(Vector3 feet) =>
        new() { Position = feet + HalfHeight * Vector3.UnitY, TimeSinceGrounded = 1 };

    static Vector3 Feet(in MoveState state) => state.Position - HalfHeight * Vector3.UnitY;

    static bool Footed(in MoveState s) => s.Grounded || s.SupportGranted;

    static string Show(in MoveState s) =>
        $"feet {Feet(s)} vy {s.VerticalVelocity} grounded {s.Grounded} granted {s.SupportGranted} " +
        $"h {s.HorizontalVelocity}";

    // A plane through the origin rising to +X at this grade, and its honest normal.
    static Vector3 RisingNormal(float grade) => Vector3.Normalize(new Vector3(-grade, 1, 0));

    // ---- #440: a held jump against a sheer face ----

    public static IEnumerable<object[]> SheerFaceRows()
    {
        foreach (string ground in new[] { "terrain", "box", "mesh" })
            foreach (int hz in new[] { 30, 60 })
                foreach (bool run in new[] { false, true })
                    foreach (bool momentum in new[] { false, true })
                        yield return [ground, hz, run, momentum];
    }

    // A 5:1 face, 78.7 degrees, rises to +X from its toe at x 0 out of a floor at 0. Terrain starts 0.05 from the toe.
    // A prop face meets the shell first, so a prop body starts where its shell's lowest hemisphere, at
    // StepHeight + radius above the feet, clears the face plane by the skin, and every jump is a wall jump. The floor
    // is the only ground a body can stand on, so a footed tick has its feet on it. The jump's ballistic reach is
    // JumpSpeed^2 / 2g, and the air pass lands only onto walkable support within StepHeight of the feet, so no tick
    // rises past that reach plus StepHeight and a 0.05 margin. Over 3000 held-jump ticks, after the first tenth, the
    // second half never peaks 20 mm above the first: the cycle gains no altitude.
    [Theory]
    [MemberData(nameof(SheerFaceRows))]
    public void HeldJumpNeverClimbsASheerFace(string ground, int hz, bool run, bool momentum)
    {
        const float Grade = 5;
        float degrees = MathF.Atan(Grade) * 180 / MathF.PI, angle = MathF.Atan(Grade);
        Vector3 faceNormal = RisingNormal(Grade);
        using Ground g = Make(ground, v => RisingFace(v, degrees, 0, 6),
            (x, _) => x < 0 ? 0 : Grade * x, (x, _) => x < 0 ? Vector3.UnitY : faceNormal);
        MoveTuning tuning = Tuning with { AirMomentum = momentum };
        float dt = 1f / hz;
        float start = g.IsTerrain
            ? -0.05f
            : ((tuning.StepHeight + tuning.CapsuleRadius) * MathF.Cos(angle) - tuning.CapsuleRadius -
                ShellMotion.ContactSkin) / MathF.Sin(angle) - HalfSkin;
        double ceiling = (double)tuning.JumpSpeed * tuning.JumpSpeed / (2 * tuning.Gravity) + tuning.StepHeight + 0.05;

        const int Ticks = 3000, Settled = Ticks / 10, Half = Settled + (Ticks - Settled) / 2;
        MoveState s = Standing(new Vector3(start, 0, 0));
        int jumps = 0;
        double firstMax = double.NegativeInfinity, secondMax = double.NegativeInfinity;
        for (int tick = 0; tick < Ticks; tick++)
        {
            s = g.Step(s, Vector2.UnitX, run, true, dt, tuning);
            Vector3 feet = Feet(s);
            if (s.VerticalVelocity == tuning.JumpSpeed) jumps++;
            Assert.True(!Footed(s) || (Math.Abs(feet.Y) <= HalfSkin && feet.X <= HalfSkin),
                $"tick {tick} has footing off the floor: {Show(s)}");
            Assert.True(feet.Y <= ceiling, $"tick {tick} rose past {ceiling}: {Show(s)}");
            if (tick < Settled) continue;
            if (tick < Half) firstMax = Math.Max(firstMax, feet.Y);
            else secondMax = Math.Max(secondMax, feet.Y);
        }
        // A free hop from the floor lasts 2 JumpSpeed / g. The cycle must at least manage one hop in two.
        double hops = Ticks * dt * tuning.Gravity / (2 * tuning.JumpSpeed);
        Assert.True(jumps >= hops / 2, $"only {jumps} jumps against {hops:F1} free hops");
        Assert.True(secondMax <= firstMax + 0.02, $"the cycle ratcheted: first {firstMax}, second {secondMax}");
    }

    // ---- #470: a lip onto a steep face ----

    // The top is level for x at most 0. A 0.15 lip drops onto a face falling to +X at 2:1, 63.4 degrees, through 3 m to
    // a pit. Terrain starts on the edge, a prop body where its foot disc still overlaps the top by a centimetre. The
    // first tick walks at 6 m/s, so at 60 Hz it ends 0.1 further with the support under the disc about 0.35 below the
    // top: the legacy repro's drop, inside StepHeight. At 30 Hz the drop is deeper. Coyote time is zero and the jump is
    // pressed on that tick, so only footing on the face could launch it. The crossing tick is neither footed nor
    // launched, no tick is footed anywhere but the top or the pit, and the body ends on the pit past the face.
    public static IEnumerable<object[]> Rates() =>
        from ground in new[] { "terrain", "box", "mesh" }
        from hz in new[] { 30, 60 }
        select new object[] { ground, hz };

    [Theory]
    [MemberData(nameof(Rates))]
    public void LipOntoSteepFaceNeverSeats(string ground, int hz)
    {
        const float Lip = 0.15f, Gradient = 2, Depth = 3;
        float degrees = MathF.Atan(Gradient) * 180 / MathF.PI, run = Depth / Gradient, pitY = -Lip - Depth;
        Vector3 faceNormal = Vector3.Normalize(new Vector3(Gradient, 1, 0));
        using Ground g = Make(ground, v => LipOntoFace(v, 0, Lip, degrees, Depth),
            (x, _) => x <= 0 ? 0 : x < run ? -Lip - Gradient * x : pitY,
            (x, _) => x > 0 && x < run ? faceNormal : Vector3.UnitY);
        MoveTuning tuning = Tuning with { CoyoteTime = 0 };
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(g.IsTerrain ? 0 : FootRadius - 0.01f, 0, 0));

        s = g.Step(s, Vector2.UnitX, false, true, dt, tuning);
        Assert.True(Feet(s).X > (g.IsTerrain ? 0 : FootRadius), $"the crossing tick never left the top: {Show(s)}");
        Assert.False(s.Grounded, $"the crossing tick seated on the face: {Show(s)}");
        Assert.False(s.SupportGranted, $"the crossing tick granted support on the face: {Show(s)}");
        Assert.True(s.VerticalVelocity <= 0, $"the crossing tick launched: {Show(s)}");

        for (int tick = 1; tick < 2 * hz; tick++)
        {
            s = g.Step(s, Vector2.UnitX, false, false, dt, tuning);
            Assert.True(s.VerticalVelocity <= 0, $"tick {tick} launched: {Show(s)}");
            Assert.True(!Footed(s) || Math.Abs(Feet(s).Y - pitY) <= HalfSkin,
                $"tick {tick} found footing off the pit: {Show(s)}");
        }
        Assert.True(s.Grounded, $"the body never reached the pit: {Show(s)}");
        Assert.True(Feet(s).X > run, $"the body ended on the face or the top: {Show(s)}");
    }

    // The control: the same walk off a 0.35 lip onto a level shelf is a step down within StepHeight, so the first tick
    // seats on the shelf, footed, and every later tick stays there.
    [Theory]
    [MemberData(nameof(Rates))]
    public void LipOntoLevelShelfSeatsInOneTick(string ground, int hz)
    {
        const float Lip = 0.35f;
        using Ground g = Make(ground, v => LipOntoFace(v, 0, Lip, 0, 0), (x, _) => x <= 0 ? 0 : -Lip,
            (_, _) => Vector3.UnitY);
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(g.IsTerrain ? 0 : FootRadius - 0.01f, 0, 0));
        for (int tick = 0; tick < hz; tick++)
        {
            s = g.Step(s, Vector2.UnitX, false, false, dt, Tuning);
            Assert.True(s.Grounded && s.SupportGranted, $"tick {tick} lost footing: {Show(s)}");
            Assert.True(Math.Abs(Feet(s).Y + Lip) <= HalfSkin, $"tick {tick} is not on the shelf: {Show(s)}");
        }
    }

    // ---- #468: a held run-jump across a creased cliff ----

    // The legacy normal for the creased cliff: a central difference over a 5 m stencil, wider than the 4 m creases, so
    // it never matches the plane under the feet.
    static Vector3 SmoothedCreaseNormal(float x, float z)
    {
        const float Stencil = 5;
        float dx = (CreasedCliffHeight(x + Stencil, z) - CreasedCliffHeight(x - Stencil, z)) / (2 * Stencil);
        float dz = (CreasedCliffHeight(x, z + Stencil) - CreasedCliffHeight(x, z - Stencil)) / (2 * Stencil);
        return Vector3.Normalize(new Vector3(-dx, 1, -dz));
    }

    public static IEnumerable<object[]> CreaseRows() =>
        from ground in new[] { "terrain", "smoothed", "mesh" }
        from hz in new[] { 30, 60 }
        select new object[] { ground, hz };

    // Every cell is a plane of 68.6 to 77.1 degrees whose fall lines lie within 24 degrees of each other, so there is
    // no footing and no wedge anywhere. Terrain seeds at rest on the face at (2, 2) with each cell's own normal, or
    // with the legacy smoothed normal. The mesh is the same planes, so its body seeds a radius and a skin out along
    // the plane's normal, where the shell clears it, and falls along the face. Holding a run and the jump along each
    // of 24 headings for 10 s, no tick is footed or launched and every tick descends, which is stronger than
    // invariant 5's bound on a footless rise. The peak feet between one crease crossing and the next never rise above
    // the previous peak, over at least two crossings.
    [Theory]
    [MemberData(nameof(CreaseRows))]
    public void CreasedCliffNeverRatchets(string ground, int hz)
    {
        using Ground g = ground switch
        {
            "mesh" => new Ground(CreasedCliff(), Abyss),
            "smoothed" => new Ground(null, CreasedCliffHeight, SmoothedCreaseNormal),
            _ => new Ground(null, CreasedCliffHeight, CreasedCliffNormal),
        };
        float dt = 1f / hz;
        // The seed cell's plane: x gradient 0.5, z gradient -4.1.
        Vector3 plane = Vector3.Normalize(new Vector3(-0.5f, 1, 4.1f));
        var surface = new Vector3(2, CreasedCliffHeight(2, 2), 2);
        Vector3 seed = g.IsTerrain ? surface : surface + (Tuning.CapsuleRadius + ShellMotion.ContactSkin) * plane;
        static (int, int) Cell(Vector3 feet) =>
            ((int)MathF.Floor(feet.X / CreaseSpacing), (int)MathF.Floor(feet.Z / CreaseSpacing));

        for (int degrees = 0; degrees < 360; degrees += 15)
        {
            float heading = Radians(degrees);
            var direction = new Vector2(MathF.Cos(heading), MathF.Sin(heading));
            MoveState s = Falling(seed);
            (int, int) cell = Cell(seed);
            double peak = seed.Y, previousPeak = double.PositiveInfinity;
            int crossings = 0;
            for (int tick = 0; tick < 10 * hz; tick++)
            {
                float before = Feet(s).Y;
                s = g.Step(s, direction, true, true, dt, Tuning);
                Vector3 feet = Feet(s);
                string context = $"heading {degrees}, tick {tick}: {Show(s)}";
                Assert.False(Footed(s), $"footing on the cliff, {context}");
                Assert.True(s.VerticalVelocity != Tuning.JumpSpeed, $"launched, {context}");
                Assert.True(feet.Y <= before - HalfSkin, $"did not descend, {context}");
                if (Cell(feet) != cell)
                {
                    Assert.True(peak <= previousPeak + HalfSkin,
                        $"crossing {crossings} peaked {peak} above {previousPeak}, {context}");
                    (previousPeak, peak, cell) = (peak, feet.Y, Cell(feet));
                    crossings++;
                }
                peak = Math.Max(peak, feet.Y);
            }
            Assert.True(peak <= previousPeak + HalfSkin, $"heading {degrees}: the last stretch peaked {peak}");
            Assert.True(crossings >= 2, $"heading {degrees}: only {crossings} crossings, {Show(s)}");
        }
    }

    // ---- #475: traction hysteresis on a bank ----

    // A bank rises to +X from its toe at x 0 out of a floor at 0. A walker standing 0.2 short of the toe walks up it at
    // 6 m/s. A lander drops idle from 3 m above the bank 1.5 along it, without footing. Only a body that keeps its
    // footing reads the banded gate, MaxSlope + 3 degrees. So at 47 degrees the walker stays footed on every tick until
    // it passes x 1.5, while the lander is never footed on the bank and slides down it. At 49 degrees, past the band,
    // the walker is refused at the toe and never leaves the floor, and the lander slides. With both traction knobs at
    // zero the gate is bare, so 47 degrees refuses the walker as 49 does.
    [Theory]
    [MemberData(nameof(Rates))]
    public void BankHysteresis(string ground, int hz)
    {
        MoveTuning bare = Tuning with { TractionHysteresisRadians = 0, SlideFrictionRampRadians = 0 };
        foreach ((float degrees, MoveTuning tuning, bool walkerKeeps) in new[]
            { (47f, Tuning, true), (49f, Tuning, false), (47f, bare, false) })
        {
            float grade = MathF.Tan(Radians(degrees));
            Vector3 normal = RisingNormal(grade);
            using Ground g = Make(ground, v => RisingFace(v, degrees, 0, 4),
                (x, _) => x < 0 ? 0 : grade * x, (x, _) => x < 0 ? Vector3.UnitY : normal);
            string row = $"{degrees} degrees, band {tuning.TractionHysteresisRadians}";
            Walker(g, hz, tuning, walkerKeeps, row);
            Lander(g, hz, tuning, grade, row);
        }
    }

    const float ProbeX = 1.5f;

    static void Walker(Ground g, int hz, in MoveTuning tuning, bool keeps, string row)
    {
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(-0.2f, 0, 0));
        for (int tick = 0; tick < 4 * hz && Feet(s).X < ProbeX; tick++)
        {
            s = g.Step(s, Vector2.UnitX, false, false, dt, tuning);
            if (keeps)
                Assert.True(s.Grounded && s.SupportGranted, $"{row}: walker tick {tick} lost footing: {Show(s)}");
            else
                Assert.True(!Footed(s) || (Math.Abs(Feet(s).Y) <= HalfSkin && Feet(s).X <= HalfSkin),
                    $"{row}: walker tick {tick} footed on the bank: {Show(s)}");
        }
        if (keeps) Assert.True(Feet(s).X >= ProbeX, $"{row}: the walker stalled: {Show(s)}");
        else Assert.True(Feet(s).X <= HalfSkin && s.Grounded, $"{row}: the walker was not held at the toe: {Show(s)}");
    }

    static void Lander(Ground g, int hz, in MoveTuning tuning, float grade, string row)
    {
        float dt = 1f / hz;
        var start = new Vector3(ProbeX, grade * ProbeX + 3, 0);
        MoveState s = Falling(start);
        for (int tick = 0; tick < 4 * hz; tick++)
        {
            s = g.Step(s, Vector2.Zero, false, false, dt, tuning);
            Assert.True(!Footed(s) || (Math.Abs(Feet(s).Y) <= HalfSkin && Feet(s).X <= HalfSkin),
                $"{row}: lander tick {tick} footed on the bank: {Show(s)}");
        }
        Assert.True(Feet(s).Y < start.Y - 3 - 1, $"{row}: the lander never slid: {Show(s)}");
        Assert.True(Feet(s).X < ProbeX, $"{row}: the lander slid uphill: {Show(s)}");
    }

    // ---- #486: walking at a cliff toe ----

    // A 60 degree face rises to +X from its toe at x 0 out of a floor at 0, both 24 m across. A walker starting 1 short
    // of the toe walks into it, and along it at 45 degrees, for 3 s. Steep ground above the feet is a wall to a footed
    // body, so every tick stays footed on the floor: footing never alternates. The walk into the face reaches the toe
    // within one tick's travel, or for a prop the point where its foot disc touches the face. The walk along it keeps
    // at least half of its free travel along the face.
    [Theory]
    [MemberData(nameof(Rates))]
    public void CliffToeNeverFlipsFooting(string ground, int hz)
    {
        float grade = MathF.Tan(Radians(60));
        Vector3 normal = RisingNormal(grade);
        using Ground g = Make(ground, v => RisingFace(v, 60, 0, 4, 12),
            (x, _) => x < 0 ? 0 : grade * x, (x, _) => x < 0 ? Vector3.UnitY : normal);
        float dt = 1f / hz;
        float stop = (g.IsTerrain ? 0 : -FootRadius) - Tuning.WalkSpeed * dt;
        foreach (Vector2 direction in new[] { Vector2.UnitX, Vector2.Normalize(Vector2.One) })
        {
            var start = new Vector3(-1, 0, -10);
            MoveState s = Standing(start);
            for (int tick = 0; tick < 3 * hz; tick++)
            {
                s = g.Step(s, direction, false, false, dt, Tuning);
                string context = $"direction {direction}, tick {tick}: {Show(s)}";
                Assert.True(s.Grounded && s.SupportGranted, $"footing flipped, {context}");
                Assert.True(Math.Abs(Feet(s).Y) <= HalfSkin, $"left the floor, {context}");
                Assert.True(Feet(s).X <= HalfSkin, $"passed the toe, {context}");
            }
            Assert.True(Feet(s).X >= stop, $"direction {direction}: stalled short of the toe: {Show(s)}");
            if (direction.Y == 0) continue;
            double free = Tuning.WalkSpeed * direction.Y * dt * 3 * hz;
            Assert.True(Feet(s).Z - start.Z >= free / 2,
                $"direction {direction}: lost the along-face travel: {Show(s)}");
        }
    }
}
