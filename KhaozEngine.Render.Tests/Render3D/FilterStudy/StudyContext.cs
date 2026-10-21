#if COVERAGE_FILTER_STUDY
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.FilterStudy;

internal sealed class StudyRange
{
    public double Minimum { get; private set; } = double.MaxValue;
    public double Maximum { get; private set; }
    public int MinimumExponent { get; private set; } = int.MaxValue;
    public int MaximumExponent { get; private set; } = int.MinValue;
    public long Zeros { get; private set; }
    public long Nonfinite { get; private set; }
    internal void Add(double value)
    {
        if (!double.IsFinite(value)) { Nonfinite++; return; }
        value = Math.Abs(value);
        if (value == 0) { Zeros++; return; }
        Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value);
        MinimumExponent = Math.Min(MinimumExponent, Math.ILogB(value)); MaximumExponent = Math.Max(MaximumExponent, Math.ILogB(value));
    }
}

internal sealed class StudyMetric
{
    public long Calls { get; set; }
    public long Zeros { get; set; }
    public long[] Signs { get; } = new long[3];
    public long[] Rounds { get; } = new long[3];
    public long[] Required { get; } = new long[3];
    public StudyRange Inputs { get; } = new();
    public StudyRange Results { get; } = new();
}

internal sealed class StudyQueue
{
    public long Units { get; set; }
    public long[] Ambiguous { get; } = new long[3];
}

internal static class StudyContext
{
    internal static readonly StudyArithmetic Arithmetic = new();
    internal static readonly Dictionary<string, StudyMetric> Metrics = new();
    internal static readonly Dictionary<string, StudyQueue> Queues = new();
    static readonly Dictionary<CoverageSupportSurface, StudySupport> supports = new();
    static readonly bool[] faceMiss = new bool[3], pairMiss = new bool[3];
    static bool faceOpen, pairOpen;
    internal static string Corpus = "", Stage = "", Context = "";
    internal static int Weight = 1;
    internal static long CachedEdges;
    internal static StudySupport Support(CoverageSupportSurface source)
    {
        if (!supports.TryGetValue(source, out var value))
        {
            string saved = Stage; Stage = "ColdSupport";
            supports.Add(source, value = new(source)); Stage = saved;
        }
        return value;
    }

    internal static IDisposable Enter(string context)
    {
        string saved = Context; Context += "/" + context;
        return new Scope(() => Context = saved);
    }
    sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }

    internal static void BeginFace()
    {
        EndFace(); faceOpen = true; Array.Clear(faceMiss); pairOpen = false;
    }
    internal static void EndFace()
    {
        EndPair();
        if (faceOpen) Queue("Face", faceMiss);
        faceOpen = false;
    }
    internal static void BeginPair() { EndPair(); pairOpen = true; Array.Clear(pairMiss); }
    static void EndPair() { if (pairOpen) Queue("Pair", pairMiss); pairOpen = false; }
    static void Queue(string kind, bool[] miss)
    {
        string key = Corpus + "/" + kind;
        if (!Queues.TryGetValue(key, out var q)) Queues.Add(key, q = new());
        q.Units += Weight;
        for (int i = 0; i < 3; i++) if (miss[i]) q.Ambiguous[i] += Weight;
    }

    static StudyMetric Metric(string operation)
    {
        string key = Corpus + "/" + Stage + "/" + operation;
        if (!Metrics.TryGetValue(key, out var value)) Metrics.Add(key, value = new());
        value.Calls += Weight; return value;
    }

    internal static void Ordinary(string op, float a, float b)
    {
        var m = Metric(op); m.Inputs.Add(a); m.Inputs.Add(b);
    }

    internal static void Predicate(string kind, string caller, Vector3[] points, CoverageReferenceScalar exact)
    {
        if (caller == "Height" && Context.Contains("/TryHeight", StringComparison.Ordinal)) { CachedEdges += Weight; return; }
        var terms = kind == "Edge" ? StudyFilters.Edge(points[0], points[1], points[2]) : StudyFilters.Dot(points[0], points[1], points[2], points[3]);
        bool signOnly = caller == "OverlayInside";
        var metric = Metric(kind + ":" + caller);
        foreach (var p in points) { metric.Inputs.Add(p.X); metric.Inputs.Add(p.Y); }
        metric.Results.Add(exact.Value);
        if (exact.Sign == 0) metric.Zeros += Weight;
        for (int mode = 0; mode < 3; mode++)
        {
            var ball = StudyFilters.Evaluate(terms, mode);
            bool sign = ball.Sign(out int s), round = ball.Round(out float r);
            if (sign) Assert.Equal(exact.Sign, s);
            if (round) Assert.Equal(Bits(exact.Value), Bits(r));
            if (sign) metric.Signs[mode] += Weight;
            if (round) metric.Rounds[mode] += Weight;
            bool hit = sign && (signOnly ? Finite(ball) : round);
            Hit(metric, mode, hit);
        }
    }

    static bool Finite(StudyBall b) => Math.Abs(b.Value) + b.Error < (double)float.MaxValue;
    static void Hit(StudyMetric metric, int mode, bool hit)
    {
        if (hit) metric.Required[mode] += Weight;
        if (!hit && faceOpen && Stage is "CorrectFaces" or "Overlay" or "Finish") faceMiss[mode] = true;
        if (!hit && pairOpen && Stage == "Overlay") pairMiss[mode] = true;
    }

    internal static void Scalar(string kind, string caller, float a, float b, float result)
    {
        var metric = Metric(kind + ":" + caller);
        metric.Inputs.Add(a); if (kind == "Div") metric.Inputs.Add(b);
        metric.Results.Add(result);
        for (int mode = 0; mode < 3; mode++)
        {
            bool hit = StudyFilters.Scalar(a, b, result, kind == "Sqrt", mode);
            if (hit) metric.Rounds[mode] += Weight;
            Hit(metric, mode, hit);
        }
    }

    internal static void Crossing(Vector3 s, Vector3 t, Vector3 a, Vector3 b, float x, float y)
    {
        var ca = StudyFilters.Edge(s, t, a); var cb = StudyFilters.Edge(s, t, b);
        var ia = BigInteger.Abs(StudyFilters.Exact(ca)); var ib = BigInteger.Abs(StudyFilters.Exact(cb));
        int exponent = checked((int)BigInteger.Max(ia, ib).GetBitLength()) - 299;
        var scale = Metric("B30:scale"); var sides = Metric("B30:scaledSides");
        sides.Calls += Weight;
        foreach (var p in new[] { s, t, a, b }) { sides.Inputs.Add(p.X); sides.Inputs.Add(p.Y); }
        scale.Inputs.Add(exponent); sides.Results.Add(x); sides.Results.Add(y);
        for (int mode = 0; mode < 3; mode++)
        {
            var ba = StudyFilters.Evaluate(ca, mode); var bb = StudyFilters.Evaluate(cb, mode);
            double low = Math.Max(Math.Max(0, Math.Abs(ba.Value) - ba.Error), Math.Max(0, Math.Abs(bb.Value) - bb.Error));
            double high = Math.Max(Math.Abs(ba.Value) + ba.Error, Math.Abs(bb.Value) + bb.Error);
            int guess = Math.ILogB(Math.Max(Math.Abs(ba.Value), Math.Abs(bb.Value)));
            bool power = double.IsFinite(high) && low >= Math.ScaleB(1, guess) && high < Math.ScaleB(1, guess + 1);
            if (power) Assert.Equal(exponent, guess);
            Hit(scale, mode, power);
            foreach (var (ball, expected) in new[] { (ba, x), (bb, y) })
            {
                var normalized = new StudyBall(Math.ScaleB(ball.Value, -exponent), Math.ScaleB(ball.Error, -exponent));
                bool hit = power && normalized.Round(out _);
                if (hit) { normalized.Round(out float value); Assert.Equal(Bits(expected), Bits(value)); sides.Rounds[mode] += Weight; }
                Hit(sides, mode, hit);
            }
        }
    }

    static uint Bits(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value), mag = bits & 0x7fffffffu;
        return mag is > 0 and <= 0x00800000u ? bits & 0x80000000u : bits;
    }
}
#endif
