using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreAdapterSelection(string? request)
{
    internal string? Warning { get; private set; }
    internal Exception? Failure { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int Select(khaoz_gpu_adapter_info* adapters, uint count, void* context)
    {
        CoreAdapterSelection? state = null;
        try
        {
            state = (CoreAdapterSelection)GCHandle.FromIntPtr((nint)context).Target!;
            int selected = Choose(state.Request, new ReadOnlySpan<khaoz_gpu_adapter_info>(adapters, checked((int)count)),
                out string? warning);
            state.Warning = warning;
            return selected;
        }
        catch (Exception ex)
        {
            if (state is not null)
                state.Failure = ex;
            return VulkanPhysicalDeviceSelection.NoDevice;
        }
    }

    private string? Request => request;

    internal static int Choose(string? request, ReadOnlySpan<khaoz_gpu_adapter_info> adapters, out string? warning)
    {
        var candidates = new VulkanPhysicalDeviceInfo[adapters.Length];
        fixed (khaoz_gpu_adapter_info* first = adapters)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                var adapter = &first[i];
                string name = VulkanPhysicalDeviceReader.ReadDeviceDisplayName(
                    (nint)(&adapter->device_name.e0), adapter->device_id);
                var kind = adapter->device_type <= (uint)VulkanPhysicalDeviceClass.Cpu
                    ? (VulkanPhysicalDeviceClass)adapter->device_type : VulkanPhysicalDeviceClass.Other;
                bool llvmpipe = adapter->mesa_llvmpipe != 0 || name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase);
                candidates[i] = new VulkanPhysicalDeviceInfo(name, kind, llvmpipe, adapter->rejection_reason == null,
                    Marshal.PtrToStringUTF8((nint)adapter->rejection_reason));
            }
        }

        return VulkanPhysicalDeviceSelection.Choose(VulkanPhysicalDeviceSelection.Parse(request), candidates, out warning);
    }
}
