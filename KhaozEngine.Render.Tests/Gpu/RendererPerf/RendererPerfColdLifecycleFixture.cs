using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed class RendererPerfColdLifecycleFixture : IDisposable
{
    internal readonly RendererPerfReport Template = RendererPerfProtocolTests.CompleteReport() with { Identity = RendererPerfProtocolTests.CompleteReport().Identity with { TimestampPeriodNs = 2.5 } };
    internal readonly RendererPerfConstructionFixture Production = new(), Diagnostic = new();
    internal readonly List<string> Trace = [];
    internal readonly RecordingPublication Publication;
    readonly BackendProviderScope _provider;
    internal readonly Provider BackendProvider;
    internal readonly List<GpuDeviceCreationAttempt> Attempts = [];
    internal readonly System.Threading.CancellationTokenSource Cancellation = new();
    internal long Now = 100;
    internal int CreatedSessions, Frames;
    internal Action<string>? Boundary;
    internal Func<IGpuDevice, RendererPerfBlock, IRenderPassObserver, IRendererPerfNativeWork?>? WorkFactory;
    internal Func<RendererPerfBlock, RendererPerfPassDefinition[]>? PassDefinitions;
    internal Func<IGpuDevice, RendererPerfRunIdentity>? Identify;
    internal Action<RendererPerfNativeSession>? SessionCreated;
    internal RendererPerfColdLifecycleFixture(RendererPerfReport? template = null)
    {
        if (template is not null) Template = template;
        Diagnostic.Configuration = RendererPerfConstructionFixture.Diagnostic;
        Diagnostic.CompilerApi = new ExecutableApi();
        Production.Trace = op => Event("production." + op);
        Production.ColdAction = op => Event("production." + op);
        Diagnostic.ColdAction = op => Event("diagnostic." + op);
        BackendProvider = new Provider(this);
        _provider = new(GpuBackendKind.VulkanNative, BackendProvider);
        Publication = new(this);
    }
    internal void Event(string stage) { Trace.Add(stage); Boundary?.Invoke(stage); }
    internal RendererPerfUnitOutcome Run(IRendererPerfUnitPublication? publication = null, RendererPerfClock? unitClock = null)
    {
        var clock = unitClock ?? new RendererPerfClock(() => Now, 1000);
        var budget = new RendererPerfUnitBudget(clock, 100, 600100, Cancellation.Token);
        var authorization = new RendererPerfAuthorization(Template.Identity.ManifestSha256, Template.Identity.WorkloadSha,
            Template.Identity.AuthorizationSha, Template.Identity.WorkloadSha, 1, [], 1, "linux-vulkan", "gpu-t4-linux", "gpu-hosted-bench.yml");
        return RendererPerfUnit.Run(Template.Manifest, authorization, budget, new Settings(new(false, false)),
            (backend, settings) => { var attempt = GpuDeviceContext.CreateHeadlessAttempt(backend, settings); Attempts.Add(attempt); return attempt; }, device =>
            {
                Event("identify"); return Identify?.Invoke(device) ?? Template.Identity;
            }, (device, capture) => (manifest, block, unitClock) =>
            {
                int ordinal = CreatedSessions++;
                var info = new RendererPerfSessionInfo(Template.Identity, true, true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty);
                var session = new RendererPerfNativeSession(device, info, block, PassDefinitions?.Invoke(block) ?? [], unitClock,
                    (wrapped, observer) => WorkFactory?.Invoke(wrapped, block, observer) ?? new Work(this, wrapped, block),
                    capture: capture, operationOrdinal: ordinal, executionClass: manifest.ExecutionClass);
                SessionCreated?.Invoke(session);
                return session;
            }, publication ?? Publication);
    }
    public void Dispose() { _provider.Dispose(); Cancellation.Dispose(); }
    internal sealed record Settings(GpuPipelineDiagnosticRequest Diagnostics, string? AdapterKey = null) : IGpuHeadlessDiagnosticSettings
    {
        public GpuBackendKind Backend => GpuBackendKind.VulkanNative;
        public IGpuHeadlessDiagnosticSettings WithDiagnostics(GpuPipelineDiagnosticRequest request) => this with { Diagnostics = request };
        public IGpuHeadlessDiagnosticSettings WithAdapter(string adapterKey) => this with { AdapterKey = adapterKey };
    }
    internal sealed class Provider(RendererPerfColdLifecycleFixture owner) : IGpuBackendProvider, IGpuHeadlessDiagnosticProvider
    {
        internal readonly List<Settings> Settings = [];
        internal Exception? Failure;
        public bool IsSupported() => throw new InvalidOperationException("Exact creation must not probe or fall back.");
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request) => throw new NotSupportedException();
        public GpuProviderDevice CreateHeadless() => Failure is { } error ? throw error : new(owner.Production, null, null);
        public IGpuHeadlessDiagnosticSettings ResolveHeadlessSettings(GpuPipelineDiagnosticRequest request) => new Settings(request);
        public GpuProviderDevice CreateHeadless(IGpuHeadlessDiagnosticSettings settings)
        {
            Settings.Add((Settings)settings);
            if (Failure is { } error) throw error;
            owner.Event(settings.Diagnostics.CompilerMetadata ? "create.diagnostic" : "create.production");
            return new(settings.Diagnostics.CompilerMetadata ? owner.Diagnostic : owner.Production, null, null);
        }
    }
    internal sealed class RecordingPublication(RendererPerfColdLifecycleFixture owner) : IRendererPerfUnitPublication
    {
        internal int Prepared, Published, EarlyWrites, Reserved, Committed, Terminated;
        internal bool Ready = true, RefuseReservation;
        internal RendererPerfUnitFailure? Early;
        internal RendererPerfReport? Report;
        RendererPerfReadReservation? _outstanding;
        public RendererPerfCollectionPreparation PrepareCollection(RendererPerfReport report,
            RendererPerfCompilerInventory inventory, RendererPerfUnitBudget budget)
        { Prepared++; owner.Event("prepare"); budget.Check("recording.prepare"); return new(Ready, []); }
        public RendererPerfReadReservation? ReserveReport(GpuBackendKind backend)
        {
            Reserved++; owner.Event("reserve");
            if (_outstanding is not null) throw new InvalidOperationException("Previous reservation was not committed.");
            return _outstanding = RefuseReservation ? null : RendererPerfCompilerReadContract.ForAvailable(backend, 1048576);
        }
        public void CommitReportBytes(RendererPerfReadReservation reservation, int actualBytes)
        {
            owner.Event("commit");
            if (!ReferenceEquals(_outstanding, reservation) || actualBytes < 0 || actualBytes > reservation.MaximumEncodedBytes)
                throw new InvalidOperationException("The reservation instance or byte charge is invalid.");
            Committed++; _outstanding = null;
        }
        public RendererPerfPublicationResult Publish(RendererPerfReport report, RendererPerfCompilerInventory inventory, RendererPerfUnitBudget budget)
        {
            Published++; Report = report; owner.Event("publish");
            if (_outstanding is not null)
            {
                if (report.Publication.State != RendererPerfPublicationState.Failed)
                    throw new InvalidOperationException("An outstanding result reservation requires terminal refusal.");
                Terminated++; _outstanding = null;
            }
            if (report.Publication.OriginalFailure is null) budget.Check("renderer.publication.return");
            // This recording account does not certify files. Task4 owns real publication.
            return new(report with { Assessment = report.Assessment with { ValidForPromotion = false } }, []);
        }
        public RendererPerfFailurePublicationResult WriteFailure(RendererPerfUnitFailure failure, RendererPerfUnitBudget budget)
        { EarlyWrites++; Early = failure; owner.Event("early"); return new(failure, true, []); }
    }
    sealed class Work : IRendererPerfNativeWork
    {
        readonly RendererPerfColdLifecycleFixture _owner;
        readonly RendererPerfBlock _block;
        readonly IGpuTexture _texture;
        readonly IGpuComputeShader _shader;
        readonly IGpuResourceLayout _layout;
        readonly IGpuComputePipeline _pipeline;
        internal Work(RendererPerfColdLifecycleFixture owner, IGpuDevice device, RendererPerfBlock block)
        {
            _owner = owner; _block = block;
            _texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget));
            Target = device.Factory.CreateFramebuffer(null, _texture);
            // Both recipes are actual captured factory calls, including created-but-unused occurrences.
            string source = "#version 450\nlayout(local_size_x=" + (block.SceneId.StartsWith("control:", StringComparison.Ordinal) ? "4" : "8") + ") in; void main() {}";
            _shader = device.Factory.CreateComputeShaderFromSpirv(source);
            _layout = device.Factory.CreateResourceLayout(new GpuResourceLayoutDescription([]));
            _pipeline = device.Factory.CreateComputePipeline(new(_shader, _layout));
        }
        public IGpuFramebuffer Target { get; }
        public Color ClearColor => new(0, 0, 0, 1);
        public string ControlStateSha256 => new('b', 64);
        public RendererPerfRecipeReport? Recipe => _owner.Template.Manifest.Recipes.FirstOrDefault(r => r.SceneId == (_block.SceneId == "conditioning:S3" ? "S3" : _block.SceneId)) is { } recipe
            ? new(recipe, "injected native operations", new('b', 64), ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, long>.Empty, ImmutableDictionary<string, RendererPerfMetric>.Empty) : null;
        public void PrepareCold(int firstFrameIndex) { }
        public void Prepare(int frameIndex) { _owner.Frames++; _owner.Event("frame"); }
        public void Record(IGpuCommandList commands, int frameIndex) { }
        public void Dispose()
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            RendererPerfFailures.Release(failures, "renderer.session.dispose.pipeline.0", _pipeline.Dispose);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.layout.1", _layout.Dispose);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.shader.2", _shader.Dispose);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.target.3", Target.Dispose);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.texture.4", _texture.Dispose);
            if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
        }
    }
    internal sealed unsafe class ExecutableApi : IVulkanPipelineExecutableApi
    {
        internal Exception? Failure;
        internal Action? AfterPropertyCount;
        internal int Calls;
        internal ShaderStageFlags Stages = ShaderStageFlags.ComputeBit;
        public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        {
            Calls++;
            if (Failure is { } error) throw error;
            count = 1;
            if (values.IsEmpty) AfterPropertyCount?.Invoke();
            if (!values.IsEmpty)
            {
                fixed (PipelineExecutablePropertiesKHR* target = values)
                {
                    target[0].Stages = Stages; target[0].SubgroupSize = 32;
                    target[0].Name[0] = (byte)'x'; target[0].Description[0] = (byte)'x';
                }
            }
            return Result.Success;
        }
        public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        { Calls++; count = 0; return Result.Success; }
    }
}
