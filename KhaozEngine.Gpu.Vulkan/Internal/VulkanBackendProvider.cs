using System;
using System.Threading;
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
    /// KEEPING THEM APART IS NOT THE SAME AS NOT ASKING, and that is what <see cref="CreateHeadless"/> got wrong
    /// until CI run 31062315211. It went straight to the device, so the only machine check on the creation path
    /// was whether the LOADER resolved, which is one of the three machine states
    /// (<see cref="VulkanSupportProbe"/> names all three). On a plain <c>ubuntu-latest</c> runner, a loader with
    /// no ICD behind it, the loader resolved and <c>vkCreateInstance</c> then failed
    /// <c>VK_ERROR_INCOMPATIBLE_DRIVER</c>, so a machine that cannot run this backend at all raised
    /// <see cref="InvalidOperationException"/> saying the failure happened "on a machine whose support probe
    /// answered yes" when no probe had been asked. The probe is now CONSULTED before creating, which is decision
    /// V-I4's split intact: a missing provider registration still THROWS, machine incapability still refuses
    /// through the probe, and the creation-time <see cref="InvalidOperationException"/> keeps its meaning because
    /// the sentence it makes about the probe is now true.
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

        // THE MACHINE GATE, asked at most once per provider instance, because the machine does not change while
        // the process runs and the probe is genuinely expensive: it creates and destroys a throwaway VkInstance
        // and walks every physical device. Without the memo the happy path would pay a second instance creation
        // for every device it builds, which is exactly the repeated vkCreateInstance the one refcounted instance
        // (V-N1) exists to stop doing.
        //
        // ON THE PROVIDER INSTANCE rather than in a static, and that is the whole reason it is allowed to exist
        // beside the two caches already in the chain. A provider instance's lifetime IS its registration's, so a
        // registration that replaces the answerer gets a new provider with a fresh memo, which is the same moment
        // GpuBackendSelector drops its own cached boolean. A static here would outlive both.
        //
        // IsSupported deliberately does NOT read it. That answer is already cached above this type by
        // GpuBackendSelector.IsBackendSupported, so a second cache would buy nothing, and the probe's own
        // stability across two real calls is a property a test asserts through this method.
        readonly Lazy<string?> _machineAnswer = new(Ask, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <inheritdoc/>
        public bool IsSupported()
        {
            string? missing = Ask();
            if (missing is null) return true;

            log.Info($"The native Vulkan backend is not available on this machine: {missing}.");
            return false;
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request) => throw NotBuiltYet();

        /// <inheritdoc/>
        public GpuProviderDevice CreateHeadless()
        {
            // THE MACHINE IS ASKED BEFORE ANYTHING NATIVE IS CREATED. A machine that cannot run this backend must
            // refuse with a NotSupportedException naming what it is missing, so the creation path can catch it,
            // WARN and fall back, and so the reader is told whether to install something or to file a bug.
            string? missing = _machineAnswer.Value;
            if (missing is not null) throw ThisMachineCannot(missing);

            return VulkanGpuDevice.CreateHeadless();
        }

        // The probe with the swallow the provider contract demands, in ONE place now that two members need it.
        // Deliberately broad, and the contract requires it: this probe must NEVER throw, because a probe that
        // blows up and a probe that answers no are the same answer to the settings screen and to the fallback
        // that consume it. Everything under it is a loader resolving native entry points, so the failure can be
        // anything from a DllNotFoundException to an EntryPointNotFoundException out of a driver that exports
        // less than its version claims.
        static string? Ask()
        {
            try
            {
                return VulkanSupportProbe.MissingRequirement();
            }
            catch (Exception ex)
            {
                return "the native Vulkan support probe could not answer at all (it threw "
                    + ex.GetType().Name + ": " + ex.Message + "), which is the same answer as no";
            }
        }

        // The MACHINE-level refusal, and it quotes the probe's own sentence rather than paraphrasing it, so the
        // three machine states each read as themselves: no loader names the loader, a loader with no driver names
        // the driver and the package that installs one, and a device below the floor names the requirement. It
        // must be tellable apart from the windowed refusal below, which is about the PACKAGE, and from
        // GpuBackendProviderMissingException, which is about the WIRING. That three-way split is decision V-I4.
        static NotSupportedException ThisMachineCannot(string missing)
            => new("The native Vulkan backend cannot create a device on this machine: " + missing
                + ". This is a statement about the MACHINE rather than about the package, whose headless path is "
                + "built and does return a real device wherever the probe answers yes. It is the same question "
                + "GpuBackendSelector.IsBackendSupported answers without creating anything, asked here so a "
                + "machine that cannot run the backend refuses instead of failing partway into creation.");

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
