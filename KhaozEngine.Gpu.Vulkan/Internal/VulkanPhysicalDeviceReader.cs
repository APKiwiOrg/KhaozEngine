using System;
using System.Globalization;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>Everything one physical device answers, as plain data with no Vulkan handle in it.</summary>
    /// <param name="Facts">What the requirement check judges (row 2's shape, unchanged).</param>
    /// <param name="Features">Every feature bit the selective enable and the capability read need (V-N4).</param>
    /// <param name="Class">The device type, which <c>KE_VULKAN_DEVICE=discrete|integrated|cpu</c> selects on.</param>
    /// <param name="IsLlvmpipe">Whether this is the Mesa software rasterizer, by driver id or by name. Half of
    /// <c>SoftwareRasterizer</c> (V-G2) and what <c>KE_VULKAN_DEVICE=llvmpipe</c> matches.</param>
    /// <param name="GraphicsQueueFamily">The first family carrying <c>VK_QUEUE_GRAPHICS_BIT</c>, or
    /// <see cref="VulkanPhysicalDeviceReader.NoQueueFamily"/> when there is none. ONE graphics queue is the whole
    /// queue model (V-N5).</param>
    /// <param name="SupportsShadowMapFormat">Whether <c>R32_SFLOAT</c> can be both a depth-stencil attachment and
    /// a sampled image, which is <c>GpuCapabilities.SupportsShadowMaps</c>.</param>
    internal readonly record struct VulkanPhysicalDeviceRead(
        VulkanDeviceFacts Facts,
        VulkanFeatureSupport Features,
        VulkanPhysicalDeviceClass Class,
        bool IsLlvmpipe,
        uint GraphicsQueueFamily,
        bool SupportsShadowMapFormat);

    /// <summary>
    /// The one place a <c>VkPhysicalDevice</c> is turned into plain data, shared by the support probe and by
    /// device creation.
    /// <para>
    /// SHARED FROM THE PROBE SIDE, which is how row 3's handoff resolved the overlap. The probe (row 2) landed
    /// first and owns the requirement LIST. This row needed the same four calls plus the feature bits it enables
    /// and the queue family it creates, so the reads moved here and
    /// <see cref="VulkanSupportProbe"/> reads through them. Two copies of a physical-device walk would drift the
    /// day a requirement moved, and the failure mode is the worst available: a probe that says yes and a creation
    /// that then refuses, which reports to a player as their machine failing after passing.
    /// </para>
    /// <para>
    /// EVERYTHING IT PRODUCES IS A SNAPSHOT, copied out of the driver's own storage, because the probe destroys
    /// its instance before deciding anything. That is why <see cref="VulkanDeviceFacts.DeviceName"/> is a managed
    /// string rather than the pointer it came from.
    /// </para>
    /// </summary>
    internal static unsafe class VulkanPhysicalDeviceReader
    {
        /// <summary>What "this device exposes no graphics queue family" looks like, so a caller cannot read it as
        /// family zero.</summary>
        internal const uint NoQueueFamily = uint.MaxValue;

        /// <summary>Read one physical device.</summary>
        internal static VulkanPhysicalDeviceRead Read(Vk vk, PhysicalDevice device)
        {
            ArgumentNullException.ThrowIfNull(vk);

            PhysicalDeviceProperties properties;
            vk.GetPhysicalDeviceProperties(device, &properties);

            bool clearsVersionFloor = properties.ApiVersion >= VulkanDeviceRequirements.MinimumApiVersion;
            VulkanFeatureSupport features = clearsVersionFloor
                ? ReadFeatures(vk, device)
                : default;

            string name = ReadDeviceName(&properties);
            uint graphicsFamily = FirstGraphicsQueueFamily(vk, device);

            var facts = new VulkanDeviceFacts(
                DeviceName: name,
                ApiVersion: properties.ApiVersion,
                DynamicRendering: features.DynamicRendering,
                Synchronization2: features.Synchronization2,
                TimelineSemaphore: features.TimelineSemaphore,
                HasCoherentHostVisibleMemoryType: HasCoherentHostVisibleMemoryType(vk, device),
                MaxDescriptorSetUniformBuffersDynamic: properties.Limits.MaxDescriptorSetUniformBuffersDynamic,
                HasGraphicsQueueFamily: graphicsFamily != NoQueueFamily,
                // Not asked, and not askable without a surface. The windowed clause is evaluated at swapchain
                // creation (https://github.com/APKiwiOrg/KhaozEngine/issues/527) against the same requirement
                // method with the flag set.
                GraphicsFamilyPresents: false);

            return new VulkanPhysicalDeviceRead(
                facts,
                features,
                Classify(properties.DeviceType),
                IsLlvmpipe(vk, device, clearsVersionFloor, name),
                graphicsFamily,
                SupportsShadowMapFormat(vk, device));
        }

        /// <summary>The driver's name for the device, copied out of the fixed byte buffer into a managed string so
        /// it can outlive the instance. A driver that reports nothing readable still has to be nameable in a log
        /// line, so this never returns null or empty.</summary>
        internal static string ReadDeviceName(PhysicalDeviceProperties* properties)
        {
            string? name = SilkMarshal.PtrToString((nint)properties->DeviceName);
            return string.IsNullOrWhiteSpace(name)
                ? "unnamed device 0x" + properties->DeviceID.ToString("x8", CultureInfo.InvariantCulture)
                : name;
        }

        // Every feature bit, off ONE chained query. Asked only of a device that already clears the version floor,
        // and that gate is load-bearing rather than an optimisation: chaining a VkPhysicalDeviceVulkan13Features
        // onto a query a 1.2 implementation never promised to understand is exactly the shape a driver is free to
        // handle badly and the validation layers are right to flag. A device below the floor is rejected on its
        // VERSION, so its feature bits are never the reason it is turned away and leaving them false changes no
        // answer.
        static VulkanFeatureSupport ReadFeatures(Vk vk, PhysicalDevice device)
        {
            var features13 = new PhysicalDeviceVulkan13Features(
                sType: StructureType.PhysicalDeviceVulkan13Features);
            var features12 = new PhysicalDeviceVulkan12Features(
                sType: StructureType.PhysicalDeviceVulkan12Features, pNext: &features13);
            var features2 = new PhysicalDeviceFeatures2(
                sType: StructureType.PhysicalDeviceFeatures2, pNext: &features12);
            vk.GetPhysicalDeviceFeatures2(device, &features2);

            PhysicalDeviceFeatures core = features2.Features;
            return new VulkanFeatureSupport(
                DynamicRendering: features13.DynamicRendering,
                Synchronization2: features13.Synchronization2,
                TimelineSemaphore: features12.TimelineSemaphore,
                SamplerAnisotropy: core.SamplerAnisotropy,
                FillModeNonSolid: core.FillModeNonSolid,
                DepthClamp: core.DepthClamp,
                IndependentBlend: core.IndependentBlend,
                GeometryShader: core.GeometryShader,
                TessellationShader: core.TessellationShader,
                MultiViewport: core.MultiViewport,
                DrawIndirectFirstInstance: core.DrawIndirectFirstInstance,
                ShaderFloat64: core.ShaderFloat64);
        }

        // V-G2's read. The driver id is the authoritative half and comes off VkPhysicalDeviceDriverProperties,
        // which is 1.2 core, so it is only asked of a device that clears the floor. The name check is the fallback
        // for a driver too old to report one, and it is deliberately not the primary: "llvmpipe" appears in a
        // vendor string this backend does not get to police.
        static bool IsLlvmpipe(Vk vk, PhysicalDevice device, bool clearsVersionFloor, string name)
        {
            if (clearsVersionFloor)
            {
                var driver = new PhysicalDeviceDriverProperties(
                    sType: StructureType.PhysicalDeviceDriverProperties);
                var properties2 = new PhysicalDeviceProperties2(
                    sType: StructureType.PhysicalDeviceProperties2, pNext: &driver);
                vk.GetPhysicalDeviceProperties2(device, &properties2);

                if (driver.DriverID == DriverId.MesaLlvmpipe) return true;
            }

            return name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase);
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

        // V-N5's read, the half that needs no surface: the FIRST family with VK_QUEUE_GRAPHICS_BIT. Whether it can
        // also present is the windowed clause, decided where a surface exists.
        static uint FirstGraphicsQueueFamily(Vk vk, PhysicalDevice device)
        {
            uint familyCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
            if (familyCount == 0) return NoQueueFamily;

            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* handles = families)
            {
                vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, handles);
            }

            for (uint i = 0; i < familyCount; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0) return i;
            }
            return NoQueueFamily;
        }

        // GpuCapabilities.SupportsShadowMaps: R32_SFLOAT usable as both a depth-stencil attachment and a sampled
        // image, which is what the shadow pass renders into and then reads.
        static bool SupportsShadowMapFormat(Vk vk, PhysicalDevice device)
        {
            FormatProperties properties;
            vk.GetPhysicalDeviceFormatProperties(device, Format.R32Sfloat, &properties);

            const FormatFeatureFlags required =
                FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;
            return (properties.OptimalTilingFeatures & required) == required;
        }

        static VulkanPhysicalDeviceClass Classify(PhysicalDeviceType type) => type switch
        {
            PhysicalDeviceType.IntegratedGpu => VulkanPhysicalDeviceClass.Integrated,
            PhysicalDeviceType.DiscreteGpu => VulkanPhysicalDeviceClass.Discrete,
            PhysicalDeviceType.VirtualGpu => VulkanPhysicalDeviceClass.Virtual,
            PhysicalDeviceType.Cpu => VulkanPhysicalDeviceClass.Cpu,
            _ => VulkanPhysicalDeviceClass.Other,
        };
    }
}
