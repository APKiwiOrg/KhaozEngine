using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Real capture/replay and native count/fill owners, with inert GPU resources only.
internal sealed class RendererPerfCompilerPublicationFixture : IDisposable
{
    internal readonly string Directory;
    readonly bool _ownsDirectory;
    internal readonly RendererPerfReport Report;
    internal readonly RendererPerfCompilerInventory Inventory;
    internal readonly RendererPerfConstructionFixture Diagnostic = new();
    internal readonly RendererPerfUnitBudget Budget;
    internal readonly System.Threading.CancellationTokenSource Cancellation = new();
    internal long Now = 100;
    internal Action<string>? Boundary;
    internal RendererPerfCompilerPublicationFixture(RendererPerfReport? report = null, bool unsupported = false, string? directory = null, bool splitSources = false)
    {
        _ownsDirectory = directory is null;
        Directory = directory ?? Path.Combine(Path.GetTempPath(), "renderer-cold-publication-" + Guid.NewGuid().ToString("N"));
        Report = report ?? RendererPerfAnalysisFixture.Report();
        Budget = new(new(() => Now, 1000), 100, 1500100, Cancellation.Token);
        var production = new RendererPerfConstructionFixture();
        if (unsupported) production.Configuration = production.Configuration with { PipelineStatisticsSupported = false };
        var capture = new RendererPerfConstructionCapture(Report.Manifest);
        for (int op = 0; op < 1 + Report.Manifest.Blocks.Length + Report.Manifest.Controls.Length; op++)
        {
            capture.BeginOperation(op);
            var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000);
            var device = RendererPerfDevice.Wrap(production, recorder, capture);
            using var layout = device.Factory.CreateResourceLayout(new([new("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute | GpuShaderStages.Vertex)]));
            using var shader = device.Factory.CreateComputeShaderFromSpirv("#version 450\nlayout(local_size_x=8) in; void main() {}" + (splitSources ? "\n// operation " + op : ""));
            using var a = device.Factory.CreateComputePipeline(new(shader, layout));
            using var unused = device.Factory.CreateComputePipeline(new(shader, layout));
            using var graphics = device.Factory.CreateShadersFromSpirv("#version 450\nvoid main(){ gl_Position=vec4(0,0,0,1); }", "#version 450\nlayout(location=0) out vec4 c; void main(){ c=vec4(1); }");
            using var pipeline = device.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                ShaderSet = graphics,
                ResourceLayouts = [layout],
                VertexLayouts = [new(12, 1, [new("Position", GpuVertexElementFormat.Float3)])],
                BlendFactor = new(.1f, .2f, .3f, .4f),
                BlendAttachments = [GpuBlendAttachment.AlphaBlend],
                Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm)
            });
            using var commands = device.Factory.CreateCommandList();
            commands.SetComputePipeline(a); commands.SetPipeline(pipeline);
            capture.EndOperation();
        }
        Inventory = capture.Freeze();
        Report = Report with
        {
            Pipelines = [],
            Compiler = new(RendererPerfCollectionState.Incomplete,
            "Cold collection has not completed.", Inventory.Counts, production.Configuration, null,
            [.. Inventory.Recipes.Select(r => new RendererPerfCompilerResult(r.Id, RendererPerfCollectionState.Incomplete,
                "Not attempted.", null, [.. Inventory.Occurrences.Where(o => o.RecipeId == r.Id).Select(o => o.Id)], null))], null, [], [])
        };
        production.Dispose();
        Diagnostic.Configuration = RendererPerfConstructionFixture.Diagnostic;
        if (unsupported) Diagnostic.Configuration = production.Configuration with { Requested = new(true, false) };
        var api = new ScalarsApi();
        Diagnostic.CompilerApi = api;
        int previousGraphics = 0;
        Diagnostic.ColdAction = stage =>
        {
            if (stage == "pipeline")
            {
                api.Stages = Diagnostic.GraphicsAttempts > previousGraphics ? ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit : ShaderStageFlags.ComputeBit;
                previousGraphics = Diagnostic.GraphicsAttempts;
            }
            Boundary?.Invoke(stage);
        };
    }
    internal RendererPerfColdResult Collect(IRendererPerfUnitPublication publication, RendererPerfUnitBudget? budget = null)
    {
        using var provider = new BackendProviderScope(GpuBackendKind.VulkanNative, new Provider(Diagnostic));
        using var context = GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative);
        return RendererPerfColdCollector.Collect(Inventory, Report.Manifest.Schedule, context, Report.Compiler.Production!, budget ?? Budget,
            publication.ReserveReport, publication.CommitReportBytes);
    }
    internal RendererPerfPublicationResult Publish(RendererPerfPublicationWriter writer)
    {
        var preparation = writer.PrepareCollection(Report, Inventory, Budget);
        if (!preparation.Ready) throw new GpuDiagnosticFailureException(preparation.Failures);
        var cold = Collect(writer);
        if (!cold.Failures.IsEmpty) throw new GpuDiagnosticFailureException(cold.Failures);
        return writer.Publish(Report with { Compiler = cold.Compiler, Pipelines = cold.Pipelines }, Inventory, Budget);
    }
    sealed class Provider(IGpuDevice device) : IGpuBackendProvider
    {
        public bool IsSupported() => true;
        public GpuProviderDevice CreateHeadless() => new(device, null, null);
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request) => throw new NotSupportedException();
    }
    public void Dispose()
    {
        Cancellation.Dispose(); Diagnostic.Dispose();
        if (_ownsDirectory && System.IO.Directory.Exists(Directory)) RendererPerfFixtureDirectory.Delete(Directory);
    }
    internal sealed unsafe class ScalarsApi : IVulkanPipelineExecutableApi
    {
        internal ShaderStageFlags Stages = ShaderStageFlags.ComputeBit;
        public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        {
            count = 1;
            if (!values.IsEmpty)
            {
                fixed (PipelineExecutablePropertiesKHR* p = values)
                { p[0].Stages = Stages; p[0].SubgroupSize = 32; p[0].Name[0] = (byte)'x'; p[0].Description[0] = (byte)'x'; }
            }
            return Result.Success;
        }
        public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        {
            count = 5;
            if (!values.IsEmpty)
            {
                fixed (PipelineExecutableStatisticKHR* p = values)
                {
                    for (int i = 0; i < 5; i++)
                    { p[i].Name[0] = (byte)('a' + i); p[i].Description[0] = (byte)'d'; }
                    p[0].Format = PipelineExecutableStatisticFormatKHR.Bool32Khr; p[0].Value.B32 = true;
                    p[1].Format = PipelineExecutableStatisticFormatKHR.Int64Khr; p[1].Value.I64 = long.MinValue;
                    p[2].Format = PipelineExecutableStatisticFormatKHR.Int64Khr; p[2].Value.I64 = long.MaxValue;
                    p[3].Format = PipelineExecutableStatisticFormatKHR.Uint64Khr; p[3].Value.U64 = ulong.MaxValue;
                    p[4].Format = PipelineExecutableStatisticFormatKHR.Float64Khr; p[4].Value.F64 = 1.25;
                }
            }
            return Result.Success;
        }
    }
}
