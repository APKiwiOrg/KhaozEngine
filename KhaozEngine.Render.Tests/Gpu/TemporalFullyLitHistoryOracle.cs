using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Measure the whole walk before asserting. A colour mismatch must not hide later state or later frames.
internal sealed class TemporalFullyLitHistoryOracle(ITestOutputHelper output, bool softwareVulkan, bool warp = false, bool nvidia = false)
{
    internal sealed record Snapshot(int Width, int InternalWidth, byte[] Colour, byte[] State, byte[] Visibility, byte[] Depth)
    {
        internal static Snapshot Read(TemporalFixture fx)
        {
            TemporalHistory h = fx.Scene.TemporalHistory;
            byte[] ReadTexture(IGpuTexture texture) => TemporalVisibilityFullyLitGpuTests.Bytes(fx.Device, texture);
            return new((int)h.Color(h.WriteIndex).Width, (int)h.PreviousDepth(h.WriteIndex).Width, ReadTexture(h.Color(h.WriteIndex)),
                ReadTexture(h.Confidence(h.WriteIndex)),
                ReadTexture(fx.Scene.TemporalResolveRendererForTests!.VisibilityHistory.Texture(h.WriteIndex)),
                ReadTexture(h.PreviousDepth(h.WriteIndex)));
        }

        internal TemporalHistorySample Sample() => new(Decode(Colour, half: true), Decode(State, half: true),
            Visibility, Decode(Depth, half: false));

        static float[] Decode(byte[] bytes, bool half)
        {
            int stride = half ? 2 : 4;
            var values = new float[bytes.Length / stride];
            for (int i = 0; i < values.Length; i++)
                values[i] = half ? (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i * stride))
                    : BitConverter.ToSingle(bytes, i * stride);
            return values;
        }
    }

    internal sealed class Difference
    {
        internal long Count, Compared;
        internal float Worst;
        internal int HalfSteps;
        internal readonly List<string> Examples = [];

        internal void Add(float a, float b, uint bitsA, uint bitsB, string location, bool half)
        {
            Compared++;
            if (bitsA == bitsB) return;
            Count++;
            Worst = float.IsFinite(a) && float.IsFinite(b) ? Math.Max(Worst, Math.Abs(a - b)) : float.PositiveInfinity;
            if (half && float.IsFinite(a) && float.IsFinite(b))
                HalfSteps = Math.Max(HalfSteps, TemporalHistoryDifference.HalfSteps(a, b));
            if (Examples.Count < 3)
                Examples.Add(FormattableString.Invariant($"{location} {a:R} (0x{bitsA:X8}) vs {b:R} (0x{bitsB:X8})"));
        }

        internal void Include(Difference frame)
        {
            Count += frame.Count;
            Compared += frame.Compared;
            Worst = Math.Max(Worst, frame.Worst);
            HalfSteps = Math.Max(HalfSteps, frame.HalfSteps);
            foreach (string example in frame.Examples)
                if (Examples.Count < 3) Examples.Add(example);
        }

        public override string ToString() => FormattableString.Invariant(
            $"{Count}/{Compared} differ, share {(double)Count / Compared:R}, worst {Worst:R}, half steps {HalfSteps}")
            + (Examples.Count == 0 ? "" : ", first " + string.Join(" | ", Examples));
    }

    enum Rule { Exact, SoftwareBound, NvidiaReport }
    readonly Dictionary<(string Pair, string Channel, Rule Rule), Difference> _totals = [];
    readonly TemporalNvidiaHistoryOracle? _nvidia = nvidia ? new(output) : null;

    internal void Report(int frame, string pair, Snapshot a, Snapshot b, bool entryComparison = false)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.InternalWidth, b.InternalWidth);
        Channel("colour", a.Colour, b.Colour, 2, 4);
        Channel("confidence", a.State, b.State, 2, 4, offset: 0);
        Channel("lock", a.State, b.State, 2, 4, offset: 1);
        Channel("mark", a.State, b.State, 2, 4, offset: 1, mark: true);
        Channel("R8", a.Visibility, b.Visibility, 1, 1, width: a.InternalWidth);
        Channel("depth", a.Depth, b.Depth, 4, 1, width: a.InternalWidth);
        if (!entryComparison) _nvidia?.Compare(frame, pair, a.Sample(), b.Sample());

        void Channel(string name, byte[] left, byte[] right, int bytes, int components, int offset = -1,
            bool mark = false, int? width = null)
        {
            Assert.Equal(left.Length, right.Length);
            var difference = new Difference();
            for (int i = offset < 0 ? 0 : offset; i < left.Length / bytes; i += offset < 0 ? 1 : components)
            {
                (float va, uint ba) = Value(left, i * bytes, bytes);
                (float vb, uint bb) = Value(right, i * bytes, bytes);
                if (mark) { va = Mark(va); vb = Mark(vb); ba = (uint)va; bb = (uint)vb; }
                int texel = i / components;
                int rowWidth = width ?? a.Width;
                difference.Add(va, vb, ba, bb, $"f{frame} ({texel % rowWidth},{texel / rowWidth}) c{i % components}",
                    bytes == 2 && !mark);
            }
            output.WriteLine($"history-frame {frame} {pair} {name}: {difference}");
            bool arithmetic = name is "colour" or "confidence";
            Rule rule = !entryComparison && nvidia && arithmetic ? Rule.NvidiaReport
                : !entryComparison && warp && arithmetic
                    || softwareVulkan && entryComparison && name == "colour" ? Rule.SoftwareBound : Rule.Exact;
            var key = (pair, name, rule);
            if (!_totals.TryGetValue(key, out Difference? total)) _totals.Add(key, total = new());
            total.Include(difference);
        }
    }

    internal void AssertComplete()
    {
        var failures = new List<string>();
        foreach (var (key, difference) in _totals)
        {
            string summary = $"{key.Pair} {key.Channel}: {difference}";
            output.WriteLine("history-summary " + summary);
            // NVIDIA's A/B oracle has already checked each certified pair's classes and exact stored channels.
            bool allowed = key.Rule switch
            {
                Rule.NvidiaReport => true,
                Rule.SoftwareBound => TemporalSoftwareCompileDivergenceRule.Allows(difference.Worst,
                    difference.Count, difference.Compared),
                _ => difference.Count == 0,
            };
            if (!allowed) failures.Add(summary);
            if (key.Rule == Rule.SoftwareBound)
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"software compile divergence absolute bound {TemporalSoftwareCompileDivergenceRule.AbsoluteBound}, share bound {TemporalSoftwareCompileDivergenceRule.DifferingShareBound}"));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    static (float Value, uint Bits) Value(byte[] data, int index, int bytes)
    {
        uint bits = bytes == 1 ? data[index] : bytes == 2 ? BitConverter.ToUInt16(data, index) : BitConverter.ToUInt32(data, index);
        return (bytes == 1 ? bits : bytes == 2 ? (float)BitConverter.UInt16BitsToHalf((ushort)bits)
            : BitConverter.UInt32BitsToSingle(bits), bits);
    }

    // The same stored mark classes as temporalStoreLock and the existing entry identity oracle.
    static int Mark(float stored) => stored < -4.5f ? 3 : stored < -2.5f ? 2 : stored < -0.5f ? 1 : 0;
}
