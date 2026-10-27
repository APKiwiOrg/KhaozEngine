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
    internal RendererPerfExecutionClass ExecutionClass => Preset is "rehearsal" or "rehearsal-full" ? RendererPerfExecutionClass.Rehearsal : RendererPerfExecutionClass.Performance;

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
        Require(manifest.Preset is "pilot" or "full" or "rehearsal" or "rehearsal-full", "Preset", "Unknown renderer preset.");
        bool pilot = manifest.ExecutionClass == RendererPerfExecutionClass.Rehearsal
            ? RendererPerfRehearsalPolicy.Class(manifest).Schedule == RendererPerfRehearsalSchedule.Pilot : manifest.Preset == "pilot";
        RendererPerfExecutionPolicy execution = manifest.Execution;
        Require(execution.Leg is "linux-vulkan" or "windows-d3d11" or "windows-vulkan", "Preset", "Unsupported hosted leg.");
        Require(!pilot || execution.Leg != "windows-vulkan", "Preset", "Windows Vulkan has no initial pilot preset.");
        Require(manifest.BatchSize == 3 && manifest.MaximumScopeOccurrences == 255
            && manifest.ConditioningFrames == (pilot ? 300 : 1200), "Preset", "Pacing, scope capacity or conditioning count differs from the preset.");
        if (manifest.ExecutionClass == RendererPerfExecutionClass.Rehearsal) RendererPerfRehearsalPolicy.Validate(manifest);
        else RendererPerfPaidPolicy.Validate(manifest);
        RendererPerfRuntimePolicy runtime = manifest.Runtime;
        Require(runtime.Configuration == "Release" && Version.TryParse(runtime.RuntimeVersion, out Version? version)
            && version.Major == 10 && runtime.GcMode is "workstation" or "server" && runtime.Environment != null
            && runtime.Environment.Count <= 128 && runtime.Environment.All(p => Text(p.Key) && Text(p.Value)),
            "Preset", "Explicit Release runtime, ordinary GC and bounded environment facts are required.");
        ValidateSchedule(manifest, pilot);
    }

    static void ValidateSchedule(RendererPerfManifest manifest, bool pilot)
    {
        string[] versions = ["empty/1", "village/1", "outdoors/1", "crowd/1", "effects-ui/1"];
        Require(!manifest.Recipes.IsDefault && manifest.Recipes.Length == (pilot ? 1 : 5), "Preset", "Recipe list is incomplete.");
        for (int i = 0; i < manifest.Recipes.Length; i++)
        {
            int scene = pilot ? 3 : i;
            RendererPerfRecipeIdentity recipe = manifest.Recipes[i];
            Require(recipe.SceneId == $"S{scene}" && recipe.Version == versions[scene]
                && recipe.Profile == (scene == 0 ? "empty-native-1080/1" : "renderer-full-native-1080/1"),
                "Preset", "Recipe order, version or profile differs from the preset.");
            Hash(recipe.RecipeSha256, 64);
            Hash(recipe.SettingsSha256, 64);
            Hash(recipe.ShaderSha256, 64);
            Hash(recipe.GeometrySha256, 64);
            Hash(recipe.AssetSha256, 64);
        }
        Require(!manifest.Blocks.IsDefault && manifest.Blocks.Length == (pilot ? 2 : 30), "Preset", "Expanded block list is incomplete.");
        int blockIndex = 0;
        for (int iteration = 0; iteration < (pilot ? 1 : 3); iteration++)
            for (int ordinal = 0; ordinal < (pilot ? 1 : 5); ordinal++)
            {
                int scene = pilot ? 3 : (ordinal + iteration) % 5;
                bool totalFirst = pilot || (scene + iteration) % 2 == 0;
                for (int layout = 0; layout < 2; layout++)
                {
                    RendererPerfLayout expected = (layout == 0) == totalFirst ? RendererPerfLayout.TotalOnly : RendererPerfLayout.PerPass;
                    Require(manifest.Blocks[blockIndex++] == new RendererPerfBlock($"S{scene}", expected, iteration,
                        pilot ? 64 : 192, pilot ? 128 : 256), "Preset", "Expanded block order or frame count differs from the preset.");
                }
            }
        int[] controlIds = pilot ? [1, 4, 5, 7, 9, 11] : Enumerable.Range(1, 12).ToArray();
        Require(!manifest.Controls.IsDefault && manifest.Controls.Length == controlIds.Length, "Preset", "Control list is incomplete.");
        for (int i = 0; i < controlIds.Length; i++)
        {
            RendererPerfControl control = manifest.Controls[i];
            Require(control.Id == controlIds[i] && control.Version == "1" && control.WarmupFrames == (pilot ? 32 : 64)
                && control.MeasuredFrames == (pilot ? 64 : 128), "Preset", "Control order, version or frame count differs from the preset.");
            Hash(control.WorkSha256, 64);
        }
        Require(manifest.TotalFrameCount == (pilot ? 1260 : 16944), "Preset", "Unit frame accounting does not reconcile.");
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
