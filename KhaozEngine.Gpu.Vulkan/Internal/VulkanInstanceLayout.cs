using System;
using System.Collections.Generic;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-N6: exactly which extensions and layers an instance is created with, as a pure function of the
    /// path (headless or windowed) and the validation knob. THAT IS THE ENTIRE LIST, and the shortness is the
    /// decision rather than an omission.
    /// <para>
    /// <b>THE HEADLESS PATH ENABLES NO SURFACE EXTENSION AT ALL.</b> That is what lets the whole golden suite run
    /// on a machine with no display server, and it is also why the swapchain has zero CI coverage (MV9). A
    /// surface extension requested on a headless runner is not harmless: a loader without the Xlib libraries fails
    /// instance creation outright, so adding one "just in case" would take the golden leg down.
    /// </para>
    /// <para>
    /// <b>EXACTLY ONE PLATFORM SURFACE EXTENSION on the windowed path</b>, chosen from
    /// <see cref="GpuWindowKind"/>, never all three. Requesting Xlib and Wayland together is the shape that works
    /// on a developer machine with both and fails on a container with one.
    /// </para>
    /// <para>
    /// <b>ONE LAYER, AND ONLY UNDER THE KNOB.</b> The incumbent additionally requested
    /// <c>VK_LAYER_LUNARG_standard_validation</c>, removed from the SDK in 2020, and passes layers to
    /// <c>vkCreateDevice</c>, which modern loaders ignore. Neither happens here.
    /// </para>
    /// <para>
    /// Everything is pure and returns plain strings, so the list an instance would be created with is asserted
    /// under <c>dotnet test</c> on a machine with no Vulkan loader.
    /// </para>
    /// </summary>
    internal static class VulkanInstanceLayout
    {
        /// <summary>The one device extension, and only on the windowed path. The headless device asks for
        /// nothing at all, which is why a machine with no display server can still create one.</summary>
        internal const string SwapchainDeviceExtension = KhrSwapchain.ExtensionName;

        /// <summary>
        /// The instance extensions for a HEADLESS device: the debug-utils extension under the validation knob,
        /// and otherwise nothing. Not even <c>VK_KHR_surface</c>, which is a surface extension and is windowed
        /// only.
        /// </summary>
        internal static IReadOnlyList<string> HeadlessInstanceExtensions(VulkanValidationMode mode)
            => VulkanValidation.WantsMessenger(mode)
                ? new[] { ExtDebugUtils.ExtensionName }
                : Array.Empty<string>();

        /// <summary>
        /// The instance extensions for a WINDOWED device on <paramref name="window"/>: <c>VK_KHR_surface</c>,
        /// exactly one platform surface extension, and the debug-utils extension under the validation knob.
        /// </summary>
        /// <exception cref="NotSupportedException"><paramref name="window"/> is a window kind Vulkan has no
        /// surface extension for in this backend. macOS is the one that matters, and it is not an oversight:
        /// presenting there needs MoltenVK's <c>VK_EXT_metal_surface</c>, and phase 4 of the program brings a real
        /// Metal backend rather than a translation layer.</exception>
        internal static IReadOnlyList<string> WindowedInstanceExtensions(GpuWindowKind window,
            VulkanValidationMode mode)
        {
            var extensions = new List<string>(3) { KhrSurface.ExtensionName, SurfaceExtensionFor(window) };
            if (VulkanValidation.WantsMessenger(mode)) extensions.Add(ExtDebugUtils.ExtensionName);
            return extensions;
        }

        /// <summary>
        /// The ONE platform surface extension <paramref name="window"/> needs. Split out because it is the whole
        /// content of "exactly one of the Win32, Xlib or Wayland surface extensions chosen from
        /// <c>GpuWindowHandle.Kind</c>", and because the swapchain row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527) reads it again to pick which create-surface call
        /// to make.
        /// </summary>
        internal static string SurfaceExtensionFor(GpuWindowKind window) => window switch
        {
            GpuWindowKind.Win32 => KhrWin32Surface.ExtensionName,
            GpuWindowKind.X11 => KhrXlibSurface.ExtensionName,
            GpuWindowKind.Wayland => KhrWaylandSurface.ExtensionName,
            // Every member is spelled out rather than leaning on a discard, so a window kind appended later shows
            // up here as a compile-time gap to fill instead of reading as macOS.
            GpuWindowKind.Cocoa => throw new NotSupportedException(
                "The native Vulkan backend cannot present to a Cocoa window. Vulkan on macOS is MoltenVK over "
                + "Metal, which needs VK_EXT_metal_surface and a translation layer this backend deliberately does "
                + "not carry: phase 4 of the native GPU program "
                + "(https://github.com/APKiwiOrg/KhaozEngine/issues/420) brings a real Metal backend instead. This "
                + "package loads harmlessly on macOS and is never selected there."),
            _ => throw new NotSupportedException(
                $"The native Vulkan backend has no surface extension for GpuWindowKind '{window}'. The windowed "
                + "path supports Win32, X11 and Wayland."),
        };

        /// <summary>The instance layers: <c>VK_LAYER_KHRONOS_validation</c> under the knob, and nothing
        /// otherwise. A separate member from the extensions because the loader takes them as two separate arrays
        /// and because the layer is the half that can be absent on a machine that has the extension.</summary>
        internal static IReadOnlyList<string> InstanceLayers(VulkanValidationMode mode)
            => VulkanValidation.WantsMessenger(mode)
                ? new[] { VulkanValidation.LayerName }
                : Array.Empty<string>();
    }
}
