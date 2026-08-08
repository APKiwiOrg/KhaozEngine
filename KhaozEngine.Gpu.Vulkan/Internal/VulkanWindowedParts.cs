namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT A WINDOWED DEVICE HAS THAT A HEADLESS ONE DOES NOT, in one object, so the device's constructor takes
    /// ONE nullable parameter rather than six that must always travel together and must always be either all
    /// present or all absent.
    /// <para>
    /// <b>NULL IS THE HEADLESS DEVICE and it is a real state rather than an unfinished one.</b> The golden and
    /// snapshot paths create a device with no surface, no swapchain extension and no presentation requirement at
    /// all (V-N6), which is what lets the whole golden suite run on a machine with no display server. That device
    /// answers <c>SwapchainFramebuffer</c> with null and refuses <c>Present</c> by name, and both of those are
    /// correct rather than unbuilt.
    /// </para>
    /// <para>
    /// The surface is created BEFORE the device, because the presenting-family check V-N5 makes against it has to
    /// be settled while the physical device is still being chosen. It is therefore handed in here already made,
    /// and the present boundary takes it over: destroying it is part of the boundary's teardown.
    /// </para>
    /// </summary>
    /// <param name="Surfaces">The surface seam, already bound to the chosen physical device.</param>
    /// <param name="Swapchains">The swapchain seam.</param>
    /// <param name="Surface">The <c>VkSurfaceKHR</c>, already created from the window.</param>
    /// <param name="Requested">The backbuffer size the device was asked for, used only while the surface dictates
    /// none of its own.</param>
    /// <param name="SyncToVerticalBlank">The initial vsync setting, which selects the present-mode ladder and
    /// which a later change re-selects through a full recreate.</param>
    /// <param name="Acquire">Which of MV2's two acquire models this run uses.</param>
    internal sealed record VulkanWindowedParts(
        IVulkanSurfaceApi Surfaces,
        IVulkanSwapchainApi Swapchains,
        ulong Surface,
        VulkanExtent Requested,
        bool SyncToVerticalBlank,
        VulkanAcquireMode Acquire);

    /// <summary>
    /// WHAT THE CREATION PATH CARRIES FROM THE PROVIDER DOWN TO THE DEVICE on the windowed path: the seam's own
    /// request plus the acquire model this run resolved from the environment.
    /// <para>
    /// The acquire model is read ONCE, at creation, rather than at each boundary. Two reads of an environment
    /// variable that moved mid-process would give one device two acquire models, and MV2's A/B is a comparison of
    /// two captures each taken wholly in one position.
    /// </para>
    /// </summary>
    /// <param name="Request">The seam's windowed request: the window handle, the backbuffer size and the initial
    /// vsync setting.</param>
    /// <param name="Acquire">Which of MV2's two acquire models this run uses.</param>
    internal sealed record VulkanWindowRequest(GpuWindowedDeviceRequest Request, VulkanAcquireMode Acquire);
}
