using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The binding-sufficiency spike, verification task one of work-breakdown row 1 in
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>. It exists to fail at COMPILE time if the
    /// Silk.NET Vulkan binding stops carrying an API this design is built on, and it exists NOWHERE else: no
    /// method here is ever called, on any path, in any configuration.
    /// <para>
    /// Decision V-P2 takes <c>Silk.NET.Vulkan</c> on the <c>2.23.0</c> line the windowing, input and audio
    /// stacks already pin, and section 3.1 names <c>Vortice.Vulkan</c> as the replacement if the binding turns
    /// out to be missing something core-1.3 this design needs. This file is that decision point, taken once and
    /// then left standing as a tripwire. Every surface below is one the design SPENDS: a Vulkan-1.3 core
    /// promotion the backend calls directly (timeline semaphores, dynamic rendering, synchronization2), an
    /// extension it loads by hand, or a piece of the loader's own function-pointer acquisition. A binding
    /// regression, a package downgrade, or a rename between Silk.NET lines shows up here as a red build in the
    /// row that changed the pin, rather than as a missing member in whichever row first needed it.
    /// </para>
    /// <para>
    /// The inventory, one never-called static method per area:
    /// </para>
    /// <list type="bullet">
    /// <item>The loader and its per-instance and per-device function-pointer acquisition (3.1).</item>
    /// <item><c>VK_EXT_validation_features</c> chained into instance creation, the V-G3 sync-validation rung (5.1).</item>
    /// <item>The device feature chain: <c>VkPhysicalDeviceFeatures2</c> with the 1.2 and 1.3 feature structures
    /// hung off <c>pNext</c>, which is how 5.2 both QUERIES support and enables features by name (5.2).</item>
    /// <item>Timeline semaphores, create through host wait through the non-blocking counter read (10.1).</item>
    /// <item>Dynamic rendering, command-side and pipeline-side (2.3, 7.1).</item>
    /// <item>Synchronization2 barriers, all three kinds (10.3).</item>
    /// <item>Dedicated-allocation discovery and the allocation that acts on it (9.1).</item>
    /// <item>The three platform surface extensions (5.1).</item>
    /// <item>The surface capability, format, present-mode and queue-support queries (5.1, 11.1).</item>
    /// <item>The swapchain itself: create, image enumeration, acquire, present, destroy (11.1 to 11.3).</item>
    /// <item>The <c>VK_EXT_debug_utils</c> messenger and its callback signature, plus object naming (5.1, V-G5).</item>
    /// </list>
    /// <para>
    /// The swapchain and surface areas earn their place by being the ones NO test can reach: every GPU CI leg is
    /// headless, so 11.1 to 11.3 has zero automated coverage and a binding surprise there would otherwise land
    /// in a hand-run windowed session many rows later. A compile-time inventory is the only cheap check
    /// available for them, so they are the last surfaces that should have been left out.
    /// </para>
    /// <para>
    /// It is deliberately readable as an API INVENTORY rather than as sample code: each method takes its
    /// handles as parameters, so nothing constructs a device and nothing touches a loader. Compiling it proves
    /// the members exist with the shapes the design assumes. It proves nothing about runtime behaviour, which
    /// is what the one local Linux loader smoke in the package README covers.
    /// </para>
    /// </summary>
    internal static unsafe class VulkanBindingSpike
    {
        /// <summary>
        /// The loader and its function-pointer acquisition (section 3.1). <c>Vk.GetApi</c> is the entry point
        /// the whole binding hangs off, <c>EnumerateInstanceVersion</c> is what section 5.1 clamps the instance
        /// apiVersion against (the incumbent hardcodes 1.0.0 at two sites and never calls it), and the two
        /// <c>TryGet*Extension</c> generics are the per-instance and per-device loading a hand-rolled binding
        /// would have to invent. <c>Vk.Version13</c> is the floor every one of the four hard device
        /// requirements in 5.2 is measured against.
        /// </summary>
        static void LoaderAndFunctionPointers(Instance instance, Device device)
        {
            Vk vk = Vk.GetApi();

            uint loaderVersion = 0;
            Result versionResult = vk.EnumerateInstanceVersion(ref loaderVersion);
            Version32 floor = Vk.Version13;
            uint clamped = Math.Min(loaderVersion, (uint)floor);

            // Per-INSTANCE function pointers: the surface extension and the debug-utils messenger.
            bool haveSurface = vk.TryGetInstanceExtension(instance, out KhrSurface _, KhrSurface.ExtensionName);
            bool haveDebugUtils = vk.TryGetInstanceExtension(instance, out ExtDebugUtils _, ExtDebugUtils.ExtensionName);

            // Per-DEVICE function pointers: the swapchain, windowed path only (V-N6).
            bool haveSwapchain = vk.TryGetDeviceExtension(instance, device, out KhrSwapchain _, KhrSwapchain.ExtensionName);

            Consume(versionResult, clamped, haveSurface, haveDebugUtils, haveSwapchain);
        }

        /// <summary>
        /// <c>VK_EXT_validation_features</c> at instance creation (section 5.1, decision V-G3). The default
        /// validation layer does not run synchronization validation, so the rung above plain validation is
        /// requested by chaining <c>VkValidationFeaturesEXT</c> into <c>VkInstanceCreateInfo.pNext</c>. That is
        /// the surface worth pinning: the enable is a <c>pNext</c> extension of a structure the backend builds
        /// anyway, so a binding that dropped it would fail silently as validation that simply never reports a
        /// race, rather than as an error anyone sees.
        /// </summary>
        static void InstanceValidationFeatures(Vk vk)
        {
            ValidationFeatureEnableEXT syncValidation = ValidationFeatureEnableEXT.SynchronizationValidationExt;
            var validation = new ValidationFeaturesEXT(
                sType: StructureType.ValidationFeaturesExt,
                enabledValidationFeatureCount: 1,
                pEnabledValidationFeatures: &syncValidation);

            var appInfo = new ApplicationInfo(
                sType: StructureType.ApplicationInfo, apiVersion: (uint)Vk.Version13);

            var createInfo = new InstanceCreateInfo(
                sType: StructureType.InstanceCreateInfo,
                pNext: &validation,
                pApplicationInfo: &appInfo);

            Result created = vk.CreateInstance(in createInfo, null, out Instance _);
            Consume(created, validation.EnabledValidationFeatureCount);
        }

        /// <summary>
        /// The device feature chain (section 5.2, and the probe row 2 builds on it). The four hard device
        /// requirements are 1.2 and 1.3 feature BITS rather than extensions, and both halves of the design run
        /// through the same chained structure: the probe QUERIES support with
        /// <c>vkGetPhysicalDeviceFeatures2</c> and a <c>VkPhysicalDeviceFeatures2</c> carrying the 1.2 and 1.3
        /// structures on its <c>pNext</c>, then device creation enables the same bits BY NAME through the same
        /// chain rather than handing the driver every supported feature back. A binding that carried the
        /// feature structures but not the query, or vice versa, would break exactly one of those two halves.
        /// </summary>
        static void DeviceFeatureChain(Vk vk, PhysicalDevice physicalDevice)
        {
            var features13 = new PhysicalDeviceVulkan13Features(
                sType: StructureType.PhysicalDeviceVulkan13Features);

            var features12 = new PhysicalDeviceVulkan12Features(
                sType: StructureType.PhysicalDeviceVulkan12Features, pNext: &features13);

            var features2 = new PhysicalDeviceFeatures2(
                sType: StructureType.PhysicalDeviceFeatures2, pNext: &features12);

            vk.GetPhysicalDeviceFeatures2(physicalDevice, &features2);

            Consume(
                features2.Features.SamplerAnisotropy,
                features12.TimelineSemaphore,
                features12.DescriptorIndexing,
                features13.DynamicRendering,
                features13.Synchronization2);
        }

        /// <summary>
        /// Timeline semaphores (section 10.1, V-F1 to V-F4). One device timeline replaces per-submit fences,
        /// so the create-time type structure, the submit-time value structure, the non-blocking counter read
        /// that <c>IGpuFence.Signaled</c> becomes, the host wait <c>WaitForIdle</c> becomes, and the host
        /// signal all have to exist. The 1.2 feature bit is here too, because 5.2 enables it BY NAME through
        /// the <c>pNext</c> chain rather than handing the driver every supported feature.
        /// </summary>
        static void TimelineSemaphores(Vk vk, Device device, Semaphore timeline)
        {
            var typeInfo = new SemaphoreTypeCreateInfo(
                sType: StructureType.SemaphoreTypeCreateInfo,
                semaphoreType: SemaphoreType.Timeline,
                initialValue: 0);

            var createInfo = new SemaphoreCreateInfo(sType: StructureType.SemaphoreCreateInfo, pNext: &typeInfo);
            Result created = vk.CreateSemaphore(device, in createInfo, null, out Semaphore _);

            // The non-blocking read. This is exactly what the seam's fence contract needs and why one monotonic
            // timeline makes the "a later fence transitively covers every earlier submission" property a
            // theorem rather than a convention.
            Result read = vk.GetSemaphoreCounterValue(device, timeline, out ulong counter);

            // The host wait, which WaitForIdle becomes (counted into DrainCount / DrainMs).
            ulong target = counter + 1;
            var waitInfo = new SemaphoreWaitInfo(
                sType: StructureType.SemaphoreWaitInfo,
                semaphoreCount: 1,
                pSemaphores: &timeline,
                pValues: &target);
            Result waited = vk.WaitSemaphores(device, in waitInfo, ulong.MaxValue);

            var signalInfo = new SemaphoreSignalInfo(
                sType: StructureType.SemaphoreSignalInfo, semaphore: timeline, value: target);
            Result signalled = vk.SignalSemaphore(device, in signalInfo);

            // The submit-side half: the value a vkQueueSubmit signals rides in the pNext chain of SubmitInfo.
            var submitValues = new TimelineSemaphoreSubmitInfo(
                sType: StructureType.TimelineSemaphoreSubmitInfo,
                signalSemaphoreValueCount: 1,
                pSignalSemaphoreValues: &target);

            var feature = new PhysicalDeviceVulkan12Features(
                sType: StructureType.PhysicalDeviceVulkan12Features, timelineSemaphore: true);

            Consume(created, read, waited, signalled, submitValues.SignalSemaphoreValueCount, feature.TimelineSemaphore);
        }

        /// <summary>
        /// Dynamic rendering (section 2.3 and 7.1, V-A1 to V-A4). There is no render pass and no framebuffer
        /// object anywhere in this design, so <c>vkCmdBeginRendering</c> plus the attachment structure that
        /// folds a clear into <c>loadOp</c> is not one option among several, it is the whole rendering path.
        /// <c>PipelineRenderingCreateInfo</c> is the pipeline-side half (row 13), built from the seam's
        /// <c>GpuOutputDescription</c>.
        /// </summary>
        static void DynamicRendering(Vk vk, CommandBuffer cmd, ImageView colorView, ImageView depthView)
        {
            var color = new RenderingAttachmentInfo(
                sType: StructureType.RenderingAttachmentInfo,
                imageView: colorView,
                imageLayout: ImageLayout.ColorAttachmentOptimal,
                loadOp: AttachmentLoadOp.Clear,
                storeOp: AttachmentStoreOp.Store,
                clearValue: new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f)));

            var depth = new RenderingAttachmentInfo(
                sType: StructureType.RenderingAttachmentInfo,
                imageView: depthView,
                imageLayout: ImageLayout.DepthStencilAttachmentOptimal,
                loadOp: AttachmentLoadOp.Load,
                storeOp: AttachmentStoreOp.Store);

            var info = new RenderingInfo(
                sType: StructureType.RenderingInfo,
                renderArea: new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)),
                layerCount: 1,
                colorAttachmentCount: 1,
                pColorAttachments: &color,
                pDepthAttachment: &depth);

            vk.CmdBeginRendering(cmd, in info);
            vk.CmdEndRendering(cmd);

            Format colorFormat = Format.B8G8R8A8Unorm;
            var pipelineRendering = new PipelineRenderingCreateInfo(
                sType: StructureType.PipelineRenderingCreateInfo,
                colorAttachmentCount: 1,
                pColorAttachmentFormats: &colorFormat,
                depthAttachmentFormat: Format.D32SfloatS8Uint);

            var feature = new PhysicalDeviceVulkan13Features(
                sType: StructureType.PhysicalDeviceVulkan13Features, dynamicRendering: true);

            Consume(pipelineRendering.ColorAttachmentCount, feature.DynamicRendering);
        }

        /// <summary>
        /// Synchronization2 barriers (section 10.3, V-F6 to V-F9). The whole barrier tracker is
        /// <c>vkCmdPipelineBarrier2</c> with explicit source and destination stage and access masks per
        /// barrier, which is what replaces the incumbent's if/else over layout pairs that ends in a debug
        /// assertion and silently emits <c>NONE</c> masks in Release. All three barrier2 structures are used,
        /// because the tracker emits all three kinds.
        /// </summary>
        static void Synchronization2(Vk vk, CommandBuffer cmd, Image image, Silk.NET.Vulkan.Buffer buffer)
        {
            var memory = new MemoryBarrier2(
                sType: StructureType.MemoryBarrier2,
                srcStageMask: PipelineStageFlags2.ComputeShaderBit,
                srcAccessMask: AccessFlags2.ShaderWriteBit,
                dstStageMask: PipelineStageFlags2.VertexShaderBit,
                dstAccessMask: AccessFlags2.UniformReadBit);

            var bufferBarrier = new BufferMemoryBarrier2(
                sType: StructureType.BufferMemoryBarrier2,
                srcStageMask: PipelineStageFlags2.TransferBit,
                srcAccessMask: AccessFlags2.TransferWriteBit,
                dstStageMask: PipelineStageFlags2.VertexShaderBit,
                dstAccessMask: AccessFlags2.UniformReadBit,
                srcQueueFamilyIndex: Vk.QueueFamilyIgnored,
                dstQueueFamilyIndex: Vk.QueueFamilyIgnored,
                buffer: buffer,
                offset: 0,
                size: Vk.WholeSize);

            var imageBarrier = new ImageMemoryBarrier2(
                sType: StructureType.ImageMemoryBarrier2,
                srcStageMask: PipelineStageFlags2.ColorAttachmentOutputBit,
                srcAccessMask: AccessFlags2.ColorAttachmentWriteBit,
                dstStageMask: PipelineStageFlags2.FragmentShaderBit,
                dstAccessMask: AccessFlags2.ShaderSampledReadBit,
                oldLayout: ImageLayout.ColorAttachmentOptimal,
                newLayout: ImageLayout.ShaderReadOnlyOptimal,
                srcQueueFamilyIndex: Vk.QueueFamilyIgnored,
                dstQueueFamilyIndex: Vk.QueueFamilyIgnored,
                image: image,
                subresourceRange: new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1));

            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                memoryBarrierCount: 1,
                pMemoryBarriers: &memory,
                bufferMemoryBarrierCount: 1,
                pBufferMemoryBarriers: &bufferBarrier,
                imageMemoryBarrierCount: 1,
                pImageMemoryBarriers: &imageBarrier);

            vk.CmdPipelineBarrier2(cmd, in dependency);

            var feature = new PhysicalDeviceVulkan13Features(
                sType: StructureType.PhysicalDeviceVulkan13Features, synchronization2: true);

            Consume(feature.Synchronization2);
        }

        /// <summary>
        /// Dedicated allocations (section 9.1). The allocator sub-allocates out of large blocks, except where
        /// the driver asks not to, and asking is the part with a binding shape: the requirement query is the
        /// <c>2</c> form with <c>VkMemoryDedicatedRequirements</c> hung off the OUT structure's <c>pNext</c>,
        /// and acting on the answer means chaining <c>VkMemoryDedicatedAllocateInfo</c> into the allocation.
        /// Both directions of the chain are inventoried here because they are separate members that a binding
        /// can lose independently, and the query one is silent when it goes: an unchained query returns
        /// success and simply never sets the bits.
        /// </summary>
        static void DedicatedAllocation(Vk vk, Device device, Image image)
        {
            var dedicatedRequirements = new MemoryDedicatedRequirements(
                sType: StructureType.MemoryDedicatedRequirements);

            var requirements = new MemoryRequirements2(
                sType: StructureType.MemoryRequirements2, pNext: &dedicatedRequirements);

            var query = new ImageMemoryRequirementsInfo2(
                sType: StructureType.ImageMemoryRequirementsInfo2, image: image);

            vk.GetImageMemoryRequirements2(device, in query, &requirements);

            var dedicated = new MemoryDedicatedAllocateInfo(
                sType: StructureType.MemoryDedicatedAllocateInfo, image: image);

            var allocateInfo = new MemoryAllocateInfo(
                sType: StructureType.MemoryAllocateInfo,
                pNext: &dedicated,
                allocationSize: requirements.MemoryRequirements.Size,
                memoryTypeIndex: 0);

            Result allocated = vk.AllocateMemory(device, in allocateInfo, null, out DeviceMemory _);

            Consume(
                allocated,
                requirements.MemoryRequirements.Alignment,
                dedicatedRequirements.PrefersDedicatedAllocation,
                dedicatedRequirements.RequiresDedicatedAllocation);
        }

        /// <summary>
        /// The three platform surface extensions (section 5.1, V-N6). Exactly one is enabled per process,
        /// chosen from <c>GpuWindowHandle.Kind</c> and only on the windowed path, so all three have to be
        /// nameable from one assembly that targets <c>net10.0</c> with no OS suffix. That is decision V-P1
        /// working: none of this needs a platform guard, because none of it resolves anything until it is
        /// called. There is no macOS surface here on purpose, and it is not an omission: VF9 declines MoltenVK
        /// because phase 4 ships a real Metal backend, so macOS never reaches this package at all.
        /// </summary>
        static void PlatformSurfaces(
            KhrWin32Surface win32, KhrXlibSurface xlib, KhrWaylandSurface wayland,
            Instance instance, nint handleA, nint handleB)
        {
            var win32Info = new Win32SurfaceCreateInfoKHR(
                sType: StructureType.Win32SurfaceCreateInfoKhr, hinstance: handleA, hwnd: handleB);
            Result win32Result = win32.CreateWin32Surface(instance, in win32Info, null, out SurfaceKHR _);

            var xlibInfo = new XlibSurfaceCreateInfoKHR(
                sType: StructureType.XlibSurfaceCreateInfoKhr, dpy: &handleA, window: handleB);
            Result xlibResult = xlib.CreateXlibSurface(instance, in xlibInfo, null, out SurfaceKHR _);

            var waylandInfo = new WaylandSurfaceCreateInfoKHR(
                sType: StructureType.WaylandSurfaceCreateInfoKhr, display: &handleA, surface: &handleB);
            Result waylandResult = wayland.CreateWaylandSurface(instance, in waylandInfo, null, out SurfaceKHR _);

            Consume(win32Result, xlibResult, waylandResult, KhrSurface.ExtensionName);
        }

        /// <summary>
        /// The four <c>VK_KHR_surface</c> queries (section 5.1 and 11.1). Section 11.1 sizes the swapchain
        /// from the capability structure and picks its format and present mode from the two enumerations, and
        /// row 2's device probe rejects a physical device whose queue family cannot present to the target
        /// surface, which is the fourth query. All three enumerating calls take the two-pass count-then-fill
        /// shape, so the null second pass is what is pinned here.
        /// </summary>
        static void SurfaceQueries(KhrSurface surface, PhysicalDevice physicalDevice, SurfaceKHR target)
        {
            Result capabilities = surface.GetPhysicalDeviceSurfaceCapabilities(
                physicalDevice, target, out SurfaceCapabilitiesKHR caps);

            uint formatCount = 0;
            Result formats = surface.GetPhysicalDeviceSurfaceFormats(
                physicalDevice, target, ref formatCount, null);

            uint presentModeCount = 0;
            Result presentModes = surface.GetPhysicalDeviceSurfacePresentModes(
                physicalDevice, target, ref presentModeCount, null);

            Result supported = surface.GetPhysicalDeviceSurfaceSupport(
                physicalDevice, 0, target, out Bool32 canPresent);

            Consume(
                capabilities, caps.MinImageCount, caps.CurrentExtent.Width, caps.CurrentTransform,
                formats, formatCount, presentModes, presentModeCount, supported, canPresent);
        }

        /// <summary>
        /// The swapchain (sections 11.1 to 11.3, V-N6). This is the area with ZERO automated coverage: every
        /// GPU CI leg in this repo is headless, so nothing here is ever exercised by a test on any platform,
        /// and a binding surprise would surface in a hand-run windowed session many rows after the pin that
        /// caused it. That makes the compile-time inventory the only cheap check the swapchain gets, which is
        /// why the whole per-frame cycle is listed rather than creation alone: create, enumerate the images the
        /// driver actually made, acquire with the semaphore 11.2 waits on, present, and destroy on the resize
        /// path 11.3 owns.
        /// </summary>
        static void Swapchain(
            KhrSwapchain swapchain, Device device, Queue presentQueue, SurfaceKHR surface, Semaphore acquired)
        {
            var createInfo = new SwapchainCreateInfoKHR(
                sType: StructureType.SwapchainCreateInfoKhr,
                surface: surface,
                minImageCount: 3,
                imageFormat: Format.B8G8R8A8Unorm,
                imageColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr,
                imageExtent: new Extent2D(1280, 720),
                imageArrayLayers: 1,
                imageUsage: ImageUsageFlags.ColorAttachmentBit,
                imageSharingMode: SharingMode.Exclusive,
                preTransform: SurfaceTransformFlagsKHR.IdentityBitKhr,
                compositeAlpha: CompositeAlphaFlagsKHR.OpaqueBitKhr,
                presentMode: PresentModeKHR.FifoKhr,
                clipped: true,
                oldSwapchain: default);

            Result created = swapchain.CreateSwapchain(device, in createInfo, null, out SwapchainKHR handle);

            // The driver decides the real image count, so the two-pass count-then-fill is not optional.
            uint imageCount = 0;
            Result counted = swapchain.GetSwapchainImages(device, handle, ref imageCount, null);

            uint imageIndex = 0;
            Result acquireResult = swapchain.AcquireNextImage(
                device, handle, ulong.MaxValue, acquired, default, &imageIndex);

            var presentInfo = new PresentInfoKHR(
                sType: StructureType.PresentInfoKhr,
                waitSemaphoreCount: 1,
                pWaitSemaphores: &acquired,
                swapchainCount: 1,
                pSwapchains: &handle,
                pImageIndices: &imageIndex);

            Result presented = swapchain.QueuePresent(presentQueue, in presentInfo);

            swapchain.DestroySwapchain(device, handle, null);

            Consume(created, counted, imageCount, acquireResult, imageIndex, presented);
        }

        /// <summary>
        /// <c>VK_EXT_debug_utils</c> (section 5.1). The messenger is what <c>KE_VULKAN_VALIDATION</c> pumps
        /// into the engine log, and the callback's exact signature is the part worth pinning: it takes the
        /// message data by raw pointer and returns a <c>Bool32</c>, so a binding change there would break the
        /// validation gate rather than the render path, which is the failure that hides longest.
        /// </summary>
        static void DebugUtilsMessenger(ExtDebugUtils debugUtils, Instance instance)
        {
            var info = new DebugUtilsMessengerCreateInfoEXT(
                sType: StructureType.DebugUtilsMessengerCreateInfoExt,
                messageSeverity: DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                                 | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                messageType: DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                             | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                             | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                pfnUserCallback: new PfnDebugUtilsMessengerCallbackEXT(&OnMessage));

            Result created = debugUtils.CreateDebugUtilsMessenger(instance, in info, null, out DebugUtilsMessengerEXT _);
            Consume(created);
        }

        /// <summary>
        /// Object naming, the other half of <c>VK_EXT_debug_utils</c> (decision V-G5). Every resource the
        /// backend creates carries the seam's debug name into the driver, which is what makes a validation
        /// message and a RenderDoc capture name the offending resource instead of a bare handle. The shape to
        /// pin is that the handle is passed as a raw <c>ulong</c> alongside a <c>VkObjectType</c> rather than
        /// as the typed handle, so nothing here is checked by the type system and a binding change would be
        /// caught only by the name being on the wrong object.
        /// </summary>
        static void DebugObjectNames(ExtDebugUtils debugUtils, Device device, Image image)
        {
            ReadOnlySpan<byte> name = "KhaozEngine.ColorTarget"u8;
            fixed (byte* pName = name)
            {
                var info = new DebugUtilsObjectNameInfoEXT(
                    sType: StructureType.DebugUtilsObjectNameInfoExt,
                    objectType: ObjectType.Image,
                    objectHandle: image.Handle,
                    pObjectName: pName);

                Result named = debugUtils.SetDebugUtilsObjectName(device, in info);
                Consume(named, info.ObjectType);
            }
        }

        // The messenger callback's pinned shape, and the one place the spike found the binding stricter than
        // the design's prose. Silk.NET types PfnDebugUtilsMessengerCallbackEXT as a CDECL function pointer, so
        // the callback MUST be [UnmanagedCallersOnly] with the cdecl convention: a plain static method is a
        // compile error (CS8786), not a silently wrong ABI. That is the good failure direction, and it is
        // recorded here because it constrains row 4, which wires the validation pump: the callback cannot
        // capture, so whatever it logs through has to be reachable statically or through pUserData.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static Bool32 OnMessage(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT types,
            DebugUtilsMessengerCallbackDataEXT* data,
            void* userData)
        {
            Consume(severity, types, data->MessageIdNumber, (nint)userData);
            return false;
        }

        // Reads every value the inventory produces, so the spike carries no unused-value warnings and no
        // discard hides a member that quietly stopped existing. Warnings are errors here, so this is load
        // bearing rather than tidiness.
        static void Consume(params object?[] values) => GC.KeepAlive(values);
    }
}
