using System;
using System.Globalization;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Optional driver strings require instance properties2 support and core 1.2 or device extension support.</summary>
internal static class VulkanDriverIdentity
{
    internal static string Read(uint instanceApiVersion, uint deviceApiVersion, uint rawVersion, Func<bool> hasExtension,
        Func<(string? Name, string? Info)> readProperties)
    {
        // Neither instance creation path enables VK_KHR_get_physical_device_properties2, so only the created
        // instance's core version can make this query available. A device's version cannot establish that.
        if (instanceApiVersion < Vk.Version11) return Raw(rawVersion);

        try
        {
            if (deviceApiVersion >= Vk.Version12 || hasExtension())
            {
                var (name, info) = readProperties();
                name = VulkanCapabilityRead.ReportedDeviceName(name);
                info = VulkanCapabilityRead.ReportedDeviceName(info);
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(info)) return name + ": " + info;
                if (!string.IsNullOrWhiteSpace(info)) return info;
                if (!string.IsNullOrWhiteSpace(name)) return name + " (" + Raw(rawVersion) + ")";
            }
        }
        catch (Exception)
        {
            // Only the optional identity enrichment degrades. The required physical-device reads still fail
            // normally, and the raw version already read from VkPhysicalDeviceProperties remains a real answer.
        }
        return Raw(rawVersion);
    }

    static string Raw(uint value)
        => "raw driverVersion=0x" + value.ToString("x8", CultureInfo.InvariantCulture) + " (implementation-defined)";

    internal static unsafe string Read(Vk vk, PhysicalDevice device, in PhysicalDeviceProperties properties,
        uint instanceApiVersion)
        => Read(instanceApiVersion, properties.ApiVersion, properties.DriverVersion,
            () => HasExtension(vk, device), () => ReadProperties(vk, device));

    static unsafe (string?, string?) ReadProperties(Vk vk, PhysicalDevice device)
    {
        if (!vk.Context.TryGetProcAddress("vkGetPhysicalDeviceProperties2", out nint entryPoint) || entryPoint == 0)
            return default;

        var driver = new PhysicalDeviceDriverProperties(sType: StructureType.PhysicalDeviceDriverProperties);
        var properties = new PhysicalDeviceProperties2(sType: StructureType.PhysicalDeviceProperties2, pNext: &driver);
        vk.GetPhysicalDeviceProperties2(device, &properties);
        return (SilkMarshal.PtrToString((nint)driver.DriverName), SilkMarshal.PtrToString((nint)driver.DriverInfo));
    }

    static unsafe bool HasExtension(Vk vk, PhysicalDevice device)
    {
        uint count = 0;
        if (vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null) != Result.Success) return false;
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* values = extensions)
        {
            Result result = vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, values);
            if (result != Result.Success && result != Result.Incomplete) return false;
            for (int i = 0; i < Math.Min(count, (uint)extensions.Length); i++)
                if (SilkMarshal.PtrToString((nint)values[i].ExtensionName) == "VK_KHR_driver_properties") return true;
        }
        return false;
    }
}
