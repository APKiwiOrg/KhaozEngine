using System;
using System.Runtime.InteropServices;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The format 2 value contracts: option limits and their first-invalid naming, the envelope copy, unsigned sequence
/// order across the wrap with the half range invalid, epoch zero refused at stream setup, and a packet that owns an
/// immutable copy of its body.
/// </summary>
public class DeltaRebuildContractTests
{
    private static void AssertInvalid(string property, DeltaRebuildOptions options)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(property, ex.ParamName);
    }

    [Fact]
    public void LimitsRejectTooFewPinsAndInvalidEnvelope()
    {
        var defaults = new DeltaRebuildOptions();
        Assert.Equal(32, defaults.MaxRetainedProjections);
        Assert.Equal(2 * 1024 * 1024, defaults.MaxRetainedPayloadBytes);
        Assert.Equal(65536, defaults.MaxKeyframeBytes);
        Assert.Equal(1024, defaults.MaxEntities);
        Assert.Equal(16384, defaults.MaxComponents);
        Assert.Equal(0, defaults.EnvelopeBytes);
        Assert.Equal(31, defaults.NoAckSendWindow);
        defaults.Validate();
        Assert.Equal(31, defaults.EffectiveNoAckSendWindow);

        // Three pins plus a candidate need four retained projections.
        AssertInvalid(nameof(DeltaRebuildOptions.MaxRetainedProjections), new() { MaxRetainedProjections = 3 });
        new DeltaRebuildOptions { MaxRetainedProjections = 4 }.Validate();
        Assert.Equal(3, new DeltaRebuildOptions { MaxRetainedProjections = 4 }.EffectiveNoAckSendWindow);
        Assert.Equal(1, new DeltaRebuildOptions { NoAckSendWindow = 1 }.EffectiveNoAckSendWindow);

        AssertInvalid(nameof(DeltaRebuildOptions.MaxRetainedPayloadBytes), new() { MaxRetainedPayloadBytes = 0 });
        AssertInvalid(nameof(DeltaRebuildOptions.MaxKeyframeBytes), new() { MaxKeyframeBytes = -1 });
        AssertInvalid(nameof(DeltaRebuildOptions.MaxEntities), new() { MaxEntities = 0 });
        AssertInvalid(nameof(DeltaRebuildOptions.MaxComponents), new() { MaxComponents = 0 });
        AssertInvalid(nameof(DeltaRebuildOptions.NoAckSendWindow), new() { NoAckSendWindow = 0 });

        // The envelope charge is nonnegative and strictly below the keyframe cap.
        AssertInvalid(nameof(DeltaRebuildOptions.EnvelopeBytes), new() { EnvelopeBytes = -1 });
        AssertInvalid(nameof(DeltaRebuildOptions.EnvelopeBytes), new() { EnvelopeBytes = 65536 });
        new DeltaRebuildOptions { EnvelopeBytes = 65535 }.Validate();

        // The first invalid property in declaration order is the one named.
        AssertInvalid(nameof(DeltaRebuildOptions.MaxRetainedProjections),
            new() { MaxRetainedProjections = 0, EnvelopeBytes = -1, NoAckSendWindow = 0 });

        var custom = new DeltaRebuildOptions
        {
            MaxRetainedProjections = 8, MaxRetainedPayloadBytes = 4096, MaxKeyframeBytes = 1024,
            MaxEntities = 10, MaxComponents = 20, NoAckSendWindow = 5,
        };
        DeltaRebuildOptions charged = custom.WithEnvelopeBytes(12);
        Assert.NotSame(custom, charged);
        Assert.Equal(12, charged.EnvelopeBytes);
        Assert.Equal(0, custom.EnvelopeBytes);
        Assert.Equal(8, charged.MaxRetainedProjections);
        Assert.Equal(4096, charged.MaxRetainedPayloadBytes);
        Assert.Equal(1024, charged.MaxKeyframeBytes);
        Assert.Equal(10, charged.MaxEntities);
        Assert.Equal(20, charged.MaxComponents);
        Assert.Equal(5, charged.NoAckSendWindow);
        Assert.Equal(nameof(DeltaRebuildOptions.EnvelopeBytes),
            Assert.Throws<ArgumentOutOfRangeException>(() => custom.WithEnvelopeBytes(1024)).ParamName);
        Assert.Equal(nameof(DeltaRebuildOptions.EnvelopeBytes),
            Assert.Throws<ArgumentOutOfRangeException>(() => custom.WithEnvelopeBytes(-1)).ParamName);
        Assert.Equal(nameof(DeltaRebuildOptions.MaxRetainedProjections),
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new DeltaRebuildOptions { MaxRetainedProjections = 2 }.WithEnvelopeBytes(12)).ParamName);

        // Epoch zero constructs as a record value but is refused at stream setup.
        var zero = new ReplicationPacketId(0, 5);
        Assert.Equal(0ul, zero.Epoch);
        Assert.Equal("epoch", Assert.Throws<ArgumentOutOfRangeException>(
            () => ReplicationSequence.RequireEpoch(0, "epoch")).ParamName);
        Assert.Equal(7ul, ReplicationSequence.RequireEpoch(7, "epoch"));
    }

    // Ruling D2.9: the byte budget holds three complete keyframes, so an acknowledged baseline, a pending keyframe and
    // a new candidate always fit together and no stream can be configured into a repair loop.
    [Fact]
    public void RetainedBudgetMustHoldThreeKeyframes()
    {
        var defaults = new DeltaRebuildOptions();
        defaults.Validate();
        Assert.True(defaults.MaxRetainedPayloadBytes >= 3 * defaults.MaxKeyframeBytes);

        AssertInvalid(nameof(DeltaRebuildOptions.MaxRetainedPayloadBytes),
            new() { MaxKeyframeBytes = 1000, MaxRetainedPayloadBytes = 2000 });
        AssertInvalid(nameof(DeltaRebuildOptions.MaxRetainedPayloadBytes),
            new() { MaxKeyframeBytes = 1000, MaxRetainedPayloadBytes = 2999 });
        new DeltaRebuildOptions { MaxKeyframeBytes = 1000, MaxRetainedPayloadBytes = 3000 }.Validate();

        // Three keyframes near int.MaxValue overflow an int product, which must not wrap into acceptance.
        AssertInvalid(nameof(DeltaRebuildOptions.MaxRetainedPayloadBytes),
            new() { MaxKeyframeBytes = int.MaxValue / 2, MaxRetainedPayloadBytes = int.MaxValue });

        Assert.Equal(nameof(DeltaRebuildOptions.MaxRetainedPayloadBytes),
            Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaRebuildOptions
            {
                MaxKeyframeBytes = 1000, MaxRetainedPayloadBytes = 2000,
            }.WithEnvelopeBytes(12)).ParamName);
        Assert.Equal(12, new DeltaRebuildOptions { MaxKeyframeBytes = 1000, MaxRetainedPayloadBytes = 3000 }
            .WithEnvelopeBytes(12).EnvelopeBytes);
    }

    [Fact]
    public void SequenceWrapIsNewerButHalfRangeIsInvalid()
    {
        Assert.True(ReplicationSequence.IsNewer(0u, uint.MaxValue));
        Assert.False(ReplicationSequence.IsNewer(9u, 9u));
        Assert.True(ReplicationSequence.IsAmbiguous(0x80000000u, 0u));
        Assert.Equal(32, new DeltaRebuildOptions().MaxRetainedProjections);
        Assert.Equal(65536, new DeltaRebuildOptions().MaxKeyframeBytes);

        Assert.True(ReplicationSequence.IsNewer(5u, uint.MaxValue - 2));
        Assert.True(ReplicationSequence.IsNewer(0x7fffffffu, 0u));
        Assert.False(ReplicationSequence.IsNewer(0x80000000u, 0u));
        Assert.False(ReplicationSequence.IsNewer(0u, 0x80000000u));
        Assert.True(ReplicationSequence.IsAmbiguous(0u, 0x80000000u));
        Assert.False(ReplicationSequence.IsNewer(uint.MaxValue, 0u));
        Assert.False(ReplicationSequence.IsAmbiguous(uint.MaxValue, 0u));
        Assert.False(ReplicationSequence.IsAmbiguous(1u, 0u));
        Assert.False(ReplicationSequence.IsAmbiguous(9u, 9u));
    }

    [Fact]
    public void PacketOwnsItsInputBytes()
    {
        var baseline = new ReplicationPacketId(3, uint.MaxValue);
        var id = new ReplicationPacketId(3, 0);
        byte[] source = { 9, 1, 2, 3, 4, 9 };
        var packet = new ReplicationDeltaPacket(id, baseline, isKeyframe: false, source.AsSpan(1, 4));
        source[1] = 99;
        source[4] = 99;

        Assert.Equal(id, packet.Id);
        Assert.Equal(baseline, packet.Baseline);
        Assert.False(packet.IsKeyframe);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, packet.Bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(packet.Bytes, out ArraySegment<byte> owned));
        Assert.NotSame(source, owned.Array);
        Assert.Equal(4, owned.Array!.Length);

        var keyframe = new ReplicationDeltaPacket(new ReplicationPacketId(4, 1), null, isKeyframe: true,
            ReadOnlySpan<byte>.Empty);
        Assert.Null(keyframe.Baseline);
        Assert.True(keyframe.IsKeyframe);
        Assert.Equal(0, keyframe.Bytes.Length);

        // A keyframe names no baseline, a delta names one in its own epoch.
        Assert.Throws<ArgumentException>(() => new ReplicationDeltaPacket(id, baseline, true, source));
        Assert.Throws<ArgumentException>(() => new ReplicationDeltaPacket(id, null, false, source));
        Assert.Throws<ArgumentException>(
            () => new ReplicationDeltaPacket(id, new ReplicationPacketId(2, 0), false, source));
    }
}
