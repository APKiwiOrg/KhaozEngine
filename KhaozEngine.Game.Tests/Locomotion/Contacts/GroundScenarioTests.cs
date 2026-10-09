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
    static SupportSample Support(IPhysicsWorld world, IPhysicsQueryLease lease, in MoveTuning tuning, Vector2 axis,
        float feetY) =>
        FootSupport.Find(null, null, world, lease, new FootSupportQuery(axis, feetY,
            Settings.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, tuning.StepHeight,
            MathF.Cos(tuning.MaxSlopeRadians)));

    static Vector2 Axis(Vector3 feet) => new(feet.X, feet.Z);

    // The feet a resting body has at an axis: its certified support height, found from a guess within the band.
    static Vector3 Rest(IPhysicsWorld world, IPhysicsQueryLease lease, in MoveTuning tuning, Vector2 axis,
        float guessY)
    {
        SupportSample support = Support(world, lease, tuning, axis, guessY);
        Assert.True(support.Status == SupportStatus.Walkable, $"No walkable rest at {axis}: {support}");
        return new Vector3(axis.X, support.Height, axis.Y);
    }

    static Vector3 Rest(FootSupportScene scene, in MoveTuning tuning, Vector2 axis, float guessY) =>
        Rest(scene.World, scene.Lease, tuning, axis, guessY);

    // A scene's queries through a CountingQueryView over a real view with no exclusions, under one lease taken
    // through the view. The scene's own lease must stay untouched while this is alive.
    sealed class Counted : IDisposable
    {
        readonly IPhysicsWorldQueryView _inner;

        internal Counted(FootSupportScene scene)
        {
            _inner = scene.World.CreateQueryViewExcludingStatics([]);
            View = new CountingQueryView(_inner);
            Lease = View.AcquireQueryReadLease();
        }

        internal CountingQueryView View { get; }
        internal IPhysicsQueryLease Lease { get; }

        public void Dispose()
        {
            try { Lease.Dispose(); }
            finally { _inner.Dispose(); }
        }
    }

    static int Substeps(Vector2 move, in MoveTuning tuning) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)move.X * move.X + (double)move.Y * move.Y) /
            (0.5 * tuning.CapsuleRadius)));

    // The escape for #1342, which phase 2b removes. A run that ends stalled, the last tick blocked with nothing
    // achieved from the same feet as the tick before, passes only when the down pass of its first substep is
    // refused because a feature query came back Unresolved. That support query is replayed through the counting
    // view at the axis the core plans for the substep, with the stalled feet.
    static bool StalledByNosingGap(Counted counted, in MoveTuning tuning, IReadOnlyList<GroundStepResult> results,
        Vector2 move, StringBuilder trace)
    {
        if (results.Count < 2) return false;
        GroundStepResult last = results[^1], previous = results[^2];
        if (!last.Blocked || last.Achieved != Vector2.Zero || last.Feet != previous.Feet) return false;
        int substeps = Substeps(move, tuning);
        Vector2 axis = Axis(last.Feet) + (substeps == 1 ? move : move * (1f / substeps));
        int unresolved = counted.View.UnresolvedFeatures;
        SupportSample support = Support(counted.View, counted.Lease, tuning, axis, last.Feet.Y);
        bool cause = support.Status == SupportStatus.Refused && counted.View.UnresolvedFeatures > unresolved;
        trace.AppendLine($"stall at {last.Feet}: support at axis {axis} is {support.Status}, " +
            $"{counted.View.UnresolvedFeatures - unresolved} feature queries Unresolved, #1342 cause {cause}");
        return cause;
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

    public static IEnumerable<object[]> SlopeEdgeRows()
    {
        foreach (float radius in new[] { 0.2f, 0.3f, 0.4f })
            foreach (float grade in new[] { 0.08f, 0.25f })
                foreach (string heading in new[] { "uphill", "downhill", "sideways" })
                    yield return [grade, heading, radius];
    }

    // KhaozEngine #1265: edges of 0.25 on smooth mesh slopes rising to +X, at the legacy capsule radii 0.2 and 0.3
    // and the default 0.4. The sideways edge crosses the quad's diagonal.
    [Theory]
    [MemberData(nameof(SlopeEdgeRows))]
    public void SlopeEdgesConverge(float grade, string heading, float radius)
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = radius };
        using FootSupportScene scene =
            new FootSupportScene(SceneVariant.Mesh).Slab("slope", Vector3.Zero, MathF.Atan(grade), 2, 2);
        (Vector2 from, Vector2 to) = heading switch
        {
            "uphill" => (new Vector2(-0.125f, 0.3f), new Vector2(0.125f, 0.3f)),
            "downhill" => (new Vector2(0.125f, 0.3f), new Vector2(-0.125f, 0.3f)),
            _ => (new Vector2(0.1f, -0.125f), new Vector2(0.1f, 0.125f)),
        };
        Vector3 start = Rest(scene, tuning, from, (float)scene.TopHeightAt("slope", from.X, from.Y));
        Vector3 target = Rest(scene, tuning, to, start.Y);

        var trace = new StringBuilder();
        int ticks = Approach(scene, tuning, start, target, 1, 30, trace);

        Assert.True(ticks > 0, $"No arrival within 30 ticks at {target}.\n{trace}");
        output.WriteLine($"{grade} {heading} radius {radius}: arrived in {ticks} ticks");
    }

    public static IEnumerable<object[]> StairRows()
    {
        foreach (SceneVariant variant in new[] { SceneVariant.Box, SceneVariant.Mesh })
            foreach (float tread in new[] { 0.40f, 0.35f })
                foreach (float riser in new[] { 0.25f, 0.30f })
                    foreach (float speed in new[] { 2f, 3f, 4f, 6f })
                        yield return [variant, tread, riser, speed];
    }

    // A run from the floor up six risers. A tick's step part is its rise less what the tick start support's own
    // plane explains between the start and end axes. The plane is level on a tread, so a lagging body's catch-up
    // counts as step part too. A run that stalls passes only through the #1342 escape.
    [Theory]
    [MemberData(nameof(StairRows))]
    public void StairsClimbEveryRiser(SceneVariant variant, float tread, float riser, float speed)
    {
        using FootSupportScene scene = Stairs(variant, tread, riser, StairRisers);
        using var counted = new Counted(scene);
        float footRadius = Settings.FootRadiusFraction * Tuning.CapsuleRadius;
        float topX = tread * (StairRisers - 1), topY = riser * StairRisers;
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;
        var move = new Vector2(speed * Dt, 0);
        Vector3 feet = Rest(counted.View, counted.Lease, Tuning, new Vector2(-1, 0), 0);
        var trace = new StringBuilder();
        var results = new List<GroundStepResult>();
        double maxStepPart = 0;

        bool reached = false;
        for (int tick = 1; tick <= 150 && !reached; tick++)
        {
            SupportSample start = Support(counted.View, counted.Lease, Tuning, Axis(feet), feet.Y);
            GroundStepResult result = Step(counted.View, counted.Lease, Tuning, feet, move);
            results.Add(result);
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

        if (!reached && StalledByNosingGap(counted, Tuning, results, move, trace))
        {
            output.WriteLine($"{variant} {tread} {riser} {speed}: stalled by #1342 at {feet}");
            return;
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

    // The #1342 escape must not pass any other stall. A sphere of radius 0.25 on the floor at x 0.3 refuses the
    // down pass ahead, because the feature query reports curved primitives Unsupported (#1331), not Unresolved.
    // The body stalls with nothing achieved, and the escape refuses it.
    [Fact]
    public void NosingEscapeRefusesAnotherStall()
    {
        using FootSupportScene scene = Floor(SceneVariant.Box);
        scene.World.AddStatic(new SphereShape(0.25f), Pose.At(new Vector3(0.3f, 0, 0)));
        using var counted = new Counted(scene);
        var move = new Vector2(0.2f, 0);
        var feet = new Vector3(-0.2f, 0, 0);
        var results = new List<GroundStepResult>();
        for (int tick = 0; tick < 3; tick++)
        {
            results.Add(Step(counted.View, counted.Lease, Tuning, feet, move));
            feet = results[^1].Feet;
        }
        var trace = new StringBuilder();

        Assert.True(results[^1].Blocked && results[^1].Achieved == Vector2.Zero && results[^1].Feet == results[^2].Feet,
            $"No stall:\n{string.Join("\n", results)}");
        Assert.False(StalledByNosingGap(counted, Tuning, results, move, trace), trace.ToString());
        Assert.Contains("is Refused", trace.ToString());
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

    // The true worst case per tick for n substeps. Recovery runs up to 1 + MaxRecoveryPasses (5) penetration tests,
    // the start support two probes, each one sweep and one feature query, and the lift one sweep. A substep resolves
    // its move and, after a wall outcome, the move's tangent. Each resolve is a lifted attempt plus an unlifted retry
    // after a refusal, so a substep makes at most 4 attempts. An attempt is up to 1 + MaxSlides (5) shell sweeps,
    // each with at most one touch normal penetration test, then a down pass of two probes. Seat clearance adds up
    // to 2 penetration tests per substep. No raycasts.
    static QueryCounts WorstCase(int substeps) => new(
        Sweeps: 3 + 4 * (5 + 2) * substeps,
        Penetrations: 5 + (4 * 5 + 2) * substeps,
        Raycasts: 0,
        Features: 2 + 4 * 2 * substeps);

    [Theory]
    [InlineData(SceneVariant.Box, "flat")]
    [InlineData(SceneVariant.Mesh, "flat")]
    [InlineData(SceneVariant.Box, "run")]
    [InlineData(SceneVariant.Mesh, "run")]
    [InlineData(SceneVariant.Box, "stairs")]
    [InlineData(SceneVariant.Mesh, "stairs")]
    [InlineData(SceneVariant.Box, "wall")]
    [InlineData(SceneVariant.Mesh, "wall")]
    public void QueryCostPerTick(SceneVariant variant, string ground)
    {
        // Flat walks 3 m/s along X and run goes 12 m/s, two substeps a tick. Stairs climbs six 0.25 risers on 0.35
        // treads at 3 m/s. Wall heads at 4 m/s into a wall at x 1 at 30 degrees off its normal and slides along it.
        using FootSupportScene scene = ground switch
        {
            "flat" or "run" => Floor(variant),
            "stairs" => Stairs(variant, 0.35f, 0.25f, StairRisers),
            _ => Floor(variant).Wall("wall", 1, 0.2f),
        };
        Vector2 move = ground switch
        {
            "wall" => new Vector2(MathF.Cos(Radians(30)), MathF.Sin(Radians(30))) * (4 * Dt),
            "run" => new Vector2(12 * Dt, 0),
            _ => new Vector2(3 * Dt, 0),
        };
        int substeps = Substeps(move, Tuning);
        if (ground == "run") Assert.True(substeps >= 2, $"The run takes {substeps} substeps.");
        // The run covers 8 m of the floor's 10 in 20 ticks, from x -3.5.
        Vector3 feet = new(ground switch { "stairs" => -1, "run" => -3.5f, _ => 0 }, 0, ground == "wall" ? -2 : 0);
        int ticks = ground == "run" ? 20 : 30;
        using var counted = new Counted(scene);
        var perTick = new List<QueryCounts>();
        var results = new List<GroundStepResult>();
        QueryCounts budget = WorstCase(substeps);
        int blocked = 0;
        var trace = new StringBuilder();
        for (int tick = 1; tick <= ticks; tick++)
        {
            QueryCounts before = counted.View.Counts;
            GroundStepResult result = Step(counted.View, counted.Lease, Tuning, feet, move);
            QueryCounts used = counted.View.Counts - before;
            perTick.Add(used);
            results.Add(result);
            trace.AppendLine($"tick {tick}: {used} -> {result}");
            if (result.Blocked) blocked++;
            feet = result.Feet;
        }

        string Kind(Func<QueryCounts, int> pick) =>
            $"{{\"min\": {perTick.Min(pick)}, \"max\": {perTick.Max(pick)}, " +
            $"\"mean\": {perTick.Average(pick):0.###}, \"worst_case\": {pick(budget)}}}";
        output.WriteLine($"COST {variant} {ground} ticks {ticks} substeps {substeps} blocked {blocked} " +
            $"sweeps {Kind(c => c.Sweeps)} penetrations {Kind(c => c.Penetrations)} " +
            $"raycasts {Kind(c => c.Raycasts)} features {Kind(c => c.Features)}");
        foreach (QueryCounts used in perTick)
        {
            Assert.True(used.Sweeps <= budget.Sweeps && used.Penetrations <= budget.Penetrations &&
                used.Raycasts <= budget.Raycasts && used.Features <= budget.Features,
                $"{used} over {budget}.\n{trace}");
        }
        if (ground == "wall") Assert.True(blocked > 0, $"The wall was never met.\n{trace}");
        if (ground == "stairs" && !(feet.Y >= 0.25f * StairRisers - 1e-5f))
        {
            if (StalledByNosingGap(counted, Tuning, results, move, trace))
            {
                output.WriteLine($"COST {variant} stairs stalled by #1342 at {feet}");
                return;
            }
            Assert.Fail($"Not up the stairs.\n{trace}");
        }
        output.WriteLine(trace.ToString());
    }
}
