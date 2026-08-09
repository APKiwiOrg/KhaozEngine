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
    /// <param name="SupportsShadowMapFormat">Whether <c>R32_SFLOAT</c> can be both a colour attachment and a
    /// sampled image, which is <c>GpuCapabilities.SupportsShadowMaps</c>. See
    /// <see cref="VulkanPhysicalDeviceReader.ShadowMapFormatFeatures"/> for why the pair is that one and not the
    /// depth-stencil one the name suggests.</param>
    /// <param name="ReportedDeviceName">What the driver actually put in
    /// <c>VkPhysicalDeviceProperties.deviceName</c>, empty when it reported nothing, which is the string
    /// <c>GpuCapabilities.DeviceName</c> carries. Separate from <see cref="VulkanDeviceFacts.DeviceName"/>, which
    /// is the LOGGABLE name and substitutes a synthetic one so a rejection line is readable: that substitution is
    /// right for a log and is a capability DIFFERENCE against an incumbent that does not make it, so the two
    /// answers are kept apart rather than shared (V-G1, and see
    /// <see cref="VulkanCapabilityRead.ReportedDeviceName"/>).</param>
    /// <param name="MaxMsaaSampleCount">The incumbent's own computation reproduced (V-C5): the minimum, over the
    /// three formats the 3D scene's MRT renders into, of the highest sample count each supports. See
    /// <see cref="VulkanMsaaLimit"/> for the citation and for why neither draft's invented formula is
    /// taken.</param>
    /// <param name="Memory">Every memory type plus the three limits the block suballocator's and the uniform
    /// ring's arithmetic need (sections 9.1 and 9.2). Read here rather than at the allocator because it is the
    /// same walk
    /// <see cref="VulkanDeviceFacts.HasCoherentHostVisibleMemoryType"/> already needed, and two walks of one
    /// device's memory properties would be two chances to disagree about what it exposes.</param>
    /// <param name="PipelineCacheIdentity">The vendor id, device id, driver version and
    /// <c>pipelineCacheUUID</c> the persisted <c>VkPipelineCache</c> is keyed and validated on (V-S7). Read here
    /// for the reason everything else here is: it comes off the SAME
    /// <c>VkPhysicalDeviceProperties</c> the version floor and the dynamic-uniform limit came off, and a second
    /// query could answer for a different device on a machine whose enumeration order moved.</param>
    internal readonly record struct VulkanPhysicalDeviceRead(
        VulkanDeviceFacts Facts,
        VulkanFeatureSupport Features,
        VulkanPhysicalDeviceClass Class,
        bool IsLlvmpipe,
        uint GraphicsQueueFamily,
        bool SupportsShadowMapFormat,
        int MaxMsaaSampleCount,
        VulkanMemoryFacts Memory,
        VulkanPipelineCacheIdentity PipelineCacheIdentity,
        string ReportedDeviceName);

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

            // TWO NAMES OFF ONE READ, deliberately. The raw one is what the capability seam carries and the
            // substituted one is what a log line prints, and collapsing them would either put "unnamed device
            // 0x…" into a capability the incumbent answers "" for, or put an empty string into a rejection
            // message nobody could act on.
            string reportedName = ReadReportedDeviceName(&properties);
            string name = ReadDeviceName(&properties);
            uint graphicsFamily = FirstGraphicsQueueFamily(vk, device);
            VulkanMemoryFacts memory = ReadMemory(vk, device, &properties);

            var facts = new VulkanDeviceFacts(
                DeviceName: name,
                ApiVersion: properties.ApiVersion,
                DynamicRendering: features.DynamicRendering,
                Synchronization2: features.Synchronization2,
                TimelineSemaphore: features.TimelineSemaphore,
                HasCoherentHostVisibleMemoryType: memory.HasCoherentHostVisibleType,
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
                SupportsShadowMapFormat(vk, device),
                MaxMsaaSampleCount(vk, device),
                memory,
                ReadPipelineCacheIdentity(&properties),
                reportedName);
        }

        /// <summary>
        /// The four values a persisted <c>VkPipelineCache</c> is keyed and header-validated on (V-S7). The UUID is
        /// COPIED out of the fixed buffer for the same reason
        /// <see cref="ReadDeviceName"/>'s string is: this whole read is a snapshot that outlives the properties
        /// structure it came from, and on the probe's path it outlives the instance as well.
        /// </summary>
        internal static VulkanPipelineCacheIdentity ReadPipelineCacheIdentity(
            PhysicalDeviceProperties* properties)
            => new(properties->VendorID, properties->DeviceID, properties->DriverVersion,
                new ReadOnlySpan<byte>(properties->PipelineCacheUuid, VulkanPipelineCacheIdentity.UuidLength));

        /// <summary>
        /// The driver's name for the device VERBATIM, copied out of the fixed byte buffer into a managed string,
        /// and empty when the driver reported nothing. This is the capability seam's answer (V-G1), where
        /// <see cref="ReadDeviceName"/> is the log's: the substitution that makes a rejection line readable is a
        /// capability difference against an incumbent that performs none, and
        /// <c>GpuCapabilities.DeviceName</c> is compared string for string by the parity test.
        /// </summary>
        internal static string ReadReportedDeviceName(PhysicalDeviceProperties* properties)
            => VulkanCapabilityRead.ReportedDeviceName(
                SilkMarshal.PtrToString((nint)properties->DeviceName));

        /// <summary>The driver's name for the device, copied out of the fixed byte buffer into a managed string so
        /// it can outlive the instance. A driver that reports nothing readable still has to be nameable in a log
        /// line, so this never returns null or empty. NOT the capability answer: that is
        /// <see cref="ReadReportedDeviceName"/>.</summary>
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
        // which is 1.2 core, so it is only asked of a device that clears the floor. The name check runs
        // unconditionally, on every path, as a belt to the driverID check rather than only as a fallback for a
        // driver too old to report one. That is slightly wider than V-G2's letter and harmless: lavapipe reports
        // itself as a Cpu device type regardless.
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

        // The memory walk, ONCE. It answers V-M4's question (is there a host-visible coherent type, which the
        // probe refuses a device without) and it produces the type table plus the three limits the block
        // suballocator and the uniform ring run on (sections 9.1 and 9.2). Doing it once is what stops the probe's
        // answer and the allocator's view of the same device drifting apart.
        //
        // NOTHING HERE READS bufferImageGranularity, and that is deliberate rather than an omission: linear and
        // optimal allocations never share a chunk (V-M2), so the constraint is satisfied structurally and there is
        // no arithmetic to feed. The incumbent reads it and rounds every non-dedicated request by it.
        static VulkanMemoryFacts ReadMemory(Vk vk, PhysicalDevice device, PhysicalDeviceProperties* properties)
        {
            PhysicalDeviceMemoryProperties memory;
            vk.GetPhysicalDeviceMemoryProperties(device, &memory);

            var types = new VulkanMemoryTypeInfo[memory.MemoryTypeCount];
            for (uint i = 0; i < memory.MemoryTypeCount; i++)
            {
                MemoryType type = memory.MemoryTypes[(int)i];
                types[i] = new VulkanMemoryTypeInfo(i, type.HeapIndex, Translate(type.PropertyFlags));
            }

            // nonCoherentAtomSize is required to be a power of two and at least 1, but a driver that reports 0
            // would make every range rounding a division by a mask of all ones. Substituting 1 turns that into an
            // identity rather than into a throw at device creation on a machine that is otherwise fine.
            ulong atom = properties->Limits.NonCoherentAtomSize;
            if (atom == 0) atom = 1;

            return new VulkanMemoryFacts(types, atom, properties->Limits.MaxMemoryAllocationCount,
                properties->Limits.MinUniformBufferOffsetAlignment);
        }

        // VkMemoryPropertyFlags to the allocator's own flags, in the ONE place a Silk.NET memory enum is turned
        // into something the device-free policy types can decide on.
        static VulkanMemoryTrait Translate(MemoryPropertyFlags flags)
        {
            VulkanMemoryTrait traits = VulkanMemoryTrait.None;

            if ((flags & MemoryPropertyFlags.DeviceLocalBit) != 0) traits |= VulkanMemoryTrait.DeviceLocal;
            if ((flags & MemoryPropertyFlags.HostVisibleBit) != 0) traits |= VulkanMemoryTrait.HostVisible;
            if ((flags & MemoryPropertyFlags.HostCoherentBit) != 0) traits |= VulkanMemoryTrait.HostCoherent;
            if ((flags & MemoryPropertyFlags.HostCachedBit) != 0) traits |= VulkanMemoryTrait.HostCached;
            if ((flags & MemoryPropertyFlags.LazilyAllocatedBit) != 0) traits |= VulkanMemoryTrait.LazilyAllocated;
            if ((flags & MemoryPropertyFlags.ProtectedBit) != 0) traits |= VulkanMemoryTrait.Protected;

            return traits;
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

        /// <summary>
        /// The two format features <c>GpuCapabilities.SupportsShadowMaps</c> asks <c>R32_SFLOAT</c> about, held as
        /// a constant so the question itself is assertable on a machine with no Vulkan loader.
        /// <para>
        /// COLOUR ATTACHMENT, NOT DEPTH-STENCIL, and asking the other one is not a stricter question but a
        /// structurally false one. <c>R32_SFLOAT</c> is a colour format, so no driver reports
        /// <c>VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT</c> for it, and the shadow pass never wanted that
        /// bit: <c>ShadowMapRenderer</c> creates the atlas as <c>R32Float</c> with
        /// <c>RenderTarget | Sampled</c> and hangs a SEPARATE depth-stencil off it, so the two features the pass
        /// actually needs are render target and sampled image.
        /// </para>
        /// <para>
        /// THE INCUMBENT ASKS THIS PAIR TOO, which is what makes it the parity answer as well as the correct one:
        /// <c>VeldridMap.SupportsShadowMaps</c> calls <c>GetPixelFormatSupport</c> with
        /// <c>RenderTarget | Sampled</c>. The Direct3D 11 sibling
        /// (<c>D3D11DxgiQueries.SupportsShadowMapsWindows</c>) settled the same question with the warning worth
        /// repeating here: a capability question stricter than the incumbent's reports false where the incumbent
        /// reports true, and the visible result is the shadow path degrading to blob shadows on ONE backend only,
        /// silently, with nothing failing.
        /// </para>
        /// </summary>
        internal const FormatFeatureFlags ShadowMapFormatFeatures =
            FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.SampledImageBit;

        /// <summary>The DECISION half of the shadow-map question, given what a driver reported for
        /// <c>R32_SFLOAT</c>'s optimal tiling. Split from the read so the bits are pinnable device-free, the same
        /// split every other rule in this backend gets.</summary>
        internal static bool SupportsShadowMapFormat(FormatFeatureFlags optimalTilingFeatures)
            => (optimalTilingFeatures & ShadowMapFormatFeatures) == ShadowMapFormatFeatures;

        // GpuCapabilities.SupportsShadowMaps: R32_SFLOAT usable as both a colour attachment and a sampled image,
        // which is what the shadow pass renders into and then reads.
        static bool SupportsShadowMapFormat(Vk vk, PhysicalDevice device)
        {
            FormatProperties properties;
            vk.GetPhysicalDeviceFormatProperties(device, Format.R32Sfloat, &properties);

            return SupportsShadowMapFormat(properties.OptimalTilingFeatures);
        }

        // GpuCapabilities.MaxMsaaSampleCount (V-C5), which is the INCUMBENT'S computation and not one of its
        // two drafts': vkGetPhysicalDeviceImageFormatProperties per format with the usage that format is used
        // under, reduced to the highest supported bit, minimised over the engine's three MRT targets. The fold,
        // the reduction and the three-format table are VulkanMsaaLimit's and are device-free. This is the one
        // line that names the driver call.
        //
        // A FAILED QUERY ANSWERS "NO MSAA" RATHER THAN THROWING, which is the incumbent's answer arrived at
        // explicitly. It ignores the result entirely, so a failure leaves it reading a zeroed structure whose
        // sampleCounts is 0, which its ladder reduces to 1. Checking the result and saying 1 is the same
        // observable value with the reason written down: a format the device cannot make an image of at all
        // supports no multisampling of it either.
        static int MaxMsaaSampleCount(Vk vk, PhysicalDevice device)
            => VulkanMsaaLimit.MinOverTheEngineTargets((format, depthAttachment) =>
            {
                ImageUsageFlags usage = ImageUsageFlags.SampledBit
                    | (depthAttachment
                        ? ImageUsageFlags.DepthStencilAttachmentBit
                        : ImageUsageFlags.ColorAttachmentBit);

                Result queried = vk.GetPhysicalDeviceImageFormatProperties(
                    device,
                    // THE DEPTH FLAG DOES NOT REACH HERE, deliberately: the incumbent passes its own depthFormat
                    // argument to the USAGE bits alone and maps the format with the default. See VulkanMsaaLimit.
                    VulkanFormats.ToVkFormat(format, depthStencil: false),
                    ImageType.Type2D,
                    ImageTiling.Optimal,
                    usage,
                    ImageCreateFlags.None,
                    out ImageFormatProperties properties);

                return queried == Result.Success ? properties.SampleCounts : SampleCountFlags.Count1Bit;
            });

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
