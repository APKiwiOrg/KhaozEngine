using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoveragePipelineStatisticsDevice(string Backend, string Adapter, string? Driver, uint? Vendor, uint? Device);
internal sealed record CoveragePipelineStatisticsKernel(string Name, string Phase, CoverageDispatchGroups LocalSize,
    CoverageDispatchGroups Groups, GpuPipelineCompilerReport? Compiler = null);
internal sealed record CoveragePipelineStatisticsPlan(string Fixture, string TopologyFingerprint, int Actors,
    ImmutableArray<CoveragePipelineStatisticsKernel> Kernels);
internal sealed record CoveragePipelineStatisticsDocument(string Schema, bool DiagnosticOnly, string TimingCaveat,
    GpuDiagnosticStatus Status, string? Reason, CoveragePipelineStatisticsDevice Device,
    GpuPipelineCompilerReport? CapabilityProbe, CoveragePipelineStatisticsPlan? DispatchPlan,
    ImmutableArray<CoveragePipelineStatisticsKernel> Kernels, string StripesVariant);

/// <summary>Cold compiler evidence only. No frame, dispatch, timestamp or occupancy measurement is performed.</summary>
internal static class CoveragePipelineStatisticsCapture
{
    internal const string Schema = "khaozengine.coverage-pipeline-statistics/1";
    const string Caveat = "Capture-enabled compilation is a separate diagnostic configuration. No timings or achieved occupancy are measured.";
    const string Stripes = "Skipped. The stripes prototype changes the shader dependency graph, work ABI and resource layout. It cannot be substituted into this option A pipeline set without importing that implementation.";
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + Environment.NewLine;

    internal static CoveragePipelineStatisticsDocument Capture(IGpuDevice device)
    {
        var caps = device.Capabilities;
        var identity = new CoveragePipelineStatisticsDevice(device.Backend.ToString(), caps.DeviceName,
            caps.DriverVersion, caps.PciVendorId, caps.PciDeviceId);
        CoveragePipelineStatisticsDocument Result(GpuDiagnosticStatus status, string? reason,
            GpuPipelineCompilerReport? probe, CoveragePipelineStatisticsPlan? plan = null,
            ImmutableArray<CoveragePipelineStatisticsKernel> kernels = default)
            => new(Schema, true, Caveat, status, reason, identity, probe, plan,
                kernels.IsDefault ? [] : kernels, Stripes);

        if (Environment.GetEnvironmentVariable("KE_VULKAN_PIPELINE_DIAGNOSTICS") != "1")
            throw new InvalidOperationException("Enable KE_VULKAN_PIPELINE_DIAGNOSTICS before opening the diagnostic device.");
        if (device.Backend != GpuBackendKind.VulkanNative)
            return Result(GpuDiagnosticStatus.Unsupported, "This capture requires the Vulkan pipeline executable statistics backend.", null);

        // A tiny pipeline tests the extension before compiling the expensive coverage programs on an unsupported device.
        using var probeLayout = device.Factory.CreateResourceLayout(new GpuResourceLayoutDescription([]));
        using var probeShader = device.Factory.CreateComputeShaderFromSpirv("#version 450\nlayout(local_size_x = 64) in;\nvoid main() {}\n");
        using var probePipeline = device.Factory.CreateComputePipeline(new(probeShader, probeLayout));
        if (probePipeline is not IGpuPipelineDiagnostics probeDiagnostics)
            return Result(GpuDiagnosticStatus.Unsupported, "The Vulkan compute pipeline has no compiler diagnostics seam.", null);
        var probe = probeDiagnostics.ReadCompilerReport();
        if (probe.Status != GpuDiagnosticStatus.Available) return Result(probe.Status, probe.Reason, probe);

        var plan = Plan();
        using var pipeline = new CoverageGpuPipeline(device);
        var native = pipeline.Pipelines.ToDictionary(p => p.Phase, p => p.Pipeline);
        var kernels = ImmutableArray.CreateBuilder<CoveragePipelineStatisticsKernel>(plan.Kernels.Length);
        foreach (var kernel in plan.Kernels)
        {
            var phase = Enum.Parse<CoverageGpuPhase>(kernel.Phase);
            if (native[phase] is not IGpuPipelineDiagnostics diagnostics)
                throw new InvalidOperationException($"{kernel.Name} has no compiler diagnostics seam.");
            kernels.Add(kernel with { Compiler = diagnostics.ReadCompilerReport() });
        }
        var reports = kernels.Select(k => k.Compiler!).ToArray();
        var status = reports.Any(r => r.Status == GpuDiagnosticStatus.Invalid) ? GpuDiagnosticStatus.Invalid
            : reports.Any(r => r.Status == GpuDiagnosticStatus.Unsupported) ? GpuDiagnosticStatus.Unsupported
            : GpuDiagnosticStatus.Available;
        string? reason = status == GpuDiagnosticStatus.Available ? null : string.Join(" | ",
            kernels.Where(k => k.Compiler!.Status != GpuDiagnosticStatus.Available).Select(k => k.Name + ": " + k.Compiler!.Reason));
        return Result(status, reason, probe, plan, kernels.MoveToImmutable());
    }

    internal static CoveragePipelineStatisticsPlan Plan()
    {
        var geometry = CoverageCostGeometry.Build(CoverageCostFixtureMode.Typical);
        var host = CoverageGpuUpload.Host(geometry.Refinement, 2);
        uint frameWords = CoverageGpuLayout.FrameWords(host.BoneCount);
        var frame = new uint[frameWords];
        frame[5] = (uint)host.SourceCount;
        frame[6] = (uint)host.VertexCount;
        frame[7] = (uint)host.TriangleCount;
        frame[8] = (uint)host.PositionGroupCount;
        frame[9] = (uint)host.AliasCount;
        frame[11] = (uint)host.BoneCount;
        // All 32 actors share this fixture topology. One CPU-only header gives the production maximum X extent.
        // The fake device stores only this small header and is never used for pipeline creation or native evidence.
        using var device = new SubmissionTestDevice(6);
        using var batch = new CoverageGpuBatch(device);
        batch.Reserve(1, checked((int)frameWords), 1, 1);
        batch.BeginFrame();
        batch.Add(CoverageGpuLayout.CreateReservation(0, 0, 1, 0, 0, 0, host), frame);
        var kernels = Enum.GetValues<CoverageGpuPhase>().OrderBy(p => p == CoverageGpuPhase.CorrectFacesOverlay ? -1 : (int)p)
            .Select(p => new CoveragePipelineStatisticsKernel(CoverageGpuSkinRefineGlsl.Name(p), p.ToString(), new(64, 1, 1),
                new(CoverageGpuSkinRefineGlsl.IsReducer(p) ? 1u : (batch.Elements(p) + 63) / 64, 32, 1)))
            .ToImmutableArray();
        return new(CoveragePerfManifest.FixtureVersion, geometry.Topology.Fingerprint, 32, kernels);
    }
}
