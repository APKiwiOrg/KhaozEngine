using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Windowing.ShadowStageProbeFrameTests;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class ShadowInputRecorderTests(ITestOutputHelper log)
{
    [Fact]
    public void AddedValueStorageFitsTheFixedBufferBudget()
    {
        int before = Unsafe.SizeOf<LegacySample>();
        int after = Unsafe.SizeOf<FrameDiagnosticsSample>();
        int added = after - before;
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageProbeInputs>());
        Assert.InRange(added, 1, 2048);
        Assert.True((long)added * 4096 <= 8388608);
        log.WriteLine($"Sample bytes before={before}, after={after}, added={added}, added at 4096={(long)added * 4096}. Input value={Unsafe.SizeOf<ShadowStageProbeInputs>()}, nullable={Unsafe.SizeOf<ShadowStageProbeInputs?>()}.");
    }

    // The pre-input schema's nested sample layout, for the actual added array stride rather than a JSON estimate.
    readonly record struct LegacySnapshot(ShadowStageProbeRequest Request, ShadowStageProbeStatus Status,
        ShadowStageProbeConfiguration Configuration, ShadowStageProbeCounts? Counters, ShadowStageProbeTicks? Timings);
    readonly record struct LegacyCallback(ShadowStageProbeRequest Request, bool Completed, int ProducerCount, LegacySnapshot? Snapshot);
    readonly record struct LegacyRow(ShadowStageProbeRequest Request, ShadowStageProbeStatus Status, LegacyCallback Callback);
    readonly record struct LegacySample(long StartTimestamp, long EndTimestamp, double MonotonicIntervalSeconds,
        double LoopDeltaSeconds, FrameDiagnosticsWindowState CompletedFrameWindow, FrameDiagnosticsWindowState BoundaryWindow,
        GpuDeviceCounters Counters, LegacyRow ShadowStage, RecordingWaitProbeRow RecordingWait);

    [Theory]
    [InlineData("valid", ShadowStageProbeStatus.Valid)]
    [InlineData("changed", ShadowStageProbeStatus.Valid)]
    [InlineData("missing", ShadowStageProbeStatus.InvalidInputs)]
    [InlineData("stale", ShadowStageProbeStatus.InvalidInputs)]
    [InlineData("duplicate", ShadowStageProbeStatus.InvalidInputs)]
    [InlineData("incomplete", ShadowStageProbeStatus.IncompleteCallback)]
    public async Task CompletedCallbackOwnsExactlyOneInputObservation(string fault, ShadowStageProbeStatus status)
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/input-diagnostics-01/implementation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "result.json");
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 2, ShadowStageProbeMode.Counts);
            var window = new FrameDiagnosticsWindowState(true, false, false, 1591, 1027, 0, true);
            File.WriteAllText(path + ".start", "");
            recorder.Observe(.016, 0, window, default);
            recorder.Observe(.016, 1000, window, default);
            var frame = new Frame();
            for (int i = 1; i <= 2; i++)
            {
                var request = recorder.NextShadowStageRequest;
                frame.BeginShadowStageCallback(request);
                ShadowStageProbeInputs? inputs = Inputs(request);
                if (fault == "missing") inputs = null;
                if (fault == "stale") inputs = inputs!.Value with { Request = request with { Sequence = 99 } };
                if (fault == "duplicate") inputs = inputs!.Value with { PrefixCount = 2 };
                if (fault == "changed" && i == 2)
                    inputs = inputs!.Value with
                    {
                        PrefixEffectTimeSeconds = 18,
                        View = inputs.Value.View with { View = Matrix4x4.CreateTranslation(1, 2, 3) }
                    };
                var payload = ShadowStageProbeFrameTests.Payload(request) with { Inputs = inputs };
                frame.PublishShadowStageSnapshot(payload);
                if (fault != "incomplete") frame.CompleteShadowStageCallback();
                recorder.Observe(.016, 1000 + i * 16, window, default, frame.CompletedShadowStageCallback);
            }
            await recorder.Publication;
            Assert.Null(recorder.Failure);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(5, json.RootElement.GetProperty("schemaVersion").GetInt32());
            var rows = json.RootElement.GetProperty("samples");
            for (int i = 0; i < 2; i++)
            {
                var row = rows[i].GetProperty("shadowStage");
                Assert.Equal((int)status, row.GetProperty("status").GetInt32());
                if (fault == "valid")
                    Assert.Equal(i + 1, row.GetProperty("callback").GetProperty("snapshot").GetProperty("inputs").GetProperty("sceneFrameIndex").GetInt64());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    static JsonDocument Export(ShadowStageProbeInputs inputs) => JsonDocument.Parse(JsonSerializer.Serialize(inputs,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void InputWordsPreserveSignedZeroAndEveryMatrixElement(int negativeZeroIndex)
    {
        float minusZero = BitConverter.UInt32BitsToSingle(0x80000000);
        float[] values = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        values[negativeZeroIndex] = minusZero;
        var m = MemoryMarshal.Read<Matrix4x4>(MemoryMarshal.AsBytes(values.AsSpan()));
        var input = Inputs(ShadowStageProbeFrameTests.Request()) with
        {
            View = new(m, m, m, m, new(minusZero, 1, 2), 1591, 1027, new(minusZero, 0)),
            Light = new(new(minusZero, 1, 2), new(3, 4, 5), new(6, 7, 8)),
            Fits = new(1, 64, new(minusZero, 0, 0, 0), m, default, default, default),
        };
        using var json = Export(input);
        var root = json.RootElement;
        uint[] expected = [0, 0x3f800000, 0x40000000, 0x40400000, 0x40800000, 0x40a00000, 0x40c00000,
            0x40e00000, 0x41000000, 0x41100000, 0x41200000, 0x41300000, 0x41400000, 0x41500000, 0x41600000, 0x41700000];
        expected[negativeZeroIndex] = 0x80000000;
        foreach (string name in new[] { "viewWords", "projectionWords", "viewProjectionWords", "absoluteViewProjectionWords" })
            Assert.Equal(expected, root.GetProperty("view").GetProperty(name).EnumerateArray().Select(x => x.GetUInt32()));
        Assert.Equal(expected, root.GetProperty("fits").GetProperty("absoluteFitWords")[0].EnumerateArray().Select(x => x.GetUInt32()));
        Assert.Equal(0x80000000u, root.GetProperty("light").GetProperty("rawWords")[0].GetUInt32());
        Assert.Equal(0x80000000u, root.GetProperty("view").GetProperty("renderOriginWords")[0].GetUInt32());
        Assert.Equal(0x80000000u, root.GetProperty("view").GetProperty("jitterPixelsWords")[0].GetUInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryLightComponentPreservesNegativeZero(int component)
    {
        float[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        values[component] = BitConverter.UInt32BitsToSingle(0x80000000);
        var input = Inputs(Request()) with
        { Light = new(new(values[0], values[1], values[2]), new(values[3], values[4], values[5]), new(values[6], values[7], values[8])) };
        using var json = Export(input);
        string field = new[] { "rawWords", "normalizedWords", "selectedWords" }[component / 3];
        Assert.Equal(0x80000000u, json.RootElement.GetProperty("light").GetProperty(field)[component % 3].GetUInt32());
    }

    [Fact]
    public void NonFiniteWordsRemainRawAndUnusable()
    {
        var input = Inputs(ShadowStageProbeFrameTests.Request()) with
        { PrefixEffectTimeSeconds = BitConverter.UInt32BitsToSingle(0x7fc01234) };
        using var json = Export(input);
        Assert.Equal(0x7fc01234u, json.RootElement.GetProperty("prefixEffectTimeWords")[0].GetUInt32());
        // Offline auditing classifies usability. The raw producer must not discard NaN payload bits.
    }

    [Fact]
    public void UnusedCascadeSlotsAreCanonicalAndUnavailable()
    {
        using var json = Export(Inputs(ShadowStageProbeFrameTests.Request()));
        var fits = json.RootElement.GetProperty("fits");
        Assert.Equal(0, fits.GetProperty("count").GetInt32());
        Assert.Equal(4, fits.GetProperty("absoluteFitWords").GetArrayLength());
        foreach (var matrix in fits.GetProperty("absoluteFitWords").EnumerateArray())
        {
            Assert.Equal(16, matrix.GetArrayLength());
            Assert.All(matrix.EnumerateArray(), word => Assert.Equal(0u, word.GetUInt32()));
        }
    }

    [Theory]
    [InlineData(ShadowStageProbeMode.Counts)]
    [InlineData(ShadowStageProbeMode.Timings)]
    public void SamplingInputsAllocatesZeroAfterWarmup(ShadowStageProbeMode mode)
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ShadowStageProbeInputs>());
        int size = Unsafe.SizeOf<ShadowStageProbeInputs?>();
        Assert.InRange(size, 1, 2048);
        Assert.True((long)size * 4096 <= 8388608);
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/input-diagnostics-01/implementation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "result.json");
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 4096, mode);
            var window = new FrameDiagnosticsWindowState(true, false, false, 1591, 1027, 0, true);
            File.WriteAllText(path + ".start", "");
            recorder.Observe(.016, 0, window, default); recorder.Observe(.016, 1000, window, default);
            var frame = new Frame();
            long tick = 1000;
            void Sample()
            {
                var request = recorder.NextShadowStageRequest;
                frame.BeginShadowStageCallback(request);
                var payload = ShadowStageProbeFrameTests.Payload(request) with { Inputs = Inputs(request) };
                frame.PublishShadowStageSnapshot(payload); frame.CompleteShadowStageCallback();
                recorder.Observe(.001, ++tick, window, default, frame.CompletedShadowStageCallback);
            }
            for (int i = 0; i < 32; i++) Sample();
            AllocationMeasurement.NoPerCallAllocation("copied input boundary", Sample);
        }
        finally { Directory.Delete(directory, true); }
    }
}
