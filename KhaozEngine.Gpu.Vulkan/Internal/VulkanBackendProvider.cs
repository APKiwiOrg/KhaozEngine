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
    /// THE PROBE AND BOTH CREATION PATHS ARE REAL. <see cref="IsSupported"/> resolves a loader, creates a
    /// throwaway instance and reads every physical device against section 5.2's requirements.
    /// <see cref="CreateHeadless"/> builds a device with no surface extension at all (V-N6), which is what lets
    /// the golden suite run on a machine with no display server. <see cref="CreateForWindow"/> builds a surface
    /// from the window, filters candidates on whether their graphics family can present to it (V-N5), enables
    /// <c>VK_KHR_swapchain</c>, and creates the swapchain and takes the first acquire before it returns
    /// (row 17, https://github.com/APKiwiOrg/KhaozEngine/issues/527).
    /// </para>
    /// <para>
    /// That ordering is deliberate rather than an artefact. The probe answers a question about the MACHINE, which
    /// is what a settings screen and the fallback path consume, and it is the row that makes a silent fallback
    /// impossible: without it a soak session could measure the incumbent Veldrid Vulkan backend and file the
    /// numbers under the native one. Whether this package can build a device is a different fact, answered
    /// row by row through the build-out and settled as yes now that every row has landed, and folding the two
    /// together would make the probe answer false for a reason that has nothing to do with the hardware.
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
    /// A WINDOWED DEVICE DRAWS AND PRESENTS, and the sentence that stood here saying it could not is dead. The
    /// pipeline binds, the vertex and index binds and the draws landed in row 14 (<c>7f4df174</c>), and the
    /// swapchain, the acquire ring, the resize, the present and the teardown in row 17 (<c>84a9bc6d</c>). The
    /// only refusal left on <c>VulkanCommandList</c> is a list constructed with no draw or rendering seam,
    /// which throws naming the seam it was built without. That is a construction fault in the caller rather
    /// than a ceiling on what a windowed device can do.
    /// </para>
    /// <para>
    /// ONE PROCESS HOLDS A HEADLESS DEVICE AND A WINDOWED ONE ONLY IN THAT ORDER, WINDOWED FIRST, which is
    /// decision V-N1's single-instance model showing through. A live <c>VkInstance</c>'s extension list is fixed
    /// at creation and Vulkan offers no way to add one afterwards, and the windowed list is the headless one
    /// plus the two surface extensions, so a headless device asked for while a windowed one is live is served by
    /// it and the other order is refused by name. See <c>VulkanInstanceRefCount.Satisfies</c> for the rule and
    /// <c>Acquire</c> for why refusing the remaining shortfall beats the two silent alternatives.
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
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        {
            // THE MACHINE IS ASKED BEFORE ANYTHING NATIVE IS CREATED, for the same reason the headless path asks:
            // a machine that cannot run this backend must refuse with a NotSupportedException naming what it is
            // missing, so the creation path can catch it, WARN and fall back.
            string? missing = _machineAnswer.Value;
            if (missing is not null) throw ThisMachineCannot(missing);

            return VulkanGpuDevice.CreateForWindow(request);
        }

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
        // must be tellable apart from a refusal about the PACKAGE (a window kind with no surface extension is the
        // one left, now that every work-breakdown row has landed and no member refuses by naming one) and from
        // GpuBackendProviderMissingException, which is about the WIRING. That three-way split is decision V-I4.
        static NotSupportedException ThisMachineCannot(string missing)
            => new("The native Vulkan backend cannot create a device on this machine: " + missing
                + ". This is a statement about the MACHINE rather than about the package, which returns a real "
                + "device on both its headless and its windowed path wherever the probe answers yes. It is the "
                + "same question GpuBackendSelector.IsBackendSupported answers without creating anything, asked "
                + "here so a machine that cannot run the backend refuses instead of failing partway into "
                + "creation.");
    }
}
