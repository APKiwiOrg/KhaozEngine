using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfControlFacts(int Id, string Name, int Dispatches, int SerialEdges, long ReadBytes,
    long WriteBytes, long Operations, string InstructionClass, int Draws, string WorkSha256, int WarmupFrames = 64, int MeasuredFrames = 128)
{
    internal uint GroupsX => Id is 7 or 8 or 9 or 10 ? 1024u : 1u;
    internal uint GroupsY => Id == 7 ? 128u : Id == 8 ? 32u : 1u;
    internal uint LocalX => Id is 7 or 8 ? 256u : Id is 9 or 10 ? 64u : 1u;
}

internal static class RendererPerfControls
{
    internal const uint Seed = 0x12340000;
    internal const int ArithmeticInvocations = 65536;
    internal const int Width = 1920, Height = 1080;
    static readonly float[] ArithmeticSeedData = Enumerable.Range(0, ArithmeticInvocations)
        .Select(invocation => .25f + (invocation % 13) / 64f).ToArray();
    internal static ReadOnlySpan<float> ArithmeticSeeds => ArithmeticSeedData;
    internal static readonly ImmutableArray<GpuPixelFormat> MrtFormats = [GpuPixelFormat.R16G16B16A16Float,
        GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float, GpuPixelFormat.R16G16Float];
    static readonly string[] Names = ["empty-frame", "empty-main-mrt", "empty-depth-only", "observable-dispatch",
        "dependent-dispatch-36", "independent-dispatch-36", "storage-128-mib", "storage-32-mib",
        "fp32-independent-fma", "dependent-divide-root", "fullscreen-write", "fullscreen-blend-depth-8"];
    internal static RendererPerfControlFacts Definition(int id) => Definition(id, id is 9 or 10 ? ArithmeticSeeds : []);
    internal static RendererPerfControlFacts Definition(int id, ReadOnlySpan<float> arithmeticSeeds)
    {
        if (id < 1 || id > 12) throw new ArgumentOutOfRangeException(nameof(id));
        if (id is 9 or 10 && arithmeticSeeds.Length != ArithmeticInvocations)
            throw new ArgumentException("The work identity requires every uploaded arithmetic seed.", nameof(arithmeticSeeds));
        long storage = id == 7 ? 128L * 1024 * 1024 : id == 8 ? 32L * 1024 * 1024 : 0;
        int dispatches = id is 5 or 6 ? 36 : id is >= 4 and <= 10 ? 1 : 0;
        long reads = storage != 0 ? storage : id is >= 4 and <= 6 ? dispatches * 4
            : id is 9 or 10 ? ArithmeticInvocations * 4 : id == 12 ? (long)Width * Height * 8 * 12 : 0;
        long writes = storage != 0 ? storage : id is >= 4 and <= 6 ? dispatches * 4
            : id == 9 ? ArithmeticInvocations * 32 : id == 10 ? ArithmeticInvocations * 4
            : id == 11 ? (long)Width * Height * 20 : id == 12 ? (long)Width * Height * 8 * 8 : 0;
        long ops = id == 9 ? (long)ArithmeticInvocations * 64 * 8 * 2
            : id == 10 ? (long)ArithmeticInvocations * 64 * 2 : 0;
        var facts = new RendererPerfControlFacts(id, Names[id - 1], dispatches, id == 5 ? 35 : 0,
            reads, writes, ops, id == 9 ? "fp32-fma" : id == 10 ? "dependent-divide-root" : "none",
            id == 12 ? 8 : id == 11 ? 1 : 0, "");
        var work = new
        {
            Version = "1",
            Facts = facts,
            facts.GroupsX,
            facts.GroupsY,
            GroupsZ = 1,
            facts.LocalX,
            LocalY = 1,
            LocalZ = 1,
            Width,
            Height,
            ShadowSize = 2048,
            Samples = 1,
            MrtFormats,
            Depth = GpuPixelFormat.D32FloatS8UInt,
            MainClear = new[] { .02f, .03f, .06f, 0f },
            MotionClear = 65504f,
            DepthValidation = "cold texelFetch into RGBA8, outside all timed spans",
            Seed,
            ArithmeticInvocations,
            Shaders = RendererPerfControlShaders.Digest,
            Ordering = "native resource dependency ordering, no added seam barriers",
            Validation = "all output elements after final completion, no timed readback",
            CommonHost = "production initial RGBA8 clear and End tail included in outer duration",
            Graphics = "control 11 overrides four MRT outputs, control 12 alpha blends color and preserves normal/depth-color/motion, depth less-equal without writes",
        };
        string hash = id is 9 or 10 ? RendererPerfRecipe.Digest(new
        {
            Work = work,
            ArithmeticSeedSha256 = RendererPerfRecipe.Hash(MemoryMarshal.AsBytes(arithmeticSeeds))
        }) : RendererPerfRecipe.Digest(work);
        return facts with { WorkSha256 = hash };
    }
    internal static IRendererPerfNativeWork Create(IGpuDevice device, RendererPerfControl control)
    {
        var facts = Definition(control.Id);
        if (control.Version != "1" || control.WorkSha256 != facts.WorkSha256
            || !((control.WarmupFrames == 64 && control.MeasuredFrames == 128) || (control.WarmupFrames == 32 && control.MeasuredFrames == 64)))
            throw new RendererPerfProtocolException("Identity", "The control work digest, version or finite counts differ from the authorized preset.");
        return new RendererPerfControlWorkload(device, control, facts);
    }
    internal static string ValidateWords(int id, ReadOnlySpan<uint> words)
    {
        int count = id is 5 or 6 ? 36 : id == 4 ? 1 : id == 7 ? 128 * 1024 * 1024 / 4 : id == 8 ? 32 * 1024 * 1024 / 4 : 0;
        if (count == 0 || words.Length != count) throw new InvalidOperationException("The control output length differs from retained work.");
        for (int i = 0; i < words.Length; i++)
        {
            uint expected = id is 7 or 8 ? Seed ^ 0xa5a5a5a5 : Seed + (uint)(id == 5 ? i + 1 : 1);
            if (words[i] != expected) throw new InvalidOperationException($"Control {id} output {i} lost its observable write or dependency.");
        }
        return RendererPerfRecipe.Hash(MemoryMarshal.AsBytes(words));
    }
    internal static ImmutableDictionary<string, RendererPerfMetric> AchievedRates(RendererPerfControlReport report)
    {
        var facts = Definition(report.Control.Id);
        if (report.Control.Version != "1" || report.Control.WorkSha256 != facts.WorkSha256
            || !((report.Control.WarmupFrames == 64 && report.Control.MeasuredFrames == 128)
                || (report.Control.WarmupFrames == 32 && report.Control.MeasuredFrames == 64))
            || report.Warmup.Length != report.Control.WarmupFrames || report.Measured.Length != report.Control.MeasuredFrames
            || report.StateSha256.Length != 64 || !report.StateSha256.All(Uri.IsHexDigit)
            || !report.Metrics.TryGetValue("control.outputValidated", out var validation)
            || validation.Status != RendererPerfMetricStatus.Available || validation.Value != 1)
            throw new InvalidOperationException("Achieved rates require complete, identity-matched, checked control outputs.");
        foreach (var sample in report.Warmup.Concat(report.Measured))
            if (!(sample.GpuFrame.Status == RendererPerfMetricStatus.Available && sample.GpuFrame.Unit == "ns"
                && sample.GpuFrame.Value is { } duration && double.IsFinite(duration) && duration > 0 && sample.Errors.IsEmpty)
                && !RendererPerfControlTiming.AcceptsZero(sample, report.Control.Id, report.Warmup[0].GpuPeriodNs))
                throw new InvalidOperationException("A control sample lacks valid native GPU duration evidence.");
        string source = $"retained logical work / reported whole-frame GPU mean, no overhead subtraction, work {facts.WorkSha256}, output {report.StateSha256}";
        RendererPerfMetric NoRate(string unit) => new(null, unit, source, RendererPerfMetricStatus.Unsupported,
            "A clear-only control supplies no matched rate work or complete numerical GPU duration. Below-resolution samples are never zero references.");
        if (report.Measured.Any(sample => sample.GpuFrame.Status == RendererPerfMetricStatus.BelowResolution))
        {
            var distribution = RendererPerfAnalysis.Distribution([.. report.Measured.Select(sample => sample.GpuFrame)], report.Control.MeasuredFrames);
            return ImmutableDictionary<string, RendererPerfMetric>.Empty
                .Add("frame.meanNs", distribution.Mean)
                .Add("bandwidth.bytesPerSecond", NoRate("bytes/s"))
                .Add("operations.perSecond", NoRate("operations/s"))
                .Add("fill.pixelsPerSecond", NoRate("pixels/s"));
        }
        double ns = report.Measured.Average(sample => sample.GpuFrame.Value!.Value);
        RendererPerfMetric Rate(double work, string unit, string kind) => work == 0
            ? new(null, unit, source, RendererPerfMetricStatus.Unsupported, "This control supplies no matched " + kind + " work.")
            : RendererPerfDriver.Value(work / ns * 1e9, unit, kind + ", " + source);
        return ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("frame.meanNs", RendererPerfDriver.Value(ns, "ns", source))
            .Add("bandwidth.bytesPerSecond", Rate(facts.ReadBytes + facts.WriteBytes, "bytes/s", "logical reads plus writes"))
            .Add("operations.perSecond", Rate(facts.Operations, "operations/s", facts.InstructionClass))
            .Add("fill.pixelsPerSecond", Rate((long)Width * Height * facts.Draws, "pixels/s", "matched fullscreen layers"));
    }

    internal static float ArithmeticSeed(int invocation) => ArithmeticSeedData[invocation];
    internal static float[] ExpectedArithmetic(int id, int invocation)
    {
        if (id is not (9 or 10)) throw new ArgumentOutOfRangeException(nameof(id));
        float seed = ArithmeticSeed(invocation);
        if (id == 10)
        {
            float x = seed;
            for (int step = 0; step < 64; step++) x = MathF.Sqrt(x + 1f) / (x + 1.25f);
            return [x];
        }
        float[] values = new float[8];
        for (int lane = 0; lane < 8; lane++)
        {
            float x = seed + lane * .125f;
            for (int step = 0; step < 64; step++)
                x = MathF.FusedMultiplyAdd(x, lane < 4 ? .999f : .998f, lane < 4 ? .001f : .002f);
            values[lane] = x;
        }
        return values;
    }
    internal static string ValidateArithmetic(int id, ReadOnlySpan<float> values, int invocations)
    {
        if (id is not (9 or 10) || invocations <= 0 || invocations > ArithmeticInvocations
            || values.Length != invocations * (id == 9 ? 8 : 1)) throw new InvalidOperationException("Incomplete arithmetic control output.");
        for (int i = 0; i < invocations; i++)
        {
            float[] expected = ExpectedArithmetic(id, i);
            for (int lane = 0; lane < expected.Length; lane++)
            {
                float actual = values[i * expected.Length + lane];
                if (!float.IsFinite(actual) || MathF.Abs(actual - expected[lane]) > .0001f)
                    throw new InvalidOperationException($"Control {id} arithmetic output {i}/{lane} differs from retained work.");
            }
        }
        return RendererPerfRecipe.Hash(MemoryMarshal.AsBytes(values));
    }
}
