using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfBlockModels(RendererPerfBlock Block, ImmutableArray<RendererPassWorkModel> Models);
internal static class RendererPerfArtifactProducers
{
    internal static string Schema(string path) => path switch
    {
        "manifest.json" => "khaozengine.renderer-perf-unit/1",
        "authorization.json" => "renderer-authorization/1",
        "runtime-admission.json" => "renderer-runtime-admission/1",
        "native-admission.json" => "renderer-native-admission/1",
        "shader-sources.json" => "renderer-shader-sources/1",
        "control-corpus.json" => "renderer-control-corpus/1",
        "control-definitions.json" => "renderer-control-definitions/1",
        "frozen-layouts.json" => "renderer-frozen-layouts/1",
        "host-telemetry.jsonl" => "renderer-host-telemetry/1",
        "work-models.json" => "renderer-work-models/1",
        _ when path.EndsWith("-recipe.json", StringComparison.Ordinal) => "khaozengine.renderer-perf-frozen-recipe/2",
        _ => throw new ArgumentException("No registered producer schema for " + path)
    };
    internal static byte[] WorkModels(RendererPerfReport report) => JsonSerializer.SerializeToUtf8Bytes(report.Blocks.Select(b =>
        new RendererPerfBlockModels(b.Block, b.Measured.IsEmpty ? [] : RendererPerfWorkModels.ForFrame(b.Measured[0],
            b.Block.SceneId + "/" + b.Block.Layout + ", first retained measured frame"))).ToArray(), RendererPerfManifest.JsonOptions);
    internal static void Verify(RendererPerfExpectedSidecar expected, byte[] bytes, RendererPerfReport report)
    {
        if (expected.Schema != Schema(expected.Path) || expected.Counts != RendererPerfSidecarCounts.Empty)
            throw new InvalidOperationException("Noncompiler producer kind/schema/counts disagree.");
        switch (expected.Path)
        {
            case "manifest.json":
                var manifest = RendererPerfManifest.Parse(bytes);
                if (RendererPerfRecipe.Hash(bytes) != report.Identity.ManifestSha256 || manifest.UnitId != report.Manifest.UnitId
                    || manifest.WorkloadSha != report.Manifest.WorkloadSha || !Same(manifest, report.Manifest)) Refuse();
                break;
            case "authorization.json":
                var auth = Read<RendererPerfAuthorization>();
                if (auth.ManifestSha256 != report.Identity.ManifestSha256 || auth.WorkloadSha != report.Identity.WorkloadSha
                    || auth.AuthorizationSha != report.Identity.AuthorizationSha || auth.RunAttempt != 1) Refuse();
                break;
            case "runtime-admission.json":
                var runtime = Read<RuntimeAdmission>();
                if (runtime.RuntimeVersion != report.Identity.Runtime || runtime.GcMode != report.Identity.GcMode
                    || !Same(runtime.Environment, report.Identity.Environment)) Refuse();
                break;
            case "native-admission.json":
                var native = Read<NativeAdmission>();
                if (!Same(native.Identity, report.Identity) || native.Facts.Vendor != report.Identity.VendorId
                    || native.Facts.Device != report.Identity.DeviceId || native.Facts.Driver != report.Identity.Driver
                    || native.Frequency <= 0 || native.ColdStartTicks < 0 || string.IsNullOrWhiteSpace(native.ClockProbe)) Refuse();
                break;
            case "shader-sources.json":
                var sources = Read<SortedDictionary<string, string>>();
                foreach (var hash in sources.Values) RendererPerfManifest.Hash(hash, 64);
                if (sources.Count == 0 || report.Manifest.Recipes.Any(r => r.ShaderSha256 != RendererPerfRecipe.Digest(new SortedDictionary<string, string>(sources, StringComparer.Ordinal)))) Refuse();
                break;
            case "control-corpus.json":
                var corpus = Read<ImmutableArray<RendererPerfControlShaderArtifact>>();
                if (corpus.Length != 6 || corpus.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != 6) Refuse();
                foreach (var c in corpus)
                {
                    foreach (var hash in new[] { c.SourceSha256, c.SpirvSha256, c.HlslSha256, c.MslSha256 }) RendererPerfManifest.Hash(hash, 64);
                    uint localX = c.Name switch
                    {
                        "chain.comp" => 1,
                        "stream.comp" => 256,
                        "fma.comp" or "dependent.comp" => 64,
                        "fullscreen" or "depth-read" => 0,
                        _ => uint.MaxValue
                    };
                    if (localX == uint.MaxValue || c.LocalX != localX || c.Stores <= 0
                        || c.FmaInstructions < 0 || c.Divides < 0 || c.Roots < 0) Refuse();
                }
                break;
            case "control-definitions.json":
                var controls = Read<ImmutableArray<RendererPerfControlFacts>>();
                if (!controls.Select(c => c.Id).SequenceEqual(report.Manifest.Controls.Select(c => c.Id))) Refuse();
                for (int n = 0; n < controls.Length; n++)
                    if (controls[n].WorkSha256 != report.Manifest.Controls[n].WorkSha256 || !Same(controls[n], RendererPerfControls.Definition(controls[n].Id))) Refuse();
                break;
            case "frozen-layouts.json":
                var layouts = Read<Dictionary<string, RendererPerfPassDefinition[]>>();
                if (!layouts.Keys.Order(StringComparer.Ordinal).SequenceEqual(report.Manifest.Recipes.Select(r => r.SceneId).Order(StringComparer.Ordinal))) Refuse();
                foreach (var pair in layouts)
                {
                    var layout = pair.Value;
                    if (layout.Length > 255 || layout.Select(p => (p.Id, p.Occurrence)).Distinct().Count() != layout.Length) Refuse();
                    foreach (var definition in layout)
                        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Occurrence < 0 || definition.Parent < -1
                            || definition.Parent >= layout.Length) Refuse();
                    foreach (var frame in report.Blocks.Where(b => b.Block.SceneId == pair.Key && b.Block.Layout == RendererPerfLayout.PerPass)
                        .SelectMany(b => b.Warmup.Concat(b.Measured)))
                    {
                        if (frame.Passes.Length != layout.Length) Refuse();
                        for (int n = 0; n < layout.Length; n++)
                        {
                            var d = layout[n]; var p = frame.Passes[n];
                            RendererPerfPassDefinition? parent = d.Parent < 0 ? null : layout[d.Parent];
                            if (d.Id != p.Id || d.Occurrence != p.Occurrence || d.DeviceOnly != p.DeviceOnly || d.BeginMarker != p.BeginMarker
                                || d.EndMarker != p.EndMarker || parent?.Id != p.ParentId || parent?.Occurrence != p.ParentOccurrence) Refuse();
                        }
                    }
                }
                break;
            case "host-telemetry.jsonl":
                RendererPerfHostTelemetry.Verify(bytes, report);
                break;
            case "work-models.json":
                var models = Read<ImmutableArray<RendererPerfBlockModels>>();
                if (!models.Select(m => m.Block).SequenceEqual(report.Blocks.Select(b => b.Block))) Refuse();
                foreach (var row in models) RendererPerfReportJson.ValidateWorkModels(row.Models);
                using (var actual = RendererPerfManifest.ReadDocument(bytes, RendererPerfReportJson.MaximumBytes))
                using (var rebuilt = RendererPerfManifest.ReadDocument(WorkModels(report), RendererPerfReportJson.MaximumBytes))
                    if (!JsonElement.DeepEquals(actual.RootElement, rebuilt.RootElement)) Refuse();
                break;
            default:
                using (var recipe = RendererPerfManifest.ReadDocument(bytes, RendererPerfReportJson.MaximumBytes))
                {
                    var root = recipe.RootElement;
                    if (root.GetProperty("Schema").GetString() != expected.Schema
                        || !report.Manifest.Recipes.Any(r => expected.Path == r.SceneId + "-recipe.json")) Refuse();
                    if (root.EnumerateObject().Count() != 3 || root.GetProperty("Authored").GetProperty("Schema").GetString() != "khaozengine.renderer-perf-recipe/1") Refuse();
                    var identity = report.Manifest.Recipes.Single(r => expected.Path == r.SceneId + "-recipe.json");
                    var authored = root.GetProperty("Authored");
                    if (authored.GetProperty("Id").GetString() + "/" + authored.GetProperty("Version").GetString() != identity.Version
                        || authored.GetProperty("Profile").GetString() != identity.Profile
                        || RendererPerfRecipe.Digest(JsonNode.Parse(authored.GetRawText())) != identity.RecipeSha256) Refuse();
                    foreach (var family in root.GetProperty("DerivedByPlatform").EnumerateObject())
                    {
                        var derived = family.Value.Deserialize<RendererPerfDerivedDigests>(new JsonSerializerOptions(RendererPerfManifest.JsonOptions) { PropertyNamingPolicy = null }) ?? throw new JsonException("Missing recipe digests.");
                        RendererPerfManifest.Hash(derived.GeometrySha256, 64); RendererPerfManifest.Hash(derived.SequenceSha256, 64);
                        RendererPerfManifest.Hash(derived.ConstructionSha256, 64);
                    }
                }
                break;
        }
        T Read<T>() => RendererPerfManifest.ReadJson<T>(bytes, RendererPerfReportJson.MaximumBytes);
        static void Refuse() => throw new InvalidOperationException("Sidecar decoded identity, schema or retained content disagrees with its producer.");
    }
    static bool Same<T>(T a, T b) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a, RendererPerfManifest.JsonOptions),
        JsonSerializer.SerializeToElement(b, RendererPerfManifest.JsonOptions));
    sealed record RuntimeAdmission(string RuntimeVersion, string GcMode, ImmutableDictionary<string, string> Environment);
    sealed record NativeAdmission(RendererPerfRunIdentity Identity, RendererPerfNativeFacts Facts, long ColdStartTicks, long Frequency, string ClockProbe);
}
