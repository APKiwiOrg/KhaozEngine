using System;
using System.Collections.Generic;
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

        internal bool Within(int steps) => !NonFinite && (Count == 0 || (steps > 0 && MaxSteps <= steps));
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
    // but Channel.Count retains their bit difference so a zero envelope still requires exact bytes.
    internal static int HalfSteps(float a, float b) => Math.Abs(Ordinal(a) - Ordinal(b));
    static int Ordinal(float value)
    {
        short bits = BitConverter.HalfToInt16Bits((Half)value);
        return bits < 0 ? -(bits & 0x7fff) : bits;
    }

    public override string ToString() => "colour RGBA [" + string.Join(" | ", Colour.Select(c => c.ToString()))
        + $"], confidence [{Confidence}], any lock differs {Locks.Count > 0} ({Locks.Count})";
}

internal sealed class TemporalHistoryEnvelope
{
    readonly int[] _colour = new int[4];
    int _confidence;

    internal void Include(TemporalHistoryDifference d)
    {
        Assert.False(d.Confidence.NonFinite || d.Colour.Any(c => c.NonFinite),
            "D's entries have a non-finite arithmetic difference");
        for (int c = 0; c < 4; c++) _colour[c] = Math.Max(_colour[c], d.Colour[c].MaxSteps);
        _confidence = Math.Max(_confidence, d.Confidence.MaxSteps);
    }

    internal bool Allows(TemporalHistoryDifference d) => d.Locks.Count == 0 && d.Confidence.Within(_confidence)
        && Enumerable.Range(0, 4).All(c => d.Colour[c].Within(_colour[c]));

    public override string ToString() => $"colour RGBA [{string.Join(", ", _colour)}], confidence {_confidence} half steps";
}

// Only the hardware NVIDIA D3D11 branch creates this oracle. It measures D's envelope over the complete frame set
// before checking any arithmetic difference, so an early frame is judged against the same envelope as a late one.
internal sealed class TemporalNvidiaHistoryOracle(ITestOutputHelper output)
{
    readonly TemporalHistoryEnvelope _envelope = new();
    readonly List<(int Frame, string What, TemporalHistoryDifference Difference)> _comparisons = [];

    internal void ObserveD(int frame, TemporalHistorySample fused, TemporalHistorySample split)
    {
        TemporalHistoryDifference d = Compare(frame, "D fused vs D split", fused, split, record: false);
        _envelope.Include(d);
        output.WriteLine($"  frame {frame}: D envelope so far: {_envelope}");
    }

    internal TemporalHistoryDifference Compare(int frame, string what, TemporalHistorySample a,
        TemporalHistorySample b, bool record = true)
    {
        TemporalHistoryDifference difference = TemporalHistoryDifference.Measure(a.Colour, a.State, b.Colour, b.State);
        output.WriteLine($"  frame {frame}: {what}: {difference}");
        Assert.True(difference.Locks.Count == 0, $"frame {frame}: {what}: stored locks differ: {difference}");
        TemporalHistorySample.Exact(frame, what + " R8", a.Visibility, b.Visibility);
        TemporalHistorySample.Exact(frame, what + " depth", a.Depth, b.Depth);
        if (record) _comparisons.Add((frame, what, difference));
        return difference;
    }

    internal void Complete()
    {
        output.WriteLine($"  NVIDIA Direct3D 11 measured D envelope: {_envelope}");
        foreach ((int frame, string what, TemporalHistoryDifference difference) in _comparisons)
            Assert.True(_envelope.Allows(difference),
                $"frame {frame}: {what} exceeds D's measured envelope ({_envelope}): {difference}");
    }
}
