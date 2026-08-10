using System;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The engine's native Metal backend as the GPU seam sees it. Registered by
    /// <see cref="KhaozEngineMetal.Register"/> and consumed only through <see cref="IGpuBackendProvider"/>, so
    /// nothing outside this package ever names an Objective-C handle.
    /// <para>
    /// HEADLESS CREATION IS REAL AND WINDOWED IS NOT YET. <see cref="IsSupported"/> acquires the device
    /// <c>KE_METAL_DEVICE</c> names and takes M-N4's four reads off it, and <see cref="CreateHeadless"/> hands
    /// back a device holding a real <c>MTLDevice</c> and a real <c>MTLCommandQueue</c>, whose unbuilt members
    /// each name the row that builds them. <see cref="CreateForWindow"/> refuses by naming the swapchain row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581), which is the honest refusal: a windowed device
    /// that cannot present is worse than one that says so at creation.
    /// </para>
    /// <para>
    /// THE PROBE LANDING A ROW BEFORE CREATION WAS DELIBERATE rather than an artefact of scheduling: it is what
    /// makes a SILENT FALLBACK impossible. Without it a soak session could measure the incumbent Veldrid Metal
    /// backend and file the numbers under the native one, which is the exact failure the whole gate-4 capture
    /// exists to avoid.
    /// </para>
    /// <para>
    /// THE PLATFORM GUARD IS THE FIRST THING EVERY ENTRY POINT DOES, and that is decision M-P1 rather than a copy
    /// made by analogy. Metal is an OS-specific API, so this package carries the Direct3D 11 package's
    /// <c>[SupportedOSPlatformGuard]</c>-over-<c>NoInlining</c> apparatus and the Vulkan package's deliberate
    /// absence of one does not transfer. Off macOS <see cref="IsSupported"/> answers false with no Objective-C
    /// call on the path, and creation raises a <see cref="PlatformNotSupportedException"/> naming the platform.
    /// </para>
    /// <para>
    /// THREE REFUSALS, KEPT TELLABLE APART, which is decision M-I4's split and the thing the soak measurement
    /// rests on. A missing REGISTRATION is a wiring fault and throws
    /// <see cref="GpuBackendProviderMissingException"/> from the registry, before this type is reached at all. An
    /// incapable MACHINE is answered by the probe and raises a <see cref="NotSupportedException"/> quoting what
    /// the device was missing. A capability of the PACKAGE that has not landed yet raises its own
    /// <see cref="NotSupportedException"/> naming the row. On macOS the OS probe already returns
    /// <see cref="GpuBackendKind.Metal"/>, so a native request that fails falls back to the incumbent and reports
    /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, which in a log line looks a great deal like a
    /// forgotten registration. A forgotten registration throws instead.
    /// </para>
    /// </summary>
    internal sealed class MetalBackendProvider : IGpuBackendProvider
    {
        static readonly ILogger log = Log.For<MetalBackendProvider>();

        // THE MACHINE GATE, asked at most once per provider instance, because the machine does not change while
        // the process runs and the probe really does create and release a Metal device to find out.
        //
        // ON THE PROVIDER INSTANCE rather than in a static, which is section 4.1's wording and the reason it is
        // allowed to exist beside the cache GpuBackendSelector already keeps. A provider instance's lifetime IS
        // its registration's, so a registration that replaces the answerer gets a new provider with a fresh
        // memo, which is the same moment GpuBackendSelector drops its own cached boolean. A static would outlive
        // both.
        //
        // IsSupported deliberately does NOT read it, following the Vulkan sibling for the same two reasons: that
        // answer is already cached above this type by GpuBackendSelector.IsBackendSupported so a second cache
        // buys nothing, and the probe's own stability across two real calls is a property a test asserts through
        // this method.
        readonly Lazy<string?> _machineAnswer = new(Ask, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <inheritdoc/>
        public bool IsSupported()
        {
            // The platform guard first, and it is the whole answer off macOS: no Objective-C call is made on
            // this path, so libobjc is never loaded on Windows or Linux.
            if (!KhaozEngineMetal.IsPlatformSupported) return false;

            string? missing = Ask();
            if (missing is null) return true;

            log.Info($"The native Metal backend is not available on this machine: {missing}.");
            return false;
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        {
            _ = request;
            if (!KhaozEngineMetal.IsPlatformSupported) throw NotOnThisPlatform("windowed");

            // THE MACHINE IS ASKED BEFORE THE PACKAGE REFUSES, so a Mac that cannot run this backend hears about
            // the device rather than about a row it was never going to reach. Phase 3 shipped this the other way
            // round once and the message told a machine with no driver that the package was still being built.
            string? missing = _machineAnswer.Value;
            if (missing is not null) throw ThisMachineCannot(missing);

            throw NotBuiltYet();
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateHeadless()
        {
            if (!KhaozEngineMetal.IsPlatformSupported) throw NotOnThisPlatform("headless");

            string? missing = _machineAnswer.Value;
            if (missing is not null) throw ThisMachineCannot(missing);

            return MetalGpuDevice.CreateHeadless();
        }

        // The probe with the swallow the provider contract demands, in ONE place now that two members need it.
        // Deliberately broad, and the contract requires it: this probe must NEVER throw, because a probe that
        // blows up and a probe that answers no are the same answer to the settings screen and to the fallback
        // that consume it. Everything under it is a P/Invoke into libobjc and the Metal framework, so the
        // failure can be anything from a DllNotFoundException to an EntryPointNotFoundException out of a macOS
        // that does not export what this one does.
        static string? Ask()
        {
            try
            {
                return MetalSupportProbe.MissingRequirement();
            }
            catch (Exception ex)
            {
                return "the native Metal support probe could not answer at all (it threw "
                    + ex.GetType().Name + ": " + ex.Message + "), which is the same answer as no";
            }
        }

        // The MACHINE-level refusal, quoting the probe's own sentence rather than paraphrasing it, so each way a
        // Mac can fail reads as itself: no device names the device, a device below the family floor names the
        // families it did answer, and a coarse buffer alignment names the number and the selector it came from.
        static NotSupportedException ThisMachineCannot(string missing)
            => new("The native Metal backend cannot create a device on this machine: " + missing
                + ". This is a statement about the MACHINE rather than about the package, and it is the same "
                + "question GpuBackendSelector.IsBackendSupported answers without creating anything, asked here "
                + "so a machine that cannot run the backend refuses instead of failing partway into creation.");

        // The PACKAGE-level refusal, which is a different fact from the two above and names the row that ends
        // it. The creation path catches this, WARNs with the message and falls back to the incumbent Veldrid
        // Metal backend, so this text is what a tester who named the native backend actually reads.
        //
        // WINDOWED ONLY NOW. Headless creation is live as of row 4, and the windowed path refuses rather than
        // handing back a device that cannot present: a swapchain is not an optional extra on a windowed device,
        // it is the whole of what makes it windowed.
        static NotSupportedException NotBuiltYet()
            => new("The native Metal backend cannot create a WINDOWED device yet. The probe above answered YES "
                + "for this machine, so this is a statement about the PACKAGE: the MTLDevice, the "
                + "MTLCommandQueue, KE_METAL_DEVICE selection and HEADLESS creation are live (work-breakdown "
                + "row 4, https://github.com/APKiwiOrg/KhaozEngine/issues/570), and the CAMetalLayer, the "
                + "drawable and the present arrive with row 15 "
                + "(https://github.com/APKiwiOrg/KhaozEngine/issues/581). Use GpuBackendKind.Metal, which goes "
                + "through Veldrid, for a windowed Metal device today.");

        // The off-macOS refusal, and it is a PLATFORM answer rather than either of the other two. Registration
        // is safe on every OS on purpose (M-I4), so this is what a consumer that registered unconditionally and
        // then named the backend anyway gets to read.
        static PlatformNotSupportedException NotOnThisPlatform(string path)
            => new($"The native Metal backend cannot create a {path} device on this operating system, which has "
                + "no Metal. Registration is safe everywhere and reports the backend as unsupported off macOS, "
                + "so read GpuBackendSelector.IsBackendSupported (or KhaozEngineMetal.IsPlatformSupported) "
                + "before naming this backend.");
    }
}
