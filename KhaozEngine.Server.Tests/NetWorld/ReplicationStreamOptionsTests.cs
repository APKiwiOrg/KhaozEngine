using System;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Format 2 option defaults and validation. The opt-ins default off and need the existing delta switch, a
/// reliable-only config never has format 2 budgets checked, and every invalid limit is refused rather than raised.
/// </summary>
public class ReplicationStreamOptionsTests
{
    private const float Tick = 1f / 30f;

    [Fact]
    public void ConcreteDefaults()
    {
        var options = new ReplicationStreamOptions();

        Assert.Equal(512, options.MaxTransportPayloadBytes);
        Assert.Equal(4, options.MaxChunksPerTick);
        Assert.Equal(30, options.RepairRequestIntervalTicks);
        Assert.Equal(90, options.RecoveryDeadlineTicks);
        Assert.Equal(32, options.Limits.MaxRetainedProjections);
        Assert.Equal(2 * 1024 * 1024, options.Limits.MaxRetainedPayloadBytes);
        Assert.Equal(64 * 1024, options.Limits.MaxKeyframeBytes);
        Assert.Equal(1024, options.Limits.MaxEntities);
        Assert.Equal(16384, options.Limits.MaxComponents);
        Assert.Equal(0, options.Limits.EnvelopeBytes);
        Assert.Equal(31, options.Limits.NoAckSendWindow);
    }

    [Fact]
    public void OptInsDefaultOffOnEveryConfig()
    {
        Assert.False(new WorldServerConfig().AllowUnreliableDeltaReplication);
        Assert.False(new ShardedWorldServerConfig().AllowUnreliableDeltaReplication);
        Assert.False(new WorldClientConfig().RequestUnreliableDeltaReplication);
        Assert.NotNull(new WorldServerConfig().ReplicationStream);
        Assert.NotNull(new ShardedWorldServerConfig().ReplicationStream);
        Assert.NotNull(new WorldClientConfig().ReplicationStream);
        Assert.Equal(512, new WorldClientConfig().ReplicationStream.MaxTransportPayloadBytes);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void FormatTwoNeedsTheDeltaSwitch(bool delta, bool optIn, bool enabled)
    {
        Assert.Equal(enabled, ReplicationStreamOptions.IsEnabled(delta, optIn));
    }

    [Fact]
    public void ReliableOnlyValidationChecksNoFormatTwoBudgets()
    {
        var broken = new ReplicationStreamOptions
        {
            Limits = new DeltaRebuildOptions { MaxRetainedProjections = 1, EnvelopeBytes = 7 },
            MaxTransportPayloadBytes = 10,
            MaxChunksPerTick = 0,
        };

        Assert.Null(broken.ValidateConfig(deltaReplication: true, optIn: false, float.NaN));
        Assert.Null(broken.ValidateConfig(deltaReplication: false, optIn: true, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => broken.ValidateConfig(true, true, Tick));
    }

    [Fact]
    public void ValidationDerivesTheTwelveByteEnvelope()
    {
        DeltaRebuildOptions fromZero = new ReplicationStreamOptions().ValidateForUnreliable(Tick);
        DeltaRebuildOptions fromTwelve = new ReplicationStreamOptions
        {
            Limits = new DeltaRebuildOptions { EnvelopeBytes = 12 },
        }.ValidateForUnreliable(Tick);

        Assert.Equal(12, fromZero.EnvelopeBytes);
        Assert.Equal(12, fromTwelve.EnvelopeBytes);
        Assert.Equal(31, fromZero.NoAckSendWindow);
        Assert.Equal(65536, fromZero.MaxKeyframeBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(13)]
    public void OtherEnvelopeChargesAreRefusedNotReplaced(int envelope)
    {
        var options = new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { EnvelopeBytes = envelope } };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateForUnreliable(Tick));
        Assert.Equal("Limits.EnvelopeBytes", error.ParamName);
    }

    [Fact]
    public void MinimumFourRetainedStates()
    {
        var three = new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { MaxRetainedProjections = 3 } };
        var four = new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { MaxRetainedProjections = 4 } };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => three.ValidateForUnreliable(Tick));
        Assert.Equal(nameof(DeltaRebuildOptions.MaxRetainedProjections), error.ParamName);
        Assert.Equal(4, four.ValidateForUnreliable(Tick).MaxRetainedProjections);
    }

    [Fact]
    public void ConfiguredValuesMustFitTheirOfferFields()
    {
        var history = new ReplicationStreamOptions
        {
            Limits = new DeltaRebuildOptions { MaxRetainedProjections = ushort.MaxValue + 1 },
        };
        var chunks = new ReplicationStreamOptions { MaxChunksPerTick = ushort.MaxValue + 1 };
        var largest = new ReplicationStreamOptions
        {
            Limits = new DeltaRebuildOptions { MaxRetainedProjections = ushort.MaxValue },
            MaxChunksPerTick = ushort.MaxValue,
        };

        Assert.Equal("Limits.MaxRetainedProjections",
            Assert.Throws<ArgumentOutOfRangeException>(() => history.ValidateForUnreliable(Tick)).ParamName);
        Assert.Equal(nameof(ReplicationStreamOptions.MaxChunksPerTick),
            Assert.Throws<ArgumentOutOfRangeException>(() => chunks.ValidateForUnreliable(Tick)).ParamName);
        Assert.Equal(ushort.MaxValue, largest.ValidateForUnreliable(Tick).MaxRetainedProjections);
    }

    [Theory]
    [InlineData(512, 0, 30, 90, nameof(ReplicationStreamOptions.MaxChunksPerTick))]
    [InlineData(512, 4, 0, 90, nameof(ReplicationStreamOptions.RepairRequestIntervalTicks))]
    [InlineData(512, 4, 30, 0, nameof(ReplicationStreamOptions.RecoveryDeadlineTicks))]
    [InlineData(280, 4, 30, 90, nameof(ReplicationStreamOptions.MaxTransportPayloadBytes))]
    [InlineData(0, 4, 30, 90, nameof(ReplicationStreamOptions.MaxTransportPayloadBytes))]
    public void NonpositiveOrInfeasibleKnobsAreRefused(int cap, int chunks, int repair, int deadline, string property)
    {
        var options = new ReplicationStreamOptions
        {
            MaxTransportPayloadBytes = cap,
            MaxChunksPerTick = chunks,
            RepairRequestIntervalTicks = repair,
            RecoveryDeadlineTicks = deadline,
        };

        Assert.Equal(property,
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateForUnreliable(Tick)).ParamName);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void TickSecondsMustBePositiveAndFiniteWhenFormatTwoIsOn(float tickSeconds)
    {
        var options = new ReplicationStreamOptions();

        Assert.Equal("tickSeconds",
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateConfig(true, true, tickSeconds)).ParamName);
        Assert.Null(options.ValidateConfig(true, false, tickSeconds));
    }

    [Fact]
    public void DefaultCapGivesWidth489And135ChunksIn34Ticks()
    {
        Assert.Equal(23, ReplicationStreamOptions.KeyframeChunkOverheadBytes);
        Assert.Equal(489, ReplicationStreamOptions.ChunkWidth(512));
        Assert.Equal(135, ReplicationStreamOptions.KeyframeChunkCount(65536, 512));
        Assert.Equal(34, (135 + 4 - 1) / 4);
        Assert.True(ReplicationStreamOptions.IsFeasiblePacketCap(512, 65536));
    }

    [Fact]
    public void Cap281IsTheSmallestFeasibleCapForA64KiBKeyframe()
    {
        Assert.Equal(258, ReplicationStreamOptions.ChunkWidth(281));
        Assert.Equal(255, ReplicationStreamOptions.KeyframeChunkCount(65536, 281));
        Assert.True(ReplicationStreamOptions.IsFeasiblePacketCap(281, 65536));
        Assert.Equal(256, ReplicationStreamOptions.KeyframeChunkCount(65536, 280));
        Assert.False(ReplicationStreamOptions.IsFeasiblePacketCap(280, 65536));
        Assert.False(ReplicationStreamOptions.IsFeasiblePacketCap(23, 1));
        Assert.Equal(int.MaxValue, ReplicationStreamOptions.KeyframeChunkCount(65536, 23));
    }

    [Fact]
    public void AnEmptyStateDatagramMustFit()
    {
        Assert.False(ReplicationStreamOptions.IsFeasiblePacketCap(39, 100));
        Assert.True(ReplicationStreamOptions.IsFeasiblePacketCap(40, 100));
    }

    [Theory]
    [InlineData(1400, 1400, true, 512)]
    [InlineData(400, 1400, true, 400)]
    [InlineData(1400, 300, true, 300)]
    [InlineData(281, 281, true, 281)]
    [InlineData(280, 1400, false, 0)]
    [InlineData(0, 1400, false, 0)]
    [InlineData(1400, 0, false, 0)]
    [InlineData(-1, 1400, false, 0)]
    public void SelectedCapIsTheSmallestLimitAndNeverRaised(int unreliable, int reliable, bool selects, int expected)
    {
        var options = new ReplicationStreamOptions();

        Assert.Equal(selects, options.TrySelectPacketCap(unreliable, reliable, out int cap));
        Assert.Equal(expected, cap);
    }

    [Fact]
    public void SmallerConfiguredCapIsKeptNotRaised()
    {
        var options = new ReplicationStreamOptions { MaxTransportPayloadBytes = 300 };

        Assert.True(options.TrySelectPacketCap(1400, 1400, out int cap));
        Assert.Equal(300, cap);
        Assert.Equal(300, options.MaxTransportPayloadBytes);
    }
}
