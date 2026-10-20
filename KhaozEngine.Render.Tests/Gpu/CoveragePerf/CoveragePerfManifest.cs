using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal enum CoveragePerfCondition { DirectOff, SharedOff, SharedOn }
internal enum CoveragePerfTimerLayout { CpuOnly, Total, Phases }
internal enum CoveragePerfAdapterPolicy { LavapipeCpuOnly, NvidiaT4 }

internal sealed record CoveragePerfBlock(int Index, int Actors, CoveragePerfCondition Condition,
    CoveragePerfTimerLayout TimerLayout, int WarmupFrames, int MeasuredFrames, string Fixture);

internal sealed record CoveragePerfPreset(string Name, GpuBackendKind Backend, CoveragePerfAdapterPolicy AdapterPolicy,
    string Runner, string Workflow, string Leg, int MaxUnitSeconds, int JobMinutes, ImmutableArray<CoveragePerfBlock> Blocks);

// Approval-time facts only. The run that executes an approval does not exist when its manifest is committed.
internal sealed record CoveragePerfAuthorization(string Identity, string ApprovedBy, string Reference);

/// <summary>
/// A strict audit record for one separately approved unit. Prefixes do not establish owner consent. The caller must
/// supply the exact approved identity and byte hash, with an independently verified workload identity. The run's
/// workflow SHA, run ID and process ID are recorded and never matched. Only a run's first attempt is admitted.
/// Parsing and binding have no device, factory, environment or clock dependencies.
/// </summary>
internal sealed record CoveragePerfManifest
{
    internal const string Schema = "khaozengine.coverage-perf-unit/1";
    internal const string AuthorizationPrefix = "coverage-gpu-era-perf-";
    internal const string FixtureVersion = "coverage-typical-gpu/1", ProfileVersion = "full-1280x720/1";
    internal const string ReleaseConfiguration = "Release", OrdinarySkinningRoute = "gpu", CoveredSurfaceRoute = "gpu-built";
    internal const int BatchFrameCount = 3, WarmupFrameCount = 64, SmokeMeasuredFrames = 32, ProofMeasuredFrames = 128;
    internal const int IterationCount = 1, AddedLoad = 0, MaxFrameMillisecondsValue = 10000, FirstRunAttempt = 1;
    internal const int MaxManifestBytes = 65536, MaxLabelCharacters = 128, MaxApprovalReferenceCharacters = 1024;

    // This is the single preset table. Counts, order, runner labels and workflow deadlines are shared with later tasks.
    internal static ImmutableArray<CoveragePerfPreset> Presets { get; } =
    [
        new("C0", GpuBackendKind.VulkanNative, CoveragePerfAdapterPolicy.LavapipeCpuOnly,
            "ubuntu-26.04", "stress-test", "ubuntu-26.04-vulkan-lavapipe", 300, 20,
            [Block(0, 0, CoveragePerfCondition.DirectOff, CoveragePerfTimerLayout.CpuOnly, SmokeMeasuredFrames),
             Block(1, 0, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, SmokeMeasuredFrames),
             Block(2, 1, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, SmokeMeasuredFrames),
             Block(3, 1, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.CpuOnly, SmokeMeasuredFrames)]),
        new("C1", GpuBackendKind.VulkanNative, CoveragePerfAdapterPolicy.LavapipeCpuOnly,
            "ubuntu-26.04", "stress-test", "ubuntu-26.04-vulkan-lavapipe", 600, 30,
            [Block(0, 32, CoveragePerfCondition.DirectOff, CoveragePerfTimerLayout.CpuOnly, ProofMeasuredFrames),
             Block(1, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, ProofMeasuredFrames),
             Block(2, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.CpuOnly, ProofMeasuredFrames),
             Block(3, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.CpuOnly, ProofMeasuredFrames),
             Block(4, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, ProofMeasuredFrames),
             Block(5, 32, CoveragePerfCondition.DirectOff, CoveragePerfTimerLayout.CpuOnly, ProofMeasuredFrames)]),
        new("G1", GpuBackendKind.VulkanNative, CoveragePerfAdapterPolicy.NvidiaT4,
            "gpu-t4-linux", "gpu-hosted-bench", "linux-vulkan", 600, 30, GpuBlocks()),
        new("G2", GpuBackendKind.Direct3D11Native, CoveragePerfAdapterPolicy.NvidiaT4,
            "gpu-t4-windows", "gpu-hosted-bench", "windows-d3d11", 600, 30, GpuBlocks()),
        new("G3", GpuBackendKind.VulkanNative, CoveragePerfAdapterPolicy.NvidiaT4,
            "gpu-t4-windows", "gpu-hosted-bench", "windows-vulkan", 600, 30, GpuBlocks()),
    ];

    CoveragePerfManifest() { }

    internal string Preset { get; private init; } = "";
    internal string UnitId { get; private init; } = "";
    internal string WorkloadSha { get; private init; } = "";
    internal string BaselineMainSha { get; private init; } = "";
    internal string Fixture { get; private init; } = "";
    internal string Profile { get; private init; } = "";
    internal string Configuration { get; private init; } = "";
    internal string OrdinarySkinning { get; private init; } = "";
    internal string CoveredSurface { get; private init; } = "";
    internal GpuBackendKind Backend { get; private init; }
    internal CoveragePerfAdapterPolicy AdapterPolicy { get; private init; }
    internal int BatchFrames { get; private init; }
    internal int Iterations { get; private init; }
    internal int Load { get; private init; }
    internal string Runner { get; private init; } = "";
    internal string Workflow { get; private init; } = "";
    internal string Leg { get; private init; } = "";
    internal int MaxUnitSeconds { get; private init; }
    internal int MaxFrameMilliseconds { get; private init; }
    /// <summary>The workflow enforces this outer timeout. The driver enforces the cooperative unit/frame limits.</summary>
    internal int JobMinutes { get; private init; }
    internal CoveragePerfAuthorization Authorization { get; private init; } = null!;
    internal ImmutableArray<CoveragePerfBlock> Blocks { get; private init; }
    internal ImmutableArray<byte> Utf8 { get; private init; }
    internal string Sha256 { get; private init; } = "";

    internal static CoveragePerfManifest Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaxManifestBytes)
            throw Invalid("manifest bytes", $"must contain 1 to {MaxManifestBytes} UTF-8 bytes");
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(utf8);
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var root = Object(document.RootElement, "manifest");
            if (String(root, "schema") != Schema)
                throw Invalid("schema", $"is retired or unsupported, must be {Schema}");
            Members(root, "manifest", "schema", "preset", "unitId", "workloadSha", "baselineMainSha", "fixture", "profile",
                "configuration", "routes", "device", "batchFrames", "runner", "workflow", "leg", "iterations", "load",
                "limits", "authorization", "blocks");
            return Build(root, utf8);
        }
        catch (JsonException ex) { throw new InvalidDataException($"manifest must be strict JSON: {ex.Message}", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("manifest bytes must be valid UTF-8", ex); }
    }

    internal void ValidateAuthorization(CoveragePerfRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        FreshIdentity(identity.AuthorizationIdentity, "authorization.identity");
        Equal(identity.AuthorizationIdentity, Authorization.Identity, "authorization.identity");
        Equal(identity.UnitId, UnitId, "identity.unitId");
        Equal(identity.ManifestSha256, Sha256, "identity.manifestSha256");
        Equal(identity.WorkloadSha, WorkloadSha, "identity.workloadSha");
        Equal(identity.Runner, Runner, "identity.runner");
        Equal(identity.Workflow, Workflow, "identity.workflow");
        Equal(identity.Leg, Leg, "identity.leg");
        // Run facts are protocol checks, not manifest bindings. A GitHub re-run cannot reuse an approval.
        if (!RunId(identity.WorkflowRunId)) throw Invalid("identity.workflowRunId", "must be a positive decimal workflow run ID");
        if (identity.WorkflowRunAttempt != FirstRunAttempt)
            throw Invalid("identity.workflowRunAttempt", "must be 1, a GitHub re-run cannot reuse an approval");
        if (identity.ProcessId <= 0) throw Invalid("identity.processId", "must be positive");
    }

    static CoveragePerfManifest Build(Dictionary<string, JsonElement> root, ReadOnlySpan<byte> utf8)
    {
        string name = String(root, "preset");
        var preset = FindPreset(name);
        var routes = Object(Value(root, "routes"), "routes", "ordinarySkinning", "coveredSurface");
        var device = Object(Value(root, "device"), "device", "backend", "adapterPolicy");
        var limits = Object(Value(root, "limits"), "limits", "maxUnitSeconds", "maxFrameMilliseconds", "jobMinutes");
        var approval = Object(Value(root, "authorization"), "authorization", "identity", "approvedBy", "reference");
        string authorizationIdentity = Label(approval, "authorization.identity");
        FreshIdentity(authorizationIdentity, "authorization.identity");
        string unitId = Label(root, "unitId");
        if (!Slug(unitId, 64)) throw Invalid("unitId", "must be 1 to 64 lowercase letters, digits or hyphens");

        return new CoveragePerfManifest
        {
            Preset = name,
            UnitId = unitId,
            WorkloadSha = Sha(root, "workloadSha"),
            BaselineMainSha = Sha(root, "baselineMainSha"),
            Fixture = Equal(String(root, "fixture"), FixtureVersion, "fixture"),
            Profile = Equal(String(root, "profile"), ProfileVersion, "profile"),
            Configuration = Equal(String(root, "configuration"), ReleaseConfiguration, "configuration"),
            OrdinarySkinning = Equal(String(routes, "routes.ordinarySkinning"), OrdinarySkinningRoute, "routes.ordinarySkinning"),
            CoveredSurface = Equal(String(routes, "routes.coveredSurface"), CoveredSurfaceRoute, "routes.coveredSurface"),
            Backend = Equal(Choice(device, "device.backend", ("vulkan-native", GpuBackendKind.VulkanNative),
                ("direct3d11-native", GpuBackendKind.Direct3D11Native)), preset.Backend, "device.backend"),
            AdapterPolicy = Equal(Choice(device, "device.adapterPolicy", ("lavapipe-cpu-only", CoveragePerfAdapterPolicy.LavapipeCpuOnly),
                ("nvidia-t4", CoveragePerfAdapterPolicy.NvidiaT4)), preset.AdapterPolicy, "device.adapterPolicy"),
            BatchFrames = Equal(Int(root, "batchFrames"), BatchFrameCount, "batchFrames"),
            Iterations = Equal(Int(root, "iterations"), IterationCount, "iterations"),
            Load = Equal(Int(root, "load"), AddedLoad, "load"),
            Runner = Equal(String(root, "runner"), preset.Runner, "runner"),
            Workflow = Equal(String(root, "workflow"), preset.Workflow, "workflow"),
            Leg = Equal(String(root, "leg"), preset.Leg, "leg"),
            MaxUnitSeconds = Equal(Int(limits, "limits.maxUnitSeconds"), preset.MaxUnitSeconds, "limits.maxUnitSeconds"),
            MaxFrameMilliseconds = Equal(Int(limits, "limits.maxFrameMilliseconds"), MaxFrameMillisecondsValue, "limits.maxFrameMilliseconds"),
            JobMinutes = Equal(Int(limits, "limits.jobMinutes"), preset.JobMinutes, "limits.jobMinutes"),
            Authorization = new(authorizationIdentity, Label(approval, "authorization.approvedBy"),
                Label(approval, "authorization.reference", MaxApprovalReferenceCharacters)),
            Blocks = BlocksFrom(Value(root, "blocks"), preset),
            Utf8 = ImmutableArray.CreateRange(utf8.ToArray()),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(utf8)),
        };
    }

    static ImmutableArray<CoveragePerfBlock> BlocksFrom(JsonElement array, CoveragePerfPreset preset)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != preset.Blocks.Length)
            throw Invalid("blocks", $"must contain exactly {preset.Blocks.Length} ordered blocks for {preset.Name}");
        var blocks = ImmutableArray.CreateBuilder<CoveragePerfBlock>(preset.Blocks.Length);
        for (int i = 0; i < preset.Blocks.Length; i++)
        {
            string path = $"blocks[{i}]";
            var fields = Object(array[i], path, "index", "actors", "condition", "timerLayout", "warmupFrames", "measuredFrames", "fixture");
            var expected = preset.Blocks[i];
            blocks.Add(new(
                Equal(Int(fields, $"{path}.index"), expected.Index, $"{path}.index"),
                Equal(Int(fields, $"{path}.actors"), expected.Actors, $"{path}.actors"),
                Equal(Choice(fields, $"{path}.condition", ("DirectOff", CoveragePerfCondition.DirectOff),
                    ("SharedOff", CoveragePerfCondition.SharedOff), ("SharedOn", CoveragePerfCondition.SharedOn)), expected.Condition, $"{path}.condition"),
                Equal(Choice(fields, $"{path}.timerLayout", ("CpuOnly", CoveragePerfTimerLayout.CpuOnly),
                    ("Total", CoveragePerfTimerLayout.Total), ("Phases", CoveragePerfTimerLayout.Phases)), expected.TimerLayout, $"{path}.timerLayout"),
                Equal(Int(fields, $"{path}.warmupFrames"), expected.WarmupFrames, $"{path}.warmupFrames"),
                Equal(Int(fields, $"{path}.measuredFrames"), expected.MeasuredFrames, $"{path}.measuredFrames"),
                Equal(String(fields, $"{path}.fixture"), expected.Fixture, $"{path}.fixture")));
        }
        return blocks.MoveToImmutable();
    }

    static CoveragePerfPreset FindPreset(string name)
    {
        foreach (var preset in Presets) if (preset.Name == name) return preset;
        throw Invalid("preset", "must be C0, C1, G1, G2 or G3");
    }

    static CoveragePerfBlock Block(int index, int actors, CoveragePerfCondition condition, CoveragePerfTimerLayout layout, int measured) =>
        new(index, actors, condition, layout, WarmupFrameCount, measured, FixtureVersion);

    static ImmutableArray<CoveragePerfBlock> GpuBlocks() =>
    [
        Block(0, 1, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Phases, SmokeMeasuredFrames),
        Block(1, 1, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Phases, SmokeMeasuredFrames),
        Block(2, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Phases, SmokeMeasuredFrames),
        Block(3, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Phases, SmokeMeasuredFrames),
        Block(4, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Total, ProofMeasuredFrames),
        Block(5, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Total, ProofMeasuredFrames),
        Block(6, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Total, ProofMeasuredFrames),
        Block(7, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Total, ProofMeasuredFrames),
    ];

    static Dictionary<string, JsonElement> Object(JsonElement element, string path, params string[] members)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(path, "must be an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!fields.TryAdd(field.Name, field.Value)) throw Invalid($"{path}.{field.Name}", "is duplicated");
        if (members.Length != 0) Members(fields, path, members);
        return fields;
    }

    static void Members(Dictionary<string, JsonElement> fields, string path, params string[] members)
    {
        foreach (string name in fields.Keys)
        {
            if (name == "gpuSkinning") throw Invalid($"{path}.{name}", "is retired, routes are fixed by the GPU-era preset");
            if (Array.IndexOf(members, name) < 0) throw Invalid($"{path}.{name}", "is an unknown field");
        }
        foreach (string name in members)
            if (!fields.ContainsKey(name)) throw Invalid($"{path}.{name}", "is required");
    }

    static JsonElement Value(Dictionary<string, JsonElement> fields, string path) =>
        fields.TryGetValue(path[(path.LastIndexOf('.') + 1)..], out var value) ? value : throw Invalid(path, "is required");

    static string String(Dictionary<string, JsonElement> fields, string path)
    {
        var value = Value(fields, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(path, "must be a string");
    }

    static int Int(Dictionary<string, JsonElement> fields, string path)
    {
        var value = Value(fields, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number)
            || value.GetRawText() != number.ToString(CultureInfo.InvariantCulture))
            throw Invalid(path, "must be a plain finite 32-bit integer without coercion");
        return number;
    }

    static string Label(Dictionary<string, JsonElement> fields, string path, int max = MaxLabelCharacters)
    {
        string text = String(fields, path);
        if (text.Length == 0 || text.Length > max || text.Trim() != text)
            throw Invalid(path, $"must contain 1 to {max} characters without outer whitespace");
        foreach (char c in text) if (char.IsControl(c)) throw Invalid(path, "must not contain control characters");
        return text;
    }

    static string Sha(Dictionary<string, JsonElement> fields, string path)
    {
        string text = String(fields, path);
        if (text.Length != 40) throw Invalid(path, "must be a full lowercase 40-character git SHA");
        foreach (char c in text)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                throw Invalid(path, "must be a full lowercase 40-character git SHA");
        return text;
    }

    static bool RunId(string? text) => text is not null
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0
        && text == value.ToString(CultureInfo.InvariantCulture);

    static void FreshIdentity(string text, string path)
    {
        if (text is null || !text.StartsWith(AuthorizationPrefix, StringComparison.Ordinal)
            || !Slug(text[AuthorizationPrefix.Length..], MaxLabelCharacters - AuthorizationPrefix.Length))
            throw Invalid(path, $"is retired or invalid, must start with {AuthorizationPrefix} and have a fresh unit suffix");
    }

    static bool Slug(string text, int max)
    {
        if (text.Length == 0 || text.Length > max || text[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        foreach (char c in text) if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        return true;
    }

    static T Choice<T>(Dictionary<string, JsonElement> fields, string path, params (string Token, T Value)[] choices)
    {
        string text = String(fields, path);
        foreach (var choice in choices) if (choice.Token == text) return choice.Value;
        throw Invalid(path, $"'{text}' is unsupported");
    }

    static T Equal<T>(T actual, T expected, string path)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw Invalid(path, $"must equal '{expected}' for this approval");
        return actual;
    }

    static InvalidDataException Invalid(string path, string reason) => new($"{path} {reason}.");
}
