using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A monotonic tick source and its frequency. Real sessions use <see cref="Stopwatch"/>.</summary>
internal sealed record CoverageCostClock(Func<long> Now, long Frequency)
{
    internal static CoverageCostClock Stopwatch { get; } = new(System.Diagnostics.Stopwatch.GetTimestamp,
        System.Diagnostics.Stopwatch.Frequency);
}

/// <summary>A session declined to measure, for example an adapter outside the manifest's device requirement.</summary>
internal sealed class CoverageCostRefusedException(string message) : Exception(message);

/// <summary>
/// One frame's observations. Ticks are clock ticks summed over every actor in the frame. Nested spans keep their own
/// totals and the driver derives the exclusive remainder, so nothing is counted twice. prepare.total contains skinning
/// and geometry evaluation, geometry evaluation contains geometry certification, encode.total contains resolution, the
/// skinned preparation and the motion commit, the skinned preparation contains surface append, ordinary skinning,
/// motion recording and skinned upload, and resolution contains render certification. Bytes are allocated managed
/// bytes on the measuring thread.
/// </summary>
internal struct CoverageCostFrameSample
{
    internal long PrepareTicks, SkinningTicks, GeometryTicks, GeometryCertificationTicks, SubmissionTicks, ReserveTicks,
        EncodeTicks, ResolutionTicks, CertificationTicks, SkinnedPreparationTicks, SurfaceTicks, OrdinarySkinningTicks,
        SkinnedUploadTicks, MotionCommitTicks, SubmitWaitTicks, FrameTicks;
    internal long PrepareBytes, ReserveBytes, ResolutionBytes, SurfaceBytes, FrameBytes;
    internal int ActorFrames, Accepted, Declined, Covered, Fallback, Skinned, Evaluated, GeometryCertified, Certified, Passes,
        MainVisible, OutlineGroups, OrdinaryCpuSkinnedVertices;
    internal RenderFrameStats Stats;
    internal Scene3DPassTimingsMs PassTimings;
}

/// <summary>An opened measurement target. Opening may allocate fixtures and devices. Frame fills one sample.</summary>
internal interface ICoverageCostSession : IDisposable
{
    /// <summary>True for fakes and injected clocks. A synthetic report can never prove a budget.</summary>
    bool Synthetic { get; }
    /// <summary>False for the recording device, which has no GPU submission to observe.</summary>
    bool NativeDevice { get; }
    /// <summary>True when the skinned preparation, its children and the motion commit are bracketed, so their cost
    /// is a measurement.</summary>
    bool ObservesSkinnedPreparation { get; }
    CoverageCostSessionInfo Info { get; }
    void Frame(bool measured, ref CoverageCostFrameSample sample);
    IReadOnlyList<CoverageCostMetric> ReadCapacities();
    IReadOnlyList<KeyValuePair<string, int>> ReadRefusals();
    /// <summary>Certified context and recorded pass evidence accumulated over the measured frames.</summary>
    IReadOnlyList<CoverageCostMetric> ReadEvidence();
}

internal delegate ICoverageCostSession CoverageCostSessionFactory(CoverageCostManifest manifest, CoverageCostClock clock);

/// <summary>Entry points for the separately selected cost project. Ordinary tests never call them.</summary>
internal static class CoverageCostHarness
{
    static CoverageCostSessionFactory Factory(CoverageCostManifest manifest) =>
        manifest.Recording ? CoverageCostRecordingSession.Open : CoverageCostNativeSession.Open;

    /// <summary>One diagnostic case on the manifest's exact native backend, with no fallback, or on the recording
    /// device. A single case never carries an added-cost verdict.</summary>
    internal static CoverageCostReport Measure(CoverageCostManifest manifest, Action<CoverageCostReport>? publish = null) =>
        CoverageCostDriver.Run(manifest, Factory(manifest), CoverageCostClock.Stopwatch, publish: publish);

    /// <summary>One authorized on and off unit, both conditions in order in this process under one unit deadline.</summary>
    internal static CoverageCostPairReport MeasurePair(CoverageCostPairManifest pair, Action<CoverageCostReport>? publish = null) =>
        CoverageCostDriver.RunPair(pair, Factory(pair.Conditions[0]), CoverageCostClock.Stopwatch, publish: publish);

    /// <summary>Measures one authorized file of either schema. Each finished condition's report JSON goes to
    /// <paramref name="publish"/> as soon as it exists, so a later hang or job kill cannot lose it. The returned JSON
    /// is the final unit report.</summary>
    internal static (string Json, bool Completed, string Reason) MeasureUnit(byte[] utf8, string expectedName, Action<string> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        void Sink(CoverageCostReport report) => publish(report.ToJson());
        string schema;
        using (var document = JsonDocument.Parse(utf8))
            schema = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("schema", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        if (schema == CoverageCostPairManifest.Schema)
        {
            var pair = CoverageCostPairManifest.Parse(utf8);
            if (pair.Unit != expectedName) throw new InvalidDataException($"Pair unit {pair.Unit} does not match its file {expectedName}.");
            var report = MeasurePair(pair, Sink);
            return (report.ToJson(), report.Status == CoverageCostRunStatus.Completed, report.Reason);
        }
        var manifest = CoverageCostManifest.Parse(utf8);
        if (manifest.Case != expectedName) throw new InvalidDataException($"Case {manifest.Case} does not match its file {expectedName}.");
        var single = Measure(manifest, Sink);
        return (single.ToJson(), single.Status == CoverageCostRunStatus.Completed, single.Reason);
    }
}

/// <summary>Identity of the process that produced a report. Values that cannot be read stay "unavailable".</summary>
internal static class CoverageCostEnvironment
{
    internal const string Unavailable = "unavailable";
    // One value per process, so two reports carrying it came from one run.
    static readonly string RunId = Guid.NewGuid().ToString("N");

    internal static IReadOnlyList<KeyValuePair<string, string>> Read() =>
    [
        new("runId", RunId),
        new("processId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("commit", Env("GITHUB_SHA") ?? Commit()),
        new("runtime", RuntimeInformation.FrameworkDescription),
        new("os", RuntimeInformation.OSDescription),
        new("architecture", RuntimeInformation.ProcessArchitecture.ToString()),
        new("cpu", Cpu()),
        new("processorCount", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("runner", string.Join(' ', new[] { Env("RUNNER_NAME"), Env("RUNNER_OS"), Env("ImageOS"), Env("ImageVersion") }
            .Where(v => v is not null)) is { Length: > 0 } runner ? runner : Unavailable),
        new("gc", $"server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode}"),
        new("tieredCompilation", Env("DOTNET_TieredCompilation") ?? "default"),
        new("tieredPgo", Env("DOTNET_TieredPGO") ?? "default"),
        new("stopwatchFrequency", System.Diagnostics.Stopwatch.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("stopwatchHighResolution", System.Diagnostics.Stopwatch.IsHighResolution.ToString()),
    ];

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    static string Commit()
    {
        string? version = typeof(CoverageCostEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = version?.IndexOf('+') ?? -1;
        return plus >= 0 ? version![(plus + 1)..] : Unavailable;
    }

    static string Cpu()
    {
        if (Env("PROCESSOR_IDENTIFIER") is { } windows) return windows;
        try
        {
            if (OperatingSystem.IsLinux())
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal)) return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Unavailable;
    }
}
