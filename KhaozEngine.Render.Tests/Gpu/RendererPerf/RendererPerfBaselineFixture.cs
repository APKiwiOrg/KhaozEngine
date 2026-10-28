using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Full schedules and real publication/accounting, with explicitly synthetic compiled-byte witnesses.
public sealed class RendererPerfBaselineFixture : IDisposable
{
    internal string Root { get; } = Environment.GetEnvironmentVariable("KE_RENDERER_BASELINE_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "renderer-baseline-" + Guid.NewGuid().ToString("N"));
    readonly RendererPerfBaselineLeg _linux, _windows;
    ImmutableArray<RendererPerfBaselineLeg> _profiles;
    public RendererPerfBaselineFixture()
    {
        _linux = Create("linux-vulkan", "full-linux", "full", 17);
        _windows = Create("windows-d3d11", "full-windows", "full", 23);
    }
    internal string Output() => Path.Combine(Root, Guid.NewGuid().ToString("N"));
    internal RendererPerfBaselineRequest Request() => new("khaozengine.renderer-baseline-request/1", Output(), [_linux, _windows]);
    internal ImmutableArray<RendererPerfBaselineLeg> ProfileLegs()
        => _profiles.IsDefault ? _profiles = [Create("linux-vulkan", "profiles-linux", "water-optics-v2", 38),
            Create("windows-d3d11", "profiles-windows", "water-optics-v2", 53)] : _profiles;
    internal RendererPerfBaselineLeg Variant(string fault)
        => Create("linux-vulkan", fault, fault == "pilot" ? "pilot" : fault == "rehearsal" ? "rehearsal" : "full", 17, fault);

    internal RendererPerfBaselineLeg Censored(string leg)
        => Create(leg, "censored-" + leg, "full", 17, "censored");

    internal RendererPerfBaselineLeg Create(string leg, string unit, string preset, int billed, string? fault = null)
    {
        bool rehearsal = preset.StartsWith("rehearsal", StringComparison.Ordinal);
        bool profiles = preset.Contains("water-optics-v2", StringComparison.Ordinal);
        var report = profiles ? RendererPerfProfileFixture.Report(preset, leg) : RendererPerfAnalysisFixture.Report(preset == "full");
        var manifestJson = profiles ? RendererPerfWaterOpticsPresetTests.ManifestJson(preset, leg)
            : rehearsal ? RendererPerfRehearsalManifestTests.ManifestJson() : RendererPerfProtocolTests.ManifestJson(preset, leg);
        manifestJson["unitId"] = unit;
        if (fault == "workload") manifestJson["workloadSha"] = new string('d', 40);
        if (fault == "baseline-source") manifestJson["baselineMainSha"] = new string('e', 40);
        if (fault == "censored")
            foreach (var control in manifestJson["controls"]!.AsArray())
                if (control!["id"]!.GetValue<int>() is >= 1 and <= 3)
                    control["workSha256"] = RendererPerfControls.Definition(control["id"]!.GetValue<int>()).WorkSha256;
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson.ToJsonString());
        var manifest = RendererPerfManifest.Parse(manifestBytes);
        report = report with
        {
            Manifest = manifest,
            Identity = report.Identity with
            {
                UnitId = unit,
                ManifestSha256 = RendererPerfRecipe.Hash(manifestBytes),
                WorkloadSha = manifest.WorkloadSha,
                BaselineMainSha = manifest.BaselineMainSha,
                Backend = manifest.Execution.Backend,
                Os = leg == "linux-vulkan" ? "Linux" : "Windows",
                SoftwareAdapter = rehearsal,
                VendorId = rehearsal ? 0x10005u : 0x10deu,
                DeviceId = rehearsal ? 0u : 0x1eb8u,
                Adapter = rehearsal ? "llvmpipe" : "NVIDIA T4",
                Driver = rehearsal ? "Mesa driver" : "driver-1"
            },
            Assessment = report.Assessment with
            {
                ValidForPromotion = !rehearsal && fault != "failed-promotion",
                WorkModels = [RendererPerfFloorModelTests.Model(1, 1, 0) with { PassId = "opaque" }],
                Reasons = rehearsal ? [RendererPerfRehearsalPolicy.Reason] : fault == "failed-promotion" ? ["held failed promotion"] : []
            },
            Blocks = [.. report.Blocks.Select(b => profiles || b.Block.Layout == RendererPerfLayout.TotalOnly ? b : b with
            {
                Measured = [.. b.Measured.Select(f => f with
                { Passes = [new("opaque", 0, null, null, 1, 2, 40, RendererPerfAnalysisFixture.Value(40), false)] })]
            })]
        };
        if (fault == "censored") report = RendererPerfControlTimingFixture.Execute(report);
        if (fault == "failed-publication") report = RendererPerfAnalysisFixture.Failed(report);
        Assert.Equal(manifest.TotalFrameCount, RendererPerfCompactReportTests.Frames(report).Count());
        using var capture = new RendererPerfCompilerPublicationFixture(report);
        var inventory = capture.Inventory;
        bool windows = leg == "windows-d3d11" || fault == "native-compiler";
        var backend = windows ? GpuBackendKind.Direct3D11Native : GpuBackendKind.VulkanNative;
        var production = RendererPerfCompilerFixture.Configuration with { Backend = backend, PipelineStatisticsSupported = false, Driver = report.Identity.Driver };
        var diagnostic = production with { Requested = new(true, false), CompilerMetadataEnabled = windows };
        var shaders = inventory.Shaders.Select(s => s with
        { Stages = [.. s.Stages.Select(t => t with { Stage = windows && t.Stage == "fragment" ? "pixel" : t.Stage })] }).ToImmutableArray();
        var recipes = inventory.Recipes.Select(r => r with { CodeGenerationSha256 = GpuDiagnosticIdentityEncoding.CodeGeneration(backend, []) })
            .Select(r => r with { RecipeSha256 = RendererPerfConstructionEncoding.Digest(r, shaders, inventory.Layouts) }).ToImmutableArray();
        GpuPipelineConstructionIdentity Witness(RendererPerfPipelineOccurrence occurrence, GpuEffectiveDiagnosticConfiguration configuration)
        {
            var recipe = recipes[occurrence.RecipeId];
            var stages = shaders[recipe.ShaderId].Stages.Select(s => new GpuPipelineStageIdentity(s.Stage, s.EntryPoint,
                GpuDiagnosticIdentityEncoding.Text(s.Source, "glsl.utf8/1"), null,
                GpuDiagnosticIdentityEncoding.Text("synthetic artifact " + s.Source, windows ? "dxbc.bytes/1" : "spirv.bytes/1"))).ToImmutableArray();
            string layout = RendererPerfConstructionResolvedIdentity.Layout(recipe, inventory.Layouts);
            string prefix = windows ? "d3d11" : "vulkan";
            var witness = occurrence.Production with
            {
                SourceSha256 = GpuDiagnosticIdentityEncoding.Stages("source", stages),
                ShaderArtifactSha256 = GpuDiagnosticIdentityEncoding.Stages("artifact", stages),
                Stages = stages,
                LayoutSha256 = GpuDiagnosticIdentityEncoding.Known(layout, prefix + ".layout/1"),
                CodeGenerationSha256 = recipe.CodeGenerationSha256,
                DiagnosticConfigurationSha256 = GpuDiagnosticIdentityEncoding.Configuration(configuration)
            };
            return witness with
            {
                PipelineSha256 = GpuDiagnosticIdentityEncoding.Known(
                RendererPerfConstructionResolvedIdentity.Pipeline(recipe, witness, layout, backend), prefix + ".pipeline/1")
            };
        }
        inventory = inventory with
        {
            Shaders = shaders,
            Recipes = recipes,
            Occurrences = [.. inventory.Occurrences.Select(o => o with { Production = Witness(o, production) })]
        };
        report = capture.Report with { Compiler = capture.Report.Compiler with { Production = production } };
        string directory = Path.Combine(Root, unit);
        var writer = RendererPerfPublicationWriter.Files(directory);
        Assert.True(writer.PrepareCollection(report, inventory, capture.Budget).Ready);
        var pipelines = ImmutableArray.CreateBuilder<GpuPipelineCompilerReport>();
        var results = ImmutableArray.CreateBuilder<RendererPerfCompilerResult>();
        foreach (var recipe in inventory.Recipes)
        {
            var occurrences = inventory.Occurrences.Where(o => o.RecipeId == recipe.Id).ToArray();
            var pipeline = new GpuPipelineCompilerReport(Witness(occurrences[0], diagnostic), GpuDiagnosticStatus.Unsupported,
                "Synthetic compiler statistics unavailable.", []);
            var reservation = writer.ReserveReport(backend)!;
            var counts = new GpuPipelineCompilerReadCounts(false, null, null, 0, 0, null, null, true, null, null);
            var read = new GpuPipelineCompilerReadResult(reservation.Request, pipeline, true, counts, null, []);
            writer.CommitReportBytes(reservation, RendererPerfCompilerReadContract.EncodedResultBytes(read));
            results.Add(new(recipe.Id, RendererPerfCollectionState.Complete, null, pipelines.Count,
                [.. occurrences.Select(o => o.Id)], new(reservation.Request, counts, true, null)));
            pipelines.Add(pipeline);
        }
        var kinds = new RendererPerfKindCounts(recipes.Count(r => r.Kind == RendererPerfPipelineKind.Graphics), recipes.Count(r => r.Kind == RendererPerfPipelineKind.Compute));
        var published = writer.Publish(report with
        {
            Pipelines = pipelines.ToImmutable(),
            Compiler = report.Compiler with
            {
                State = RendererPerfCollectionState.Complete,
                Reason = null,
                Diagnostic = diagnostic,
                Results = results.ToImmutable(),
                Counts = inventory.Counts with { ReplayAttempts = kinds, Reports = kinds }
            }
        }, inventory, capture.Budget);
        Assert.Empty(published.Failures);
        string name = unit + ".json";
        if (fault == "failed-publication") name = unit + ".partial.json";
        if (fault == "missing-sidecar") File.Delete(Path.Combine(directory, published.Report.Compiler.InventoryPath!));
        return new(leg, directory, name, "https://github.com/APKiwiOrg/KhaozEngine/actions/runs/123/attempts/1", billed,
            report.Identity.ManifestSha256, report.Identity.WorkloadSha, report.Identity.WorkflowSha, report.Identity.HarnessSha256);
    }
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("KE_RENDERER_BASELINE_EVIDENCE") is null && Directory.Exists(Root))
            RendererPerfFixtureDirectory.Delete(Root);
    }
}
