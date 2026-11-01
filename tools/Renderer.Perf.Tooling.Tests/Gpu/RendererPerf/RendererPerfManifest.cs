using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfManifest(string Schema, string UnitId, string Preset, string WorkloadSha,
    string BaselineMainSha, ImmutableArray<RendererPerfRecipeIdentity> Recipes, ImmutableArray<RendererPerfBlock> Blocks,
    ImmutableArray<RendererPerfControl> Controls, int ConditioningFrames, int BatchSize, int MaximumScopeOccurrences,
    RendererPerfRuntimePolicy Runtime, RendererPerfExecutionPolicy Execution, RendererPerfLimits Limits,
    RendererPerfBudget Budget, RendererPerfApproval Approval)
{
    // Validate refuses an unknown preset before anything reads its schedule.
    internal RendererPerfSchedule Schedule => RendererPerfSchedules.For(Preset);
    internal RendererPerfExecutionClass ExecutionClass => Schedule.ExecutionClass;

    internal int TotalFrameCount => checked(ConditioningFrames + Blocks.Sum(b => checked(b.WarmupFrames + b.MeasuredFrames))
        + Controls.Sum(c => checked(c.WarmupFrames + c.MeasuredFrames)));

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
    };

    internal static RendererPerfManifest Parse(ReadOnlySpan<byte> utf8)
    {
        RendererPerfManifest manifest = ReadJson<RendererPerfManifest>(utf8, 64 * 1024);
        Validate(manifest);
        return manifest;
    }

    // The workflow supplies independent commit facts. Parsing never reads a clock or opens a device.
    internal static RendererPerfManifest Parse(ReadOnlySpan<byte> utf8, RendererPerfAuthorization authorization)
    {
        RendererPerfManifest manifest = Parse(utf8);
        Hash(authorization.ManifestSha256, 64);
        Hash(authorization.WorkloadSha, 40);
        Hash(authorization.AuthorizationSha, 40);
        Hash(authorization.ParentSha, 40);
        Require(Convert.ToHexStringLower(SHA256.HashData(utf8)) == authorization.ManifestSha256,
            "Hash", "Manifest bytes do not match the dispatched SHA-256.");
        Require(authorization.WorkloadSha == manifest.WorkloadSha && authorization.ParentSha == manifest.WorkloadSha
            && authorization.AuthorizationSha != manifest.WorkloadSha && authorization.ParentCount == 1
            && authorization.RunAttempt == 1, "Authorization", "Authorization must have the workload as its sole parent and execute only attempt 1.");
        Require(authorization.PreviousRunId == null, "Authorization", $"Authorization was already used by workflow run '{authorization.PreviousRunId}'.");
        Require(!authorization.ChangedPaths.IsDefault && authorization.ChangedPaths.Length == 1
            && authorization.ChangedPaths[0] == $"tools/Renderer.Perf.Tests/authorized/{manifest.UnitId}.json"
            && authorization.ManifestChangeKind == "A",
            "Authorization", "Authorization may only add this renderer unit's manifest.");
        Require(authorization.Leg == manifest.Execution.Leg && authorization.Runner == manifest.Execution.Runner
            && authorization.Workflow == manifest.Execution.Workflow,
            "Authorization", "Dispatch leg, runner or workflow differs from the approved unit.");
        return manifest;
    }

    internal static void Validate(RendererPerfManifest manifest)
    {
        Require(manifest.Schema == "khaozengine.renderer-perf-unit/1", "Schema", "Expected renderer-perf-unit/1. Coverage schemas are not renderer schemas.");
        Require(Text(manifest.UnitId) && manifest.UnitId.Length <= 64
            && manifest.UnitId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            && manifest.UnitId[0] != '-', "Identity", "Unit ID must be a safe lowercase filename token.");
        Hash(manifest.WorkloadSha, 40);
        Hash(manifest.BaselineMainSha, 40);
        RendererPerfSchedule schedule = RendererPerfSchedules.For(manifest.Preset);
        // A rehearsal leg with no software class is refused before the hosted leg checks.
        if (schedule.ExecutionClass == RendererPerfExecutionClass.Rehearsal) _ = RendererPerfRehearsalPolicy.Class(manifest);
        RendererPerfExecutionPolicy execution = manifest.Execution;
        Require(execution.Leg is "linux-vulkan" or "windows-d3d11" or "windows-vulkan", "Preset", "Unsupported hosted leg.");
        Require(schedule.Legs.Contains(execution.Leg), "Preset", schedule.Preset == "pilot"
            ? $"{LegName(execution.Leg)} has no initial pilot preset."
            : $"{LegName(execution.Leg)} is not an approved leg for the {schedule.Preset} campaign.");
        Require(manifest.BatchSize == 3 && manifest.MaximumScopeOccurrences == 255
            && manifest.ConditioningFrames == schedule.ConditioningFrames, "Preset", "Pacing, scope capacity or conditioning count differs from the preset.");
        if (schedule.ExecutionClass == RendererPerfExecutionClass.Rehearsal) RendererPerfRehearsalPolicy.Validate(manifest);
        else RendererPerfPaidPolicy.Validate(manifest);
        RendererPerfRuntimePolicy runtime = manifest.Runtime;
        Require(runtime.Configuration == "Release" && Version.TryParse(runtime.RuntimeVersion, out Version? version)
            && version.Major == 10 && runtime.GcMode is "workstation" or "server" && runtime.Environment != null
            && runtime.Environment.Count <= 128 && runtime.Environment.All(p => Text(p.Key) && Text(p.Value)),
            "Preset", "Explicit Release runtime, ordinary GC and bounded environment facts are required.");
        ValidateSchedule(manifest, schedule);
    }

    static string LegName(string leg) => leg switch
    {
        "linux-vulkan" => "Linux Vulkan",
        "windows-d3d11" => "Windows D3D11",
        "windows-vulkan" => "Windows Vulkan",
        _ => leg,
    };

    static void ValidateSchedule(RendererPerfManifest manifest, RendererPerfSchedule schedule)
    {
        Require(!manifest.Recipes.IsDefault && manifest.Recipes.Length == schedule.Recipes.Length, "Preset", "Recipe list is incomplete.");
        for (int i = 0; i < manifest.Recipes.Length; i++)
        {
            RendererPerfScheduledRecipe expected = schedule.Recipes[i];
            RendererPerfRecipeIdentity recipe = manifest.Recipes[i];
            Require(recipe.SceneId == expected.SceneId && recipe.Version == expected.Version && recipe.Profile == expected.Profile,
                "Preset", "Recipe order, version or profile differs from the preset.");
            Hash(recipe.RecipeSha256, 64);
            Hash(recipe.SettingsSha256, 64);
            Hash(recipe.ShaderSha256, 64);
            Hash(recipe.GeometrySha256, 64);
            Hash(recipe.AssetSha256, 64);
        }
        ImmutableArray<RendererPerfBlock> blocks = schedule.Blocks();
        if (blocks.IsEmpty)
        {
            Require(!manifest.Blocks.IsDefault && manifest.Blocks.IsEmpty, "Preset", "All-controls rehearsal has no scene blocks.");
            ValidateControls(manifest, schedule);
            Require(manifest.TotalFrameCount == schedule.TotalFrames, "Preset", "All-controls frame accounting does not reconcile.");
            return;
        }
        Require(!manifest.Blocks.IsDefault && manifest.Blocks.Length == blocks.Length, "Preset", "Expanded block list is incomplete.");
        for (int i = 0; i < blocks.Length; i++)
            Require(manifest.Blocks[i] == blocks[i], "Preset", "Expanded block order or frame count differs from the preset.");
        ValidateControls(manifest, schedule);
        Require(manifest.TotalFrameCount == schedule.TotalFrames, "Preset", "Unit frame accounting does not reconcile.");
    }

    static void ValidateControls(RendererPerfManifest manifest, RendererPerfSchedule schedule)
    {
        ImmutableArray<int> controlIds = schedule.ControlIds;
        Require(!manifest.Controls.IsDefault && manifest.Controls.Length == controlIds.Length, "Preset", "Control list is incomplete.");
        for (int i = 0; i < controlIds.Length; i++)
        {
            RendererPerfControl control = manifest.Controls[i];
            Require(control.Id == controlIds[i] && control.Version == "1" && control.WarmupFrames == schedule.ControlWarmupFrames
                && control.MeasuredFrames == schedule.ControlMeasuredFrames, "Preset", "Control order, version or frame count differs from the preset.");
            Hash(control.WorkSha256, 64);
        }
    }

    internal static T ReadJson<T>(ReadOnlySpan<byte> utf8, int maximumBytes)
    {
        try
        {
            using JsonDocument document = ReadDocument(utf8, maximumBytes);
            return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new JsonException("Null document is forbidden.");
        }
        catch (JsonException error)
        {
            throw new RendererPerfProtocolException("Json", error.Message, error);
        }
    }

    internal static JsonDocument ReadDocument(ReadOnlySpan<byte> utf8, int maximumBytes)
    {
        if (utf8.Length == 0 || utf8.Length > maximumBytes) throw new JsonException("Document exceeds its bounded byte capacity or is empty.");
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 32 });
        JsonDocument document = JsonDocument.ParseValue(ref reader);
        try
        {
            if (reader.Read()) throw new JsonException("Trailing JSON content is forbidden.");
            CheckJson(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    static void CheckJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate field '{property.Name}'.");
                CheckJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new JsonException("Null array entries are forbidden.");
                CheckJson(item);
            }
        else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out double number) || !double.IsFinite(number)))
            throw new JsonException("Nonfinite or excessive numeric value.");
    }

    internal static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    internal static void Hash(string? value, int length) => Require(value != null && value.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Hash", $"Expected a lowercase {length}-digit hexadecimal hash.");
    internal static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new RendererPerfProtocolException(code, message);
    }
}
