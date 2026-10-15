using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Research;

/// <summary>Caller-owned inputs for one opt-in capture measurement.</summary>
public sealed record NavCaptureResearchInput(IDisposable Owner, GroundMoveContext Context,
    PhysicsNavBakeOptions Options, NavAreaClassifier Classifier);

/// <summary>One stage's process-wide and capture-thread counters.</summary>
public sealed record NavCaptureStage(string Name, double WallSeconds, double CpuSeconds,
    long ProcessAllocatedBytes, long ThreadAllocatedBytes, int Gen0, int Gen1, int Gen2);

/// <summary>Test-only research entry point. It never runs a warm-up or builds navigation profiles.</summary>
public static class NavCaptureResearch
{
    public static IReadOnlyList<NavCaptureStage> Run(Func<NavCaptureResearchInput> setup, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var stages = new List<NavCaptureStage>();
        using var process = Process.GetCurrentProcess();
        _ = process.TotalProcessorTime;
        CaptureResearchEvents.Log.Stage("session", "start");
        NavCaptureResearchInput input = Measure("setup", setup, process, stages);
        using (input.Owner)
        using (input.Context)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "options.json"), JsonSerializer.Serialize(input.Options));
            using PhysicsNavBake capture = Measure("capture",
                () => PhysicsNavBake.Capture(input.Context, input.Options, input.Classifier), process, stages);
            SaveMetrics();
            Measure("snapshot", () =>
            {
                WriteColumns(capture, Path.Combine(outputDirectory, "columns.bin"));
                return true;
            }, process, stages);
            SaveMetrics();
        }
        CaptureResearchEvents.Log.Stage("session", "stop");
        return stages.AsReadOnly();

        void SaveMetrics() => File.WriteAllText(Path.Combine(outputDirectory, "metrics.json"),
            JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId,
                Framework = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Engine = typeof(PhysicsNavBake).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion,
                Stages = stages,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T Measure<T>(string name, Func<T> action, Process process, List<NavCaptureStage> stages)
    {
        CaptureResearchEvents.Log.Stage(name, "start");
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long threadAllocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        T result = action();
        double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
        long threadDelta = GC.GetAllocatedBytesForCurrentThread() - threadAllocated;
        long allocationDelta = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        int delta0 = GC.CollectionCount(0) - gen0, delta1 = GC.CollectionCount(1) - gen1,
            delta2 = GC.CollectionCount(2) - gen2;
        process.Refresh();
        double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
        CaptureResearchEvents.Log.Stage(name, "stop");
        stages.Add(new NavCaptureStage(name, wall, cpuSeconds, allocationDelta, threadDelta, delta0, delta1, delta2));
        return result;
    }

    // Diagnostic parity bytes, independent of profile derivation and the shipped KENB format.
    // BinaryWriter writes little-endian values. Columns and water retain the engine's canonical order.
    private static void WriteColumns(PhysicsNavBake capture, string path)
    {
        PhysicsNavColumns columns = capture.Columns;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("PCAP1");
            writer.Write(columns.Width);
            writer.Write(columns.Height);
            writer.Write(capture.Origin.X);
            writer.Write(capture.Origin.Y);
            writer.Write(capture.Origin.Z);
            for (int z = 0; z < columns.Height; z++)
                for (int x = 0; x < columns.Width; x++)
                {
                    ReadOnlySpan<PhysicsNavSurface> surfaces = columns.GetColumn(x, z);
                    writer.Write(surfaces.Length);
                    foreach (PhysicsNavSurface surface in surfaces)
                    {
                        writer.Write(surface.Height);
                        writer.Write(surface.Headroom);
                        writer.Write(surface.Areas);
                    }
                }
            writer.Write(columns.Water.Length);
            foreach (PhysicsNavWater water in columns.Water)
            {
                writer.Write(water.Cell);
                writer.Write(water.SurfaceY);
                writer.Write(water.Areas);
            }
        }
        using var stream = File.OpenRead(path);
        File.WriteAllText(Path.ChangeExtension(path, "sha256"), Convert.ToHexString(SHA256.HashData(stream)) + "\n");
    }
}

[EventSource(Name = "KhaozEngine-NavCaptureResearch")]
internal sealed class CaptureResearchEvents : EventSource
{
    internal static readonly CaptureResearchEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Stage(string name, string edge) => WriteEvent(1, name, edge);
}
