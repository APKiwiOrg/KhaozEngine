using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

// Fictional native identities and fixed vectors test arithmetic only. No factory or clock is opened.
internal static class CoveragePerfReportVectors
{
    internal static CoveragePerfReport Create(string preset = "C1")
    {
        var run = new CoveragePerfFakeRun(preset);
        var manifest = CoveragePerfManifest.Parse(run.Json);
        var identity = run.Identity with
        {
            Execution = new()
            {
                HarnessAssembly = "fixed-vector-assembly",
                Runtime = "fixed-vector-runtime",
                GcSettings = "ordinary GC",
                TieringSettings = "default",
                ClockFrequency = 1000000000,
                HostCpu = "fixed-vector-cpu",
                HostIdentity = "fixed-vector-host",
                RunnerImage = "fixed-vector-image",
                LegacyBaselineSha = CoveragePerfLegacyHost.BaselineSha,
                LegacySequenceFingerprint = CoveragePerfLegacyHost.SequenceFingerprint,
                BatchCompletionPolicy = "three-frame batches, one drain",
                TimestampCompletionPolicy = "one nonblocking poll after drain",
                ProductionCompletionPolicy = "shared submission retention, no measured waits",
            }
        };
        var blocks = manifest.Blocks.Select(b =>
        {
            var info = new CoveragePerfSessionInfo
            {
                Synthetic = false,
                NativeDevice = true,
                Backend = manifest.Backend,
                Adapter = preset.StartsWith('G') ? "NVIDIA T4" : "llvmpipe (LLVM)",
                Vendor = preset.StartsWith('G') ? "NVIDIA" : "Mesa",
                Device = preset.StartsWith('G') ? "T4" : "lavapipe",
                Driver = "fixed-vector-driver",
                SoftwareAdapter = !preset.StartsWith('G'),
                TimestampSupported = preset.StartsWith('G'),
                TimestampPeriodNanoseconds = preset.StartsWith('G') ? .001 : null,
                D3DRecordingMode = "deferred",
                OrdinarySkinningRoute = "gpu",
                CoveredSurfaceRoute = "gpu-built",
                FixtureFingerprint = "topology-and-equipment",
                ProfileFingerprint = "full-profile",
                CompletionPolicy = "three-frame batches, one drain, one poll",
                Environment = [new("settings", "fixed-profile"), new("topology", "fixed-topology")],
            };
            var warm = Enumerable.Range(0, b.WarmupFrames).Select(i => Sample(b, i)).ToImmutableArray();
            var measured = Enumerable.Range(b.WarmupFrames, b.MeasuredFrames).Select(i => Sample(b, i)).ToImmutableArray();
            return new CoveragePerfBlockResult(b, CoveragePerfRunStatus.Completed, "", info, warm, measured) { RunIdentity = identity };
        }).ToImmutableArray();
        return new(CoveragePerfRunStatus.Completed, "fixed vectors", manifest, manifest.Sha256, identity, blocks)
        { ReturnedByRun = true, FinalUnitPublished = true };
    }

    static CoveragePerfFrameSample Sample(CoveragePerfBlock block, int index) => new()
    {
        FrameIndex = index,
        PoseId = index % 2,
        CpuFramePreparationTicks = 10000000,
        FrameTicks = 10000000,
        CpuChildren = [new("diagnostic", 999000000)],
        Actors = block.Actors,
        MainVisibleActors = block.Actors,
        Passes = 15,
        PointShadowFaces = 6,
        Submissions = 1,
        SubmittedCompletionFences = block.TimerLayout != CoveragePerfTimerLayout.CpuOnly || block.Condition == CoveragePerfCondition.SharedOn ? 1 : 0,
        SubmissionParticipants = block.Condition == CoveragePerfCondition.DirectOff ? 0 : block.TimerLayout == CoveragePerfTimerLayout.CpuOnly ? 1 : 2,
        CompletionConsumers = block.Condition == CoveragePerfCondition.DirectOff ? 0 :
            (block.TimerLayout == CoveragePerfTimerLayout.CpuOnly ? 0 : 1) + (block.Condition == CoveragePerfCondition.SharedOn ? 1 : 0),
        StageComplete = true,
        DeviceComplete = true,
        PoseFingerprint = "pose-" + index % 2,
        PassFingerprint = "schedule-" + index % 2,
        CompletedFrameTail = block.TimerLayout != CoveragePerfTimerLayout.CpuOnly,
        Gpu = block.TimerLayout == CoveragePerfTimerLayout.CpuOnly ? null : new(block.TimerLayout,
            block.TimerLayout == CoveragePerfTimerLayout.Total ? [0, 10000000000] : [0, 100, 200, 300, 10000000000],
            .001, true, false, ""),
    };

    internal static CoveragePerfReport Cpu(CoveragePerfReport report, int block, params double[] milliseconds) =>
        ChangeBlock(report, block, b => b with
        {
            Samples = b.Samples.Select((s, i) => s with
            { CpuFramePreparationTicks = checked((long)Math.Round(milliseconds[i % milliseconds.Length] * 1000000)) }).ToImmutableArray()
        });

    internal static CoveragePerfReport Gpu(CoveragePerfReport report, int block, params double[] milliseconds) =>
        ChangeBlock(report, block, b => b with
        {
            Samples = b.Samples.Select((s, i) => s with
            { Gpu = s.Gpu! with { Ticks = [0, checked((ulong)Math.Round(milliseconds[i % milliseconds.Length] * 1000000000))] } }).ToImmutableArray()
        });

    internal static CoveragePerfReport ChangeBlock(CoveragePerfReport report, int block, Func<CoveragePerfBlockResult, CoveragePerfBlockResult> change)
        => report with { Blocks = report.Blocks.SetItem(block, change(report.Blocks[block])) };
    internal static CoveragePerfReport ChangeSample(CoveragePerfReport report, int block, Func<CoveragePerfFrameSample, CoveragePerfFrameSample> change)
        => ChangeBlock(report, block, b => b with { Samples = b.Samples.SetItem(0, change(b.Samples[0])) });
}
