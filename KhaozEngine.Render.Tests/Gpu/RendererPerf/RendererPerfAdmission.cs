using System;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfAdmission
{
    internal static void Identity(RendererPerfManifest manifest, RendererPerfRunIdentity identity)
    {
        RendererPerfManifest.Validate(manifest);
        var runtime = manifest.Runtime;
        Require(identity.UnitId == manifest.UnitId && identity.WorkloadSha == manifest.WorkloadSha
            && identity.BaselineMainSha == manifest.BaselineMainSha && identity.Attempt == 1 && identity.ProcessId > 0,
            "Execution identity differs from the admitted manifest.");
        RendererPerfManifest.Hash(identity.ManifestSha256, 64);
        RendererPerfManifest.Hash(identity.AuthorizationSha, 40);
        RendererPerfManifest.Hash(identity.WorkflowSha, 40);
        RendererPerfManifest.Hash(identity.HarnessSha256, 64);
        Require(identity.AuthorizationSha != identity.WorkloadSha && !string.IsNullOrWhiteSpace(identity.RunId),
            "An independent first-attempt authorization identity is required.");
        Require(RendererPerfAdapterPolicy.Matches(manifest.ExecutionClass, identity.SoftwareAdapter, identity.Backend, identity.VendorId, identity.DeviceId)
            && identity.TimestampSupported && identity.TimestampValidBits is >= 1 and <= 64
            && identity.TimestampPeriodNs is > 0 && double.IsFinite(identity.TimestampPeriodNs.Value),
            "The execution class requires its actual adapter identity and usable timestamps.");
        Require(identity.Backend == manifest.Execution.Backend && identity.CompletionPolicy == manifest.Execution.CompletionPolicy
            && identity.Runtime == runtime.RuntimeVersion && identity.GcMode == runtime.GcMode
            && identity.TieredCompilation == runtime.TieredCompilation && identity.Environment.Count == runtime.Environment.Count
            && identity.Environment.All(p => runtime.Environment.TryGetValue(p.Key, out string? value) && value == p.Value),
            "Runtime, backend or completion policy differs from the manifest.");
        Require(new[] { identity.Adapter, identity.Driver, identity.RecordingMode, identity.Os, identity.Image,
            identity.Cpu, identity.ValidationFlags, identity.CaptureFlags }.All(s => !string.IsNullOrWhiteSpace(s)),
            "Hardware and runtime provenance must be explicit.");
        Require(identity.ValidationFlags == "none" && identity.CaptureFlags == "none", "Diagnostic pipelines and validation cannot enter timed blocks.");
    }

    internal static void Session(RendererPerfRunIdentity identity, RendererPerfSessionInfo info)
    {
        Require(info.Enabled && info.CompletionFences, "Disabled instrumentation or missing completion fences cannot enter timing.");
        Require(info.Failure is null, info.Failure ?? "Session admission failed.");
        // Environment dictionaries can come from independent probes and need value equality.
        Require(info.Identity with { Environment = identity.Environment } == identity
            && info.Identity.Environment.Count == identity.Environment.Count
            && info.Identity.Environment.All(p => identity.Environment.TryGetValue(p.Key, out string? value) && value == p.Value),
            "The actual session adapter, driver or runtime changed.");
    }

    internal static void Device(IGpuDevice device, RendererPerfSessionInfo info,
        RendererPerfExecutionClass executionClass = RendererPerfExecutionClass.Performance)
    {
        Require(info.Enabled && info.CompletionFences && device.Capabilities.SupportsTimestamps
            && device.Capabilities.SupportsCompletionFences
            && device.Diagnostics.SoftwareAdapter == (executionClass == RendererPerfExecutionClass.Rehearsal)
            && !device.Diagnostics.IsDeviceLost, "The native device cannot supply hardware timestamp proof.");
        string backend = device.Backend switch
        {
            GpuBackendKind.VulkanNative => "vulkan",
            GpuBackendKind.Direct3D11Native => "d3d11",
            GpuBackendKind.MetalNative => "metal",
            _ => "unsupported"
        };
        Require(info.Identity.Backend == backend && info.Identity.Adapter == device.Capabilities.DeviceName
            && RendererPerfAdapterPolicy.Matches(executionClass, info.Identity.SoftwareAdapter, backend, info.Identity.VendorId, info.Identity.DeviceId)
            && info.Identity.TimestampSupported && info.Identity.SoftwareAdapter == device.Diagnostics.SoftwareAdapter,
            "Native device facts disagree with the independently inspected T4 identity.");
    }

    static void Require(bool condition, string reason)
    {
        if (!condition) throw new RendererPerfProtocolException("Admission", reason);
    }
}
