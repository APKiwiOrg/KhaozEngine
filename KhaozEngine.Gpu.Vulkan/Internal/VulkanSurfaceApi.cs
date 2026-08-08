using System;
using System.Collections.Generic;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL DRIVER CALLS BEHIND <see cref="IVulkanSurfaceApi"/>, and nothing else. Everything that decides
    /// anything from what they report is above this line in <see cref="VulkanSwapchainPolicy"/>, which is what
    /// makes the format choice, the present-mode ladder, the image count and the extent clamp assertable with no
    /// loader and no window.
    /// <para>
    /// EXACTLY ONE PLATFORM SURFACE CALL IS EVER MADE, chosen from the same <see cref="GpuWindowKind"/> that
    /// chose the one instance extension (V-N6). The other two extensions are not loaded and their entry points
    /// are never resolved, which is what makes an assembly with no OS platform guard correct rather than lucky:
    /// nothing here resolves until it is called.
    /// </para>
    /// <para>
    /// <c>vkGetPhysicalDeviceSurfaceSupportKHR</c> IS THE ONE CALL THAT RUNS BEFORE A DEVICE EXISTS, because
    /// V-N5 requires the one graphics queue to be the presenting one and that has to be settled while the
    /// physical device is still being chosen.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanSurfaceApi : IVulkanSurfaceApi
    {
        readonly Vk _vk;
        readonly Instance _instance;
        readonly KhrSurface _surface;

        // MUTABLE, AND THE ONE PIECE OF STATE HERE, because the windowed creation path has to ask
        // vkGetPhysicalDeviceSurfaceSupportKHR about EVERY candidate before it can choose one (V-N5), and the
        // surface it asks about is an instance-level object that exists before any of them is chosen. It is bound
        // to each candidate in turn during selection and then to the winner for the rest of the device's life.
        PhysicalDevice _physicalDevice;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="instance">The shared instance, which the surface is a child of.</param>
        /// <exception cref="NotSupportedException">The instance carries no <c>VK_KHR_surface</c>, which means a
        /// headless instance was handed to a windowed path.</exception>
        internal VulkanSurfaceApi(Vk vk, Instance instance)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
            _instance = instance;

            if (!vk.TryGetInstanceExtension(instance, out KhrSurface surface, KhrSurface.ExtensionName))
            {
                throw new NotSupportedException(
                    "The native Vulkan backend could not load VK_KHR_surface from its instance. The headless path "
                    + "enables no surface extension at all (V-N6), so this is a headless instance being asked for "
                    + "a windowed device. The single-instance model cannot serve both in one process: create the "
                    + "windowed device first, or run them in separate processes.");
            }

            _surface = surface;
        }

        /// <summary>
        /// Point every capability query at <paramref name="physicalDevice"/>. Called once per candidate while the
        /// physical device is being chosen, and once more with the winner. Not on the interface, because nothing
        /// above this line names a <c>VkPhysicalDevice</c> and nothing above this line chooses one either.
        /// </summary>
        internal void BindPhysicalDevice(PhysicalDevice physicalDevice) => _physicalDevice = physicalDevice;

        /// <inheritdoc/>
        public ulong CreateSurface(GpuWindowKind kind, IntPtr windowHandle, IntPtr displayHandle)
        {
            // ASKED FOR ITS OWN SAKE, so a window kind with no surface extension is turned away by the ONE method
            // that owns that mapping, carrying that method's own message, rather than by a second copy of it here
            // that would drift from it. Cocoa is the case that matters and it throws there.
            _ = VulkanInstanceLayout.SurfaceExtensionFor(kind);

            return kind switch
            {
                GpuWindowKind.Win32 => CreateWin32(windowHandle, displayHandle),
                GpuWindowKind.X11 => CreateXlib(windowHandle, displayHandle),
                GpuWindowKind.Wayland => CreateWayland(windowHandle, displayHandle),
                _ => throw new NotSupportedException(
                    $"The native Vulkan backend has no surface call for GpuWindowKind '{kind}'. This is "
                    + "unreachable while VulkanInstanceLayout.SurfaceExtensionFor covers the same set, and it is "
                    + "here so a window kind appended to the enum shows up as a refusal rather than as a zero "
                    + "handle."),
            };
        }

        /// <inheritdoc/>
        public bool SupportsPresent(ulong surface, uint queueFamily)
        {
            Result result = _surface.GetPhysicalDeviceSurfaceSupport(
                _physicalDevice, queueFamily, new SurfaceKHR(surface), out Bool32 supported);

            // A FAILED QUERY READS AS NO. The caller's answer to a family that cannot present is to reject the
            // device with a named reason and fall back, and a driver that cannot answer the question is in no
            // better shape than one that answered no.
            return !VulkanResultCodes.IsFailure(result) && supported;
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome Query(ulong surface, out VulkanSurfaceReport report)
        {
            report = default;
            var handle = new SurfaceKHR(surface);

            // REPORTED RATHER THAN REQUIRED, unlike every other creation-time call in this file. The caller is the
            // present boundary on a running frame loop rather than the device constructor, and this is where a
            // window that died shows up as VK_ERROR_SURFACE_LOST_KHR first.
            Result capabilities = _surface.GetPhysicalDeviceSurfaceCapabilities(
                _physicalDevice, handle, out SurfaceCapabilitiesKHR caps);

            if (VulkanResultCodes.IsFailure(capabilities))
            {
                return capabilities == Result.ErrorSurfaceLostKhr
                    ? VulkanPresentOutcome.SurfaceLost
                    : VulkanPresentOutcome.Failed;
            }

            report = new VulkanSurfaceReport(
                caps.MinImageCount,
                caps.MaxImageCount,
                new VulkanExtent(caps.CurrentExtent.Width, caps.CurrentExtent.Height),
                new VulkanExtent(caps.MinImageExtent.Width, caps.MinImageExtent.Height),
                new VulkanExtent(caps.MaxImageExtent.Width, caps.MaxImageExtent.Height),
                caps.CurrentTransform,
                ReadFormats(handle),
                ReadPresentModes(handle));

            return VulkanPresentOutcome.Success;
        }

        /// <inheritdoc/>
        public void DestroySurface(ulong surface)
        {
            if (surface == 0) return;

            _surface.DestroySurface(_instance, new SurfaceKHR(surface), null);
        }

        ulong CreateWin32(IntPtr windowHandle, IntPtr moduleHandle)
        {
            if (!_vk.TryGetInstanceExtension(_instance, out KhrWin32Surface win32, KhrWin32Surface.ExtensionName))
                throw Missing(KhrWin32Surface.ExtensionName);

            // A ZERO HINSTANCE IS THE INCUMBENT'S SHAPE, and the engine's windowing package passes no module
            // handle for a Win32 window either. Every Windows loader in this fleet accepts it, and reading the
            // module handle here would be a departure from the reproduction the design names exactly two of.
            var info = new Win32SurfaceCreateInfoKHR(
                sType: StructureType.Win32SurfaceCreateInfoKhr,
                hinstance: moduleHandle,
                hwnd: windowHandle);

            VulkanResultCodes.Require(
                win32.CreateWin32Surface(_instance, in info, null, out SurfaceKHR surface),
                "vkCreateWin32SurfaceKHR");
            return surface.Handle;
        }

        ulong CreateXlib(IntPtr windowHandle, IntPtr displayHandle)
        {
            if (!_vk.TryGetInstanceExtension(_instance, out KhrXlibSurface xlib, KhrXlibSurface.ExtensionName))
                throw Missing(KhrXlibSurface.ExtensionName);

            var info = new XlibSurfaceCreateInfoKHR(
                sType: StructureType.XlibSurfaceCreateInfoKhr,
                dpy: (nint*)displayHandle,
                window: windowHandle);

            VulkanResultCodes.Require(
                xlib.CreateXlibSurface(_instance, in info, null, out SurfaceKHR surface),
                "vkCreateXlibSurfaceKHR");
            return surface.Handle;
        }

        ulong CreateWayland(IntPtr windowHandle, IntPtr displayHandle)
        {
            if (!_vk.TryGetInstanceExtension(_instance, out KhrWaylandSurface wayland,
                KhrWaylandSurface.ExtensionName))
            {
                throw Missing(KhrWaylandSurface.ExtensionName);
            }

            var info = new WaylandSurfaceCreateInfoKHR(
                sType: StructureType.WaylandSurfaceCreateInfoKhr,
                display: (nint*)displayHandle,
                surface: (nint*)windowHandle);

            VulkanResultCodes.Require(
                wayland.CreateWaylandSurface(_instance, in info, null, out SurfaceKHR surface),
                "vkCreateWaylandSurfaceKHR");
            return surface.Handle;
        }

        IReadOnlyList<VulkanSurfaceFormatPair> ReadFormats(SurfaceKHR surface)
        {
            uint count = 0;
            Result counted = _surface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, surface, ref count, null);
            if (VulkanResultCodes.IsFailure(counted) || count == 0)
                return Array.Empty<VulkanSurfaceFormatPair>();

            var native = new SurfaceFormatKHR[count];
            fixed (SurfaceFormatKHR* buffer = native)
            {
                Result filled = _surface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, surface, ref count, buffer);
                if (VulkanResultCodes.IsFailure(filled)) return Array.Empty<VulkanSurfaceFormatPair>();
            }

            var pairs = new VulkanSurfaceFormatPair[count];
            for (uint i = 0; i < count; i++)
                pairs[i] = new VulkanSurfaceFormatPair(native[i].Format, native[i].ColorSpace);

            return pairs;
        }

        IReadOnlyList<PresentModeKHR> ReadPresentModes(SurfaceKHR surface)
        {
            uint count = 0;
            Result counted = _surface.GetPhysicalDeviceSurfacePresentModes(
                _physicalDevice, surface, ref count, null);

            // FIFO IS THE FLOOR THE LADDER ENDS ON and the specification requires every implementation to support
            // it, so a query that could not answer still leaves a working preference rather than no preference.
            if (VulkanResultCodes.IsFailure(counted) || count == 0) return new[] { PresentModeKHR.FifoKhr };

            var modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* buffer = modes)
            {
                Result filled = _surface.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice, surface, ref count, buffer);
                if (VulkanResultCodes.IsFailure(filled)) return new[] { PresentModeKHR.FifoKhr };
            }

            return modes;
        }

        static NotSupportedException Missing(string extension)
            => new("The native Vulkan backend could not load the instance extension '" + extension
                + "', which its own instance was created with. That means the instance is a HEADLESS one being "
                + "asked for a windowed device, since the headless path enables no surface extension at all "
                + "(V-N6). The single-instance model cannot serve both configurations in one process: create the "
                + "windowed device first, or run them in separate processes.");
    }
}
