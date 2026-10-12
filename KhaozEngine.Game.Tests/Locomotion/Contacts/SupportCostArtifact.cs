using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Physics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.Locomotion.Contacts;

internal sealed record SupportCostSample(string Operation, string Scene, bool GeometryPassed, string Outcome,
    double Height, double HeightError, Vector3 AchievedMotion, int Substeps, SupportQueryCounts Counts,
    long ColdBytes, long WarmBytes, double ColdMicroseconds, double WarmMicroseconds,
    long ScratchHighWaterBytes, int StackBytes)
{
    public QueryCounts Queries { get; init; }
    public QueryCounts ColdQueries { get; init; }
    public SupportQueryCounts ColdCounts { get; init; }
    [JsonIgnore]
    public SupportCostRow? Input { get; init; }
    public bool Comparable { get; init; } = true;
    public bool Dense { get; init; }
    public bool Refusal { get; init; }
    public double? CandidateLegacyRatio { get; init; }
    public double? WholeTickDeltaMicroseconds { get; init; }
    public bool? GatePassed { get; init; }
    public string OracleOutcome { get; init; } = "";
    public double OracleHeight { get; init; }
    public bool GeometryMatchesOracle { get; init; }
    public bool KnownIncorrect => !GeometryMatchesOracle;
    public SupportRouteCounts Routes { get; init; }
    public SupportFindObservation[] SupportReads { get; init; } = [];
    public SupportFindObservation[] ColdSupportReads { get; init; } = [];
    public string InputDetails { get; init; } = "";
    public string[] Notes { get; init; } = [];
    public bool ColdGeometryPassed { get; init; }
    public string ColdOutcome { get; init; } = "";
    public double ColdHeight { get; init; }
    public double ColdHeightError { get; init; }
    public Vector3 ColdAchievedMotion { get; init; }
    public MoveState? EndState { get; init; }
    public MoveState? ColdEndState { get; init; }
    public bool WarmAllocationFinding => Operation == "Find" && WarmBytes > 0;
    public string RefusalKind => !Refusal ? "None" : Counts.CapacityExceeded > 0 ? "CapacityExceeded"
        : Counts.Ambiguous > 0 ? "Ambiguous" : Counts.Unsupported > 0 ? "Unsupported" : Operation == "Find" ? "ProposalOrCertification" : "Environment";
}

internal static class SupportCostVerdict
{
    internal static bool Evaluate(in SupportCostSample candidate, in SupportCostSample legacy, bool dense,
        double hostScale = 1, long explicitLifetimeBytes = 0)
    {
        if (!(hostScale > 0) || !double.IsFinite(hostScale)) throw new ArgumentOutOfRangeException(nameof(hostScale));
        if (!candidate.GeometryPassed || candidate.Refusal || !double.IsFinite(candidate.WarmMicroseconds) ||
            candidate.WarmMicroseconds < 0 || candidate.WarmBytes < 0) return false;
        if (candidate.Operation == "Find")
            return candidate.WarmBytes == 0 && candidate.WarmMicroseconds <= (dense ? 500 : 50) * hostScale;
        if (candidate.Operation.StartsWith("GroundSubstep", StringComparison.Ordinal))
            return candidate.WarmBytes == 0 && candidate.WarmMicroseconds <= (dense ? 1000 : 200) * hostScale;
        if (candidate.Operation is "DryTick" or "ExplicitTick")
        {
            long allocationLimit = candidate.Operation == "DryTick" ? 208 : explicitLifetimeBytes;
            if (allocationLimit <= 0) return false;
            if (candidate.AchievedMotion.X == 0 && candidate.AchievedMotion.Z == 0 || candidate.Substeps > 3 || candidate.Counts.ModeSegments > 1 || !candidate.Comparable ||
                candidate.WarmBytes > allocationLimit || candidate.WarmMicroseconds > (dense ? 4000 : 1000) * hostScale)
                return false;
            return dense || legacy.GeometryPassed && double.IsFinite(legacy.WarmMicroseconds) &&
                legacy.WarmMicroseconds >= 0 && candidate.WarmMicroseconds <= legacy.WarmMicroseconds +
                Math.Max(hostScale * 100, 0.25 * legacy.WarmMicroseconds);
        }
        return false;
    }
}

internal static class SupportCostArtifact
{
    internal static void Write(string path, IReadOnlyList<SupportCostSample> samples, string baseSha,
        string candidateSha, string command)
    {
        if (samples.Count == 0) throw new ArgumentException("A cost artifact needs samples.", nameof(samples));
        var scenes = new Dictionary<string, SupportCostRow?>();
        foreach (SupportCostSample sample in samples)
        {
            if (scenes.TryGetValue(sample.Scene, out SupportCostRow? input) && input != sample.Input)
                throw new ArgumentException("A scene must have one immutable input across its samples.", nameof(samples));
            scenes[sample.Scene] = sample.Input;
        }
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        JsonObject root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new();
        if (root["pendingFinalization"] is not null) throw new InvalidOperationException("The prior invocation has no process exit.");
        string section = root["baseline"] is null ? "baseline" : "changed";
        if (section == "changed" && root[section] is not null) section += "-" + candidateSha;
        if (root.Any(entry => entry.Key.StartsWith("changed", StringComparison.Ordinal) &&
            entry.Value?["CandidateSha"]?.GetValue<string>() == candidateSha))
            throw new InvalidOperationException("This candidate algorithm is already recorded.");
        if (root[section] is not null) throw new InvalidOperationException("This cost section is already recorded.");
        var lifetime = ContactLifetimeAllocationTests.Inventory();
        string runId = Guid.NewGuid().ToString("N");
        var compared = samples.Select(sample =>
        {
            if (sample.Operation is not ("Find" or "DryTick" or "ExplicitTick" or "GroundTickBorrowed") &&
                !sample.Operation.StartsWith("GroundSubstep", StringComparison.Ordinal)) return sample;
            string legacyOperation = sample.Operation == "Find" ? "LegacyDownPass" : "LegacyTick";
            SupportCostSample? legacy = samples.FirstOrDefault(s => s.Scene == sample.Scene && s.Operation == legacyOperation);
            return legacy is null ? sample : sample with
            {
                CandidateLegacyRatio = sample.Operation is "Find" or "DryTick" or "ExplicitTick" && legacy.WarmMicroseconds > 0 ? sample.WarmMicroseconds / legacy.WarmMicroseconds : null,
                WholeTickDeltaMicroseconds = sample.Operation is "DryTick" or "ExplicitTick" ? sample.WarmMicroseconds - legacy.WarmMicroseconds : null,
                GatePassed = SupportCostVerdict.Evaluate(sample, legacy, sample.Dense,
                    explicitLifetimeBytes: lifetime.Movement + lifetime.Physics + lifetime.Pin),
            };
        }).ToArray();
        root[section] = JsonSerializer.SerializeToNode(new
        {
            RunId = runId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            BaseSha = baseSha,
            CandidateSha = candidateSha,
            MeasuredBeforeHarnessCommit = section == "baseline",
            Command = command,
            Hardware = Hardware(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Compilation = "Release, tiering disabled, first use may include optimized JIT, no warm-up loop",
            ColdJit = "Only first use of a method on this process can include JIT. ColdBytes measures managed allocation, not JIT code memory.",
            ColdScratch = "Scene construction excluded. Each new scene has its own backend scratch. FootSupport thread scratch is shared across rows.",
            Scratch = "High-water retained managed array payload reachable from support scratch. Excludes object headers and backend native buffers.",
            HostScale = 1,
            Backend = "Bepu, selected query view, empty static exclusions, one authenticated lease per operation",
            Tuning = SupportGateCostScenes.Tuning,
            ExplicitScope = "Physics frame at the origin, bounds (-1500, -30, -30) to (1500, 30, 30), MaxRise 20 and MaxDrop 20. Controller inputs stay within the released 512 m local radius.",
            Counters = "IntervalFallbacks counts unresolved interval signs. ExactFallbacks counts dyadic predicate entries only. JoinPairs includes broadphase-pruned pairs. Proposals counts attempted sweeps even without a contact.",
            Stack = "4352 is the released FootSupport certificate span bound. It is not a measured total call stack or a new extremum-kernel allowance.",
            LifetimeInventory = new
            {
                MovementQueryLeaseAndReleaseBindingBytes = lifetime.Movement,
                PhysicsReadLeaseBytes = lifetime.Physics,
                ProducerPinBytes = lifetime.Pin,
                AcquiredExplicitTickBytes = lifetime.Movement + lifetime.Physics + lifetime.Pin,
                Measurement = "Actual acquisition, producer pin and fresh movement-token constructors on this runtime, outside sample windows. Borrowed support remains zero allocation."
            },
            Acquisition = "Lease rows isolate physics acquisition and disposal. Tick rows include all acquisition and disposal. Explicit rows use the real MovementEnvironmentContext with the test producer, whose witness and pin allocations remain visible.",
            Observers = "Opt-in counters and preallocated Find/substep callbacks are enabled inside samples. Oracle checks, callback installation, scratch inspection, delegate construction and serialization are outside sample windows.",
            AllocationIsolation = "Each warm operation runs inside its own checked 4 MiB no-GC region. Region setup and disposal are outside the sample window. Fresh gameplay allocations remain counted. Cold allocation is recorded without isolation because first-use scratch can exceed the region allowance. See dotnet/runtime#134724.",
            Geometry = "Every captured Find is checked against the independent installed-geometry oracle after both sample windows. Correct None and capacity refusals cannot pass a support cost gate. Tick geometry requires matching support reads and a finite advancing state.",
            ProcessExitStatus = (int?)null,
            Scenes = scenes,
            Samples = compared,
        }, options);
        root["schemaVersion"] = 2;
        root["pendingFinalization"] = JsonSerializer.SerializeToNode(new { Section = section, RunId = runId });
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(options));
        File.Move(temporary, path, true);
    }

    static string Hardware()
    {
        if (!OperatingSystem.IsMacOS()) return RuntimeInformation.OSDescription;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string")
        { RedirectStandardOutput = true, UseShellExecute = false });
        string hardware = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return hardware;
    }
}
