using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
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
        long before = GC.GetAllocatedBytesForCurrentThread();
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find);
        SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
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
        var sampler = new SupportCostSampler(false) { IsolateAllocations = true, IsolateColdAllocations = false };
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
            Assert.False(SupportCostVerdict.Evaluate(find, find, row.Dense));
        }
        else if (row.KnownIncomplete)
        {
            Assert.True(find.KnownIncorrect);
            Assert.False(find.GeometryPassed);
            Assert.False(SupportCostVerdict.Evaluate(find, find, row.Dense));
        }
        else
        {
            Assert.True(find.GeometryMatchesOracle, $"{name} oracle {find.OracleOutcome}/{find.OracleHeight}, actual {find.Outcome}/{find.Height}");
            if (find.OracleOutcome is "Walkable" or "Steep") Assert.True(find.GeometryPassed);
            else Assert.False(SupportCostVerdict.Evaluate(find, find, row.Dense));
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
            if (!sample.GeometryPassed) Assert.False(SupportCostVerdict.Evaluate(sample, legacy, row.Dense));
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
        int signals = row.StairSignal ? 3 : 0;
        int findBound = 1 + 8 * n + signals;
        if (row.Explicit) findBound += 4 + 8 * c.MediumRetries;
        if (!row.Grounded || row.Normal.Y < 0.70710677f)
            findBound += 2 * Math.Max(1, c.ModeSegments) +
                2 * (int)Math.Ceiling((Math.Abs(row.VerticalVelocity) + 25 * row.Dt) * row.Dt / 0.2);
        Assert.True(c.Find <= findBound, $"{row.Name}: {c.Find} > derived route {findBound}");
        Assert.InRange(c.MediumRetries, 0, row.Explicit ? 16 * n : 0);
        int retry = row.Explicit ? 36 * c.MediumRetries + 16 * Math.Max(1, c.ModeSegments) : 0;
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

    static void AssertNamedRoute(SupportCostRow row, SupportCostSample sample)
    {
        Assert.Equal(sample.Counts.Find, sample.SupportReads.Length);
        Assert.InRange(sample.Routes.MaximumMediumRetries, 0, 8);
        Assert.InRange(sample.Counts.MediumRetries, 0, 8 * sample.Routes.MediumResolves);
        if (row.StairSignal)
            Assert.Equal(3, sample.SupportReads.Count(q => q.ReachUp > 1));
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
        var sampler = new SupportCostSampler(true) { IsolateAllocations = true, IsolateColdAllocations = false };
        var samples = new List<SupportCostSample>();
        var failures = new List<string>();
        foreach (object[] data in SupportGateCostScenes.Rows())
        {
            SupportCostRow row = (SupportCostRow)data[0];
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
        string label = File.Exists(path) ? "sg9-cost" : "sg2-baseline";
        string command = "DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=" + path +
            " build-slot --label " + label + " -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter \"FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact\"";
        SupportCostArtifact.Write(path, samples, BaseSha, sha, command);
        Assert.True(File.Exists(path));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        output.WriteLine($"Recorded {samples.Count} first-use/warm operation pairs at {sha}.");
    }

    static string GitSha()
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
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
        Assert.Equal(expected, SupportCostVerdict.Evaluate(candidate, Synthetic("LegacyDownPass", legacyTime), false, scale));
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
            Synthetic("LegacyTick", legacyTime), false, scale));
    }

    static SupportCostSample Synthetic(string operation, double time) => new(operation, "synthetic", true,
        "Grounded", 0, 0, Vector3.UnitX, 1, default, 0, 0, 0, time, 0, 4352);

    public void Dispose()
    {
        SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
        SupportQueryDiagnostics.SetFindObserver(null);
        SupportQueryDiagnostics.Enable(false);
        SupportQueryDiagnostics.Reset();
    }
}
