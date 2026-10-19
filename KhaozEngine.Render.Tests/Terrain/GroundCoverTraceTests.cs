using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;
using static KhaozEngine.Tests.Terrain.GroundCoverTraceFixtures;

namespace KhaozEngine.Tests.Terrain;

/// <summary>The additive candidate trace and single-address evaluator against frozen released bits and the
/// independent channel oracle, never against the changed kernel's own output.</summary>
public sealed class GroundCoverTraceTests
{
    public static TheoryData<string> Inputs()
    {
        var data = new TheoryData<string> { "basic" };
        foreach (string name in GroundCoverReleasedBaselineTests.VariantNames) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Trace_EqualsReleasedGenerateForEveryOldChannel(string input)
    {
        (RectArea area, GroundCoverSettings settings, Func<float, float, GroundCoverSample> surface) = Input(input);
        var releasedCallbackPoints = new List<(float, float)>();
        IReadOnlyList<GroundCoverInstance> releasedInstances = GroundCoverDistribution.Generate(area, settings, (x, z) =>
        {
            releasedCallbackPoints.Add((x, z));
            return surface(x, z);
        });
        var tracedCallbackPoints = new List<(float, float)>();
        var decisions = new List<GroundCoverDecision>();
        IReadOnlyList<GroundCoverInstance> tracedInstances = GroundCoverDistribution.GenerateTraced(area, settings, c =>
        {
            tracedCallbackPoints.Add((c.WorldX, c.WorldZ));
            return new GroundCoverSurfaceInput(surface(c.WorldX, c.WorldZ));
        }, decisions.Add);

        List<GroundCoverExpectedRow> expected = Expected(area, settings, surface);
        GroundCoverDecision[] owned = decisions.Where(d => d.Kind != GroundCoverDecisionKind.OutsideArea).ToArray();
        Assert.Equal(expected.Select(r => (r.WorldX, r.WorldZ)), releasedCallbackPoints);
        Assert.Equal(releasedCallbackPoints, tracedCallbackPoints);
        Assert.Equal(releasedInstances.Select(i => Key(i)), tracedInstances.Select(i => Key(i)));
        Assert.Equal(expected.Count, owned.Length);
        for (int i = 0; i < owned.Length; i++)
        {
            GroundCoverChannels want = expected[i].Channels;
            Assert.Equal(ChannelText(want, settings.Spacing), CandidateText(owned[i].Candidate));
            bool accepted = owned[i].Kind == GroundCoverDecisionKind.Accepted;
            Assert.Equal(accepted ? Bits(Scale(settings, want.Scale)) : 0u, Bits(owned[i].Scale));
            Assert.Equal(accepted ? Bits(Yaw(want.Yaw)) : 0u, Bits(owned[i].YawRadians));
            Assert.Equal(expected[i].Instance is null ? "-" : Key(expected[i].Instance!.Value),
                owned[i].Instance is null ? "-" : Key(owned[i].Instance!.Value));
            Assert.Equal(ExpectedKind(owned[i].Sample, want), owned[i].Kind);
        }
        Assert.Equal(expected.Where(r => r.Instance is not null).Select(r => Key(r.Instance!.Value)),
            tracedInstances.Select(i => Key(i)));

        if (input == "basic")
        {
            Assert.Equal(ReleasedBits.Select(RowKey), tracedInstances.Select(i => Key(i)));
            Assert.Equal(ReleasedBits.Select(r => (r.X, r.Z)), owned.Select(d => (d.Candidate.X, d.Candidate.Z)));
            Assert.Equal(ReleasedBits.Select(r => r.Channels), owned.Select(d => CandidateChannelBits(d.Candidate)));
            Assert.Equal(ReleasedBits.Select(r => ReleasedPoint(r, settings.Spacing)), tracedCallbackPoints);
            Assert.All(owned, d => Assert.Equal(GroundCoverDecisionKind.Accepted, d.Kind));
        }
        if (input == "zero-density") Assert.All(owned, d => Assert.Equal(GroundCoverDecisionKind.ZeroDensity, d.Kind));
        if (input == "threshold-equal") Assert.All(owned, d => Assert.Equal(GroundCoverDecisionKind.DensityRejected, d.Kind));
        if (input == "threshold-above") Assert.All(owned, d => Assert.Equal(GroundCoverDecisionKind.Accepted, d.Kind));
        Assert.Equal(4_000_000, GroundCoverDistribution.MaxCandidateCount);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("spacing-0.3")]
    public void UnalignedPartitionUnion_PreservesEveryOldChannel(string input)
    {
        (RectArea area, GroundCoverSettings settings, Func<float, float, GroundCoverSample> surface) = Input(input);
        const float splitX = 0.17f;
        const float splitZ = -0.23f;
        RectArea[] parts =
        [
            new(area.MinX, area.MinZ, splitX, splitZ), new(splitX, area.MinZ, area.MaxX, splitZ),
            new(area.MinX, splitZ, splitX, area.MaxZ), new(splitX, splitZ, area.MaxX, area.MaxZ),
        ];

        (List<(int, int)> wholeOwned, List<string> wholeDecisions, List<string> wholeInstances) = Run(area);
        var partitionOwned = new List<(int, int)>();
        var partitionDecisions = new List<string>();
        var partitionInstances = new List<string>();
        foreach (RectArea part in parts)
        {
            (List<(int, int)> owned, List<string> rows, List<string> instances) = Run(part);
            partitionOwned.AddRange(owned);
            partitionDecisions.AddRange(rows);
            partitionInstances.AddRange(instances);
        }

        (int, int)[] wholeOwnedAddresses = [.. wholeOwned.Order()];
        (int, int)[] partitionOwnedAddresses = [.. partitionOwned.Order()];
        Assert.NotEmpty(wholeOwnedAddresses);
        Assert.Equal(wholeOwnedAddresses, partitionOwnedAddresses);
        Assert.Equal(wholeOwnedAddresses.Length, partitionOwnedAddresses.Distinct().Count());
        Assert.Equal(wholeDecisions.Order(StringComparer.Ordinal), partitionDecisions.Order(StringComparer.Ordinal));
        Assert.Equal(wholeInstances.Order(StringComparer.Ordinal), partitionInstances.Order(StringComparer.Ordinal));
        if (input == "basic")
            Assert.Equal(ReleasedBits.Select(RowKey).Order(StringComparer.Ordinal), partitionInstances.Order(StringComparer.Ordinal));
        else
            Assert.Equal(Expected(area, settings, surface).Where(r => r.Instance is not null)
                .Select(r => Key(r.Instance!.Value)).Order(StringComparer.Ordinal), partitionInstances.Order(StringComparer.Ordinal));

        (List<(int, int)>, List<string>, List<string>) Run(RectArea window)
        {
            var owned = new List<(int, int)>();
            var rows = new List<string>();
            IReadOnlyList<GroundCoverInstance> instances = GroundCoverDistribution.GenerateTraced(window, settings,
                c => new GroundCoverSurfaceInput(surface(c.WorldX, c.WorldZ)), d =>
                {
                    if (d.Kind == GroundCoverDecisionKind.OutsideArea) return;
                    owned.Add((d.Candidate.X, d.Candidate.Z));
                    rows.Add(DecisionText(d));
                });
            return (owned, rows, [.. instances.Select(i => Key(i))]);
        }
    }

    [Fact]
    public void OutsideSurface_IsNotAnInventedFloor()
    {
        GroundCoverSettings settings = Basic();
        var decisions = new List<GroundCoverDecision>();
        IReadOnlyList<GroundCoverInstance> instances = GroundCoverDistribution.GenerateTraced(BasicArea, settings,
            c => new GroundCoverSurfaceInput(c.WorldX < 0f ? null : Flat(c.WorldX, c.WorldZ)), decisions.Add);

        GroundCoverDecision[] outside = decisions
            .Where(d => d.Kind != GroundCoverDecisionKind.OutsideArea && d.Candidate.WorldX < 0f).ToArray();
        Assert.NotEmpty(outside);
        Assert.All(outside, row => Assert.Equal(GroundCoverDecisionKind.OutsideSurface, row.Kind));
        Assert.All(outside, row => Assert.Null(row.Sample));
        Assert.All(outside, row => Assert.Null(row.Instance));
        Assert.Equal(ReleasedBits.Where(r => ReleasedPoint(r, settings.Spacing).X >= 0f).Select(RowKey),
            instances.Select(i => Key(i)));
        Assert.All(instances, i => Assert.Equal(1f - 0.01f, i.Position.Y));

        GroundCoverCandidateEvaluator evaluator = GroundCoverDistribution.Prepare(settings);
        GroundCoverDecision single = evaluator.Evaluate(outside[0].Candidate.X, outside[0].Candidate.Z,
            static _ => new GroundCoverSurfaceInput(null));
        Assert.Equal(DecisionText(outside[0]), DecisionText(single));
    }

    [Fact]
    public void TraceObserver_DoesNotChangeCallbackOrderOrOutput()
    {
        GroundCoverSettings settings = Basic();
        var area = new RectArea(-1.3f, -2.1f, 1.7f, 0.9f);
        var withoutCalls = new List<(int, int, float, float)>();
        IReadOnlyList<GroundCoverInstance> without = GroundCoverDistribution.GenerateTraced(area, settings, c =>
        {
            withoutCalls.Add((c.X, c.Z, c.WorldX, c.WorldZ));
            return new GroundCoverSurfaceInput(Rolling(c.WorldX, c.WorldZ));
        });
        var withCalls = new List<(int, int, float, float)>();
        var observed = new List<GroundCoverDecision>();
        IReadOnlyList<GroundCoverInstance> with = GroundCoverDistribution.GenerateTraced(area, settings, c =>
        {
            withCalls.Add((c.X, c.Z, c.WorldX, c.WorldZ));
            return new GroundCoverSurfaceInput(Rolling(c.WorldX, c.WorldZ));
        }, observed.Add);
        var legacyCalls = new List<(float, float)>();
        IReadOnlyList<GroundCoverInstance> legacy = GroundCoverDistribution.Generate(area, settings, (x, z) =>
        {
            legacyCalls.Add((x, z));
            return Rolling(x, z);
        });

        Assert.Equal(withoutCalls, withCalls);
        Assert.Equal(legacyCalls, withCalls.Select(c => (c.Item3, c.Item4)));
        Assert.Equal(without.Select(i => Key(i)), with.Select(i => Key(i)));
        Assert.Equal(legacy.Select(i => Key(i)), with.Select(i => Key(i)));
        Assert.Contains(observed, d => d.Kind == GroundCoverDecisionKind.DensityRejected);
        Assert.Contains(observed, d => d.Kind == GroundCoverDecisionKind.Accepted);

        (int xLo, int xHi, int zLo, int zHi) = Bounds(area, settings.Spacing);
        var scan = new List<(int, int)>();
        for (int z = zLo; z <= zHi; z++)
            for (int x = xLo; x <= xHi; x++) scan.Add((x, z));
        Assert.Equal(scan, observed.Select(d => (d.Candidate.X, d.Candidate.Z)));
        Assert.Equal(withCalls.Select(c => (c.Item1, c.Item2)),
            observed.Where(d => d.Kind != GroundCoverDecisionKind.OutsideArea).Select(d => (d.Candidate.X, d.Candidate.Z)));
        Assert.All(observed.Where(d => d.Kind == GroundCoverDecisionKind.OutsideArea), d =>
        {
            Assert.Null(d.Sample);
            Assert.Null(d.Instance);
        });
        Assert.Equal(with.Select(i => Key(i)), observed.Where(d => d.Instance is not null).Select(d => Key(d.Instance!.Value)));
    }

    [Theory]
    [InlineData(128, 0, 17, 0.5f)]
    [InlineData(128, 0, 0, 0.5f)]
    [InlineData(128, 0, 1, 0.51f)]
    [InlineData(-3, -5, 0, 0.5f)]
    [InlineData(-3, -5, 1, 0.51f)]
    [InlineData(0, 0, 17, 0.51f)]
    [InlineData(7, -2, 1, 0.5f)]
    [InlineData(-1, 3, 0, 0.51f)]
    public void SingleAddressEvaluator_EqualsTracedOwnedDecision(int x, int z, int seed, float spacing)
    {
        GroundCoverSettings settings = Basic(seed, spacing);
        GroundCoverCandidateEvaluator evaluator = GroundCoverDistribution.Prepare(settings);
        GroundCoverChannels want = ChannelsAt(x, z, seed);

        GroundCoverCandidate candidate = evaluator.Candidate(x, z);
        Assert.Equal(ChannelText(want, spacing), CandidateText(candidate));

        int calls = 0;
        GroundCoverDecision single = evaluator.Evaluate(x, z, c =>
        {
            calls++;
            return new GroundCoverSurfaceInput(Rolling(c.WorldX, c.WorldZ));
        });
        Assert.Equal(1, calls);

        var window = new RectArea(candidate.WorldX - spacing, candidate.WorldZ - spacing,
            candidate.WorldX + spacing, candidate.WorldZ + spacing);
        var traced = new List<GroundCoverDecision>();
        GroundCoverDistribution.GenerateTraced(window, settings,
            c => new GroundCoverSurfaceInput(Rolling(c.WorldX, c.WorldZ)), traced.Add);
        GroundCoverDecision owned = Assert.Single(traced,
            d => d.Candidate.X == x && d.Candidate.Z == z && d.Kind != GroundCoverDecisionKind.OutsideArea);

        Assert.Equal(DecisionText(owned), DecisionText(single));
        bool accepted = single.Kind == GroundCoverDecisionKind.Accepted;
        Assert.Equal(accepted ? Bits(Scale(settings, want.Scale)) : 0u, Bits(single.Scale));
        Assert.Equal(accepted ? Bits(Yaw(want.Yaw)) : 0u, Bits(single.YawRadians));
        float worldX = WorldX(want, spacing);
        float worldZ = WorldZ(want, spacing);
        GroundCoverSample sample = Rolling(worldX, worldZ);
        Assert.Equal(ExpectedKind(sample, want), single.Kind);
        string expectedInstance = single.Kind == GroundCoverDecisionKind.Accepted
            ? Key(Instance(settings, want, worldX, worldZ, sample))
            : "-";
        Assert.Equal(expectedInstance, single.Instance is null ? "-" : Key(single.Instance!.Value));
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("zero-density")]
    [InlineData("threshold-equal")]
    [InlineData("noncoplanar")]
    [InlineData("spacing-0.3")]
    public void Generate_KeepsReleasedHashCountPerOutcome(string input)
    {
        (RectArea area, GroundCoverSettings settings, Func<float, float, GroundCoverSample> surface) = Input(input);
        (int xLo, int xHi, int zLo, int zHi) = Bounds(area, settings.Spacing);
        long scanned = (long)(xHi - xLo + 1) * (zHi - zLo + 1);
        List<GroundCoverExpectedRow> owned = Expected(area, settings, surface);
        long accepted = owned.Count(r => r.Instance is not null);
        long zero = owned.Count(r => r.Instance is null && surface(r.WorldX, r.WorldZ).Density == 0f);
        long rejected = owned.Count - accepted - zero;
        long outside = scanned - owned.Count;
        long[] want = [scanned, scanned, rejected + accepted, accepted, accepted, accepted, accepted];

        long[] legacy = Counted(() => GroundCoverDistribution.Generate(area, settings, surface));
        long[] traced = Counted(() => GroundCoverDistribution.GenerateTraced(area, settings,
            c => new GroundCoverSurfaceInput(surface(c.WorldX, c.WorldZ))));

        Assert.Equal(want, legacy);
        Assert.Equal(2 * outside + 2 * zero + 3 * rejected + 7 * accepted, legacy.Sum());
        Assert.Equal(want, traced);
    }

    [Fact]
    public void SingleAddressEvaluator_HashesOnlyWhatItsOutcomeReads()
    {
        GroundCoverCandidateEvaluator evaluator = GroundCoverDistribution.Prepare(Basic());
        long[] outside = Counted(() => evaluator.Evaluate(3, -2, static _ => new GroundCoverSurfaceInput(null)));
        long[] zero = Counted(() => evaluator.Evaluate(3, -2,
            static _ => new GroundCoverSurfaceInput(new GroundCoverSample(1f, Vector3.UnitY, 0f))));
        long[] accepted = Counted(() => evaluator.Evaluate(3, -2,
            static _ => new GroundCoverSurfaceInput(new GroundCoverSample(1f, Vector3.UnitY, 1f))));

        Assert.Equal(new long[] { 1, 1, 0, 0, 0, 0, 0 }, outside);
        Assert.Equal(new long[] { 1, 1, 0, 0, 0, 0, 0 }, zero);
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1 }, accepted);
    }

    static long[] Counted(Action run)
    {
        var counts = new long[7];
        GroundCoverCandidateEvaluator.HashCounts = counts;
        try { run(); }
        finally { GroundCoverCandidateEvaluator.HashCounts = null; }
        return counts;
    }

    [Fact]
    public void SurfaceRefusal_ThrowsToTheBatchBoundary()
    {
        GroundCoverSettings settings = Basic();
        var observed = new List<GroundCoverDecision>();
        Assert.Throws<InvalidOperationException>(() => GroundCoverDistribution.GenerateTraced(BasicArea, settings,
            static _ => throw new InvalidOperationException("refused"), observed.Add));
        Assert.DoesNotContain(observed, d => d.Kind != GroundCoverDecisionKind.OutsideArea);
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.GenerateTraced(BasicArea, settings,
            static _ => new GroundCoverSurfaceInput(new GroundCoverSample(float.NaN, Vector3.UnitY, 1f))));
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Prepare(settings).Evaluate(0, 0,
            static _ => new GroundCoverSurfaceInput(new GroundCoverSample(1f, -Vector3.UnitY, 1f))));
    }

    [Fact]
    public void TracedEntryPoints_KeepLegacyArgumentFailuresAndEmptyArea()
    {
        GroundCoverSettings settings = Basic();
        static GroundCoverSurfaceInput Surface(GroundCoverCandidate c) => new(Flat(c.WorldX, c.WorldZ));

        Assert.Throws<ArgumentNullException>(() => GroundCoverDistribution.GenerateTraced(BasicArea, null!, Surface));
        Assert.Throws<ArgumentNullException>(() => GroundCoverDistribution.GenerateTraced(BasicArea, settings, null!));
        Assert.Throws<ArgumentNullException>(() => GroundCoverDistribution.Prepare(null!));
        Assert.Throws<ArgumentNullException>(() => GroundCoverDistribution.Prepare(settings).Evaluate(0, 0, null!));
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.GenerateTraced(
            new RectArea(1f, 0f, 0f, 1f), settings, Surface));
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.GenerateTraced(
            new RectArea(0f, 0f, 1000f, 1000f), Basic(spacing: 0.01f), Surface));
        foreach (GroundCoverSettings invalid in InvalidSettings())
        {
            Assert.Throws<ArgumentException>(() => GroundCoverDistribution.GenerateTraced(BasicArea, invalid, Surface));
            Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Prepare(invalid));
        }

        int calls = 0;
        int observed = 0;
        IReadOnlyList<GroundCoverInstance> empty = GroundCoverDistribution.GenerateTraced(
            new RectArea(1f, -2f, 1f, 2f), settings, c => { calls++; return Surface(c); }, _ => observed++);
        Assert.Empty(empty);
        Assert.Equal(0, calls);
        Assert.Equal(0, observed);

        GroundCoverCandidateEvaluator evaluator = GroundCoverDistribution.Prepare(settings);
        settings.Seed = 99;
        settings.Models[0] = new GroundCoverModel("changed", 50f);
        Assert.Equal(ChannelText(ChannelsAt(4, -4, 17), 0.5f), CandidateText(evaluator.Candidate(4, -4)));
        Assert.Equal(new[] { "reed-a", "reed-b" }, evaluator.Models.Select(m => m.Id));
    }

    static IEnumerable<GroundCoverSettings> InvalidSettings()
    {
        GroundCoverSettings spacing = Basic(spacing: 0f);
        GroundCoverSettings scale = Basic();
        scale.ScaleMax = 0.5f;
        GroundCoverSettings root = Basic();
        root.RootOffset = float.NaN;
        GroundCoverSettings none = Basic();
        none.Models = [];
        GroundCoverSettings blank = Basic();
        blank.Models = [new GroundCoverModel(" ", 1f)];
        GroundCoverSettings weight = Basic();
        weight.Models = [new GroundCoverModel("reed-a", 0f)];
        return [spacing, scale, root, none, blank, weight];
    }

    static (RectArea, GroundCoverSettings, Func<float, float, GroundCoverSample>) Input(string input) =>
        input == "basic" ? (BasicArea, Basic(), Flat) : GroundCoverReleasedBaselineTests.Variant(input);

    static GroundCoverDecisionKind ExpectedKind(GroundCoverSample? sample, in GroundCoverChannels want) => sample switch
    {
        null => GroundCoverDecisionKind.OutsideSurface,
        { Density: 0f } => GroundCoverDecisionKind.ZeroDensity,
        { } s when want.Density >= s.Density => GroundCoverDecisionKind.DensityRejected,
        _ => GroundCoverDecisionKind.Accepted,
    };

    static (float X, float Z) ReleasedPoint(GroundCoverReleasedRow row, float spacing) =>
        ((row.X + Float(row.Channels[0]) - 0.5f) * spacing, (row.Z + Float(row.Channels[1]) - 0.5f) * spacing);

    static string RowKey(GroundCoverReleasedRow row) =>
        row.Model + "|" + string.Join(",", row.Pose) + "|" + row.Channels[6];

    static uint[] CandidateChannelBits(in GroundCoverCandidate c) =>
        [Bits(c.JitterX), Bits(c.JitterZ), Bits(c.DensityPick), Bits(c.ModelPick), Bits(c.ScalePick), Bits(c.YawPick), Bits(c.Rank)];

    static string CandidateText(in GroundCoverCandidate c) =>
        $"{c.X},{c.Z}|{Bits(c.WorldX)},{Bits(c.WorldZ)}|{string.Join(",", CandidateChannelBits(c))}";

    static string ChannelText(in GroundCoverChannels c, float spacing) =>
        $"{c.X},{c.Z}|{Bits(WorldX(c, spacing))},{Bits(WorldZ(c, spacing))}|{string.Join(",", ChannelBits(c))}";

    static string DecisionText(in GroundCoverDecision d)
    {
        string sample = d.Sample is { } s
            ? $"{Bits(s.Height)},{Bits(s.Normal.X)},{Bits(s.Normal.Y)},{Bits(s.Normal.Z)},{Bits(s.Density)}"
            : "-";
        string instance = d.Instance is { } i ? Key(i) : "-";
        return $"{d.Kind}|{CandidateText(d.Candidate)}|{sample}|{instance}|{Bits(d.Scale)}|{Bits(d.YawRadians)}";
    }
}
