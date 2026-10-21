using System;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Readbacks keep the exact stored channels separate from the half-float arithmetic channels.
internal sealed record TemporalHistorySample(float[] Colour, float[] State, byte[] Visibility, float[] Depth)
{
    internal static TemporalHistorySample Read(TemporalResolveRig rig) => new(rig.ReadColor(), rig.ReadState(),
        TemporalVisibilityHistoryGpuTests.Read(rig.Device,
            rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex)), rig.ReadPreviousDepth());

    internal static TemporalHistorySample Read(TemporalFixture fx)
    {
        TemporalHistory history = fx.Scene.TemporalHistory;
        return new(TemporalTextureIo.Read(fx.Device, history.Color(history.WriteIndex)),
            TemporalTextureIo.Read(fx.Device, history.Confidence(history.WriteIndex)),
            TemporalVisibilityHistoryGpuTests.Read(fx.Device,
                fx.Scene.TemporalResolveRendererForTests!.VisibilityHistory.Texture(history.WriteIndex)),
            TemporalTextureIo.Read(fx.Device, history.PreviousDepth(history.WriteIndex)));
    }

    internal static void Exact(int frame, string what, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.SequenceEqual(b)) return;
        int first = 0;
        while (first < Math.Min(a.Length, b.Length) && a[first] == b[first]) first++;
        Assert.Fail($"frame {frame}: {what} differs at byte {first} (lengths {a.Length}, {b.Length})");
    }

    internal static void Exact(int frame, string what, float[] a, float[] b)
        => Exact(frame, what, MemoryMarshal.AsBytes(a.AsSpan()), MemoryMarshal.AsBytes(b.AsSpan()));

    internal static void Exact(int frame, string what, TemporalHistorySample a, TemporalHistorySample b)
    {
        Exact(frame, what + " colour", a.Colour, b.Colour);
        Exact(frame, what + " state", a.State, b.State);
        Exact(frame, what + " R8", a.Visibility, b.Visibility);
        Exact(frame, what + " depth", a.Depth, b.Depth);
    }
}

internal sealed class TemporalHistoryDifference
{
    internal sealed class Channel
    {
        internal int Count, MaxSteps;
        internal bool NonFinite;

        internal void Add(float a, float b)
        {
            if (BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)) return;
            Count++;
            if (!float.IsFinite(a) || !float.IsFinite(b)) NonFinite = true;
            else MaxSteps = Math.Max(MaxSteps, HalfSteps(a, b));
        }

        public override string ToString() => $"{Count} differ, max {MaxSteps} half steps"
            + (NonFinite ? ", non-finite difference" : "");
    }

    internal readonly Channel[] Colour = [new(), new(), new(), new()];
    internal readonly Channel Confidence = new(), Locks = new();

    internal static TemporalHistoryDifference Measure(float[] a, float[] stateA, float[] b, float[] stateB)
    {
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(stateA.Length, stateB.Length);
        var difference = new TemporalHistoryDifference();
        for (int i = 0; i < a.Length; i++) difference.Colour[i % 4].Add(a[i], b[i]);
        for (int i = 0; i < stateA.Length; i += 2)
        {
            difference.Confidence.Add(stateA[i], stateB[i]);
            difference.Locks.Add(stateA[i + 1], stateB[i + 1]);
        }
        return difference;
    }

    // Ordered half encodings count representable steps on either side of zero. The two zeros share an ordinal,
    // but Channel.Count retains their bit difference for exact lock comparisons.
    internal static int HalfSteps(float a, float b) => Math.Abs(Ordinal(a) - Ordinal(b));
    static int Ordinal(float value)
    {
        short bits = BitConverter.HalfToInt16Bits((Half)value);
        return bits < 0 ? -(bits & 0x7fff) : bits;
    }

    public override string ToString() => "colour RGBA [" + string.Join(" | ", Colour.Select(c => c.ToString()))
        + $"], confidence [{Confidence}], any lock differs {Locks.Count > 0} ({Locks.Count})";
}

// The hardware NVIDIA D3D11 A/B rule. That driver compiles confidence arithmetic unrelated to the visibility check
// differently whenever the visibility code changes, frame 0 included, while WARP running the same DXBC matches D bit
// for bit. Stored locks, R8 and depth must equal D's. Colour and confidence differences are reported, never
// asserted. Entry identity never uses this rule.
internal static class TemporalNvidiaComparisonRule
{
    internal static bool Allows(TemporalHistoryDifference d) => d.Locks.Count == 0;
}

// Only hardware NVIDIA D3D11 creates this oracle. D's paired entries and every comparison it prints in entry
// identity are diagnostic only.
internal sealed class TemporalNvidiaHistoryOracle(ITestOutputHelper output)
{
    internal void ObserveD(int frame, TemporalHistorySample fused, TemporalHistorySample split)
    {
        TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(fused.Colour, fused.State,
            split.Colour, split.State);
        output.WriteLine($"  frame {frame}: D fused vs D split (diagnostic only): {difference}, "
            + $"R8 equal {fused.Visibility.AsSpan().SequenceEqual(split.Visibility)}, "
            + $"depth bits equal {MemoryMarshal.AsBytes(fused.Depth.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(split.Depth.AsSpan()))}");
    }

    // Prints a comparison and asserts nothing.
    internal TemporalHistoryDifference Report(int frame, string what, TemporalHistorySample a, TemporalHistorySample b)
    {
        TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(a.Colour, a.State, b.Colour, b.State);
        output.WriteLine($"  frame {frame}: {what} (diagnostic only): {difference}, "
            + $"R8 equal {a.Visibility.AsSpan().SequenceEqual(b.Visibility)}, "
            + $"depth bits equal {MemoryMarshal.AsBytes(a.Depth.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Depth.AsSpan()))}");
        return difference;
    }

    // The A/B comparison: exact stored locks, R8 and depth, colour and confidence printed only.
    internal TemporalHistoryDifference Compare(int frame, string what, TemporalHistorySample a,
        TemporalHistorySample b)
    {
        TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(a.Colour, a.State, b.Colour, b.State);
        output.WriteLine($"  frame {frame}: {what}: {difference}");
        TemporalHistorySample.Exact(frame, what + " R8", a.Visibility, b.Visibility);
        TemporalHistorySample.Exact(frame, what + " depth", a.Depth, b.Depth);
        Assert.True(TemporalNvidiaComparisonRule.Allows(difference), $"frame {frame}: {what}: stored locks differ: {difference}");
        return difference;
    }

    internal void Complete() => output.WriteLine("  NVIDIA Direct3D 11: exact stored locks, R8 and depth against D. "
        + "Colour and confidence differences against D are reported only.");
}
