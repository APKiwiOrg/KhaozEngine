using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public class SupportGateCostTests(ITestOutputHelper output) : IDisposable
{
    const string BaseSha = "06b734c5722bc2cc95cea459255d6ec93e9236c7";
    public static IEnumerable<object[]> CostRows() => SupportGateCostScenes.Rows().Select(r => new object[] { ((SupportCostRow)r[0]).Name });
    static SupportCostRow Row(string name) => SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);

    [Theory]
    [InlineData(0, 0.401f, false)]
    [InlineData(0.149f, 0.252f, false)]
    [InlineData(0, 0.25f, true)]
    [InlineData(0, -10, true)]
    [InlineData(0, -10.001f, false)]
    public void BodyWaterPointUsesCanonicalFeetMembership(float x, float feetY, bool wet)
    {
        using var execution = new SupportCostExecution(Row("explicit medium descent retry"), "ExplicitTick");
        var body = new MovementBodyQuery(new(x, feetY + 0.9f, 0), 0.4f, 0.9f, new("world", "room"), null);
        MovementWaterPoint point = execution.SampleBody(body);
        Assert.Equal(wet, point.InWater);
        Assert.Equal(wet, point.Domain.HasValue);
        Assert.Equal(wet, point.Interval.HasValue);
    }

    [Fact]
    public void ChangedAxisRoundingEarnsItsOwnSupportRead()
    {
        SupportQueryDiagnostics.Enable(true);
        SupportCostRow row = Row("explicit changed-axis rounding");
        SupportCostSample sample = new SupportCostSampler(false).Measure(row, "ExplicitTick")[0];
        output.WriteLine($"End {sample.EndState}, routes {sample.Routes}");
        foreach (SupportFindObservation read in sample.SupportReads) output.WriteLine($"Find {read}");
        AssertNamedRoute(row, sample);
        Assert.True(sample.GeometryPassed);
    }

    [Fact]
    public void DiagnosticsAreAnOptInThreadLocalObserver()
    {
        SupportQueryDiagnostics.Enable(false);
        SupportQueryDiagnostics.Reset();
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().Find);
        SupportQueryDiagnostics.Enable(true);
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 2);
        Assert.Equal(2, SupportQueryDiagnostics.Snapshot().Find);
        SupportQueryCounts another = default;
        var thread = new Thread(() => another = SupportQueryDiagnostics.Snapshot());
        thread.Start();
        thread.Join();
        Assert.Equal(0, another.Find);
        SupportQueryCounts counts = default;
        long allocated = AllocationMeasurement.Measure(nameof(DiagnosticsAreAnOptInThreadLocalObserver), () =>
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.Find);
            counts = SupportQueryDiagnostics.Snapshot();
        });
        Assert.Equal(0, allocated);
        Assert.Equal(3, counts.Find);
    }

    [Fact]
    public void ActualSubstepCallbacksIncludeBlockedAndExceptionalExits()
    {
        SupportQueryDiagnostics.Enable(true);
        int begins = 0, ends = 0;
        SupportQueryDiagnostics.SetGroundSubstepObserver(() => begins++, () => ends++);
        MoveTuning tuning = SupportGateCostScenes.Tuning;
        GroundStepResult blocked = GroundCore.Step(Vector3.Zero, Vector2.UnitX * 0.2f, 1f / 30,
            tuning, new(), DryQueries.For((_, _) => 0, null, null, null, tuning),
            clampXz: (_, z) => new(0, z));
        Assert.True(blocked.Blocked);
        Assert.Equal(1, begins);
        Assert.Equal(begins, ends);
        begins = ends = 0;
        Func<float, float, float> broken = (_, _) => begins > 0 ? throw new InvalidOperationException("sample failure") : 0;
        Assert.Throws<InvalidOperationException>(() => GroundCore.Step(Vector3.Zero, Vector2.UnitX * 0.2f,
            1f / 30, tuning, new(), DryQueries.For(broken, null, null, null, tuning)));
        Assert.Equal(1, begins);
        Assert.Equal(begins, ends);
    }

    [Theory]
    [MemberData(nameof(CostRows))]
    public void CostRowsHaveSuccessfulGeometryAndRouteBounds(string name)
    {
        SupportCostRow row = Row(name);
        SupportQueryDiagnostics.Enable(true);
        var sampler = new SupportCostSampler(false) { IsolateAllocations = true };
        SupportCostSample find = sampler.Measure(row, "Find")[0];
        Assert.Equal(1, find.Counts.Find);
        Assert.Equal(2, find.Counts.Proposals);
        Assert.InRange(find.Queries.Sweeps, 0, 2);
        Assert.InRange(find.Queries.Features, 0, 2);
        Assert.Equal(0, find.Queries.Raycasts);
        output.WriteLine($"{name} Find geometry={find.GeometryPassed}, oracle={find.OracleOutcome}/{find.OracleHeight}, " +
            $"actual={find.Outcome}/{find.Height}, bytes={find.WarmBytes}, counts={find.Counts}");
        Assert.Equal(0, find.WarmBytes);
        Assert.Equal(4352, find.StackBytes);
        if (row.ExpectedRefusal)
        {
            Assert.True(find.Refusal);
            Assert.True(find.Counts.CapacityExceeded > 0);
            Assert.True(find.GeometryMatchesOracle);
            Assert.False(find.GeometryPassed);
            Assert.NotEqual(SupportCostGate.Pass, SupportCostVerdict.Evaluate(find, find, row.Dense));
        }
        else if (row.KnownIncomplete)
        {
            Assert.True(find.KnownIncorrect);
            Assert.False(find.GeometryPassed);
            Assert.NotEqual(SupportCostGate.Pass, SupportCostVerdict.Evaluate(find, find, row.Dense));
        }
        else
        {
            Assert.True(find.GeometryMatchesOracle, $"{name} oracle {find.OracleOutcome}/{find.OracleHeight}, actual {find.Outcome}/{find.Height}");
            if (find.OracleOutcome is "Walkable" or "Steep") Assert.True(find.GeometryPassed);
            else Assert.NotEqual(SupportCostGate.Pass, SupportCostVerdict.Evaluate(find, find, row.Dense));
        }
        SupportCostSample legacy = sampler.Measure(row, "LegacyTick")[0];
        foreach (SupportCostSample sample in sampler.Measure(row, row.Explicit ? "ExplicitTick" : "DryTick"))
        {
            if (sample.Operation.StartsWith("GroundSubstep", StringComparison.Ordinal))
            {
                Assert.InRange(sample.Counts.Find, 0, 8 * (1 + sample.Counts.MediumRetries));
                Assert.InRange(sample.Queries.Sweeps, 0, 36 * (1 + sample.Counts.MediumRetries));
                Assert.InRange(sample.Queries.Penetrations, 0, 28 * (1 + sample.Counts.MediumRetries));
                Assert.InRange(sample.Queries.Features, 0, 16 * (1 + sample.Counts.MediumRetries));
                Assert.Equal(0, sample.Queries.Raycasts);
                continue;
            }
            AssertRoute(row, sample);
            output.WriteLine($"{name} tick {sample.Outcome}, queries={sample.Queries}, routes={sample.Routes}, counts={sample.Counts}");
            AssertNamedRoute(row, sample);
            if (row.MustPass && !row.KnownIncomplete) Assert.True(sample.GeometryPassed, $"{name} tick {sample.Outcome}, motion {sample.AchievedMotion}");
            if (!sample.GeometryPassed) Assert.NotEqual(SupportCostGate.Pass, SupportCostVerdict.Evaluate(sample, legacy, row.Dense));
            output.WriteLine($"{name} {sample.Operation} geometry={sample.GeometryPassed} {sample.Outcome}, " +
                $"Find={sample.Counts.Find}, substeps={sample.Substeps}, bytes={sample.WarmBytes}, counts={sample.Counts}");
        }
    }

    static void AssertRoute(SupportCostRow row, SupportCostSample sample)
    {
        SupportQueryCounts c = sample.Counts;
        Assert.InRange(c.ModeSegments, 0, 8);
        Assert.Equal(c.GroundSubsteps, sample.Substeps);
        Assert.Equal(c.ModeSegments, sample.Routes.GroundSegments + sample.Routes.SlideSegments +
            sample.Routes.AirSegments + sample.Routes.WaterSegments);
        Assert.Equal(1, c.PhysicsLeases);
        Assert.Equal(row.Explicit ? 1 : 0, c.MovementLeases);
        Assert.Equal(row.Explicit ? 1 : 0, c.ProducerPins);
        int n = Math.Max(1, sample.Substeps);
        // A tick that samples its look-ahead takes all three Finds, and one whose ascent is decided takes none.
        int signals = LookAheadReads(sample);
        Assert.True(signals is 0 or GroundSignals.Samples, $"{row.Name}: {signals} look-ahead reads");
        long findBound = 1 + 8 * n + signals;
        if (row.Explicit) findBound += 4 + 8 * c.MediumRetries;
        if (!row.Grounded || row.Normal.Y < 0.70710677f)
            findBound += 2 * Math.Max(1, c.ModeSegments) +
                2 * (int)Math.Ceiling((Math.Abs(row.VerticalVelocity) + 25 * row.Dt) * row.Dt / 0.2);
        Assert.True(c.Find <= findBound, $"{row.Name}: {c.Find} > derived route {findBound}");
        Assert.InRange(c.MediumRetries, 0, row.Explicit ? 16 * n : 0);
        long retry = row.Explicit ? 36 * c.MediumRetries + 16 * Math.Max(1, c.ModeSegments) : 0;
        Assert.InRange(sample.Queries.Sweeps, 0, 3 + 36 * n + 2 * signals + retry);
        Assert.InRange(sample.Queries.Penetrations, 0, 5 + 28 * n + retry);
        Assert.InRange(sample.Queries.Features, 0, 2 + 16 * n + 2 * signals + retry);
        Assert.Equal(0, sample.Queries.Raycasts);
        if (row.Explicit)
        {
            Assert.True(c.AnalyticSamples >= c.Find);
            Assert.True(c.Bindings > 0 || sample.Refusal || !row.Grounded);
        }
    }

    // Look-ahead reads reach StepHeight + 2 k CapsuleRadius for k = 1, 2, 3.
    static int LookAheadReads(SupportCostSample sample)
    {
        MoveTuning tuning = SupportGateCostScenes.Tuning;
        return sample.SupportReads.Count(q => Enumerable.Range(1, GroundSignals.Samples)
            .Any(k => q.ReachUp == tuning.StepHeight + 2 * (k * tuning.CapsuleRadius)));
    }

    static void AssertNamedRoute(SupportCostRow row, SupportCostSample sample)
    {
        Assert.Equal(sample.Counts.Find, sample.SupportReads.Length);
        Assert.InRange(sample.Routes.MaximumMediumRetries, 0, 8);
        Assert.InRange(sample.Counts.MediumRetries, 0, 8 * sample.Routes.MediumResolves);
        // The pushed seat carries no run in, so its three look-ahead samples decide the ascent it reports. The hidden
        // crate carries none either, so it takes all three, though they find no run. The stairs carry an ascent their
        // owed climb already continues, which skips the samples (SC-13).
        if (row.Name is "pushed seat" or "hidden crate")
            Assert.Equal(GroundSignals.Samples, LookAheadReads(sample));
        if (row.StairSignal)
            Assert.Equal(0, LookAheadReads(sample));
        if (row.Name == "wall retry") Assert.True(sample.Routes.WallRetries > 0, $"{sample.Routes}");
        if (row.Name == "pushed seat") Assert.True(sample.Routes.PushedPlacements > 0, $"{sample.Routes}");
        if (row.Name == "descending landing")
        {
            Assert.True(sample.Routes.AirSegments > 0);
            Assert.True(sample.Routes.GroundSegments > 0);
        }
        if (row.Name == "slide opposing face")
        {
            Assert.True(sample.Routes.SlideSegments > 0);
            Assert.Contains(sample.SupportReads, q => q.ReachUp > 0.6f);
        }
        if (row.Name == "explicit rounding handoff")
        {
            Assert.True(sample.Routes.WaterSegments > 0 && sample.Routes.GroundSegments > 0);
            Assert.Equal(0, sample.Routes.RoundedReads);
            int supportedReads = sample.SupportReads.Count(q => (SupportStatus)q.Status is SupportStatus.Walkable or SupportStatus.Steep);
            Assert.True(sample.Counts.Bindings > supportedReads);
        }
        if (row.Name == "explicit changed-axis rounding")
        {
            Assert.True(sample.Routes.WaterSegments >= 2);
            Assert.True(sample.Routes.RoundedReads > 0, $"{sample.Routes}");
            Assert.Equal(-256.08334f, sample.EndState!.Value.Position.X);
            Assert.Contains(sample.SupportReads, q => q.Axis.X == -256.08334f &&
                (SupportStatus)q.Status == SupportStatus.Walkable);
            Assert.Contains(sample.SupportReads, q => q.Axis.X == -256.08331f &&
                (SupportStatus)q.Status == SupportStatus.Walkable);
        }
        if (row.Name == "explicit dry swim handoff")
        {
            Assert.True(sample.Routes.MediumHandoffs > 0);
            Assert.True(sample.Counts.ModeSegments > 1);
        }
        if (row.Name == "explicit dry wade handoff")
        {
            Assert.True(sample.GeometryPassed);
            Assert.True(sample.AchievedMotion.X >= row.WaterMinX);
            Assert.True(sample.Routes.GroundSegments > 0);
        }
        if (row.Name == "final clamp")
        {
            Assert.Equal(2, sample.Counts.Find);
            Assert.True(sample.AchievedMotion.X < 0);
            Assert.Equal(0.19f, sample.SupportReads[^1].Axis.X);
        }
        if (row.Name == "explicit medium descent retry")
        {
            Assert.True(sample.Counts.MediumRetries > 0);
            Assert.True(sample.GeometryPassed);
            Assert.True(sample.GeometryMatchesOracle);
            using var execution = new SupportCostExecution(row, "ExplicitTick");
            foreach (MoveState state in new[] { row.Input, sample.ColdEndState!.Value, sample.EndState!.Value })
            {
                Assert.True(state.Grounded);
                var body = new MovementBodyQuery(state.Position, 0.4f, 0.9f, new("world", "room"), null);
                Assert.False(execution.SampleBody(body).InWater);
            }
        }
    }

    [Fact]
    public void FanCostSceneFacesUpward()
    {
        using SupportFootprintScene scene = SupportFootprintScenes.Fan(96);
        OracleFace first = scene.Faces[0];
        Assert.True(first.Normal.Y > 0, $"Installed fan normal {first.Normal}. The cost fixture needs an upward floor.");
    }

    [Theory]
    [InlineData("stairs box")]
    [InlineData("stairs hull")]
    [InlineData("stairs mesh")]
    public void LegacyTreadSampleReachesTheConditionalRaySearch(string name)
    {
        SupportQueryDiagnostics.Enable(true);
        SupportCostSample sample = new SupportCostSampler(false).Measure(Row(name), "LegacyTreadDownPass")[0];
        Assert.True(sample.Queries.Raycasts > 0);
        Assert.True(sample.ColdQueries.Raycasts > 0);
        Assert.Equal(0, sample.Counts.Find);
        Assert.Equal(0, sample.Counts.PhysicsLeases);
        Assert.False(sample.Comparable);
    }

    [Fact]
    public void OneShotCostArtifact()
    {
        string? path = Environment.GetEnvironmentVariable("KHAOZ_SUPPORT_COST_ARTIFACT");
        if (string.IsNullOrEmpty(path)) return;
        Assert.Equal("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
        string sha = GitSha();
        SupportQueryDiagnostics.Enable(true);
        var sampler = new SupportCostSampler(true) { IsolateAllocations = true };
        var failures = new List<string>();
        List<SupportCostSample> samples = MeasureCostRun(sampler,
            SupportGateCostScenes.Rows().Select(data => (SupportCostRow)data[0]), failures);
        bool hosted = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        string label = File.Exists(path) ? "sg9-cost" : "sg2-baseline";
        string command = "DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=" + path +
            (hosted ? " dotnet test " + Environment.GetEnvironmentVariable("PROJECT") + " -c Release --no-build --filter \"" +
                Environment.GetEnvironmentVariable("FILTER") + "\" --logger \"trx;LogFileName=" + Path.GetFileNameWithoutExtension(path) +
                ".trx\" --results-directory \"" + Path.GetDirectoryName(Path.GetDirectoryName(path)) + "\""
                : " build-slot --label " + label + " -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter \"FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact\"");
        // A hosted run records only. Its verdict is computed offline against the reference recorded at this commit.
        SupportCostArtifact.Write(path, samples, BaseSha, sha, command, hosted);
        Assert.True(File.Exists(path));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        output.WriteLine($"Recorded {samples.Count} first-use/warm operation pairs at {sha}.");
    }

    internal static List<SupportCostSample> MeasureCostRun(SupportCostSampler sampler, IEnumerable<SupportCostRow> rows,
        List<string> failures)
    {
        // The first measured row reads high on every machine (SC-16). A fixed cheap scene takes that position under
        // the full protocol and is recorded under an operation no verdict reads.
        var samples = new List<SupportCostSample>
        {
            sampler.Measure(Row("flat box"), "Find")[0] with { Operation = SupportCostVerdict.DiscardedLeadingOperation }
        };
        foreach (SupportCostRow row in rows)
        {
            foreach (string operation in new[] { "Find", "LegacyDownPass", "GroundTickBorrowed", "LegacyTick", "Lease",
                row.Explicit ? "ExplicitTick" : "DryTick" })
            {
                try { samples.AddRange(sampler.Measure(row, operation)); }
                catch (Exception error)
                {
                    failures.Add(row.Name + " " + operation + " " + error);
                    samples.Add(new(operation, row.Name, false, "Exception", double.NaN, double.NaN,
                        Vector3.Zero, 0, SupportQueryDiagnostics.Snapshot(), -1, -1, double.NaN, double.NaN, 0, 4352)
                    { Input = row, Refusal = true, Notes = [error.ToString(), "No completed sample window. Retained before reporting invocation failure."] });
                    SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
                    SupportQueryDiagnostics.SetFindObserver(null);
                }
            }
            if (row.Name.StartsWith("stairs ", StringComparison.Ordinal))
                samples.AddRange(sampler.Measure(row, "LegacyTreadDownPass"));
        }
        return samples;
    }

    [Fact]
    public void CostRunDiscardsALeadingRow()
    {
        SupportQueryDiagnostics.Enable(true);
        var failures = new List<string>();
        List<SupportCostSample> samples = MeasureCostRun(new SupportCostSampler(false) { IsolateAllocations = true },
            [Row("flat box")], failures);
        Assert.Empty(failures);
        SupportCostSample leading = samples[0], first = samples[1];
        Assert.Equal(SupportCostVerdict.DiscardedLeadingOperation, leading.Operation);
        Assert.Equal("flat box", leading.Scene);
        Assert.Equal(("flat box", "Find"), (first.Scene, first.Operation));
        Assert.Single(samples, sample => sample.Operation == SupportCostVerdict.DiscardedLeadingOperation);
        // A full first-use and warm sample, each one Find.
        Assert.Equal(1, leading.ColdCounts.Find);
        Assert.Equal(1, leading.Counts.Find);
        Assert.False(SupportCostVerdict.IsGated(leading.Operation));
        Assert.Null(SupportCostVerdict.JudgeRow(leading, samples, 1, 0, hosted: false));
        Assert.Null(SupportCostVerdict.JudgeRow(leading, samples, 1, 0, hosted: true));
        Assert.Same(samples.Single(sample => sample.Operation == "LegacyDownPass"), SupportCostVerdict.PairedLegacy(first, samples));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SupportCostArtifact.Write(path, samples, "base", "candidate", "command");
            JsonNode section = JsonNode.Parse(File.ReadAllText(path))!["baseline"]!;
            Assert.Contains(SupportCostVerdict.DiscardedLeadingOperation, section["LeadingRow"]!.GetValue<string>(), StringComparison.Ordinal);
            JsonArray recorded = section["Samples"]!.AsArray();
            Assert.Equal(samples.Count, recorded.Count);
            Assert.Equal(SupportCostVerdict.DiscardedLeadingOperation, recorded[0]!["Operation"]!.GetValue<string>());
            Assert.Null(recorded[0]!["GatePassed"]);
            Assert.Null(recorded[0]!["Envelope"]);
            Assert.Null(recorded[0]!["CandidateLegacyRatio"]);
            Assert.Equal("Find", recorded[1]!["Operation"]!.GetValue<string>());
            Assert.NotNull(recorded[1]!["GatePassed"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LookAheadOracleModelsSg6Refusals()
    {
        SupportCostRow row = Row("pushed seat");
        using SupportFootprintScene scene = SupportGateCostScenes.Build(row);
        float cosine = MathF.Cos(MathF.PI / 4);
        // The pushed seat dry tick's second look-ahead read starts its probes one binary32 step inside the ceiling
        // slab, whose top is 2.02. A look-ahead from feet at 0.019 starts them exactly on that top.
        var recorded = new FootSupportQuery(new(1, 0), 0.018999912f, 0.2f, 2, 2, cosine);
        var onSlab = recorded with { FeetY = 0.019f };
        Assert.Equal(MathF.BitDecrement(2.02f), recorded.FeetY + recorded.ReachUp + FootSupport.BandMargin);
        Assert.Equal(2.02f, onSlab.FeetY + onSlab.ReachUp + FootSupport.BandMargin);
        Assert.True(0.0195f + 2f + FootSupport.BandMargin > 2.02f && (double)0.0195f + 2 < 2.02f);
        (FootSupportQuery Query, SupportStatus Status, float Height)[] cases =
        [
            (recorded, SupportStatus.Refused, float.NaN),
            (onSlab, SupportStatus.Refused, float.NaN),
            // The probes start clear above the slab and stop on its top above the band.
            (recorded with { FeetY = 0.0195f }, SupportStatus.Refused, float.NaN),
            // The raised start lies inside the slab.
            (new(new(1, 0), 1.499f, 0.2f, 0.4f, 0.4f, cosine), SupportStatus.Refused, float.NaN),
            // The probes start below the slab and stop on the seat.
            (new(new(1, 0), 0.25f, 0.2f, 0.4f, 0.4f, cosine), SupportStatus.Walkable, 0.25f),
            // The probes start above the slab and stop on its top inside the band.
            (new(new(1, 0), 1.9f, 0.2f, 0.4f, 0.4f, cosine), SupportStatus.Walkable, 2.02f),
        ];
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        foreach (var (query, status, height) in cases)
        {
            SupportSample candidate = FootSupport.Find(null, null, scene.World, lease, query);
            FootprintOracleSupport oracle = SupportGateCostScenes.Expected(scene, row, query, guarded: true);
            Assert.Equal(status, candidate.Status);
            Assert.Equal(status, oracle.Status);
            if (status == SupportStatus.Refused) continue;
            Assert.Equal(height, candidate.Height, 0.0005f);
            Assert.Equal(height, oracle.Height, 0.0005f);
        }
    }

    [Theory]
    [InlineData(0.5f, true)]
    [InlineData(0.5f, false)]
    [InlineData(0.4005f, true)]
    [InlineData(0.4005f, false)]
    public void GuardOracleCountsAMeshPlateOnlyWhileItsFrontFacesUp(float height, bool front)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new BoxShape(new(4, 1, 4)), Pose.At(new(0, -1, 0)));
        StaticHandle plate = scene.Install(new TriangleMeshShape(
            [new(-1, height, -1), new(1, height, -1), new(-1, height, 1), new(1, height, 1)],
            front ? [0, 1, 2, 1, 3, 2] : [0, 2, 1, 1, 2, 3]), Pose.Identity);
        Assert.All(scene.Faces.Where(face => face.Static == plate), face => Assert.Equal(front, face.Normal.Y > 0));
        // The probes start at 0.401, inside the plate at 0.5 and clear above the plate at 0.4005.
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        SupportSample candidate = FootSupport.Find(null, null, scene.World, lease, query);
        FootprintOracleSupport oracle = SupportGateCostScenes.Expected(scene, new SupportCostRow("mesh plate"), query,
            guarded: true);
        SupportStatus expected = front ? SupportStatus.Refused : SupportStatus.Walkable;
        Assert.Equal(expected, candidate.Status);
        Assert.Equal(expected, oracle.Status);
        if (!front) Assert.Equal(0, oracle.Height);
    }

    [Fact]
    public void GuardOracleRefusesToJudgeTheThirdGuardRefusal()
    {
        // A steep plate falls toward +X from its 0.415 top edge. The foot probe stops on that edge at 0.388, inside
        // the band, but the plate supports nothing at or below the 0.4 band top, so the third guard rule refuses.
        float drop = 0.2f * MathF.Tan(MathF.PI / 3);
        using var scene = new SupportFootprintScene();
        StaticHandle plate = scene.Install(new TriangleMeshShape(
            [new(0.1f, 0.415f, -1), new(0.3f, 0.415f - drop, -1), new(0.1f, 0.415f, 1), new(0.3f, 0.415f - drop, 1)],
            [0, 1, 2, 1, 3, 2]), Pose.Identity);
        Assert.All(scene.Faces.Where(face => face.Static == plate), face => Assert.InRange(face.Normal.Y, 0.49, 0.51));
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        Assert.Equal(SupportStatus.Refused, FootSupport.Find(null, null, scene.World, lease, query).Status);
        var error = Assert.Throws<NotSupportedException>(() =>
            SupportGateCostScenes.Expected(scene, new SupportCostRow("steep plate"), query, guarded: true));
        Assert.Contains("third guard refusal", error.Message, StringComparison.Ordinal);
    }

    static string GitSha() => GitOutput("rev-parse HEAD");

    static string GitOutput(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        { RedirectStandardOutput = true, UseShellExecute = false });
        string sha = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return sha;
    }

    [Theory]
    [InlineData(1, 50, 50, true)]
    [InlineData(1, 50.01, 50, false)]
    [InlineData(2, 100, 50, true)]
    [InlineData(0.5, 25, 50, true)]
    public void VerdictScalesAbsoluteCeilings(double scale, double time, double legacyTime, bool expected)
    {
        SupportCostSample candidate = Synthetic("Find", time);
        Assert.Equal(expected, SupportCostVerdict.Evaluate(candidate, Synthetic("LegacyDownPass", legacyTime), false, scale) == SupportCostGate.Pass);
    }

    [Theory]
    [InlineData(0.5, 90, 40, true)]
    [InlineData(0.5, 91, 40, false)]
    [InlineData(2, 280, 80, true)]
    [InlineData(2, 281, 80, false)]
    [InlineData(1, 625, 500, true)]
    [InlineData(1, 626, 500, false)]
    public void VerdictScalesTheAdditiveLegacyTerm(double scale, double candidateTime, double legacyTime, bool expected)
    {
        Assert.Equal(expected, SupportCostVerdict.Evaluate(Synthetic("DryTick", candidateTime),
            Synthetic("LegacyTick", legacyTime), false, scale) == SupportCostGate.Pass);
    }

    internal static SupportCostSample Synthetic(string operation, double time) => new(operation, "synthetic", true,
        "Grounded", 0, 0, Vector3.UnitX, 1, default, 0, 0, 0, time, 0, 4352);

    [Theory]
    [InlineData(200, 0.5, 25, 100, 500, 250, 500, 2000, 250)]
    [InlineData(800, 2, 100, 400, 2000, 1000, 2000, 8000, 1000)]
    public void HostedNormalizationScalesFasterAndSlowerHosts(double hostedLegacy, double expectedScale,
        double ordinaryFind, double ordinarySubstep, double ordinaryTick, double denseFind,
        double denseSubstep, double denseTick, double relativeTick)
    {
        // The flat box legacy tick is an outlier. The median over the other rows sets the scale.
        JsonObject hostedSection = HostedSection(hostedLegacy);
        hostedSection["Samples"]![0]!["WarmMicroseconds"] = 400 * 0.3;
        Assert.True(SupportCostVerdict.TryHostedScale(HostedSection(400, true), hostedSection, out HostedScale hostScale));
        Assert.Equal(new HostedScale(expectedScale, HostedRows, expectedScale, expectedScale), hostScale);
        double scale = hostScale.K;
        foreach (var row in new[] { ("Find", false, ordinaryFind), ("GroundSubstep", false, ordinarySubstep),
            ("DryTick", false, ordinaryTick), ("Find", true, denseFind),
            ("GroundSubstep", true, denseSubstep), ("DryTick", true, denseTick) })
        {
            SupportCostSample candidate = Synthetic(row.Item1, row.Item3) with { WarmBytes = row.Item1 == "DryTick" ? 208 : 0 };
            SupportCostSample legacy = Synthetic("LegacyTick", 10000);
            Assert.Equal(SupportCostGate.Pass, SupportCostVerdict.Evaluate(candidate, legacy, row.Item2, scale));
            Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(candidate with { WarmMicroseconds = Math.BitIncrement(row.Item3) }, legacy, row.Item2, scale));
            Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(candidate with { WarmBytes = candidate.WarmBytes + 1 }, legacy, row.Item2, scale));
        }
        SupportCostSample tick = Synthetic("DryTick", relativeTick);
        SupportCostSample hosted = Synthetic("LegacyTick", hostedLegacy);
        Assert.Equal(SupportCostGate.Pass, SupportCostVerdict.Evaluate(tick, hosted, false, scale));
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(tick with { WarmMicroseconds = Math.BitIncrement(relativeTick) }, hosted, false, scale));
        Assert.Equal(SupportCostGate.Pass, SupportCostVerdict.Evaluate(tick with { Substeps = 4 }, hosted, false, scale));
        Assert.Equal(SupportCostGate.Pass, SupportCostVerdict.Evaluate(tick with { Operation = "ExplicitTick", WarmBytes = 624 }, hosted, false, scale, 624));
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(tick with { Operation = "ExplicitTick", WarmBytes = 625 }, hosted, false, scale, 624));
    }

    [Theory]
    [InlineData("missing artifact")]
    [InlineData("missing hosted")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("hosted zero")]
    [InlineData("hosted negative")]
    [InlineData("hosted nan")]
    [InlineData("hosted infinity")]
    [InlineData("geometry")]
    [InlineData("cold geometry")]
    [InlineData("oracle")]
    [InlineData("refusal")]
    [InlineData("scale underflow")]
    [InlineData("scale overflow")]
    [InlineData("ceiling overflow")]
    [InlineData("missing row")]
    [InlineData("duplicate row")]
    [InlineData("missing scene")]
    [InlineData("mismatched scene")]
    [InlineData("mismatched tuning")]
    [InlineData("missing hardware")]
    [InlineData("wrong reference hardware")]
    [InlineData("missing architecture")]
    [InlineData("missing runtime")]
    [InlineData("missing OS")]
    [InlineData("tiered")]
    [InlineData("debug")]
    [InlineData("reference exit")]
    [InlineData("hosted exit")]
    public void InvalidHostedBaselineCannotPass(string fault)
    {
        JsonObject? reference = HostedSection(400, true);
        JsonObject? hosted = HostedSection(200);
        JsonNode baseline = reference["Samples"]![0]!;
        JsonNode row = hosted["Samples"]![0]!;
        switch (fault)
        {
            case "missing artifact": reference = null; break;
            case "missing hosted": hosted = null; break;
            case "zero": baseline["WarmMicroseconds"] = 0; break;
            case "negative": baseline["WarmMicroseconds"] = -1; break;
            case "nan": baseline["WarmMicroseconds"] = "NaN"; break;
            case "infinity": baseline["WarmMicroseconds"] = "Infinity"; break;
            case "hosted zero": row["WarmMicroseconds"] = 0; break;
            case "hosted negative": row["WarmMicroseconds"] = -1; break;
            case "hosted nan": row["WarmMicroseconds"] = "NaN"; break;
            case "hosted infinity": row["WarmMicroseconds"] = "Infinity"; break;
            case "geometry": row["GeometryPassed"] = false; break;
            case "cold geometry": row["ColdGeometryPassed"] = false; break;
            case "oracle": row["GeometryMatchesOracle"] = false; break;
            case "refusal": row["Refusal"] = true; break;
            case "scale underflow": SetTimes(reference, double.MaxValue); SetTimes(hosted, double.Epsilon); break;
            case "scale overflow": SetTimes(reference, double.Epsilon); SetTimes(hosted, double.MaxValue); break;
            case "ceiling overflow": SetTimes(reference, 1); SetTimes(hosted, double.MaxValue); break;
            case "missing row": hosted["Samples"] = new JsonArray(); break;
            case "duplicate row": hosted["Samples"]!.AsArray().Add(row.DeepClone()); break;
            case "missing scene": hosted["Scenes"] = new JsonObject(); break;
            case "mismatched scene": hosted["Scenes"]!["flat box"]!["Feet"]!["X"] = 1; break;
            case "mismatched tuning": hosted["Tuning"]!["Speed"] = 7; break;
            case "missing hardware": hosted.Remove("Hardware"); break;
            case "wrong reference hardware": reference["Hardware"] = "Other CPU"; break;
            case "missing architecture": hosted.Remove("Architecture"); break;
            case "missing runtime": hosted.Remove("Runtime"); break;
            case "missing OS": hosted.Remove("OperatingSystem"); break;
            case "tiered": hosted["TieredCompilation"] = "1"; break;
            case "debug": hosted["Compilation"] = "Debug"; break;
            case "reference exit": reference["ProcessExitStatus"] = 1; break;
            case "hosted exit": hosted["ProcessExitStatus"] = 1; break;
        }
        // Each section holds exactly the minimum row count, so one excluded row refuses the scale.
        Assert.False(SupportCostVerdict.TryHostedScale(reference, hosted, out HostedScale scale));
        Assert.Equal(default, scale);
    }

    static void SetTimes(JsonObject section, double time)
    {
        foreach (JsonNode? sample in section["Samples"]!.AsArray()) sample!["WarmMicroseconds"] = time;
    }

    [Theory]
    [InlineData("defaults", true)]
    [InlineData("controller", false)]
    [InlineData("foot radius", false)]
    [InlineData("water mode", false)]
    [InlineData("water launch", false)]
    public void HostedNormalizationMatchesCommittedPhysicalInputs(string tuning, bool expected)
    {
        string path = Path.Combine(GitOutput("rev-parse --show-toplevel"), "docs", "verification", "2026-10-11-support-gates-cost.json");
        JsonObject? reference = SupportCostArtifact.ReadReference(path, "376971dda");
        Assert.NotNull(reference);
        JsonObject hosted = HostedSection(1);
        hosted["Samples"] = reference["Samples"]!.DeepClone();
        foreach (JsonNode? sample in hosted["Samples"]!.AsArray())
            if (sample!["WarmMicroseconds"] is JsonValue time && time.TryGetValue(out double microseconds))
                sample["WarmMicroseconds"] = microseconds / 2;
        var inputs = SupportGateCostScenes.Rows().Select(row => (SupportCostRow)row[0]).ToDictionary(row => row.Name);
        hosted["Scenes"] = JsonSerializer.SerializeToNode(inputs, HostedOptions);
        hosted["Tuning"] = JsonSerializer.SerializeToNode(SupportGateCostScenes.Tuning, HostedOptions);
        switch (tuning)
        {
            case "controller": hosted["Tuning"]!["Controller"] = (int)MovementControllerKind.Contact; break;
            case "foot radius": hosted["Tuning"]!["FootRadiusFraction"] = 0.75f; break;
            case "water mode": hosted["Tuning"]!["Water"]!["Mode"] = (int)WaterTraversalMode.WadeOnly; break;
            case "water launch": hosted["Tuning"]!["Water"]!["SurfaceJumpSpeed"] = 1f; break;
        }
        Assert.Equal(expected, SupportCostVerdict.TryHostedScale(reference, hosted, out HostedScale scale));
        Assert.Equal(expected ? 0.5 : 0, scale.K);
        if (expected) Assert.InRange(scale.Rows, SupportCostVerdict.MinimumCalibrationRows, int.MaxValue);
    }

    [Theory]
    [InlineData("Controller", "1")]
    [InlineData("FootRadiusFraction", "0.75")]
    [InlineData("Water", "{\"Mode\":2}")]
    public void HostedNormalizationDefaultsOnlyMissingNewBaselineFields(string field, string changedValue)
    {
        JsonObject baseline = HostedSection(400, true), hosted = HostedSection(200);
        baseline["Tuning"] = JsonSerializer.SerializeToNode(SupportGateCostScenes.Tuning, HostedOptions);
        hosted["Tuning"] = baseline["Tuning"]!.DeepClone();
        baseline["Tuning"]!.AsObject().Remove(field);
        JsonNode original = baseline.DeepClone();

        Assert.True(SupportCostVerdict.TryHostedScale(baseline, hosted, out HostedScale scale));
        Assert.Equal(0.5, scale.K);
        Assert.True(JsonNode.DeepEquals(original, baseline));

        hosted["Tuning"]![field] = JsonNode.Parse(changedValue);
        Assert.False(SupportCostVerdict.TryHostedScale(baseline, hosted, out scale));
        Assert.Equal(default, scale);

        baseline["Tuning"]![field] = hosted["Tuning"]![field]!.DeepClone();
        Assert.True(SupportCostVerdict.TryHostedScale(baseline, hosted, out scale));
        Assert.Equal(0.5, scale.K);
    }

    static readonly JsonSerializerOptions HostedOptions = new()
    { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    internal const int HostedRows = 40;

    /// <summary>A section with one successful legacy tick per scene. The first scene is the flat box.</summary>
    internal static JsonObject HostedSection(double time, bool reference = false, int rows = HostedRows)
    {
        var scenes = new JsonObject();
        var samples = new JsonArray();
        for (int i = 0; i < rows; i++)
        {
            string scene = i == 0 ? "flat box" : "legacy row " + i;
            scenes[scene] = JsonSerializer.SerializeToNode(new SupportCostRow(scene), HostedOptions);
            samples.Add(JsonSerializer.SerializeToNode(Synthetic("LegacyTick", time) with
            {
                Scene = scene,
                GeometryMatchesOracle = true,
                ColdGeometryPassed = true,
                ColdAchievedMotion = Vector3.UnitX,
                ColdOutcome = "Grounded"
            }, HostedOptions));
        }
        return new JsonObject
        {
            ["Hardware"] = reference ? "Apple M2 Max" : "Hosted x64 CPU",
            ["Architecture"] = reference ? "Arm64" : "X64",
            ["Runtime"] = ".NET 10.0.12",
            ["OperatingSystem"] = reference ? "macOS" : "Linux",
            ["TieredCompilation"] = "0",
            ["Compilation"] = "Release, tiering disabled",
            ["ProcessExitStatus"] = reference ? JsonValue.Create(0) : null,
            ["Tuning"] = new JsonObject { ["Speed"] = 6 },
            ["Scenes"] = scenes,
            ["Samples"] = samples
        };
    }

    public void Dispose()
    {
        SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
        SupportQueryDiagnostics.SetFindObserver(null);
        SupportQueryDiagnostics.Enable(false);
        SupportQueryDiagnostics.Reset();
    }
}
