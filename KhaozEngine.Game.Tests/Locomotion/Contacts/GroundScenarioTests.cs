// Multi-tick scenarios through GroundCore.Step at 30 Hz. Every tick feeds its result's feet into the next. The
// default tuning gives a shell radius of 0.4, substeps of at most 0.2, a foot disc radius of 0.2 and a step
// budget of MaxStepClimbSpeed * dt = 3.5 / 30 per tick.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class GroundScenarioTests(ITestOutputHelper output)
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new();
    const float Dt = 1f / 30f;
    // The navigation arrival ball.
    const float ArrivalBall = 0.001f;
    const int StairRisers = 6;

    static GroundStepResult Step(IPhysicsWorld world, IPhysicsQueryLease lease, in MoveTuning tuning, Vector3 feet,
        Vector2 displacement) =>
        GroundCore.Step(feet, displacement, Dt, tuning, Settings, null, null, world, lease);

    static GroundStepResult Step(FootSupportScene scene, in MoveTuning tuning, Vector3 feet, Vector2 displacement) =>
        Step(scene.World, scene.Lease, tuning, feet, displacement);

    // The support the core itself queries at a tick start: the start axis, a band of StepHeight both ways.
    static SupportSample Support(FootSupportScene scene, in MoveTuning tuning, Vector2 axis, float feetY) =>
        FootSupport.Find(null, null, scene.World, scene.Lease, new FootSupportQuery(axis, feetY,
            Settings.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, tuning.StepHeight,
            MathF.Cos(tuning.MaxSlopeRadians)));

    static Vector2 Axis(Vector3 feet) => new(feet.X, feet.Z);

    // The feet a resting body has at an axis: its certified support height, found from a guess within the band.
    static Vector3 Rest(FootSupportScene scene, in MoveTuning tuning, Vector2 axis, float guessY)
    {
        SupportSample support = Support(scene, tuning, axis, guessY);
        Assert.True(support.Status == SupportStatus.Walkable, $"No walkable rest at {axis}: {support}");
        return new Vector3(axis.X, support.Height, axis.Y);
    }

    // Commands min(remaining, speed * dt) toward the target axis every tick. Returns the tick count on arrival
    // within the ball, else -1 with the trace in the message.
    static int Approach(FootSupportScene scene, in MoveTuning tuning, Vector3 feet, Vector3 target, float speed,
        int maxTicks, StringBuilder trace)
    {
        for (int tick = 1; tick <= maxTicks; tick++)
        {
            Vector2 remaining = Axis(target) - Axis(feet);
            float step = speed * Dt, length = remaining.Length();
            Vector2 move = length <= step ? remaining : remaining * (step / length);
            GroundStepResult result = Step(scene, tuning, feet, move);
            trace.AppendLine($"tick {tick}: move {move} -> {result}");
            Assert.True(result.Footing == GroundFooting.Walkable, $"Footing left walkable.\n{trace}");
            feet = result.Feet;
            if (Vector3.Distance(feet, target) <= ArrivalBall) return tick;
        }
        return -1;
    }

    // KhaozEngine #1270: the 4.25 cm lip with the reporter's capsule, from the bank toward the lip edge at x 0. The
    // target is whatever FootSupport reports at x -0.125. The 0.15 probe's rounded bottom meets the floor before
    // the lip edge 0.125 off its axis, so that is the floor.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void RestingBankApproachConverges(SceneVariant variant)
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f, CapsuleHalfHeight = 0.75f };
        using FootSupportScene scene = Lip(variant);
        Vector3 start = Rest(scene, tuning, new Vector2(-0.375f, 0), 0);
        Vector3 target = Rest(scene, tuning, new Vector2(-0.125f, 0), start.Y);

        var trace = new StringBuilder();
        int ticks = Approach(scene, tuning, start, target, 1, 30, trace);

        Assert.True(ticks > 0, $"No arrival within 30 ticks at {target}.\n{trace}");
        output.WriteLine($"{variant}: target {target}, arrived in {ticks} ticks");
    }

    // KhaozEngine #1265: edges of 0.25 on smooth mesh slopes rising to +X. The sideways edge crosses the quad's
    // diagonal.
    [Theory]
    [InlineData(0.08f, "uphill")]
    [InlineData(0.08f, "downhill")]
    [InlineData(0.08f, "sideways")]
    [InlineData(0.25f, "uphill")]
    [InlineData(0.25f, "downhill")]
    [InlineData(0.25f, "sideways")]
    public void SlopeEdgesConverge(float grade, string heading)
    {
        using FootSupportScene scene =
            new FootSupportScene(SceneVariant.Mesh).Slab("slope", Vector3.Zero, MathF.Atan(grade), 2, 2);
        (Vector2 from, Vector2 to) = heading switch
        {
            "uphill" => (new Vector2(-0.125f, 0.3f), new Vector2(0.125f, 0.3f)),
            "downhill" => (new Vector2(0.125f, 0.3f), new Vector2(-0.125f, 0.3f)),
            _ => (new Vector2(0.1f, -0.125f), new Vector2(0.1f, 0.125f)),
        };
        Vector3 start = Rest(scene, Tuning, from, (float)scene.TopHeightAt("slope", from.X, from.Y));
        Vector3 target = Rest(scene, Tuning, to, start.Y);

        var trace = new StringBuilder();
        int ticks = Approach(scene, Tuning, start, target, 1, 30, trace);

        Assert.True(ticks > 0, $"No arrival within 30 ticks at {target}.\n{trace}");
        output.WriteLine($"{grade} {heading}: arrived in {ticks} ticks");
    }

    // Rows where a substep lands a probe on a convex nosing, which the feature query cannot resolve (#1342). The
    // body stalls there for good, so the run never reaches the top. They stay pinned until phase 2b.
    const string NosingGap = "Blocked by #1342: the feature query is Unresolved at a convex nosing";
    static readonly (SceneVariant Variant, float Tread, float Riser, float Speed)[] NosingStalls =
    [
        (SceneVariant.Box, 0.40f, 0.25f, 3), (SceneVariant.Box, 0.40f, 0.25f, 6),
        (SceneVariant.Mesh, 0.40f, 0.25f, 2), (SceneVariant.Mesh, 0.40f, 0.25f, 3),
        (SceneVariant.Mesh, 0.40f, 0.25f, 6), (SceneVariant.Mesh, 0.40f, 0.30f, 2),
        (SceneVariant.Mesh, 0.40f, 0.30f, 3), (SceneVariant.Mesh, 0.40f, 0.30f, 6),
        (SceneVariant.Mesh, 0.35f, 0.25f, 2), (SceneVariant.Mesh, 0.35f, 0.25f, 3),
        (SceneVariant.Mesh, 0.35f, 0.25f, 4), (SceneVariant.Mesh, 0.35f, 0.25f, 6),
        (SceneVariant.Mesh, 0.35f, 0.30f, 2), (SceneVariant.Mesh, 0.35f, 0.30f, 3),
        (SceneVariant.Mesh, 0.35f, 0.30f, 4), (SceneVariant.Mesh, 0.35f, 0.30f, 6),
    ];

    static IEnumerable<object[]> AllStairRows(bool stalled)
    {
        foreach (SceneVariant variant in new[] { SceneVariant.Box, SceneVariant.Mesh })
            foreach (float tread in new[] { 0.40f, 0.35f })
                foreach (float riser in new[] { 0.25f, 0.30f })
                    foreach (float speed in new[] { 2f, 3f, 4f, 6f })
                        if (NosingStalls.Contains((variant, tread, riser, speed)) == stalled)
                            yield return [variant, tread, riser, speed];
    }

    public static IEnumerable<object[]> StairRows() => AllStairRows(stalled: false);

    public static IEnumerable<object[]> StalledStairRows() => AllStairRows(stalled: true);

    // A run from the floor up six risers. A tick's step part is its rise less what the tick start support's own
    // plane explains between the start and end axes. The plane is level on a tread, so a lagging body's catch-up
    // counts as step part too.
    [Theory]
    [MemberData(nameof(StairRows))]
    [MemberData(nameof(StalledStairRows), Skip = NosingGap)]
    public void StairsClimbEveryRiser(SceneVariant variant, float tread, float riser, float speed)
    {
        using FootSupportScene scene = Stairs(variant, tread, riser, StairRisers);
        float footRadius = Settings.FootRadiusFraction * Tuning.CapsuleRadius;
        float topX = tread * (StairRisers - 1), topY = riser * StairRisers;
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;
        var move = new Vector2(speed * Dt, 0);
        Vector3 feet = Rest(scene, Tuning, new Vector2(-1, 0), 0);
        var trace = new StringBuilder();
        double maxStepPart = 0;

        bool reached = false;
        for (int tick = 1; tick <= 150 && !reached; tick++)
        {
            SupportSample start = Support(scene, Tuning, Axis(feet), feet.Y);
            GroundStepResult result = Step(scene, Tuning, feet, move);
            double explained = start.Status is SupportStatus.Walkable or SupportStatus.Steep
                ? PlaneAt(start, Axis(feet), Axis(result.Feet)) - start.Height
                : 0;
            double stepPart = result.Rise - explained;
            trace.AppendLine($"tick {tick}: step part {stepPart:R} -> {result}");
            Assert.True(result.Footing != GroundFooting.None, $"Footing None.\n{trace}");
            Assert.True(stepPart <= budget + result.Support.HeightError,
                $"Step part {stepPart:R} over {budget:R}.\n{trace}");
            maxStepPart = Math.Max(maxStepPart, stepPart);
            feet = result.Feet;
            reached = feet.X >= topX + footRadius && Math.Abs(feet.Y - topY) <= result.Support.HeightError;
        }

        Assert.True(reached, $"The top tread at {topY} from x {topX} was not reached.\n{trace}");
        output.WriteLine($"{variant} {tread} {riser} {speed}: max step part {maxStepPart:R}");
    }

    // The start support's plane at an axis, or level at its height when it has no plane.
    static double PlaneAt(in SupportSample support, Vector2 from, Vector2 to)
    {
        Vector3 n = support.Normal;
        if (!(n.Y > 0)) return support.Height;
        return support.Height + ((double)n.X * ((double)from.X - to.X) + (double)n.Z * ((double)from.Y - to.Y)) /
            n.Y;
    }

    // A 40 degree mesh incline from x -6, so 60 ticks of 0.2 end at x 6 on the same triangle.
    [Fact]
    public void SteepWalkableSlopeIsNeverPaced()
    {
        using FootSupportScene scene = Incline(40);
        var move = new Vector2(6 * Dt, 0);
        Vector3 feet = Rest(scene, Tuning, new Vector2(-6, 0), (float)scene.TopHeightAt("incline", -6, 0));
        var trace = new StringBuilder();

        for (int tick = 1; tick <= 60; tick++)
        {
            GroundStepResult result = Step(scene, Tuning, feet, move);
            trace.AppendLine($"tick {tick}: {result}");
            Assert.True(result.Footing == GroundFooting.Walkable, $"Footing left walkable.\n{trace}");
            Assert.False(result.Blocked, $"Blocked.\n{trace}");
            Assert.Equal(move, result.Achieved);
            double plane = scene.TopHeightAt("incline", result.Feet.X, result.Feet.Z);
            Assert.True(Math.Abs(result.Feet.Y - plane) <= result.Support.HeightError,
                $"Feet off the plane {plane:R}.\n{trace}");
            feet = result.Feet;
        }
    }

    // A 0.05 wall faces the body at x 1. One 5 m move ends with the shell against its face, less the skin.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void LargeMoveCannotTunnelAThinWall(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant).Wall("wall", 1, 0.05f);

        GroundStepResult result = Step(scene, Tuning, Vector3.Zero, new Vector2(5, 0));

        Assert.True(result.Blocked, $"{result}");
        Assert.True(result.Footing == GroundFooting.Walkable, $"{result}");
        Assert.True(result.Feet.X + Tuning.CapsuleRadius <= 1, $"Past the wall face: {result}");
        Assert.True(result.Feet.X >= 1 - Tuning.CapsuleRadius - ShellMotion.ContactSkin - 1e-5f,
            $"Short of the wall: {result}");
        Assert.True(Math.Abs(result.Feet.Y) <= result.Support.HeightError, $"{result}");
    }

    // Six risers of 0.25 on treads 0.35, 10 m wide, with a wall across the landing at x 3.2. The run climbs at
    // varying speed and heading into the wall, slides along it, then walks back down.
    static Vector2 ReplayCommand(int tick)
    {
        if (tick < 128)
        {
            float speed = 2 + tick % 5;
            return Vector2.Normalize(new Vector2(1, 0.6f * MathF.Sin(tick * 0.15f))) * (speed * Dt);
        }
        return Vector2.Normalize(new Vector2(-1, 0.4f * MathF.Sin(tick * 0.1f))) * (1.5f * Dt);
    }

    static FootSupportScene ReplayScene(SceneVariant variant) =>
        Stairs(variant, 0.35f, 0.25f, StairRisers, halfWidth: 5).Wall("end", 3.2f, 0.2f, halfWidth: 5);

    static GroundStepResult[] Run(FootSupportScene scene, Vector3 feet, int from, int to)
    {
        var results = new GroundStepResult[to - from];
        for (int tick = from; tick < to; tick++)
        {
            results[tick - from] = Step(scene, Tuning, feet, ReplayCommand(tick));
            feet = results[tick - from].Feet;
        }
        return results;
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void ReplayIsBitIdentical(SceneVariant variant)
    {
        var start = new Vector3(-1, 0, 0);
        using FootSupportScene first = ReplayScene(variant);
        using FootSupportScene second = ReplayScene(variant);

        GroundStepResult[] original = Run(first, start, 0, 256);
        Vector3 copy = start;
        GroundStepResult[] replay = Run(second, copy, 0, 256);
        Vector3 middle = original[127].Feet;
        GroundStepResult[] resumed = Run(first, middle, 128, 256);

        Assert.Contains(original, r => r.Blocked);
        Assert.Contains(original, r => r.Feet.Y >= 0.25f * StairRisers - r.Support.HeightError);
        for (int tick = 0; tick < 256; tick++)
        {
            AssertBitIdentical(original[tick], replay[tick], tick);
            if (tick >= 128) AssertBitIdentical(original[tick], resumed[tick - 128], tick);
        }
    }

    static void AssertBitIdentical(in GroundStepResult expected, in GroundStepResult actual, int tick)
    {
        Assert.True(Bits(expected).SequenceEqual(Bits(actual)),
            $"Tick {tick} differs.\nexpected {expected}\nactual   {actual}");
    }

    static IEnumerable<long> Bits(GroundStepResult r)
    {
        static long F(float value) => BitConverter.SingleToInt32Bits(value);
        SupportSample s = r.Support;
        return [F(r.Feet.X), F(r.Feet.Y), F(r.Feet.Z), (long)r.Footing, F(r.Rise), F(r.Achieved.X),
            F(r.Achieved.Y), r.Blocked ? 1 : 0, (long)s.Status, F(s.Height), F(s.HeightError), F(s.Normal.X),
            F(s.Normal.Y), F(s.Normal.Z), s.Static?.Value ?? -1, s.FeatureId, F(s.Witness.X), F(s.Witness.Y),
            F(s.Witness.Z)];
    }

    // The spec's per-tick budget for n substeps: two support probes, each a sweep and at most one feature query,
    // at the start and in every substep's down pass, the lift sweep, up to 1 + MaxSlides (4) shell sweeps per
    // substep, recovery's 1 + MaxRecoveryPasses (4) penetration tests, and per substep up to two clearance tests
    // plus one touch normal per shell sweep. No raycasts.
    static QueryCounts Budget(int substeps) => new(
        Sweeps: 2 * (1 + substeps) + 1 + 5 * substeps,
        Penetrations: 5 + substeps * (2 + 5),
        Raycasts: 0,
        Features: 2 * (1 + substeps));

    static int Substeps(Vector2 move) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)move.X * move.X + (double)move.Y * move.Y) /
            (0.5 * Tuning.CapsuleRadius)));

    [Theory]
    [InlineData(SceneVariant.Box, "flat")]
    [InlineData(SceneVariant.Mesh, "flat")]
    [InlineData(SceneVariant.Box, "stairs")]
    [InlineData(SceneVariant.Mesh, "stairs", Skip = NosingGap)]
    [InlineData(SceneVariant.Box, "wall")]
    [InlineData(SceneVariant.Mesh, "wall")]
    public void QueryCostPerTick(SceneVariant variant, string ground)
    {
        // Flat runs 3 m/s along X. Stairs climbs six 0.25 risers on 0.35 treads at 3 m/s. Wall heads at 4 m/s
        // into a wall at x 1 at 30 degrees off its normal and slides along it.
        using FootSupportScene scene = ground switch
        {
            "flat" => Floor(variant),
            "stairs" => Stairs(variant, 0.35f, 0.25f, StairRisers),
            _ => Floor(variant).Wall("wall", 1, 0.2f),
        };
        Vector2 move = ground switch
        {
            "wall" => new Vector2(MathF.Cos(Radians(30)), MathF.Sin(Radians(30))) * (4 * Dt),
            _ => new Vector2(3 * Dt, 0),
        };
        Vector3 feet = new(ground == "stairs" ? -1 : 0, 0, ground == "wall" ? -2 : 0);
        int ticks = 30;
        using IPhysicsWorldQueryView inner = scene.World.CreateQueryViewExcludingStatics([]);
        var counting = new CountingQueryView(inner);
        var perTick = new List<QueryCounts>();
        QueryCounts budget = Budget(Substeps(move));
        int blocked = 0;
        var trace = new StringBuilder();
        using (IPhysicsQueryLease lease = counting.AcquireQueryReadLease())
        {
            for (int tick = 1; tick <= ticks; tick++)
            {
                QueryCounts before = counting.Counts;
                GroundStepResult result = Step(counting, lease, Tuning, feet, move);
                QueryCounts used = counting.Counts - before;
                perTick.Add(used);
                trace.AppendLine($"tick {tick}: {used} -> {result}");
                if (result.Blocked) blocked++;
                feet = result.Feet;
            }
        }

        string Kind(Func<QueryCounts, int> pick) =>
            $"{{\"min\": {perTick.Min(pick)}, \"max\": {perTick.Max(pick)}, " +
            $"\"mean\": {perTick.Average(pick):0.###}, \"budget\": {pick(budget)}}}";
        output.WriteLine($"COST {variant} {ground} ticks {ticks} substeps {Substeps(move)} blocked {blocked} " +
            $"sweeps {Kind(c => c.Sweeps)} penetrations {Kind(c => c.Penetrations)} " +
            $"raycasts {Kind(c => c.Raycasts)} features {Kind(c => c.Features)}");
        output.WriteLine(trace.ToString());
        if (ground == "wall") Assert.True(blocked > 0, $"The wall was never met.\n{trace}");
        if (ground == "stairs") Assert.True(feet.Y >= 0.25f * StairRisers - 1e-5f, $"Not up the stairs.\n{trace}");
        foreach (QueryCounts used in perTick)
        {
            Assert.True(used.Sweeps <= budget.Sweeps && used.Penetrations <= budget.Penetrations &&
                used.Raycasts <= budget.Raycasts && used.Features <= budget.Features,
                $"{used} over {budget}.\n{trace}");
        }
    }
}
