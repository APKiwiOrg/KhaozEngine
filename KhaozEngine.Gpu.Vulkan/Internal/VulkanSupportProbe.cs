using System;
using System.Collections.Generic;
using System.Globalization;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The READING half of the support probe behind
    /// <see cref="KhaozEngine.Gpu.IGpuBackendProvider.IsSupported"/>: resolve the loader, create a THROWAWAY
    /// instance at the 1.3 floor, read every physical device, and hand each device's values to
    /// <see cref="VulkanDeviceRequirements"/> to be judged. Section 4.1 and 5.2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>.
    /// <para>
    /// FUNCTIONAL rather than a guess, and that is the whole point of the row. The machine is asked, not the
    /// operating system: a Linux box with no ICD, a driver stuck at 1.2 and a device below the descriptor limit
    /// are three different sentences here, and every one of them routes through the reported fallback rather than
    /// through a crash on frame one. On this repo's own developer machines, which are macOS with no Vulkan loader
    /// at all, the first read answers and the rest is never reached.
    /// </para>
    /// <para>
    /// THE INSTANCE IS DESTROYED BEFORE THIS RETURNS, on every path, which is why the decision is taken over a
    /// snapshot (<see cref="VulkanDeviceFacts"/>) rather than over live handles. The probe deliberately does NOT
    /// use the refcounted process instance row 4 creates
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/514): it has to answer before any device exists, which is
    /// before that instance is allowed to. Whichever of the two lands second shares the physical-device walk below
    /// rather than growing a second copy of the requirement list.
    /// </para>
    /// <para>
    /// NOTHING IS CACHED HERE, mirroring <c>D3D11FeatureProbe</c>. The per-backend answer is cached for the
    /// process by <c>GpuBackendSelector.IsBackendSupported</c>, which is also the only caller and the only place
    /// that knows when a registration replaces the answerer. A second cache in the package would be a second
    /// lifetime to get wrong and would survive a re-registration the selector's own cache correctly drops.
    /// </para>
    /// </summary>
    internal static unsafe class VulkanSupportProbe
    {
        /// <summary>
        /// Null when this machine can run the native Vulkan backend, or a sentence saying what is missing, phrased
        /// for a log line a player or a tester will read. Never returns an empty string, so null is the only "yes".
        /// <para>
        /// The caller swallows exceptions: the provider contract says the probe must NEVER throw, because "we
        /// could not even ask" and "no" are the same answer to the settings screen and the fallback that consume
        /// it. The two failures worth a WORDED answer rather than a swallowed exception are caught here, because
        /// a machine with no loader and a machine with a 1.0 loader are the two most likely "no" answers in the
        /// fleet and both deserve to say so.
        /// </para>
        /// </summary>
        internal static string? MissingRequirement()
        {
            Vk vk;
            try
            {
                vk = Vk.GetApi();
            }
            catch (Exception ex)
            {
                // No loader at all: no libvulkan on the search path, or nothing behind it. The common case on
                // macOS, where this package ships, is never selected, and phase 4 brings a real Metal backend.
                return "no Vulkan loader could be resolved on this machine (" + ex.GetType().Name + ": "
                    + ex.Message + ")";
            }

            uint loaderVersion = 0;
            try
            {
                Result versionResult = vk.EnumerateInstanceVersion(ref loaderVersion);
                if (versionResult != Result.Success)
                {
                    return "the Vulkan loader could not report its instance version (vkEnumerateInstanceVersion "
                        + "returned " + versionResult + ")";
                }
            }
            catch (Exception ex)
            {
                // vkEnumerateInstanceVersion is itself a 1.1 entry point, so a loader that does not export it is
                // a Vulkan 1.0 loader and is three versions below this backend's floor. Caught rather than left
                // to the provider's blanket catch so the message says which fact was missing.
                return "the Vulkan loader exposes no vkEnumerateInstanceVersion, which means a 1.0 loader, and "
                    + "this backend needs 1.3 (" + ex.GetType().Name + ")";
            }

            if (loaderVersion < VulkanDeviceRequirements.MinimumApiVersion)
            {
                return "the Vulkan loader reports instance version "
                    + VulkanDeviceRequirements.FormatApiVersion(loaderVersion)
                    + ", below the 1.3 floor this backend is built on";
            }

            // V-N2's clamp, kept in its stated form rather than simplified to the constant. The comparison above
            // already proves which side wins, and writing the min anyway is what keeps this line readable as the
            // decision it implements when somebody moves the floor.
            uint requestedVersion = Math.Min(loaderVersion, VulkanDeviceRequirements.MinimumApiVersion);
            return MissingRequirementThroughInstance(vk, requestedVersion);
        }

        // The throwaway instance and everything under it. No layers, no extensions: the headless path enables no
        // surface extension (V-N6) and the validation knob is a device-creation concern, so the smallest instance
        // that can enumerate devices is the right one to ask through.
        static string? MissingRequirementThroughInstance(Vk vk, uint apiVersion)
        {
            var applicationInfo = new ApplicationInfo(
                sType: StructureType.ApplicationInfo, apiVersion: apiVersion);

            var createInfo = new InstanceCreateInfo(
                sType: StructureType.InstanceCreateInfo, pApplicationInfo: &applicationInfo);

            Result created = vk.CreateInstance(in createInfo, null, out Instance instance);
            if (created != Result.Success)
            {
                return "a Vulkan instance could not be created at apiVersion "
                    + VulkanDeviceRequirements.FormatApiVersion(apiVersion) + " (vkCreateInstance returned "
                    + created + ")";
            }

            try
            {
                return MissingRequirementAcrossDevices(vk, instance);
            }
            finally
            {
                // Every path, including the one that found a device it likes. Nothing above this line may outlive
                // the instance, which is what makes the facts a copied snapshot rather than a handle.
                vk.DestroyInstance(instance, null);
            }
        }

        // Null as soon as ANY physical device meets the requirements, which is the same "first device that
        // qualifies" shape V-N3's default selection takes. When none does, every device's own reason is reported,
        // because on a two-device machine the interesting information is usually why the OTHER one was rejected.
        static string? MissingRequirementAcrossDevices(Vk vk, Instance instance)
        {
            uint deviceCount = 0;
            Result counted = vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
            if (counted != Result.Success && counted != Result.Incomplete)
            {
                return "the physical devices could not be enumerated (vkEnumeratePhysicalDevices returned "
                    + counted + ")";
            }

            if (deviceCount == 0)
            {
                return "the Vulkan loader resolved but found no physical device, which is a loader with no "
                    + "installed ICD behind it";
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* handles = devices)
            {
                Result filled = vk.EnumeratePhysicalDevices(instance, &deviceCount, handles);
                if (filled != Result.Success && filled != Result.Incomplete)
                {
                    return "the physical devices could not be read (vkEnumeratePhysicalDevices returned "
                        + filled + ")";
                }
            }

            var rejections = new List<string>();
            for (uint i = 0; i < deviceCount; i++)
            {
                VulkanDeviceFacts facts = Read(vk, devices[i]);

                // presentationRequired: false. IsSupported() receives no window, so there is no VkSurfaceKHR to
                // ask about, and building one would need a platform surface extension the headless path is not
                // allowed to enable. The windowed clause is evaluated at swapchain creation against the same
                // method. VulkanDeviceRequirements.MissingRequirement documents the whole split.
                string? missing = VulkanDeviceRequirements.MissingRequirement(facts, presentationRequired: false);
                if (missing is null) return null;

                rejections.Add(facts.DeviceName + ": " + missing);
            }

            return "no physical device on this machine can run the native Vulkan backend. "
                + string.Join(". ", rejections);
        }

        // One device, read into plain data. Four calls, in the order the requirements consume them: properties
        // (the version floor and the descriptor limit), the chained feature query (the three feature bits),
        // memory (the coherent host-visible type), queue families (the graphics family).
        static VulkanDeviceFacts Read(Vk vk, PhysicalDevice device)
        {
            PhysicalDeviceProperties properties;
            vk.GetPhysicalDeviceProperties(device, &properties);

            (bool dynamicRendering, bool synchronization2, bool timelineSemaphore) = properties.ApiVersion
                >= VulkanDeviceRequirements.MinimumApiVersion
                    ? ReadFeatures(vk, device)
                    : (false, false, false);

            return new VulkanDeviceFacts(
                DeviceName: ReadDeviceName(&properties),
                ApiVersion: properties.ApiVersion,
                DynamicRendering: dynamicRendering,
                Synchronization2: synchronization2,
                TimelineSemaphore: timelineSemaphore,
                HasCoherentHostVisibleMemoryType: HasCoherentHostVisibleMemoryType(vk, device),
                MaxDescriptorSetUniformBuffersDynamic:
                    properties.Limits.MaxDescriptorSetUniformBuffersDynamic,
                HasGraphicsQueueFamily: HasGraphicsQueueFamily(vk, device),
                // Not asked, and not askable without a surface. False here is never read, because the probe
                // passes presentationRequired: false.
                GraphicsFamilyPresents: false);
        }

        // The three feature bits, off ONE chained query. Asked only of a device that already clears the version
        // floor, and that gate is load-bearing rather than an optimisation: a device below 1.3 is rejected on its
        // VERSION by the decision half, so its feature bits are never the reason it is turned away, and chaining a
        // VkPhysicalDeviceVulkan13Features onto a query a 1.2 implementation never promised to understand is
        // exactly the shape a driver is free to handle badly and the validation layers are right to flag. Leaving
        // them false on that path asks nothing that is not owed and changes no answer.
        static (bool DynamicRendering, bool Synchronization2, bool TimelineSemaphore) ReadFeatures(
            Vk vk, PhysicalDevice device)
        {
            var features13 = new PhysicalDeviceVulkan13Features(
                sType: StructureType.PhysicalDeviceVulkan13Features);
            var features12 = new PhysicalDeviceVulkan12Features(
                sType: StructureType.PhysicalDeviceVulkan12Features, pNext: &features13);
            var features2 = new PhysicalDeviceFeatures2(
                sType: StructureType.PhysicalDeviceFeatures2, pNext: &features12);
            vk.GetPhysicalDeviceFeatures2(device, &features2);

            return (features13.DynamicRendering, features13.Synchronization2, features12.TimelineSemaphore);
        }

        // The driver's name for the device, copied out of the fixed byte buffer into a managed string so it can
        // outlive the instance. A driver that reports nothing readable still has to be nameable in a log line, so
        // this never returns null or empty.
        static string ReadDeviceName(PhysicalDeviceProperties* properties)
        {
            string? name = SilkMarshal.PtrToString((nint)properties->DeviceName);
            return string.IsNullOrWhiteSpace(name)
                ? "unnamed device 0x" + properties->DeviceID.ToString("x8", CultureInfo.InvariantCulture)
                : name;
        }

        // V-M4's read. One type carrying both bits is enough, and the Vulkan spec requires at least one to exist,
        // so this is the check that fails loudly on a device that cannot happen rather than one that gates a
        // device that can.
        static bool HasCoherentHostVisibleMemoryType(Vk vk, PhysicalDevice device)
        {
            const MemoryPropertyFlags required =
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

            PhysicalDeviceMemoryProperties memory;
            vk.GetPhysicalDeviceMemoryProperties(device, &memory);

            for (uint i = 0; i < memory.MemoryTypeCount; i++)
            {
                if ((memory.MemoryTypes[(int)i].PropertyFlags & required) == required) return true;
            }
            return false;
        }

        // V-N5's read, the half that needs no surface: does a graphics family exist at all. Whether it can also
        // present is the windowed clause, decided where a surface exists.
        static bool HasGraphicsQueueFamily(Vk vk, PhysicalDevice device)
        {
            uint familyCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
            if (familyCount == 0) return false;

            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* handles = families)
            {
                vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, handles);
            }

            foreach (QueueFamilyProperties family in families)
            {
                if ((family.QueueFlags & QueueFlags.GraphicsBit) != 0) return true;
            }
            return false;
        }
    }
}
