using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfNativeFacts(uint Vendor, uint Device, string Driver, string Recording,
    int TimestampBits, double TimestampPeriodNs);

// Cold, test-only inspection of the selected native device. No backend API or package changes.
internal static class RendererPerfNativeIdentity
{
    internal static RendererPerfNativeFacts Read(IGpuDevice device)
    {
        RendererPerfManifest.Require(device.Diagnostics.SoftwareAdapter == false && device.Capabilities.SupportsTimestamps
            && device.Capabilities.SupportsCompletionFences, "Admission", "Native hardware, timestamps and completion fences are required.");
        RendererPerfNativeFacts facts;
        if (device is VulkanGpuDevice vulkan)
        {
            var configuration = vulkan.DiagnosticConfiguration;
            RendererPerfManifest.Require(vulkan.DiagnosticValidationRequest == VulkanValidationMode.Off
                && !configuration.ValidationEnabled && !configuration.DebugLayerEnabled
                && !configuration.CompilerMetadataEnabled && !configuration.RuntimeInvocationsEnabled && configuration.PipelineCaptureFlags == 0,
                "Admission", "Validation or compiler/invocation diagnostics cannot enter timed blocks.");
            var identity = vulkan.DiagnosticAdapterIdentity
                ?? throw new InvalidOperationException("The selected native Vulkan adapter identity was not retained.");
            facts = new(identity.VendorId, identity.DeviceId,
                "VkPhysicalDeviceProperties.driverVersion=" + identity.DriverVersion.ToString(CultureInfo.InvariantCulture),
                "vulkan-native", vulkan.TimestampSupport.ValidBits, vulkan.TimestampSupport.TickPeriodNanoseconds);
        }
        else if (OperatingSystem.IsWindows() && device is D3D11GpuDevice d3d)
            facts = ReadD3D(d3d);
        else throw new RendererPerfProtocolException("Admission", "Only the approved native T4 backends can enter this workload.");
        RendererPerfManifest.Require(facts.Vendor == 0x10de && facts.Device == 0x1eb8 && facts.TimestampBits is >= 1 and <= 64
            && double.IsFinite(facts.TimestampPeriodNs) && facts.TimestampPeriodNs > 0,
            "Admission", "Actual native vendor/device IDs or timestamp calibration do not identify the approved T4.");
        return facts;
    }

    [SupportedOSPlatform("windows")]
    static RendererPerfNativeFacts ReadD3D(D3D11GpuDevice device)
    {
        var native = Field<ID3D11Device>(device, "_device");
        RendererPerfManifest.Require((native.CreationFlags & DeviceCreationFlags.Debug) == 0,
            "Admission", "The D3D11 debug layer cannot enter timed blocks.");
        using var dxgi = native.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var description = adapter.Description;
        RendererPerfManifest.Require(description.VendorId == 0x10de && description.DeviceId == 0x1eb8,
            "Admission", "The selected DXGI adapter is not the approved T4. No clock probe was submitted.");
        // DXGI's driver-version query takes IDXGIDevice. A D3D11 interface returns Unsupported.
        // https://learn.microsoft.com/windows/win32/api/dxgi/nf-dxgi-idxgiadapter-checkinterfacesupport
        var result = adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long version);
        if (result.Failure) throw new InvalidOperationException("DXGI could not identify the selected device driver.");
        return new(checked((uint)description.VendorId), checked((uint)description.DeviceId), "DXGI driver=" + version.ToString("x16", CultureInfo.InvariantCulture),
            device.RecordMode.ToString().ToLowerInvariant(), 64, ProbePeriod(device));
    }

    static double ProbePeriod(IGpuDevice device)
    {
        // A cold capability probe, not a rendered frame or a throughput measurement. Resolve once.
        using var pool = device.Factory.CreateTimestampPool(2);
        using var commands = device.Factory.CreateCommandList();
        using var fence = device.Factory.CreateFence();
        commands.Begin();
        commands.BeginTimestamps(pool, 2);
        commands.WriteTimestamp(pool, 0);
        if (commands is not IGpuCommandListTimestampEnd tail)
            throw new InvalidOperationException("The selected backend cannot timestamp native End finalization.");
        tail.FinishTimestampsOnEnd(pool, 1);
        commands.End();
        device.Submit(commands, fence);
        device.WaitForIdle();
        Span<ulong> ticks = stackalloc ulong[2];
        if (pool.TryResolve(fence, ticks, out double period) != GpuTimestampStatus.Ready)
            throw new InvalidOperationException("The single cold timestamp calibration was unavailable. No retry is permitted.");
        return period;
    }

    static T Field<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as T
        ?? throw new InvalidOperationException($"Cold native identity field {owner.GetType().Name}.{name} is unavailable.");
}
