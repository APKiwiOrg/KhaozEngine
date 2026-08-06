using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The engine's native Vulkan backend as the GPU seam sees it. Registered by
    /// <see cref="KhaozEngineVulkan.Register"/> and consumed only through <see cref="IGpuBackendProvider"/>, so
    /// nothing outside this package ever names a Silk.NET type.
    /// <para>
    /// THE PROBE AND HEADLESS CREATION ARE BOTH REAL, and the WINDOWED path is not, which is exactly the state
    /// work-breakdown row 4 of <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> leaves the package
    /// in. <see cref="IsSupported"/> resolves a loader, creates a throwaway instance and reads every physical
    /// device against section 5.2's requirements. <see cref="CreateHeadless"/> then builds a real device on the
    /// shared refcounted instance (row 4, https://github.com/APKiwiOrg/KhaozEngine/issues/514).
    /// <see cref="CreateForWindow"/> throws until row 17
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527) builds the surface and the swapchain.
    /// </para>
    /// <para>
    /// That ordering is deliberate rather than an artefact. The probe answers a question about the MACHINE, which
    /// is what a settings screen and the fallback path consume, and it is the row that makes a silent fallback
    /// impossible: without it a soak session could measure the incumbent Veldrid Vulkan backend and file the
    /// numbers under the native one. Whether this package can build a device is a different fact, answered by
    /// whether the row that builds it has landed, and folding the two together would make the probe answer false
    /// for a reason that has nothing to do with the hardware.
    /// </para>
    /// <para>
    /// THE WINDOWED PATH REFUSES RATHER THAN HANDING BACK A DEVICE THAT CANNOT PRESENT, and that is a decision
    /// rather than an ordering accident. Everything a windowed device needs beyond a headless one (a
    /// <c>VkSurfaceKHR</c>, the presenting-family check V-N5 makes against it, <c>VK_KHR_swapchain</c>, the
    /// acquire ring) is row 17's. A device created without them would be adopted by <c>GpuDeviceContext</c>, would
    /// report a null swapchain framebuffer, and would render a window that never updates, which is a far worse
    /// answer to a tester than a refusal naming the row.
    /// </para>
    /// <para>
    /// No platform guard, anywhere, and that is decision V-P1 rather than an omission. Vulkan is not a Windows
    /// API: the same managed code runs on Windows and Linux, the loader is resolved at runtime, and a machine
    /// without one answers <see cref="IsSupported"/> with a named reason. The
    /// <c>[SupportedOSPlatformGuard]</c>-over-<c>NoInlining</c> apparatus <c>D3D11BackendProvider</c> opens with
    /// has no analogue here and must not be added back by analogy.
    /// </para>
    /// </summary>
    internal sealed class VulkanBackendProvider : IGpuBackendProvider
    {
        static readonly ILogger log = Log.For<VulkanBackendProvider>();

        /// <inheritdoc/>
        public bool IsSupported()
        {
            try
            {
                string? missing = VulkanSupportProbe.MissingRequirement();
                if (missing is null) return true;

                log.Info($"The native Vulkan backend is not available on this machine: {missing}.");
                return false;
            }
            catch (Exception ex)
            {
                // Deliberately broad, and the contract requires it: this probe must NEVER throw, because a probe
                // that blows up and a probe that answers no are the same answer to the settings screen and to the
                // fallback that consume it. Everything under it is a loader resolving native entry points, so the
                // failure can be anything from a DllNotFoundException to an EntryPointNotFoundException out of a
                // driver that exports less than its version claims.
                log.Info("The native Vulkan support probe could not answer, so this machine is reported as "
                    + $"unsupported. It threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request) => throw NotBuiltYet();

        /// <inheritdoc/>
        public GpuProviderDevice CreateHeadless() => VulkanGpuDevice.CreateHeadless();

        // The windowed refusal. It says which row builds the swapchain, because the creation path CATCHES this,
        // WARNs with the message and falls back to the incumbent, so this text is what a tester who named the
        // native backend actually reads. It must not read as a machine problem: an incapable machine is answered
        // by IsSupported above, with its own sentence, and decision V-I4 exists to keep the two tellable apart.
        static NotSupportedException NotBuiltYet()
            => new("The native Vulkan backend cannot create a windowed device yet. The instance, the device and "
                + "the queue ARE built (https://github.com/APKiwiOrg/KhaozEngine/issues/514) and the HEADLESS "
                + "path works, so the golden and snapshot paths reach a real native device. What is missing is "
                + "the surface, the presenting-family check and the swapchain, which land in "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/527. This is a statement about the package, "
                + "not about this machine: read GpuBackendSelector.IsBackendSupported for that. Until then the "
                + "Vulkan path through Veldrid is the working windowed one.");
    }
}
