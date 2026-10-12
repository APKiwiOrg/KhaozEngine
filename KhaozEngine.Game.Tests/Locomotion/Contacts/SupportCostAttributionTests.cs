using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportCostAttributionTests(ITestOutputHelper output) : IDisposable
{
    [Fact]
    public void FindReusesTheCertifiedColumnAcrossAllRawFaces()
    {
        SupportQueryDiagnostics.Enable(true);
        var recorder = new PhaseRecorder(false);
        ContactAllocationDiagnostics.SetObserver(recorder.Observe);
        SupportCostSample sample = new SupportCostSampler(false)
        { BeforeWarm = recorder.Reset, IsolateAllocations = true }.Measure(Row("flat box"), "Find")[0];
        Assert.True(sample.GeometryMatchesOracle);
        Assert.Equal(6, sample.Counts.Polygons);
        Assert.Equal(1, recorder.Rows().Single(r => r.Phase == "SupportColumnSetup").Calls);
        Assert.Equal(0, sample.WarmBytes);
    }

    [Fact]
    public void FindPhaseBracketsCoverTheCompleteOperation()
    {
        SupportQueryDiagnostics.Enable(true);
        var recorder = new PhaseRecorder(false);
        ContactAllocationDiagnostics.SetObserver(recorder.Observe);
        SupportCostSample sample = new SupportCostSampler(false)
        { BeforeWarm = recorder.Reset, IsolateAllocations = true }.Measure(Row("flat box"), "Find")[0];
        Assert.True(sample.GeometryMatchesOracle);
        Assert.Equal(0, recorder.Depth);
        Assert.Equal(sample.WarmBytes, recorder.Bytes.Sum());
        Assert.Contains(recorder.Rows(), r => r.Phase == "SupportCapture" && r.Calls > 0);
        Assert.Contains(recorder.Rows(), r => r.Phase == "SupportExtrema" && r.Calls > 0);
        Assert.Contains(recorder.Rows(), r => r.Phase == "SupportJoins" && r.Calls > 0);
    }

    [Fact]
    public void OneShotPhaseAttribution()
    {
        string? path = Environment.GetEnvironmentVariable("KHAOZ_SUPPORT_ATTRIBUTION");
        if (string.IsNullOrEmpty(path)) return;
        Assert.True(Path.IsPathFullyQualified(path), "Attribution needs an absolute output path before measuring.");
        Assert.False(File.Exists(path), "A completed attribution is immutable.");
        Assert.Equal("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
        SupportQueryDiagnostics.Enable(true);
        var measurements = new List<object>();
        foreach (string name in new[] { "flat box", "flat mesh", "fan 96", "cluster 32 compound",
            "cluster 32 split", "cluster 256 mesh" })
            foreach (string operation in name is "cluster 32 split" or "cluster 256 mesh"
                ? new[] { "Find" } : new[] { "Find", "DryTick" })
            {
                var recorder = new PhaseRecorder(true);
                ContactAllocationDiagnostics.SetObserver(recorder.Observe);
                SupportCostSample sample = new SupportCostSampler(false)
                { BeforeWarm = recorder.Reset, IsolateAllocations = true }.Measure(Row(name), operation)[0];
                var phases = recorder.Rows();
                measurements.Add(new
                {
                    Scene = name,
                    Operation = operation,
                    sample.WarmBytes,
                    sample.GeometryMatchesOracle,
                    sample.Counts,
                    Phases = phases
                });
                output.WriteLine($"{name} {operation}. {sample.WarmBytes} B, {phases.Sum(p => p.Microseconds):F2} us.");
                foreach (var phase in phases) output.WriteLine($"{phase}");
                Assert.Equal(0, recorder.Depth);
                Assert.Equal(sample.WarmBytes, recorder.Bytes.Sum());
                Assert.True(sample.GeometryMatchesOracle);
                Assert.Equal(operation == "Find" ? 0 : 64, sample.WarmBytes);
            }
        File.WriteAllText(path, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    }

    static SupportCostRow Row(string name) => SupportGateCostScenes.Rows()
        .Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);

    public void Dispose()
    {
        ContactAllocationDiagnostics.SetObserver(null);
        SupportQueryDiagnostics.Enable(false);
        SupportQueryDiagnostics.Reset();
    }

    internal sealed record PhaseRow(string Phase, int Calls, long Bytes, double Microseconds, int Collections);

    internal sealed class PhaseRecorder(bool timing)
    {
        readonly ContactAllocationPhase[] stack = new ContactAllocationPhase[64];
        readonly int[] calls = new int[Enum.GetValues<ContactAllocationPhase>().Length];
        readonly long[] ticks = new long[Enum.GetValues<ContactAllocationPhase>().Length];
        readonly int[] collections = new int[Enum.GetValues<ContactAllocationPhase>().Length];
        internal long[] Bytes { get; } = new long[Enum.GetValues<ContactAllocationPhase>().Length];
        internal int Depth { get; private set; }
        bool active;
        long previousBytes, previousTicks;
        int previousCollections;

        internal void Reset()
        {
            Array.Clear(calls);
            Array.Clear(ticks);
            Array.Clear(collections);
            Array.Clear(Bytes);
            active = true;
            Depth = 0;
        }

        internal void Observe(ContactAllocationPhase phase, bool entering)
        {
            if (!active) return;
            long nowBytes = GC.GetAllocatedBytesForCurrentThread();
            long nowTicks = timing ? Stopwatch.GetTimestamp() : 0;
            int nowCollections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            if (Depth > 0)
            {
                int current = (int)stack[Depth - 1];
                Bytes[current] += nowBytes - previousBytes;
                ticks[current] += nowTicks - previousTicks;
                collections[current] += nowCollections - previousCollections;
            }
            previousBytes = nowBytes;
            previousTicks = nowTicks;
            previousCollections = nowCollections;
            if (entering)
            {
                stack[Depth++] = phase;
                calls[(int)phase]++;
            }
            else
            {
                if (Depth == 0 || stack[Depth - 1] != phase) throw new InvalidOperationException("Unbalanced support phase.");
                Depth--;
            }
        }

        internal PhaseRow[] Rows() => Enum.GetValues<ContactAllocationPhase>()
            .Where(p => calls[(int)p] > 0).Select(p => new PhaseRow(p.ToString(), calls[(int)p],
                Bytes[(int)p], ticks[(int)p] * 1_000_000d / Stopwatch.Frequency, collections[(int)p])).ToArray();
    }
}
