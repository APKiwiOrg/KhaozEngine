using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal enum CoverageCostFixtureMode { Typical, Limit }
internal enum CoverageCostPassSet { ColourOnly, Full }

/// <summary>
/// One authorized cost measurement case, parsed strictly from exact UTF-8 JSON. The parser is the only constructor,
/// so a manifest in hand has passed every check below. Unknown, duplicate, mistyped, nonfinite, negative or
/// excessive values throw <see cref="InvalidDataException"/> naming the field. Nothing is coerced or defaulted.
/// The authorization block records who approved the workload and where. It is required, but it is a record of
/// approval, not a credential the code can verify, so a manifest is committed or supplied only after approval.
/// </summary>
internal sealed partial class CoverageCostManifest
{
    internal const string Schema = "khaozengine.coverage-cost-manifest/1";
    // The double-buffered motion history first holds committed surfaces after frame one, so warm capacity growth
    // finishes on the second frame (Task 9A). Fewer warm-up frames would measure that growth.
    internal const int MinWarmupFrames = 2, MaxWarmupFrames = 600, MaxMeasuredFrames = 5000;
    internal const double MaxWallSecondsLimit = 3600, MaxFrameMillisecondsLimit = 10_000;
    static readonly int[] ActorCounts = [1, 32, 128];

    CoverageCostManifest() { }

    internal string Case { get; private init; } = "";
    internal int Actors { get; private init; }
    internal bool Coverage { get; private init; }
    internal bool GpuSkinning { get; private init; }
    internal CoverageCostFixtureMode Fixture { get; private init; }
    internal CoverageCostPassSet Passes { get; private init; }
    /// <summary>The exact native backend, or null for the recording device, which measures CPU work with no GPU.</summary>
    internal GpuBackendKind? Backend { get; private init; }
    internal bool Recording => Backend is null;
    internal bool RejectSoftwareAdapter { get; private init; }
    internal string? AdapterContains { get; private init; }
    internal int WarmupFrames { get; private init; }
    internal int MeasuredFrames { get; private init; }
    internal double MaxWallSeconds { get; private init; }
    internal double MaxFrameMilliseconds { get; private init; }
    internal string AuthorizationIdentity { get; private init; } = "";
    internal string AuthorizedBy { get; private init; } = "";
    internal string AuthorizationReference { get; private init; } = "";
    /// <summary>Lowercase SHA-256 of the exact manifest bytes, the pair's bytes for a paired condition.</summary>
    internal string Sha256 { get; private init; } = "";

    static readonly string[] SharedMembers = ["actors", "gpuSkinning", "fixture", "passes", "device", "warmupFrames",
        "measuredFrames", "limits", "gpuExecutionMetric", "authorization"];

    internal static CoverageCostManifest Parse(ReadOnlySpan<byte> utf8)
    {
        using var document = Document(utf8);
        var root = Object(document.RootElement, "manifest", ["schema", "case", "coverage", .. SharedMembers]);
        if (String(root, "schema") != Schema) throw Invalid("schema", $"must be {Schema}");
        string name = String(root, "case");
        if (!CaseName().IsMatch(name)) throw Invalid("case", "must be 1 to 64 lowercase letters, digits or hyphens");
        return Build(root, name, Choice(root, "coverage", ("on", true), ("off", false)), Convert.ToHexStringLower(SHA256.HashData(utf8)));
    }

    /// <summary>Both conditions of one paired unit, in its authorized order, each fully validated before return.</summary>
    internal static (string Unit, CoverageCostManifest[] Conditions, string Sha256) ParsePair(ReadOnlySpan<byte> utf8)
    {
        using var document = Document(utf8);
        var root = Object(document.RootElement, "pair", ["schema", "unit", "order", .. SharedMembers]);
        if (String(root, "schema") != CoverageCostPairManifest.Schema) throw Invalid("schema", $"must be {CoverageCostPairManifest.Schema}");
        string unit = String(root, "unit");
        if (unit.Length > 60 || !CaseName().IsMatch(unit)) throw Invalid("unit", "must be 1 to 60 lowercase letters, digits or hyphens");
        var order = root["order"];
        if (order.ValueKind != JsonValueKind.Array || order.GetArrayLength() != 2)
            throw Invalid("order", "must list exactly the two conditions");
        var tokens = new string[2];
        for (int i = 0; i < 2; i++)
            tokens[i] = order[i].ValueKind == JsonValueKind.String && order[i].GetString() is "on" or "off"
                ? order[i].GetString()! : throw Invalid($"order[{i}]", "must be on or off");
        if (tokens[0] == tokens[1]) throw Invalid("order", "must name on and off once each");
        string sha = Convert.ToHexStringLower(SHA256.HashData(utf8));
        return (unit, [Build(root, $"{unit}-{tokens[0]}", tokens[0] == "on", sha), Build(root, $"{unit}-{tokens[1]}", tokens[1] == "on", sha)], sha);
    }

    static JsonDocument Document(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) throw new InvalidDataException("Coverage cost manifest is empty.");
        try
        {
            return JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
        }
        catch (JsonException ex) { throw new InvalidDataException($"Coverage cost manifest is not strict JSON: {ex.Message}", ex); }
    }

    static CoverageCostManifest Build(Dictionary<string, JsonElement> root, string name, bool coverage, string sha)
    {
        var device = Object(root["device"], "device", "backend", "rejectSoftwareAdapter", "adapterContains");
        var limits = Object(root["limits"], "limits", "maxWallSeconds", "maxFrameMilliseconds");
        var authorization = Object(root["authorization"], "authorization", "identity", "approvedBy", "reference");
        // No GPU timestamp-query API exists, so no native GPU execution metric can be selected yet.
        if (String(root, "gpuExecutionMetric") != "unavailable")
            throw Invalid("gpuExecutionMetric", "must be unavailable until an authorized GPU timing metric exists");
        int actors = Int(root, "actors", 1, 128);
        if (Array.IndexOf(ActorCounts, actors) < 0) throw Invalid("actors", "must be 1, 32 or 128");
        GpuBackendKind? backend = Choice<GpuBackendKind?>(device, "device.backend", ("recording-cpu", null),
            ("metal-native", GpuBackendKind.MetalNative), ("direct3d11-native", GpuBackendKind.Direct3D11Native),
            ("vulkan-native", GpuBackendKind.VulkanNative));
        bool rejectSoftware = Bool(device, "device.rejectSoftwareAdapter");
        string? adapter = OptionalLabel(device, "device.adapterContains");
        // The recording device has no adapter, so adapter requirements on it are contradictions, not defaults.
        if (backend is null && (rejectSoftware || adapter is not null))
            throw Invalid("device", "recording-cpu takes no adapter requirement");
        return new CoverageCostManifest
        {
            Case = name,
            Actors = actors,
            Coverage = coverage,
            GpuSkinning = Bool(root, "gpuSkinning"),
            Fixture = Choice(root, "fixture", ("typical", CoverageCostFixtureMode.Typical), ("limit", CoverageCostFixtureMode.Limit)),
            Passes = Choice(root, "passes", ("colour-only", CoverageCostPassSet.ColourOnly), ("full", CoverageCostPassSet.Full)),
            Backend = backend,
            RejectSoftwareAdapter = rejectSoftware,
            AdapterContains = adapter,
            WarmupFrames = Int(root, "warmupFrames", MinWarmupFrames, MaxWarmupFrames),
            MeasuredFrames = Int(root, "measuredFrames", 1, MaxMeasuredFrames),
            MaxWallSeconds = Positive(limits, "limits.maxWallSeconds", MaxWallSecondsLimit),
            MaxFrameMilliseconds = Positive(limits, "limits.maxFrameMilliseconds", MaxFrameMillisecondsLimit),
            AuthorizationIdentity = Label(authorization, "authorization.identity"),
            AuthorizedBy = Label(authorization, "authorization.approvedBy"),
            AuthorizationReference = Label(authorization, "authorization.reference"),
            Sha256 = sha,
        };
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseName();

    // Exactly the named members, each once. Missing, extra and duplicate members are all refusals.
    static Dictionary<string, JsonElement> Object(JsonElement element, string path, params string[] members)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(path, "must be an object");
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(members, property.Name) < 0) throw Invalid($"{path}.{property.Name}", "is not a known field");
            if (!found.TryAdd(property.Name, property.Value)) throw Invalid($"{path}.{property.Name}", "is repeated");
        }
        foreach (string member in members)
            if (!found.ContainsKey(member)) throw Invalid($"{path}.{member}", "is required");
        return found;
    }

    static JsonElement Member(Dictionary<string, JsonElement> owner, string path) => owner[path[(path.LastIndexOf('.') + 1)..]];

    static string String(Dictionary<string, JsonElement> owner, string path)
    {
        var value = Member(owner, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(path, "must be a string");
    }

    static bool Bool(Dictionary<string, JsonElement> owner, string path) => Member(owner, path).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Invalid(path, "must be true or false"),
    };

    static int Int(Dictionary<string, JsonElement> owner, string path, int min, int max)
    {
        var value = Member(owner, path);
        // GetRawText keeps 32.0 and 3e1 apart from 32, so only a plain integer literal is accepted.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)
            || value.GetRawText() != result.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid(path, "must be a plain integer");
        return result >= min && result <= max ? result : throw Invalid(path, $"must be {min} to {max}");
    }

    static double Positive(Dictionary<string, JsonElement> owner, string path, double max)
    {
        var value = Member(owner, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result))
            throw Invalid(path, "must be a finite number");
        return result > 0 && result <= max ? result : throw Invalid(path, $"must be greater than 0 and at most {max}");
    }

    static T Choice<T>(Dictionary<string, JsonElement> owner, string path, params (string Token, T Value)[] choices)
    {
        string token = String(owner, path);
        foreach (var choice in choices) if (choice.Token == token) return choice.Value;
        throw Invalid(path, $"'{token}' is not a supported value");
    }

    static string Label(Dictionary<string, JsonElement> owner, string path)
    {
        string value = String(owner, path);
        return value.Length is > 0 and <= 200 && !string.IsNullOrWhiteSpace(value) && value.Trim() == value
            ? value : throw Invalid(path, "must be 1 to 200 characters without surrounding whitespace");
    }

    static string? OptionalLabel(Dictionary<string, JsonElement> owner, string path) =>
        Member(owner, path).ValueKind == JsonValueKind.Null ? null : Label(owner, path);

    static InvalidDataException Invalid(string path, string reason) => new($"Coverage cost manifest {path} {reason}.");
}
