using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;

namespace EquipmentCoverage.Perf.Tests;

internal static class CoveragePipelineStatisticsUnit
{
    internal static void Capture(string directory, CoveragePerfRequest request, CoveragePerfRunIdentity identity)
    {
        string? oldGate = Environment.GetEnvironmentVariable("KE_VULKAN_PIPELINE_DIAGNOSTICS");
        string? oldCache = Environment.GetEnvironmentVariable("KE_VULKAN_PIPELINE_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("KE_VULKAN_PIPELINE_DIAGNOSTICS", "1");
            Environment.SetEnvironmentVariable("KE_VULKAN_PIPELINE_CACHE", "off");
            using var context = GpuDeviceContext.CreateHeadless();
            var caps = context.Capabilities;
            if (context.Backend != GpuBackendKind.VulkanNative || caps.PciVendorId != 0x10de
                || !caps.DeviceName.Contains("T4", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(caps.DriverVersion))
                throw new InvalidDataException("The statistics unit requires native NVIDIA T4 Vulkan identity and driver evidence.");
            var capture = CoveragePipelineStatisticsCapture.Capture(context.GpuDevice);
            string json = CoveragePipelineStatisticsCapture.Serialize(new
            {
                schema = CoveragePipelineStatisticsCapture.Schema,
                identity,
                authorizationCommitSha = identity.AuthorizationCommitSha,
                manifestSha256 = request.Manifest.Sha256,
                capture,
            });
            string path = Path.Combine(directory, "coverage-pipeline-statistics.json");
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path);
            if (capture.Status == GpuDiagnosticStatus.Invalid)
                throw new InvalidDataException("The native compiler capture is invalid: " + capture.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KE_VULKAN_PIPELINE_DIAGNOSTICS", oldGate);
            Environment.SetEnvironmentVariable("KE_VULKAN_PIPELINE_CACHE", oldCache);
        }
    }
}
