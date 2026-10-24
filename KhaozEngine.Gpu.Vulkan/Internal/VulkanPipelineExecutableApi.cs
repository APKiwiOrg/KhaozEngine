using System;
using System.Text;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Device setup and driver calls for the explicitly requested compiler-diagnostic device.</summary>
internal sealed unsafe class VulkanPipelineExecutableApi : IVulkanPipelineExecutableApi
{
    internal const string ExtensionName = "VK_KHR_pipeline_executable_properties";
    readonly KhrPipelineExecutableProperties _api;
    readonly Device _device;
    readonly IDeviceLiveness _liveness;
    readonly VulkanDeviceLossLatch _loss;

    internal VulkanPipelineExecutableApi(Vk vk, Instance instance, Device device,
        IDeviceLiveness liveness, VulkanDeviceLossLatch loss)
    {
        if (!vk.TryGetDeviceExtension(instance, device, out _api!))
            throw new InvalidOperationException("VK_KHR_pipeline_executable_properties entry points could not be loaded.");
        _device = device;
        _liveness = liveness;
        _loss = loss;
    }

    internal static VulkanPipelineDiagnostics Discover(Vk vk, PhysicalDevice device, VulkanInstanceValidation validation, bool? compilerMetadata = null)
    {
        string configuration = validation.DiagnosticConfiguration;
        bool requested = compilerMetadata ?? Environment.GetEnvironmentVariable("KE_VULKAN_PIPELINE_DIAGNOSTICS") == "1";
        if (compilerMetadata is null && !requested) return new(false, false, false, null, configuration);
        try
        {
            bool extension = HasExtension(vk, device);
            var feature = new PhysicalDevicePipelineExecutablePropertiesFeaturesKHR
            {
                SType = StructureType.PhysicalDevicePipelineExecutablePropertiesFeaturesKhr
            };
            if (extension)
            {
                var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &feature };
                vk.GetPhysicalDeviceFeatures2(device, &features);
            }
            return new(requested, extension, extension && feature.PipelineExecutableInfo, null, configuration);
        }
        catch (InvalidOperationException error)
        {
            if (compilerMetadata.HasValue) throw;
            return new(requested, false, false, null, configuration, error.Message);
        }
    }

    static bool HasExtension(Vk vk, PhysicalDevice device)
    {
        const int capacity = 512;
        var extensions = new ExtensionProperties[capacity];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint count = 0;
            Result counted = vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
            Check(counted);
            if (count > capacity) throw new InvalidOperationException("Vulkan device extension count exceeds fixed capacity 512.");
            if (count == 0 && counted == Result.Success) return false;
            if (count == 0) continue;
            uint requested = count;
            fixed (ExtensionProperties* values = extensions)
            {
                Result filled = vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, values);
                Check(filled);
                if (count > requested) throw new InvalidOperationException("Vulkan device extension count exceeded its output capacity.");
                if (filled == Result.Incomplete) continue;
                for (int i = 0; i < count; i++)
                {
                    var name = new ReadOnlySpan<byte>(values[i].ExtensionName, 256);
                    int end = name.IndexOf((byte)0);
                    if (Encoding.UTF8.GetString(end < 0 ? name : name[..end]) == ExtensionName) return true;
                }
                return false;
            }
        }
        throw new InvalidOperationException("Vulkan device extension enumeration remained VK_INCOMPLETE after 4 attempts.");
    }

    static void Check(Result result)
    {
        if (result != Result.Success && result != Result.Incomplete)
            throw new InvalidOperationException($"vkEnumerateDeviceExtensionProperties failed with {result} ({(int)result}).");
    }

    public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
    {
        if (_liveness.IsDead) return Result.ErrorDeviceLost;
        var info = new PipelineInfoKHR { SType = StructureType.PipelineInfoKhr, Pipeline = new Pipeline(pipeline) };
        fixed (PipelineExecutablePropertiesKHR* target = values)
        {
            Result result = _api.GetPipelineExecutableProperties(_device, in info, ref count, target);
            _loss.Check(result, "vkGetPipelineExecutablePropertiesKHR");
            return result;
        }
    }

    public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
    {
        if (_liveness.IsDead) return Result.ErrorDeviceLost;
        var info = new PipelineExecutableInfoKHR
        {
            SType = StructureType.PipelineExecutableInfoKhr,
            Pipeline = new Pipeline(pipeline),
            ExecutableIndex = executable
        };
        fixed (PipelineExecutableStatisticKHR* target = values)
        {
            Result result = _api.GetPipelineExecutableStatistic(_device, in info, ref count, target);
            _loss.Check(result, "vkGetPipelineExecutableStatisticsKHR");
            return result;
        }
    }
}
