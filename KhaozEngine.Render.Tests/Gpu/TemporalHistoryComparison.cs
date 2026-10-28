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
    // A value's non-finite class: NaN, positive infinity, negative infinity or finite.
    internal enum ValueClass { Finite, NaN, PositiveInfinity, NegativeInfinity }

    internal static ValueClass Classify(float value) => float.IsNaN(value) ? ValueClass.NaN
        : float.IsPositiveInfinity(value) ? ValueClass.PositiveInfinity
        : float.IsNegativeInfinity(value) ? ValueClass.NegativeInfinity : ValueClass.Finite;

    internal sealed class Channel
    {
        internal int Count, MaxSteps, ClassMismatches;
        internal bool NonFinite;
        internal string? FirstClassMismatch;

        internal void Add(int index, float a, float b)
        {
            if (BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)) return;
            Count++;
            if (Classify(a) != Classify(b))
            {
                ClassMismatches++;
                FirstClassMismatch ??= $"value {index} is {Classify(a)} against {Classify(b)}";
            }
            if (!float.IsFinite(a) || !float.IsFinite(b)) NonFinite = true;
            else MaxSteps = Math.Max(MaxSteps, HalfSteps(a, b));
        }

        public override string ToString() => $"{Count} differ, max {MaxSteps} half steps"
            + (NonFinite ? ", non-finite difference" : "")
            + (ClassMismatches > 0 ? $", {ClassMismatches} non-finite class mismatches, first {FirstClassMismatch}" : "");
    }

    internal readonly Channel[] Colour = [new(), new(), new(), new()];
    internal readonly Channel Confidence = new(), Locks = new();

    internal static TemporalHistoryDifference Measure(float[] a, float[] stateA, float[] b, float[] stateB)
    {
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(stateA.Length, stateB.Length);
        var difference = new TemporalHistoryDifference();
        for (int i = 0; i < a.Length; i++) difference.Colour[i % 4].Add(i, a[i], b[i]);
        for (int i = 0; i < stateA.Length; i += 4)
        {
            difference.Confidence.Add(i, stateA[i], stateB[i]);
            difference.Locks.Add(i + 1, stateA[i + 1], stateB[i + 1]);
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

// The hardware NVIDIA D3D11 rules. That driver compiles confidence arithmetic unrelated to the visibility check
// differently whenever the visibility code changes, frame 0 included, while WARP running the same DXBC matches D bit
// for bit. Every colour and confidence value must keep D's non-finite class (NaN, positive infinity, negative infinity
// or finite), and only finite differences are reported. The A/B rule also holds stored locks, R8 and depth to D's
// bits. Entry identity holds its production against D comparisons to the class rule alone.
internal static class TemporalNvidiaComparisonRule
{
    internal static bool ClassesMatch(TemporalHistoryDifference d)
        => d.Confidence.ClassMismatches == 0 && d.Colour.All(c => c.ClassMismatches == 0);

    internal static bool Allows(TemporalHistoryDifference d) => d.Locks.Count == 0 && ClassesMatch(d);
}

// Software-device compile divergence, with the single bound pair accepted in temporal amendment 23. llvmpipe's
// production entry colour and WARP's certified-versus-check colour and confidence use this rule. Each channel is
// measured separately. Callers keep every other channel exact. A non-finite difference never passes.
internal static class TemporalSoftwareCompileDivergenceRule
{
    internal const float AbsoluteBound = 0.0625f, DifferingShareBound = 0.0025f;

    internal static double ShareOf(long differing, long compared) => (double)differing / compared;

    internal static bool Allows(float worst, long differing, long compared)
        => compared > 0 && worst <= AbsoluteBound && ShareOf(differing, compared) <= DifferingShareBound;
}

// Only hardware NVIDIA D3D11 creates this oracle. D's paired entries are diagnostic only. Entry identity's production
// against D comparisons assert only that colour and confidence keep D's non-finite classes.
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

    // Entry identity's comparison against D: colour and confidence keep D's non-finite classes, everything else is
    // printed only.
    internal TemporalHistoryDifference Report(int frame, string what, TemporalHistorySample a, TemporalHistorySample b)
    {
        TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(a.Colour, a.State, b.Colour, b.State);
        output.WriteLine($"  frame {frame}: {what} (finite differences diagnostic only): {difference}, "
            + $"R8 equal {a.Visibility.AsSpan().SequenceEqual(b.Visibility)}, "
            + $"depth bits equal {MemoryMarshal.AsBytes(a.Depth.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Depth.AsSpan()))}");
        Assert.True(TemporalNvidiaComparisonRule.ClassesMatch(difference),
            $"frame {frame}: {what}: non-finite classes differ: {difference}");
        return difference;
    }

    // The A/B comparison: exact stored locks, R8 and depth, colour and confidence in D's non-finite classes, their
    // finite differences printed only.
    internal TemporalHistoryDifference Compare(int frame, string what, TemporalHistorySample a,
        TemporalHistorySample b)
    {
        TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(a.Colour, a.State, b.Colour, b.State);
        output.WriteLine($"  frame {frame}: {what}: {difference}");
        TemporalHistorySample.Exact(frame, what + " R8", a.Visibility, b.Visibility);
        TemporalHistorySample.Exact(frame, what + " depth", a.Depth, b.Depth);
        Assert.True(TemporalNvidiaComparisonRule.Allows(difference),
            $"frame {frame}: {what}: stored locks or non-finite classes differ: {difference}");
        return difference;
    }

    internal void Complete() => output.WriteLine("  NVIDIA Direct3D 11: exact stored locks, R8 and depth against D, "
        + "and colour and confidence in D's non-finite classes. Finite colour and confidence differences against D are "
        + "reported only.");
}
