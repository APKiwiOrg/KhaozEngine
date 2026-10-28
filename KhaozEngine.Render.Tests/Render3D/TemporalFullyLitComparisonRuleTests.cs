using System;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalFullyLitComparisonRuleTests(ITestOutputHelper output)
{
    [Fact]
    public void WarpBoundsColourAndConfidenceSeparatelyAndKeepsStoredChannelsExact()
    {
        Compare(colour: 2, confidence: 2);
        Compare(colour: 3, confidence: 0);
        Reject(colour: 0, confidence: 3);
        Reject(colour: 11, confidence: 0);
        Reject(colour: 1, confidence: 0, bits: 0x2c01);
        Reject(colour: 0, confidence: 1, bits: 0x2c01);
        Reject(colour: 1, confidence: 0, bits: 0x7c00);
        Reject(colour: 0, confidence: 1, bits: 0x7e00);
        foreach (string channel in new[] { "lock", "R8", "depth" })
            Assert.ThrowsAny<Exception>(() => Compare(exactMutation: channel));
        Assert.ThrowsAny<Exception>(() => Compare(colour: 1, entry: true));
        Assert.ThrowsAny<Exception>(() => Compare(colour: 1, warp: false));
        Assert.ThrowsAny<Exception>(() => Compare(confidence: 1, softwareVulkan: true, warp: false));
        Compare(colour: 1, entry: true, softwareVulkan: true, warp: false);

        void Reject(int colour, int confidence, ushort bits = 0x2c00)
            => Assert.ThrowsAny<Exception>(() => Compare(colour, confidence, bits));
    }

    [Fact]
    public void NvidiaCertifiedPairsReuseTheAbOracleWhileProductionEntriesStayExact()
    {
        Compare(colour: 100, confidence: 100, bits: 0x3c00, warp: false, nvidia: true);
        Compare(colour: 1, confidence: 1, bits: 0x7e01, warp: false, nvidia: true, baselineBits: 0x7e00);
        Assert.ThrowsAny<Exception>(() => Compare(colour: 1, bits: 0x7c00, warp: false, nvidia: true));
        Assert.ThrowsAny<Exception>(() => Compare(confidence: 1, bits: 0xfc00, warp: false, nvidia: true));
        foreach (string channel in new[] { "lock", "R8", "depth" })
            Assert.ThrowsAny<Exception>(() => Compare(exactMutation: channel, warp: false, nvidia: true));
        Assert.ThrowsAny<Exception>(() => Compare(colour: 1, entry: true, warp: false, nvidia: true));
    }

    void Compare(int colour = 0, int confidence = 0, ushort bits = 0x2c00, string? exactMutation = null,
        bool entry = false, bool softwareVulkan = false, bool warp = true, bool nvidia = false, ushort baselineBits = 0)
    {
        // 1000 texels: four half colour channels, four half state channels (confidence, lock, drift, swing), one R8
        // byte and one single-float depth each.
        var a = new TemporalFullyLitHistoryOracle.Snapshot(1000, 1000, new byte[8000], new byte[8000],
            new byte[1000], new byte[4000]);
        var b = a with
        {
            Colour = (byte[])a.Colour.Clone(),
            State = (byte[])a.State.Clone(),
            Visibility = (byte[])a.Visibility.Clone(),
            Depth = (byte[])a.Depth.Clone()
        };
        for (int i = 0; i < colour; i++)
        {
            Store(a.Colour, i * 2, baselineBits);
            Store(b.Colour, i * 2, bits);
        }
        for (int i = 0; i < confidence; i++)
        {
            Store(a.State, i * 8, baselineBits);
            Store(b.State, i * 8, bits);
        }
        if (exactMutation == "lock") Store(b.State, 2, 0x8000); // Signed zero still differs in storage.
        if (exactMutation == "R8") b.Visibility[0] = 1;
        if (exactMutation == "depth") b.Depth[0] = 1;
        var oracle = new TemporalFullyLitHistoryOracle(output, softwareVulkan, warp, nvidia);
        oracle.Report(0, "pair", a, b, entry);
        oracle.AssertComplete();
    }

    static void Store(byte[] bytes, int offset, ushort bits)
        => BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), bits);
}
