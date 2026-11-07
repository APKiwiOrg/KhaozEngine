using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    static (RendererPerfCompilerInventory Inventory, ImmutableArray<GpuPipelineCompilerReport> Pipelines, RendererPerfCompilerEvidence Compiler) Correspondence(bool unsupported = false, bool graphics = false)
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        if (graphics)
        {
            using var shaders = device.Factory.CreateShadersFromSpirv("vertex", "fragment");
            using var layout = device.Factory.CreateResourceLayout(new([new("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, true)]));
            using var pipeline = device.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                ShaderSet = shaders,
                ResourceLayouts = [layout],
                VertexLayouts = [new(12, 1, [new("Position", GpuVertexElementFormat.Float3)])],
                BlendAttachments = [GpuBlendAttachment.AlphaBlend],
                Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm)
            });
        }
        else
        {
            using var first = Compute(device); using var repeated = Compute(device);
            using var different = Compute(device, ComputeSource.Replace("= 8", "= 16", StringComparison.Ordinal));
        }
        capture.EndOperation(); var inventory = capture.Freeze();
        var pipelines = inventory.Recipes.Select(r =>
        {
            var production = inventory.Occurrences.First(o => o.RecipeId == r.Id).Production;
            var identity = production with { DiagnosticConfigurationSha256 = GpuDiagnosticIdentityEncoding.Configuration(RendererPerfConstructionFixture.Diagnostic) };
            var exemplar = RendererPerfProtocolTests.CompleteReport().Pipelines[0].Executables[0];
            var executable = exemplar with { Stage = graphics ? "vertex" : "compute", EntryPoint = "main", LocalSizeX = identity.LocalSizeX, LocalSizeY = identity.LocalSizeY, LocalSizeZ = identity.LocalSizeZ };
            return new GpuPipelineCompilerReport(identity, unsupported ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
                unsupported ? "Synthetic capability absence." : null, unsupported ? [] : [executable]);
        }).ToImmutableArray();
        var results = inventory.Recipes.Select(r => new RendererPerfCompilerResult(r.Id, RendererPerfCollectionState.Complete, null, r.Id,
            [.. inventory.Occurrences.Where(o => o.RecipeId == r.Id).Select(o => o.Id)], new(new(64, 256, 16384, 256, 16384, 4),
                unsupported ? new(false, null, null, 0, 0, null, null, true, null, null)
                    : new(true, 1, (ulong)pipelines[r.Id].Executables[0].Statistics.Length, 1, pipelines[r.Id].Executables[0].Statistics.Length, 0, 0, true, null, null), true, null))).ToImmutableArray();
        var compiler = new RendererPerfCompilerEvidence(RendererPerfCollectionState.Complete, null,
            inventory.Counts with
            {
                ReplayAttempts = new(graphics ? 1 : 0, graphics ? 0 : 2),
                Reports = new(graphics ? 1 : 0, graphics ? 0 : 2),
                Executables = pipelines.Sum(p => p.Executables.Length),
                Statistics = pipelines.Sum(p => p.Executables.Sum(e => e.Statistics.Length))
            }, RendererPerfConstructionFixture.Production,
            RendererPerfConstructionFixture.Diagnostic, results, "synthetic-inventory.json", [], []);
        if (unsupported)
        {
            var p = compiler.Production! with { PipelineStatisticsSupported = false };
            var d = p with { Requested = new(true, false) };
            inventory = inventory with
            {
                Occurrences = [.. inventory.Occurrences.Select(o => o with { Production = o.Production with
                { DiagnosticConfigurationSha256 = GpuDiagnosticIdentityEncoding.Configuration(p) } })]
            };
            pipelines = [.. pipelines.Select(row => row with { Identity = row.Identity with { DiagnosticConfigurationSha256 = GpuDiagnosticIdentityEncoding.Configuration(d) } })];
            compiler = compiler with { Production = p, Diagnostic = d };
        }
        return (inventory, pipelines, compiler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pure_correspondence_accepts_synthetic_complete_and_truthful_unsupported_cases(bool unsupported)
    {
        var x = Correspondence(unsupported);
        Assert.Empty(RendererPerfCompilerCorrespondence.Validate(x.Inventory, x.Pipelines, x.Compiler, 9, "conditioning:S3/TotalOnly/0"));
        Assert.Equal(new[] { 0, 1 }, x.Compiler.Results[0].OccurrenceIds);
        Assert.Equal(new[] { 0, 0, 1 }, x.Inventory.Occurrences.Select(o => o.RecipeId));
        _output.WriteLine("synthetic configuration only unsupported={0} occurrences=3 recipes=2 reports=2", unsupported);
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("duplicate-result")]
    [InlineData("missing-occurrence")]
    [InlineData("foreign-occurrence")]
    [InlineData("duplicate-occurrence")]
    [InlineData("foreign-report")]
    [InlineData("duplicate-report")]
    [InlineData("unattempted")]
    [InlineData("counts")]
    [InlineData("native-counts")]
    [InlineData("factory-output")]
    [InlineData("operation")]
    [InlineData("compatibility-configuration")]
    public void Pure_correspondence_refuses_missing_foreign_duplicate_and_incomplete_associations(string fault)
    {
        var (i, p, c) = Correspondence(); var r = c.Results[0];
        switch (fault)
        {
            case "missing-result": c = c with { Results = c.Results.RemoveAt(0) }; break;
            case "duplicate-result": c = c with { Results = c.Results.Add(r) }; break;
            case "missing-occurrence": c = c with { Results = c.Results.SetItem(0, r with { OccurrenceIds = [0] }) }; break;
            case "foreign-occurrence": c = c with { Results = c.Results.SetItem(0, r with { OccurrenceIds = [0, 99] }) }; break;
            case "duplicate-occurrence": c = c with { Results = c.Results.SetItem(0, r with { OccurrenceIds = [0, 0] }) }; break;
            case "foreign-report": c = c with { Results = c.Results.SetItem(0, r with { PipelineIndex = 99 }) }; break;
            case "duplicate-report": c = c with { Results = c.Results.SetItem(1, c.Results[1] with { PipelineIndex = 0 }) }; break;
            case "unattempted": c = c with { Results = c.Results.SetItem(0, r with { State = RendererPerfCollectionState.Incomplete, Read = null }) }; break;
            case "counts": c = c with { Counts = c.Counts with { FactoryAttempts = 1 } }; break;
            case "native-counts": c = c with { Results = c.Results.SetItem(0, r with { Read = r.Read! with { Counts = r.Read.Counts! with { TotalStatistics = 99 } } }) }; break;
            case "factory-output": i = i with { FactoryEvents = i.FactoryEvents.SetItem(1, i.FactoryEvents[1] with { OutputId = 2 }) }; break;
            case "operation": i = i with { Occurrences = i.Occurrences.SetItem(0, i.Occurrences[0] with { OperationOrdinal = 1 }) }; break;
            case "compatibility-configuration":
                p = p.SetItem(0, p[0] with
                {
                    Identity = p[0].Identity with
                    {
                        DiagnosticConfigurationSha256 =
                GpuDiagnosticIdentityEncoding.Text("actual backend-local compatibility settings", "vulkan.diagnostic-configuration/1")
                    }
                }); break;
        }
        Assert.NotEmpty(RendererPerfCompilerCorrespondence.Validate(i, p, c, 9, "conditioning:S3/TotalOnly/0"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("layout")]
    [InlineData("pipeline")]
    [InlineData("artifact")]
    [InlineData("code-generation")]
    [InlineData("local-x")]
    [InlineData("local-y")]
    [InlineData("local-z")]
    [InlineData("entry")]
    [InlineData("adapter")]
    [InlineData("driver")]
    [InlineData("runtime-invocations")]
    [InlineData("debug")]
    [InlineData("validation")]
    [InlineData("recording")]
    [InlineData("compiler-options")]
    [InlineData("capture-flags")]
    public void Correspondence_compares_actual_witness_and_effective_configuration_fields(string fault)
    {
        var (i, p, c) = Correspondence(); var identity = p[0].Identity;
        GpuDiagnosticDigest Change(GpuDiagnosticDigest d) => d with { Sha256 = new string('a', 64) };
        identity = fault switch
        {
            "source" => identity with { SourceSha256 = Change(identity.SourceSha256) },
            "layout" => identity with { LayoutSha256 = Change(identity.LayoutSha256) },
            "pipeline" => identity with { PipelineSha256 = Change(identity.PipelineSha256) },
            "artifact" => identity with { ShaderArtifactSha256 = Change(identity.ShaderArtifactSha256) },
            "code-generation" => identity with { CodeGenerationSha256 = Change(identity.CodeGenerationSha256) },
            "local-x" => identity with { LocalSizeX = 7 },
            "local-y" => identity with { LocalSizeY = 7 },
            "local-z" => identity with { LocalSizeZ = 7 },
            "entry" => identity with { Stages = [identity.Stages[0] with { EntryPoint = "other" }] },
            _ => identity
        };
        if (fault == "artifact")
        {
            var stage = identity.Stages[0] with { ShaderArtifactSha256 = GpuDiagnosticIdentityEncoding.Text("different synthetic compiled bytes", "spirv.bytes/1") };
            identity = identity with { Stages = [stage], ShaderArtifactSha256 = stage.ShaderArtifactSha256 };
            identity = identity with
            {
                PipelineSha256 = identity.PipelineSha256 with
                {
                    Sha256 = RendererPerfConstructionResolvedIdentity.Pipeline(
                i.Recipes[0], identity, identity.LayoutSha256.Sha256!, GpuBackendKind.VulkanNative)
                }
            };
        }
        p = p.SetItem(0, p[0] with { Identity = identity });
        var d = c.Diagnostic!;
        d = fault switch
        {
            "adapter" => d with { AdapterKey = "different actual adapter" },
            "driver" => d with { Driver = "different driver" },
            "runtime-invocations" => d with { RuntimeInvocationsEnabled = true },
            "debug" => d with { DebugLayerEnabled = true },
            "validation" => d with { ValidationEnabled = true },
            "recording" => d with { ObservationOptions = [new("recording", "different")] },
            "compiler-options" => d with { CodeGenerationOptions = [new("optimization", "different")] },
            "capture-flags" => d with { PipelineCaptureFlags = 0 },
            _ => d
        };
        c = c with { Diagnostic = d };
        Assert.NotEmpty(RendererPerfCompilerCorrespondence.Validate(i, p, c, 9, "conditioning:S3/TotalOnly/0"));
    }
    [Theory]
    [InlineData("colour")]
    [InlineData("depth")]
    [InlineData("sample")]
    [InlineData("raster-cull")]
    [InlineData("raster-fill")]
    [InlineData("raster-front")]
    [InlineData("raster-depth-clip")]
    [InlineData("raster-scissor")]
    [InlineData("vertex-stride")]
    [InlineData("vertex-step")]
    [InlineData("vertex-format")]
    [InlineData("blend-enable")]
    [InlineData("blend-source-colour")]
    [InlineData("blend-dest-colour")]
    [InlineData("blend-colour-function")]
    [InlineData("blend-source-alpha")]
    [InlineData("blend-dest-alpha")]
    [InlineData("blend-alpha-function")]
    [InlineData("blend-factor-x")]
    [InlineData("blend-factor-y")]
    [InlineData("blend-factor-z")]
    [InlineData("blend-factor-w")]
    [InlineData("depth-test")]
    [InlineData("depth-write")]
    [InlineData("depth-compare")]
    [InlineData("topology")]
    [InlineData("layout-name")]
    [InlineData("layout-stages")]
    [InlineData("layout-dynamic")]
    public void Detached_state_tampering_cannot_be_hidden_by_rehashing_the_recipe(string field)
    {
        var (i, p, c) = Correspondence(graphics: true);
        Assert.Empty(RendererPerfCompilerCorrespondence.Validate(i, p, c, 9, "conditioning:S3/TotalOnly/0"));
        var r = i.Recipes[0]; var g = r.Graphics!; var v = g.VertexLayouts[0];
        g = field switch
        {
            "colour" => g with { Colour = [GpuPixelFormat.R8UNorm] },
            "depth" => g with { Depth = GpuPixelFormat.D32FloatS8UInt },
            "sample" => g with { SampleCount = 4 },
            "vertex-stride" => g with { VertexLayouts = [v with { Stride = 16 }] },
            "vertex-step" => g with { VertexLayouts = [v with { InstanceStepRate = 0 }] },
            "vertex-format" => g with { VertexLayouts = [v with { Elements = [new("Position", GpuVertexElementFormat.Float4)] }] },
            "topology" => g with { Topology = GpuPrimitiveTopology.LineList },
            _ => g
        };
        var raster = g.Rasterizer;
        if (field.StartsWith("raster-", StringComparison.Ordinal))
            g = g with
            {
                Rasterizer = new(field == "raster-cull" ? GpuFaceCull.Front : raster.CullMode,
                field == "raster-fill" ? GpuPolygonFill.Wireframe : raster.FillMode,
                field == "raster-front" ? GpuFrontFace.CounterClockwise : raster.FrontFace,
                field == "raster-depth-clip" || raster.DepthClipEnabled, field == "raster-scissor" || raster.ScissorTestEnabled)
            };
        var blend = g.BlendAttachments[0];
        if (field.StartsWith("blend-", StringComparison.Ordinal) && !field.StartsWith("blend-factor-", StringComparison.Ordinal))
            g = g with
            {
                BlendAttachments = [new(field != "blend-enable" && blend.BlendEnabled,
                field == "blend-source-colour" ? GpuBlendFactor.One : blend.SourceColorFactor,
                field == "blend-dest-colour" ? GpuBlendFactor.Zero : blend.DestinationColorFactor,
                field == "blend-colour-function" ? GpuBlendFunction.Subtract : blend.ColorFunction,
                field == "blend-source-alpha" ? GpuBlendFactor.One : blend.SourceAlphaFactor,
                field == "blend-dest-alpha" ? GpuBlendFactor.Zero : blend.DestinationAlphaFactor,
                field == "blend-alpha-function" ? GpuBlendFunction.Subtract : blend.AlphaFunction)]
            };
        if (field.StartsWith("blend-factor-", StringComparison.Ordinal))
            g = g with
            {
                BlendFactor = new(field.EndsWith('x') ? 1 : 0, field.EndsWith('y') ? 1 : 0,
                field.EndsWith('z') ? 1 : 0, field.EndsWith('w') ? 1 : 0)
            };
        if (field.StartsWith("depth-", StringComparison.Ordinal))
            g = g with
            {
                DepthStencil = new(field == "depth-test", field == "depth-write",
                field == "depth-compare" ? GpuComparison.Always : g.DepthStencil.Comparison)
            };
        var e = i.Layouts[0].Elements[0];
        if (field.StartsWith("layout-", StringComparison.Ordinal))
            i = i with
            {
                Layouts = [i.Layouts[0] with { Elements = [new(field == "layout-name" ? "Other" : e.Name, e.Kind,
                field == "layout-stages" ? GpuShaderStages.Fragment : e.Stages, field != "layout-dynamic" && e.Dynamic)] }]
            };
        r = r with { Graphics = g };
        r = r with { RecipeSha256 = RendererPerfConstructionEncoding.Digest(r, i.Shaders, i.Layouts) };
        i = i with { Recipes = [r] };
        Assert.NotEmpty(RendererPerfCompilerCorrespondence.Validate(i, p, c, 9, "conditioning:S3/TotalOnly/0"));
    }

    [Fact]
    public void Supported_capture_cannot_be_relabelled_as_complete_capability_absence()
    {
        var (i, p, c) = Correspondence(unsupported: true);
        c = c with { Production = RendererPerfConstructionFixture.Production, Diagnostic = RendererPerfConstructionFixture.Diagnostic };
        i = i with
        {
            Occurrences = [.. i.Occurrences.Select(o => o with { Production = o.Production with
            { DiagnosticConfigurationSha256 = GpuDiagnosticIdentityEncoding.Configuration(c.Production!) } })]
        };
        p = [.. p.Select(row => row with { Identity = row.Identity with { DiagnosticConfigurationSha256 = GpuDiagnosticIdentityEncoding.Configuration(c.Diagnostic!) } })];
        Assert.Contains(RendererPerfCompilerCorrespondence.Validate(i, p, c, 9, "conditioning:S3/TotalOnly/0"), reason => reason.Contains("not-applicable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("code-generation")]
    [InlineData("native-text")]
    public void Coherent_synthetic_rows_cannot_substitute_source_provenance_or_oversized_native_text(string field)
    {
        var (i, p, c) = Correspondence();
        GpuPipelineConstructionIdentity Change(GpuPipelineConstructionIdentity w)
        {
            if (field == "artifact")
            {
                var source = w.Stages[0].SourceSha256;
                w = w with { Stages = [w.Stages[0] with { ShaderArtifactSha256 = source }], ShaderArtifactSha256 = source };
                w = w with
                {
                    PipelineSha256 = w.PipelineSha256 with
                    {
                        Sha256 = RendererPerfConstructionResolvedIdentity.Pipeline(
                    i.Recipes[0], w, w.LayoutSha256.Sha256!, GpuBackendKind.VulkanNative)
                    }
                };
            }
            else if (field == "code-generation") w = w with { CodeGenerationSha256 = w.CodeGenerationSha256 with { Source = "glsl.utf8/1" } };
            return w;
        }
        if (field == "native-text")
        {
            var executable = p[0].Executables[0];
            p = p.SetItem(0, p[0] with
            {
                Executables = [executable with { Statistics = executable.Statistics.SetItem(0,
                executable.Statistics[0] with { Name = new string('x', 257) }) }]
            });
        }
        else
        {
            i = i with { Occurrences = [.. i.Occurrences.Select(o => o.RecipeId == 0 ? o with { Production = Change(o.Production) } : o)] };
            p = p.SetItem(0, p[0] with { Identity = Change(p[0].Identity) });
            if (field == "code-generation")
            {
                var recipe = i.Recipes[0] with { CodeGenerationSha256 = p[0].Identity.CodeGenerationSha256 };
                recipe = recipe with { RecipeSha256 = RendererPerfConstructionEncoding.Digest(recipe, i.Shaders, i.Layouts) };
                i = i with { Recipes = i.Recipes.SetItem(0, recipe) };
            }
        }
        Assert.NotEmpty(RendererPerfCompilerCorrespondence.Validate(i, p, c, 9, "conditioning:S3/TotalOnly/0"));
    }

}
