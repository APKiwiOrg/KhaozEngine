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
    /// It is deliberately readable as an API INVENTORY rather than as sample code: one never-called static
    /// method per area, taking its handles as parameters so nothing constructs a device and nothing touches a
    /// loader. Compiling it proves the members exist with the shapes the design assumes. It proves nothing
    /// about runtime behaviour, which is what the Linux loader smoke in the package README covers.
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
