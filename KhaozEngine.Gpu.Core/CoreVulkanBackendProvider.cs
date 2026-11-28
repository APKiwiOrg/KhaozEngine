using System;
using System.Text;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Core.Internal;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan;

namespace KhaozEngine.Gpu.Core;

/// <summary>Creates a headless Vulkan device through the native core library.</summary>
public sealed unsafe class CoreVulkanBackendProvider : IGpuBackendProvider
{
    private static readonly ILogger Logger = Log.For<CoreVulkanBackendProvider>();

    /// <summary>Creates a provider for explicit registration.</summary>
    public CoreVulkanBackendProvider() { }

    /// <inheritdoc/>
    public bool IsSupported()
    {
        try
        {
            if (NativeLibraryGate.Probe() != NativeLoadStatus.Ready)
                return false;

            fixed (byte* loader = Encode(Environment.GetEnvironmentVariable("KHAOZ_NATIVE_VULKAN_LOADER")))
                return KhaozNative.khaoz_gpu_device_probe((sbyte*)loader) == khaoz_result.KHAOZ_OK;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        => throw new VulkanBackendCreationUnsupportedException(VulkanBackendCreationRefusal.WindowKindUnsupported,
            "The native core supports headless Vulkan devices only.") { WindowKind = request.Window.Kind };

    /// <inheritdoc/>
    public GpuProviderDevice CreateHeadless()
    {
        NativeLoadStatus status = NativeLibraryGate.Probe();
        if (status != NativeLoadStatus.Ready)
            throw Unavailable($"The native core library is {status}.");

        // .NET can update its environment without updating the native C runtime's copy.
        byte[]? loaderPath = Encode(Environment.GetEnvironmentVariable("KHAOZ_NATIVE_VULKAN_LOADER"));
        var state = new CoreAdapterSelection(Environment.GetEnvironmentVariable("KE_VULKAN_DEVICE"));
        GCHandle context = GCHandle.Alloc(state);
        khaoz_gpu_device* handle;
        khaoz_result result;
        uint startup;
        try
        {
            fixed (byte* loader = loaderPath)
            {
                var desc = new khaoz_gpu_device_desc
                {
                    enable_validation = Environment.GetEnvironmentVariable("KHAOZ_NATIVE_VALIDATION") == "1" ? 1u : 0u,
                };
                var selection = new khaoz_gpu_device_selection
                {
                    select = &CoreAdapterSelection.Select,
                    context = (void*)GCHandle.ToIntPtr(context),
                };
                result = KhaozNative.khaoz_gpu_device_create_headless_with_selection(
                    &desc, (sbyte*)loader, &selection, &startup, &handle);
            }
        }
        finally
        {
            context.Free();
        }

        try
        {
            if (state.Warning is { } warning)
                Logger.Warn(warning);
            if (state.Failure is { } failure)
                throw new InvalidOperationException("Native core Vulkan adapter selection failed.", failure);
            if (result == khaoz_result.KHAOZ_ERROR_VULKAN_UNAVAILABLE)
                throw Unavailable(NativeResult.ReadLastError(),
                    startup == (uint)khaoz_gpu_startup_status.KHAOZ_GPU_STARTUP_NO_DRIVER);
            if (result != khaoz_result.KHAOZ_OK)
                throw new InvalidOperationException($"Native core Vulkan device creation failed ({result}). {NativeResult.ReadLastError()}");

            return new GpuProviderDevice(new CoreGpuDevice(handle), null, null);
        }
        catch
        {
            if (handle != null)
                KhaozNative.khaoz_gpu_device_destroy(handle, null);
            throw;
        }
    }

    private static VulkanBackendCreationUnsupportedException Unavailable(string missing, bool noDriver = false)
        => new(VulkanBackendCreationRefusal.MachineUnsupported, $"Native core Vulkan is unavailable. {missing}")
        {
            MissingRequirement = missing,
            Probe = "GpuBackendSelector.IsBackendSupported",
            DriverPackage = noDriver ? "mesa-vulkan-drivers" : null,
        };

    private static byte[]? Encode(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value + '\0');
}
