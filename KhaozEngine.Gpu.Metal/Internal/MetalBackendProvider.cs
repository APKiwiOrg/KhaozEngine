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
    /// BOTH CREATION PATHS ARE REAL AS OF ROW 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    /// <see cref="IsSupported"/> acquires the device <c>KE_METAL_DEVICE</c> names and takes M-N4's four reads off
    /// it, <see cref="CreateHeadless"/> hands back an offscreen device, and <see cref="CreateForWindow"/> hands
    /// back one with a <c>CAMetalLayer</c> over the request's Cocoa window, its drawable already acquired.
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
    /// TWO REFUSALS, KEPT TELLABLE APART, which is decision M-I4's split and the thing the soak measurement
    /// rests on. A missing REGISTRATION is a wiring fault and throws
    /// <see cref="GpuBackendProviderMissingException"/> from the registry, before this type is reached at all. An
    /// incapable MACHINE is answered by the probe and raises a <see cref="NotSupportedException"/> quoting what
    /// the device was missing. There was a third until row 15, for a capability of the PACKAGE that had not
    /// landed, and nothing is left for it to describe. A request that fails on a machine falls back to the
    /// platform default and reports <see cref="GpuBackendSource.FallbackAfterFailure"/>, which in a log line
    /// looks a great deal like a forgotten registration. A forgotten registration throws instead, and telling
    /// the two apart is what the whole soak measurement rests on.
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
            if (!KhaozEngineMetal.IsPlatformSupported) throw NotOnThisPlatform("windowed");

            // THE MACHINE IS ASKED BEFORE ANYTHING IS BUILT, so a Mac that cannot run this backend hears about
            // the device rather than about the window. Phase 3 shipped this the other way round once and the
            // message told a machine with no driver that the package was still being built.
            string? missing = _machineAnswer.Value;
            if (missing is not null) throw ThisMachineCannot(missing);

            return MetalGpuDevice.CreateForWindow(request);
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

        // THERE IS NO PACKAGE-LEVEL REFUSAL LEFT, and its removal is row 15
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/581). Until this row a windowed request was refused
        // outright, because a swapchain is not an optional extra on a windowed device: it is the whole of what
        // makes it windowed, and handing back one that cannot present would be worse than saying so. Both
        // creation paths are real now, so the only two refusals left are the two that describe the WORLD (this
        // operating system, and this machine) rather than the state of the package.

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
