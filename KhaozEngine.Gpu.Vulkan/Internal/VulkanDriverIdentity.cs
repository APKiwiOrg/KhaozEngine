using System;
using System.Globalization;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Driver properties are core in 1.2. Older devices must advertise VK_KHR_driver_properties.</summary>
internal static class VulkanDriverIdentity
{
    internal static string Read(uint apiVersion, uint rawVersion, Func<bool> hasExtension,
        Func<(string? Name, string? Info)> readProperties)
    {
        if (apiVersion >= Vk.Version12 || hasExtension())
        {
            var (name, info) = readProperties();
            name = VulkanCapabilityRead.ReportedDeviceName(name);
            info = VulkanCapabilityRead.ReportedDeviceName(info);
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(info)) return name + ": " + info;
            if (!string.IsNullOrWhiteSpace(info)) return info;
            if (!string.IsNullOrWhiteSpace(name)) return name + " (" + Raw(rawVersion) + ")";
        }
        return Raw(rawVersion);
    }

    static string Raw(uint value)
        => "raw driverVersion=0x" + value.ToString("x8", CultureInfo.InvariantCulture) + " (implementation-defined)";

    internal static unsafe string Read(Vk vk, PhysicalDevice device, in PhysicalDeviceProperties properties)
        => Read(properties.ApiVersion, properties.DriverVersion,
            () => HasExtension(vk, device), () => ReadProperties(vk, device));

    static unsafe (string?, string?) ReadProperties(Vk vk, PhysicalDevice device)
    {
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
