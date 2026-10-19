using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Terrain.GroundCoverTraceFixtures;

namespace KhaozEngine.Tests.Terrain;

/// <summary>Pins the released public generator against an independent channel oracle. Uses only the released
/// Generate entry point, so it holds before and after the additive trace seam.</summary>
public sealed class GroundCoverReleasedBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public void ReleasedGenerator_MatchesIndependentChannels()
    {
        GroundCoverSettings settings = Basic();
        IReadOnlyList<GroundCoverInstance> released = GroundCoverDistribution.Generate(BasicArea, settings, Flat);
        List<GroundCoverExpectedRow> expected = Expected(BasicArea, settings, Flat);

        Assert.Equal(4_000_000, GroundCoverDistribution.MaxCandidateCount);
        Assert.NotEmpty(expected);
        Assert.Contains(expected, row => row.Channels.X < 0 && row.Channels.Z < 0);
        Assert.All(expected, row => Assert.NotNull(row.Instance));
        Assert.Equal(expected.Count, released.Count);
        var rows = new List<GroundCoverReleasedRow>();
        for (int i = 0; i < expected.Count; i++)
        {
            GroundCoverInstance want = expected[i].Instance!.Value;
            Assert.Equal(want.ModelId, released[i].ModelId);
            Assert.Equal(PoseBits(want), PoseBits(released[i]));
            Assert.Equal(Bits(want.ThinningRank), Bits(released[i].ThinningRank));
            rows.Add(new GroundCoverReleasedRow(expected[i].Channels.X, expected[i].Channels.Z, released[i].ModelId,
                ChannelBits(expected[i].Channels), PoseBits(released[i])));
        }
        Assert.Contains(released, p => p.ModelId == "reed-a");
        Assert.Contains(released, p => p.ModelId == "reed-b");

        output.WriteLine(Literal(rows));
        Assert.Equal(ReleasedBits.Count, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal((ReleasedBits[i].X, ReleasedBits[i].Z, ReleasedBits[i].Model), (rows[i].X, rows[i].Z, rows[i].Model));
            Assert.Equal(ReleasedBits[i].Channels, rows[i].Channels);
            Assert.Equal(ReleasedBits[i].Pose, rows[i].Pose);
        }
    }

    /// <summary>The invented variant names shared with the trace tests.</summary>
    public static string[] VariantNames { get; } =
    [
        "negative", "zero-density", "threshold-equal", "threshold-above", "noncoplanar", "spacing-0.3",
        "seed-0", "seed-1", "spacing-0.51", "address-128",
    ];

    public static TheoryData<string> Variants()
    {
        var data = new TheoryData<string>();
        foreach (string name in VariantNames) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void ReleasedGenerator_MatchesIndependentChannelsAcrossVariants(string variant)
    {
        (RectArea area, GroundCoverSettings settings, Func<float, float, GroundCoverSample> surface) = Variant(variant);
        var calls = new List<(float X, float Z)>();
        IReadOnlyList<GroundCoverInstance> released = GroundCoverDistribution.Generate(area, settings, (x, z) =>
        {
            calls.Add((x, z));
            return surface(x, z);
        });
        List<GroundCoverExpectedRow> expected = Expected(area, settings, surface);
        GroundCoverInstance[] accepted = expected.Where(r => r.Instance is not null).Select(r => r.Instance!.Value).ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(r => (r.WorldX, r.WorldZ)), calls);
        Assert.Equal(accepted.Select(i => Key(i)), released.Select(i => Key(i)));
        if (variant is "zero-density" or "threshold-equal") Assert.Empty(released);
        else Assert.NotEmpty(released);
    }

    /// <summary>The invented variant inputs shared with the trace tests.</summary>
    public static (RectArea Area, GroundCoverSettings Settings, Func<float, float, GroundCoverSample> Surface) Variant(
        string variant)
    {
        var negative = new RectArea(-7.3f, -5.9f, -1.1f, -0.4f);
        return variant switch
        {
            "negative" => (negative, Basic(), Flat),
            "zero-density" => (BasicArea, Basic(), static (x, z) => new GroundCoverSample(1f, Vector3.UnitY, 0f)),
            "threshold-equal" => (BasicArea, Basic(), Threshold(BasicArea, Basic(), 0)),
            "threshold-above" => (BasicArea, Basic(), Threshold(BasicArea, Basic(), 1)),
            "noncoplanar" => (negative, Basic(), Rolling),
            "spacing-0.3" => (new RectArea(-1.7f, -2.3f, 1.9f, 1.3f), Basic(spacing: 0.3f), Rolling),
            "seed-0" => (BasicArea, Basic(seed: 0), Rolling),
            "seed-1" => (BasicArea, Basic(seed: 1), Rolling),
            "spacing-0.51" => (BasicArea, Basic(spacing: 0.51f), Rolling),
            "address-128" => (new RectArea(63.5f, -1f, 64.5f, 1f), Basic(), Rolling),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
    }
}
