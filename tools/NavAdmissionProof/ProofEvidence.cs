using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed class PairEvidence
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int? ProcessExitStatus { get; init; }
    public string Fixture { get; init; } = ProofFixture.Identity;
    public string Order { get; init; } = "A false, B true";
    public int ChildTimeoutSeconds { get; init; } = 180;
    public int ProfilesPerSuccessfulPair { get; init; } = 2;
    public int Degree { get; init; } = 1;
    public int Warmups { get; init; }
    public int Retries { get; init; }
    public int Load { get; init; }
    public string Measurement { get; init; } = "One BuildProfile per fresh process. Setup, capture, graph checks and serialization outside timing. No sampling or production instrumentation.";
    public string Limitations { get; init; } = "One ordered pair cannot establish a small statistical win or private-world speedup. No operation counts or native allocation measurement. CPU includes first-use JIT and measurement boundary overhead. Process counters include runtime threads. GC and contention snapshots bracket the profile, not exclusive stage attribution.";
    public string DecisionRule { get; init; } = "Complete canonical byte and graph parity plus a clear coherent reduction in process and main-thread CPU supports reconsideration for this public fixture. Small, mixed, worse or failed results keep the candidate parked. No automatic repeat.";
    public ProofEnvironment? Environment { get; set; }
    public List<ChildAttempt> Samples { get; init; } = [];
    public bool CompleteKenbBytesEqual { get; set; }
    public bool GraphCountsEqual { get; set; }
    public bool SameBinarySet { get; set; }
    public double? CpuRatioBOverA { get; set; }
    public double? MainThreadCpuRatioBOverA { get; set; }
    public double? WallRatioBOverA { get; set; }
    public string Outcome { get; set; } = "Pending";
    public string? Failure { get; set; }
}

internal sealed class ChildAttempt
{
    public required string Label { get; init; }
    public required bool UseStableLegacyReadScope { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public double ElapsedSeconds { get; set; }
    public int? ProcessId { get; set; }
    public int? ExitStatus { get; set; }
    public bool TimedOut { get; set; }
    public string? Failure { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public ChildResult? Result { get; set; }
}

internal sealed record ProofEnvironment(string Runtime, string RuntimeVersion, string OperatingSystem,
    string Architecture, int ProcessorCount, string Sdk, string Configuration, string Commit,
    string MovementVersion, string BinarySetSha256, string TieredCompilation, string? ComPlusTieredCompilation,
    string? ReadyToRun, string? ComPlusReadyToRun, string? ServerGc, string? GcHeapCount,
    string? EnableDiagnostics, string CompilationEnvironment, string RunnerEnvironment, string RunnerOs, string RunnerArch,
    string Workflow, string Event, string RunId, string RunAttempt);

internal sealed record ProfileMetrics(double WallSeconds, double ProcessCpuSeconds, double MainThreadCpuSeconds,
    long ManagedProcessAllocatedBytes, long ManagedThreadAllocatedBytes, int Gen0Collections,
    int Gen1Collections, int Gen2Collections, long MonitorLockContentions);

internal sealed record GraphCounts(int Layers, int AcceptedNodes, int RejectedLayerCells, int DirectedEdges,
    int CandidateLinks, int AcceptedLinks, bool ClearEdgeAccepted, bool CentralObstacleRejected);

internal sealed record ChildResult(string RunId, string Label, bool UseStableLegacyReadScope, string Fixture,
    ProofEnvironment Environment, ProfileMetrics Metrics, GraphCounts Graph, int KenbLength, string KenbSha256,
    bool CanonicalRoundTrip, bool ScopeReleasedAfterBuild);

internal static class ProofJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal const string Section = "navAdmissionPair";

    internal static void WriteEnvelope(string path, PairEvidence pair)
    {
        // The shared workflow finalizes ProcessExitStatus only after dotnet test has exited.
        Write(path, new
        {
            schemaVersion = 1,
            navAdmissionPair = pair,
            pendingFinalization = new { Section, pair.RunId },
        });
    }

    internal static PairEvidence ReadEnvelope(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        PairEvidence pair = root.GetProperty(Section).Deserialize<PairEvidence>(Options)
            ?? throw new InvalidDataException("Missing paired evidence.");
        JsonElement pending = root.GetProperty("pendingFinalization");
        if (pending.GetProperty("Section").GetString() != Section ||
            pending.GetProperty("RunId").GetString() != pair.RunId || pair.ProcessExitStatus is not null)
            throw new InvalidDataException("Malformed pending finalization envelope.");
        return pair;
    }

    internal static void Write<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal static ChildResult ReadChild(string path) => JsonSerializer.Deserialize<ChildResult>(
        File.ReadAllText(path), Options) ?? throw new InvalidDataException("Missing child result.");
}
