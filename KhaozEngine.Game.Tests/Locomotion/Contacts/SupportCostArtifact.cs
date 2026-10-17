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
using KhaozEngine.Locomotion.Contacts;

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
    public bool? Envelope { get; init; }
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

/// <summary>The SC-2 hosted scale: the median hosted/M2 ratio over comparable legacy rows, with its spread.</summary>
internal readonly record struct HostedScale(double K, int Rows, double LowerQuartile, double UpperQuartile);

/// <summary>A cost row verdict. Envelope rows can never pass by construction and count as neither (SC-3).</summary>
internal enum SupportCostGate { Reject, Pass, Envelope }

internal static class SupportCostVerdict
{
    internal const int MinimumCalibrationRows = 40;
    internal const double MinimumReferenceMicroseconds = 0.5;

    internal static bool TryHostedScale(JsonObject? m2MaxReference, JsonObject? hosted, out HostedScale scale)
    {
        scale = default;
        try
        {
            if (!ValidMetadata(m2MaxReference) || !ValidMetadata(hosted) ||
                m2MaxReference!["Hardware"]!.GetValue<string>() != "Apple M2 Max" ||
                m2MaxReference["Architecture"]!.GetValue<string>() != "Arm64" ||
                m2MaxReference["ProcessExitStatus"]?.GetValue<int>() != 0 ||
                hosted!["ProcessExitStatus"] is { } exit && exit.GetValue<int>() != 0 ||
                !SameSceneInputs(m2MaxReference!["Scenes"], hosted!["Scenes"]) ||
                !SameTuningInputs(m2MaxReference["Tuning"], hosted["Tuning"]))
                return false;
            Dictionary<(string, string), SupportCostSample>? reference = LegacyRows(m2MaxReference), current = LegacyRows(hosted);
            if (reference is null || current is null) return false;
            var ratios = new List<double>();
            foreach (var (key, m2) in reference)
            {
                if (!current.TryGetValue(key, out SupportCostSample? run) || !Succeeded(m2) || !Succeeded(run) ||
                    !(m2.WarmMicroseconds > MinimumReferenceMicroseconds)) continue;
                double ratio = run.WarmMicroseconds / m2.WarmMicroseconds;
                if (ratio > 0 && double.IsFinite(ratio)) ratios.Add(ratio);
            }
            if (ratios.Count < MinimumCalibrationRows) return false;
            ratios.Sort();
            double k = Quantile(ratios, 0.5);
            if (!(k > 0) || !double.IsFinite(k * 4000)) return false;
            scale = new(k, ratios.Count, Quantile(ratios, 0.25), Quantile(ratios, 0.75));
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Linear interpolation at rank (n + 1) p over the sorted ratios, clamped to the ends.</summary>
    static double Quantile(List<double> sorted, double p)
    {
        double rank = Math.Clamp((sorted.Count + 1) * p - 1, 0, sorted.Count - 1);
        int lower = (int)rank;
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    static bool ValidMetadata(JsonObject? section) => section is not null &&
        new[] { "Hardware", "Architecture", "Runtime", "OperatingSystem", "Compilation" }
            .All(key => !string.IsNullOrWhiteSpace(section[key]?.GetValue<string>())) &&
        Enum.TryParse(section["Architecture"]!.GetValue<string>(), out Architecture architecture) && Enum.IsDefined(architecture) &&
        section["Runtime"]!.GetValue<string>().StartsWith(".NET ", StringComparison.Ordinal) &&
        section["TieredCompilation"]?.GetValue<string>() == "0" &&
        section["Compilation"]!.GetValue<string>().StartsWith("Release,", StringComparison.Ordinal);

    static bool SameSceneInputs(JsonNode? reference, JsonNode? hosted)
    {
        if (reference is not JsonObject original || hosted is not JsonObject current ||
            original.Count == 0 || original["flat box"] is not JsonObject || original.Count != current.Count) return false;
        foreach (var scene in original)
        {
            if (scene.Value is not JsonObject || current[scene.Key] is not JsonObject) return false;
            JsonObject a = scene.Value.DeepClone().AsObject(), b = current[scene.Key]!.DeepClone().AsObject();
            // Correctness dispositions change after a repair. The physical input must still match.
            foreach (string key in new[] { "KnownIncomplete", "ExpectedRefusal", "MustPass" })
            {
                a.Remove(key);
                b.Remove(key);
            }
            if (!JsonNode.DeepEquals(a, b)) return false;
        }
        return true;
    }

    static bool SameTuningInputs(JsonNode? reference, JsonNode? hosted)
    {
        if (reference is not JsonObject original || hosted is not JsonObject current) return false;
        JsonObject a = original.DeepClone().AsObject(), b = current.DeepClone().AsObject();
        // Only the fields added after the committed evidence have implicit defaults.
        JsonObject defaults = new()
        {
            [nameof(MoveTuning.Controller)] = (int)MovementControllerKind.Legacy,
            [nameof(MoveTuning.FootRadiusFraction)] = 0.5f,
            [nameof(MoveTuning.Water)] = JsonSerializer.SerializeToNode(default(WaterTraversalPolicy))
        };
        foreach (var field in defaults)
        {
            if (!a.ContainsKey(field.Key)) a.Add(field.Key, field.Value!.DeepClone());
            if (!b.ContainsKey(field.Key)) b.Add(field.Key, field.Value!.DeepClone());
        }
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>Every legacy sample keyed by scene and operation, or null when a key repeats.</summary>
    static Dictionary<(string, string), SupportCostSample>? LegacyRows(JsonObject section)
    {
        if (section["Samples"] is not JsonArray samples) return null;
        var options = new JsonSerializerOptions { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var rows = new Dictionary<(string, string), SupportCostSample>();
        foreach (JsonNode? node in samples)
        {
            if (node is not JsonObject || node["Operation"]?.GetValue<string>() is not { } operation ||
                !operation.StartsWith("Legacy", StringComparison.Ordinal)) continue;
            SupportCostSample sample = node.Deserialize<SupportCostSample>(options)!;
            if (!rows.TryAdd((sample.Scene, operation), sample)) return null;
        }
        return rows;
    }

    static bool Succeeded(SupportCostSample sample) => sample.GeometryPassed && sample.ColdGeometryPassed &&
        sample.GeometryMatchesOracle && !sample.Refusal && sample.WarmMicroseconds > 0 && double.IsFinite(sample.WarmMicroseconds);

    /// <summary>A Find whose outcome and saved oracle outcome are both None, with the oracle in agreement (SC-3).</summary>
    static bool OracleCorrectRefusal(SupportCostSample sample) => sample.Outcome == "None" &&
        sample.OracleOutcome == "None" && sample.GeometryMatchesOracle && !sample.Refusal;

    /// <summary>A stationary substep or tick whose every support read matched its oracle and was steep or
    /// absent (SC-3). A refused read certifies nothing, so it does not count as steep or absent.</summary>
    static bool OracleBlocked(SupportCostSample sample) => sample.AchievedMotion == Vector3.Zero &&
        !sample.Refusal && sample.GeometryMatchesOracle && sample.SupportReads.Length > 0 &&
        sample.SupportReads.All(read => (SupportStatus)read.Status is SupportStatus.None or SupportStatus.Steep);

    internal static bool IsGated(string operation) => operation is "Find" or "DryTick" or "ExplicitTick" ||
        operation.StartsWith("GroundSubstep", StringComparison.Ordinal);

    /// <summary>The legacy row a cost row is compared with: the down pass for a Find, otherwise the legacy tick.</summary>
    internal static SupportCostSample? PairedLegacy(SupportCostSample sample, IEnumerable<SupportCostSample> samples)
    {
        string operation = sample.Operation == "Find" ? "LegacyDownPass" : "LegacyTick";
        return samples.FirstOrDefault(s => s.Scene == sample.Scene && s.Operation == operation);
    }

    /// <summary>The verdict for one recorded row, or null for an operation without a cost gate. A row without
    /// its legacy pair is rejected. <paramref name="hosted"/> adds the hosted precondition of <see cref="Check"/>.</summary>
    internal static SupportCostRowVerdict? JudgeRow(SupportCostSample sample, IReadOnlyList<SupportCostSample> samples,
        double hostScale, long explicitLifetimeBytes, bool hosted)
    {
        if (!IsGated(sample.Operation)) return null;
        SupportCostSample? legacy = PairedLegacy(sample, samples);
        return new(legacy, legacy is null ? SupportCostGate.Reject
            : Evaluate(sample, legacy, sample.Dense, hostScale, explicitLifetimeBytes, hosted));
    }

    /// <summary>Calibrates a finalized hosted section against its reference and judges every gated row offline.
    /// A malformed section is refused like an uncalibrated one.</summary>
    internal static bool TryJudgeHosted(JsonObject? reference, JsonObject hosted, out HostedScale scale,
        out (SupportCostSample Sample, SupportCostRowVerdict Verdict)[] rows)
    {
        rows = [];
        if (!TryHostedScale(reference, hosted, out scale)) return false;
        try
        {
            if (hosted["LifetimeInventory"]?["AcquiredExplicitTickBytes"]?.GetValue<long>() is not long lifetime ||
                hosted["Samples"] is not JsonArray recorded) return Refuse(out scale, out rows);
            var options = new JsonSerializerOptions { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            SupportCostSample[]? samples = recorded.Deserialize<SupportCostSample[]>(options);
            if (samples is null || !samples.All(Judgeable)) return Refuse(out scale, out rows);
            double k = scale.K;
            rows = samples.Select(sample => (Sample: sample, Verdict: JudgeRow(sample, samples, k, lifetime, hosted: true)))
                .Where(row => row.Verdict is not null).Select(row => (row.Sample, row.Verdict!.Value)).ToArray();
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return Refuse(out scale, out rows);
        }
    }

    // Deserialization leaves a missing reference member null. The verdict reads these.
    static bool Judgeable(SupportCostSample? sample) => sample is { Operation: not null, Scene: not null, SupportReads: not null };

    static bool Refuse(out HostedScale scale, out (SupportCostSample Sample, SupportCostRowVerdict Verdict)[] rows)
    {
        scale = default;
        rows = [];
        return false;
    }

    internal static SupportCostGate Evaluate(in SupportCostSample candidate, in SupportCostSample legacy, bool dense,
        double hostScale = 1, long explicitLifetimeBytes = 0, bool hosted = false)
    {
        if (!(hostScale > 0) || !double.IsFinite(hostScale)) throw new ArgumentOutOfRangeException(nameof(hostScale));
        if (!IsGated(candidate.Operation)) return SupportCostGate.Reject;
        if (candidate.Operation == "Find" ? OracleCorrectRefusal(candidate) : OracleBlocked(candidate)) return SupportCostGate.Envelope;
        return Check(candidate, legacy, dense, hostScale, explicitLifetimeBytes, hosted).Passed ? SupportCostGate.Pass : SupportCostGate.Reject;
    }

    /// <summary>The SG-12 limits for a gated row and every rule it breaks. A hosted row also needs successful
    /// first-use geometry and oracle agreement, which counts as a geometry failure.</summary>
    internal static SupportCostCheck Check(in SupportCostSample candidate, in SupportCostSample legacy, bool dense,
        double hostScale, long explicitLifetimeBytes, bool hosted)
    {
        if (!IsGated(candidate.Operation)) throw new ArgumentException("Not a gated cost row.", nameof(candidate));
        bool geometry = !candidate.GeometryPassed || candidate.Refusal ||
            hosted && !(candidate.ColdGeometryPassed && candidate.GeometryMatchesOracle);
        bool tick = candidate.Operation is "DryTick" or "ExplicitTick";
        double timeLimit = (candidate.Operation == "Find" ? dense ? 500 : 50 : tick ? dense ? 4000 : 1000 : dense ? 1000 : 200) * hostScale;
        long byteLimit = candidate.Operation switch { "DryTick" => 208, "ExplicitTick" => explicitLifetimeBytes, _ => 0 };
        bool outsideEnvelope = false, legacyFailed = false;
        if (tick)
        {
            outsideEnvelope = candidate.AchievedMotion.X == 0 && candidate.AchievedMotion.Z == 0 || candidate.Substeps > 3 ||
                candidate.Counts.ModeSegments > 1 || !candidate.Comparable;
            if (!dense)
            {
                bool legacyTimed = double.IsFinite(legacy.WarmMicroseconds) && legacy.WarmMicroseconds >= 0;
                legacyFailed = !legacy.GeometryPassed || !legacyTimed;
                if (legacyTimed) timeLimit = Math.Min(timeLimit,
                    legacy.WarmMicroseconds + Math.Max(hostScale * 100, 0.25 * legacy.WarmMicroseconds));
            }
        }
        bool time = !(double.IsFinite(candidate.WarmMicroseconds) && candidate.WarmMicroseconds >= 0 &&
            candidate.WarmMicroseconds <= timeLimit);
        bool allocation = candidate.WarmBytes < 0 || tick && byteLimit <= 0 || candidate.WarmBytes > byteLimit;
        return new(timeLimit, byteLimit, time, geometry, outsideEnvelope, legacyFailed, allocation);
    }
}

/// <summary>One recorded row's verdict and the legacy row it was compared with.</summary>
internal readonly record struct SupportCostRowVerdict(SupportCostSample? Legacy, SupportCostGate Gate);

/// <summary>A gated row's limits and failures: <c>T</c> time, <c>G</c> geometry, oracle or refusal, <c>E</c> outside
/// the comparable moving single-mode envelope, <c>L</c> unsuccessful ordinary legacy, <c>A</c> allocation.</summary>
internal readonly record struct SupportCostCheck(double TimeLimit, long ByteLimit, bool Time, bool Geometry,
    bool OutsideEnvelope, bool Legacy, bool Allocation)
{
    public bool Passed => !(Time || Geometry || OutsideEnvelope || Legacy || Allocation);

    public string Codes => string.Join(",", new[] { (Time, "T"), (Geometry, "G"), (OutsideEnvelope, "E"), (Legacy, "L"), (Allocation, "A") }
        .Where(code => code.Item1).Select(code => code.Item2));
}

internal static class SupportCostArtifact
{
    /// <summary>Records one invocation. A local recording judges its own rows at scale 1. A hosted recording
    /// only records: its scale and verdict are computed offline against the reference section recorded at
    /// the run's commit (SC-2).</summary>
    internal static void Write(string path, IReadOnlyList<SupportCostSample> samples, string baseSha,
        string candidateSha, string command, bool hosted = false)
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
        root[section] = JsonSerializer.SerializeToNode(new
        {
            RunId = runId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            BaseSha = baseSha,
            CandidateSha = candidateSha,
            RecordingLabel = "harness-corrected",
            UnmeasuredPrimingCallsPerRow = 1,
            MeasuredBeforeHarnessCommit = section == "baseline",
            Command = command,
            Hardware = Hardware(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Compilation = "Release, tiering disabled. First-use sample, one unmeasured priming call, warm sample. No warm-up loop.",
            ColdJit = "First use may include optimized JIT. The second invocation primes hit-only paths outside measurement before the warm sample. ColdBytes measures managed allocation, not JIT code memory.",
            ColdScratch = "Scene construction excluded. Each new scene has its own backend scratch. FootSupport thread scratch is shared across rows.",
            Scratch = "High-water retained managed array payload in support scratch, every constructed query scratch and privately owned installed local geometry, plus each query scratch's native Bepu pool capacity. Excludes object headers.",
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
            Samples = samples,
        }, options);
        JsonObject recording = root[section]!.AsObject();
        long explicitLifetime = lifetime.Movement + lifetime.Physics + lifetime.Pin;
        if (hosted)
        {
            recording["HostScale"] = null;
            recording["ReferenceSection"] = ReferenceSection(candidateSha);
            recording["RecordingLabel"] = "hosted-support-cost";
            recording["MeasuredBeforeHarnessCommit"] = false;
            recording["Normalization"] = "Recorded only. The scale and verdict are computed offline against the M2 Max " +
                "reference section named in ReferenceSection, recorded at this run's commit in one invocation. The scale is the median " +
                "hosted/M2 Max warm time over every legacy row present and successful in both, with M2 warm time above 0.5 us, " +
                "over at least 40 rows. Only absolute time ceilings scale.";
        }
        var compared = samples.Select(sample =>
        {
            if (sample.Operation != "GroundTickBorrowed" && !SupportCostVerdict.IsGated(sample.Operation)) return sample;
            SupportCostSample? legacy = SupportCostVerdict.PairedLegacy(sample, samples);
            if (legacy is null) return sample;
            SupportCostRowVerdict? verdict = hosted ? null : SupportCostVerdict.JudgeRow(sample, samples, 1, explicitLifetime, hosted: false);
            return sample with
            {
                CandidateLegacyRatio = sample.Operation is "Find" or "DryTick" or "ExplicitTick" && legacy.WarmMicroseconds > 0 ? sample.WarmMicroseconds / legacy.WarmMicroseconds : null,
                WholeTickDeltaMicroseconds = sample.Operation is "DryTick" or "ExplicitTick" ? sample.WarmMicroseconds - legacy.WarmMicroseconds : null,
                // A borrowed whole tick is compared but has no cost gate, so a local recording marks it failed.
                Envelope = hosted ? null : verdict?.Gate == SupportCostGate.Envelope,
                GatePassed = hosted || verdict?.Gate == SupportCostGate.Envelope ? null : verdict?.Gate == SupportCostGate.Pass,
            };
        }).ToArray();
        recording["Samples"] = JsonSerializer.SerializeToNode(compared, options);
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
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            string? model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model is not null) return model[(model.IndexOf(':') + 1)..].Trim();
        }
        if (!OperatingSystem.IsMacOS()) return RuntimeInformation.OSDescription;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string")
        { RedirectStandardOutput = true, UseShellExecute = false });
        string hardware = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return hardware;
    }

    /// <summary>The M2 Max reference section for a commit, named by its first nine hex digits (SC-2).</summary>
    internal static string ReferenceSection(string commit) => "reference-" + (commit.Length >= 9 ? commit[..9] : commit);

    /// <summary>The finalized M2 Max reference recorded at <paramref name="commit"/>, or null when it is absent.</summary>
    internal static JsonObject? ReadReference(string path, string commit)
    {
        if (commit.Length < 9) return null;
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            return root is JsonObject && root["schemaVersion"]?.GetValue<int>() == 2 && root["pendingFinalization"] is null &&
                root[ReferenceSection(commit)] is JsonObject section &&
                section["CandidateSha"]?.GetValue<string>() is { } recorded && recorded.StartsWith(commit, StringComparison.Ordinal)
                ? section : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
