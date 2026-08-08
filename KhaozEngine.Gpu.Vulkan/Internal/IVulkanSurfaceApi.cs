using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE FOUR NATIVE CALLS A SURFACE IS: one platform <c>vkCreate*SurfaceKHR</c> chosen from
    /// <see cref="GpuWindowKind"/>, <c>vkGetPhysicalDeviceSurfaceSupportKHR</c>, the three capability queries read
    /// as one report, and <c>vkDestroySurfaceKHR</c>.
    /// <para>
    /// The same split <see cref="IVulkanCommandApi"/> and <see cref="IVulkanResourceApi"/> take. What is left
    /// below this line is four driver calls with no decisions in them. What sits above it is every choice made
    /// from what they report, which is <see cref="VulkanSwapchainPolicy"/>, and all of it runs under
    /// <c>dotnet test</c> on a machine with no Vulkan loader and no window.
    /// </para>
    /// <para>
    /// HANDLES ARE <c>ulong</c>, so this interface and everything above it name no Silk.NET handle type at all,
    /// and a fake invents plain numbers rather than binding handles it has no instance to make. The ENUMS in
    /// <see cref="VulkanSurfaceReport"/> are Vulkan's own, deliberately: they are values rather than handles, and
    /// reproducing the incumbent's choices exactly is easier to read and to assert against the real names.
    /// </para>
    /// <para>
    /// <b>THE SURFACE IS AN INSTANCE-LEVEL OBJECT, WHICH IS WHY IT IS ITS OWN SEAM RATHER THAN PART OF THE
    /// SWAPCHAIN'S.</b> It is created from the instance and the window before a device exists, it is what
    /// <see cref="SupportsPresent"/> is asked about while a physical device is still being chosen, and it OUTLIVES
    /// every swapchain made against it. A recreate destroys swapchains and never this.
    /// </para>
    /// </summary>
    internal interface IVulkanSurfaceApi
    {
        /// <summary>
        /// The ONE platform surface call for <paramref name="kind"/>: <c>vkCreateWin32SurfaceKHR</c>,
        /// <c>vkCreateXlibSurfaceKHR</c> or <c>vkCreateWaylandSurfaceKHR</c>. Never all three, matching the one
        /// instance extension <see cref="VulkanInstanceLayout.SurfaceExtensionFor"/> asked for.
        /// </summary>
        /// <param name="kind">Which windowing system the handle belongs to.</param>
        /// <param name="windowHandle">The platform window handle (an <c>HWND</c>, an X11 <c>Window</c> or a
        /// <c>wl_surface</c>).</param>
        /// <param name="displayHandle">The platform display or connection handle, unused on Win32.</param>
        /// <returns>The <c>VkSurfaceKHR</c> handle. Never 0 on success.</returns>
        /// <exception cref="NotSupportedException"><paramref name="kind"/> has no surface extension in this
        /// backend, which is Cocoa.</exception>
        ulong CreateSurface(GpuWindowKind kind, IntPtr windowHandle, IntPtr displayHandle);

        /// <summary>
        /// <c>vkGetPhysicalDeviceSurfaceSupportKHR</c>: whether <paramref name="queueFamily"/> can present to
        /// <paramref name="surface"/>. Decision V-N5 requires the ONE graphics queue to be the presenting one, so
        /// a false here rejects the device with a named reason rather than starting a cross-family ownership
        /// transfer path nobody can produce a machine for.
        /// </summary>
        bool SupportsPresent(ulong surface, uint queueFamily);

        /// <summary>
        /// The three capability queries as one report:
        /// <c>vkGetPhysicalDeviceSurfaceCapabilitiesKHR</c>, <c>vkGetPhysicalDeviceSurfaceFormatsKHR</c> and
        /// <c>vkGetPhysicalDeviceSurfacePresentModesKHR</c>.
        /// <para>
        /// ONE CALL BECAUSE IT IS ONE READING. Every recreate re-reads all three, since a window that changed can
        /// change any of them, and three seam members would let a caller refresh two and reason from a stale
        /// third. It needs no queue and takes no lock, which is what lets the present boundary decide the whole
        /// create-info BEFORE it takes the submit lock.
        /// </para>
        /// <para>
        /// <b>IT REPORTS RATHER THAN THROWS, for the same reason the acquire and the present do.</b> Its caller is
        /// the present boundary, which never throws and never reports failure upward, and
        /// <c>VK_ERROR_SURFACE_LOST_KHR</c> shows up HERE first when a window dies under a running frame loop: the
        /// capability query is the first thing a recreate does. A throw at that point would leave
        /// <c>IGpuDevice.Present</c> propagating a driver failure into a frame loop that has no answer for one.
        /// </para>
        /// </summary>
        /// <param name="surface">The surface to read.</param>
        /// <param name="report">The reading, or the default when the answer is anything but
        /// <see cref="VulkanPresentOutcome.Success"/>.</param>
        /// <returns><see cref="VulkanPresentOutcome.Success"/>,
        /// <see cref="VulkanPresentOutcome.SurfaceLost"/> when the surface is gone, or
        /// <see cref="VulkanPresentOutcome.Failed"/> for anything else, which in practice is one of the two
        /// out-of-memory results.</returns>
        VulkanPresentOutcome Query(ulong surface, out VulkanSurfaceReport report);

        /// <summary><c>vkDestroySurfaceKHR</c>. TERMINAL, and called only once every swapchain made against the
        /// surface has been destroyed and the device has gone idle.</summary>
        void DestroySurface(ulong surface);
    }
}
