using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A <see cref="FactAttribute"/> that gates GPU tests on the <c>KE_GPU_TESTS</c> environment variable. GPU
    /// golden tests need a real GPU device, so they must not run under a plain <c>dotnet test</c>. Two run modes:
    /// <list type="bullet">
    ///   <item><description><c>KE_GPU_TESTS=1</c> (strict): attempt to run. Device-creation failure surfaces as a
    ///   test error, NOT a skip, so CI can never silently skip GPU coverage on a machine that is supposed to have a
    ///   device. This is what the cross-platform GPU matrix uses.</description></item>
    ///   <item><description><c>KE_GPU_TESTS=probe</c>: run GPU tests only if a headless device can actually be
    ///   created; otherwise skip with the concrete reason. The probe runs <c>GpuDeviceContext.CreateHeadless()</c>
    ///   once per process (cached in a <see cref="Lazy{T}"/>) and disposes it. Use this on a dev box where a device
    ///   may or may not be present and a skip is preferable to a hard error.</description></item>
    /// </list>
    /// Anything else (unset, empty, or any other value) skips with a "set KE_GPU_TESTS" reason.
    /// <para>
    /// A test may also declare a device CAPABILITY it needs (<see cref="RequiresCompletionFences"/>), which skips
    /// with the backend named on a device that lacks it, in either mode. That is the only skip strict mode allows,
    /// and it is about what the device can do rather than whether it exists.
    /// </para>
    /// </summary>
    public sealed class GpuFactAttribute : FactAttribute
    {
        /// <summary>
        /// WHERE THE ENGINE'S NATIVE BACKENDS GET REGISTERED, for every assembly that runs GPU tests. An
        /// explicit static constructor is what makes the CLR run this the first time anything touches this type,
        /// which is during xUnit's discovery pass in any assembly carrying a <c>[GpuFact]</c> (or a
        /// <c>[GpuTheory]</c>, whose own constructor calls straight into this one's statics), so it lands before
        /// any test body rather than whenever the loader happens to pull the support assembly in.
        /// <para>
        /// It is deliberately not a <c>[ModuleInitializer]</c>: this is a LIBRARY, where CA2255 rejects one, and
        /// the reason the analyzer gives is the reason the hook belongs here anyway. Registration should follow
        /// the ATTRIBUTE, which is what says "this assembly runs GPU tests", not the assembly load, which says
        /// nothing. Full reasoning on <see cref="D3D11BackendRegistration"/>.
        /// </para>
        /// <para>
        /// ALL THREE native backend packages register here, which is the property the shared home was moved for,
        /// and the third one arriving is what says the move worked: it cost one line. Each call is idempotent
        /// and thread-safe on its own side, so ordering between them means nothing and none can be reached
        /// twice. Each is also safe on every operating system, the Metal and Direct3D 11 ones because their
        /// platform guards keep the OS-specific interop off the load path and the Vulkan one because it has no
        /// platform boundary to keep.
        /// </para>
        /// </summary>
        static GpuFactAttribute()
        {
            D3D11BackendRegistration.EnsureRegistered();
            VulkanBackendRegistration.EnsureRegistered();
            MetalBackendRegistration.EnsureRegistered();
        }

        // One headless device-creation attempt per process. null = a device was created (and disposed) fine, so
        // probe mode may run; a non-null string is the reason probe mode skips.
        static readonly Lazy<string?> ProbeReason = new(ProbeHeadlessDevice);

        /// <summary>The shared probe accessor <see cref="GpuTheoryAttribute"/> passes to <see cref="SkipReason"/>,
        /// so both attributes hit the SAME once-per-process device probe instead of each keeping their own.</summary>
        internal static string? ProbeReasonValue() => ProbeReason.Value;

        // The capability probe, separate from the device probe above and equally once-per-process: the backend name
        // and whether that backend signals a fence on GPU completion. Null when no device could be created, which
        // deliberately does NOT skip - see RequiresCompletionFences.
        static readonly Lazy<(string Backend, bool Fences)?> Caps = new(ProbeCapabilities);
        static readonly Lazy<string?> DeviceName = new(ProbeDeviceName);

        public GpuFactAttribute()
        {
            string? reason = SkipReason(
                Environment.GetEnvironmentVariable("KE_GPU_TESTS"),
                ProbeReasonValue);
            if (reason != null) Skip = reason;
        }

        /// <summary>
        /// Declare that this test needs <see cref="KhaozEngine.Gpu.GpuCapabilities.SupportsCompletionFences"/>, so
        /// it SKIPS with a reason naming the backend on a device that has none, instead of failing an assertion it
        /// can never satisfy. The incumbent Direct3D11 backend is the one without them, and it reported two red
        /// tests for a feature it does not have (part of <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see>).
        /// Vulkan, Metal and the engine's own <c>Direct3D11Native</c> backend all report them, so this skips
        /// nothing on those three and the gate is read off the device rather than off a backend list.
        /// <para>
        /// This is the ONE thing that skips in strict mode, and it is not the hole strict mode exists to close: a
        /// missing capability is a property of the device, decided by <c>VeldridMap</c>, not a device that failed
        /// to come up. If no device can be created at all, this skips nothing and the test still errors, so a CI
        /// leg with a broken device can never go quiet.
        /// </para>
        /// </summary>
        public bool RequiresCompletionFences
        {
            get => _requiresCompletionFences;
            set
            {
                _requiresCompletionFences = value;
                // Attribute properties are assigned after the constructor, so the decision is applied here. An
                // existing Skip (the env gate) wins: it is the more fundamental reason.
                if (value && Skip == null) Skip = CompletionFenceSkipReason(Caps.Value);
            }
        }

        bool _requiresCompletionFences;

        /// <summary>Pure decision for <see cref="RequiresCompletionFences"/>: the skip reason for the probed
        /// <paramref name="caps"/>, or null to RUN. A null probe (no device could be created) runs, so a broken
        /// device errors downstream instead of being reported as a capability skip. Factored out to be unit-tested
        /// headlessly, exactly like <see cref="SkipReason"/>.</summary>
        internal static string? CompletionFenceSkipReason((string Backend, bool Fences)? caps)
            => caps is { Fences: false } c
                ? $"the {c.Backend} device reports no GPU-completion fence support "
                    + "(GpuCapabilities.SupportsCompletionFences), which is what this test measures"
                : null;

        /// <summary>
        /// Pure decision for whether a <see cref="GpuFactAttribute"/> should skip, given the raw
        /// <c>KE_GPU_TESTS</c> value and a device <paramref name="probe"/> (invoked only in probe mode). Returns
        /// null to RUN, else the skip reason. Factored out so it can be unit-tested headlessly with stub probes,
        /// without mutating process environment variables:
        /// <list type="bullet">
        ///   <item><description><c>"1"</c> (strict) always runs: never skips, so device-creation failure becomes a
        ///   test error downstream, not a silent skip.</description></item>
        ///   <item><description><c>"probe"</c> runs iff <paramref name="probe"/> returns null, else skips with the
        ///   probe's reason.</description></item>
        ///   <item><description>anything else skips with the "set KE_GPU_TESTS" reason.</description></item>
        /// </list>
        /// </summary>
        internal static string? SkipReason(string? envValue, Func<string?> probe)
        {
            if (envValue == "1") return null;                 // strict: run; failures error, never skip.
            if (envValue == "probe")
                return probe();                               // null => run, else the probe's skip reason.
            return "set KE_GPU_TESTS=1 (strict) or KE_GPU_TESTS=probe (skip if no device) to run GPU golden tests";
        }

        /// <summary>Create a headless device once, read what it can do, dispose it. Null when it could not be
        /// created: a capability requirement never turns a dead device into a skip.</summary>
        /// <summary>
        /// Declare that this test needs a REAL GPU, so it SKIPS on a virtualised adapter instead of failing an
        /// assertion that adapter can never satisfy. GitHub's hosted macOS runners expose an "Apple Paravirtual
        /// device", and it renders some effects as a no-op: the distortion apply pass resamples at a warped UV and
        /// perturbs nothing, so a test asserting "the ripple moved pixels" measures zero and fails. That is a gap in
        /// the virtual adapter, not a rendering regression, and the goldens all pass on it.
        /// <para>
        /// Use this ONLY where the assertion is about an effect being visible at all. A golden comparison does not
        /// need it: goldens are tolerance-based and pass on the paravirtual device, so gating them here would hide
        /// real regressions on the leg that runs most often.
        /// </para>
        /// </summary>
        public bool RequiresRealGpu
        {
            get => _requiresRealGpu;
            set
            {
                _requiresRealGpu = value;
                if (value && Skip == null) Skip = VirtualGpuSkipReason(DeviceName.Value);
            }
        }

        bool _requiresRealGpu;

        /// <summary>Pure decision for <see cref="RequiresRealGpu"/>: the skip reason for a virtualised adapter, or
        /// null to RUN. A null name (no device could be created) RUNS, so a broken device errors downstream rather
        /// than going quiet, exactly as <see cref="CompletionFenceSkipReason"/> does. The name test is duplicated
        /// in the engine's <c>MetalSamplerPolicy.IsVirtualisedAdapter</c> (this assembly references the engine and
        /// not the reverse): a change here visits that site too.</summary>
        internal static string? VirtualGpuSkipReason(string? deviceName)
            => deviceName is { Length: > 0 } n
                && (n.Contains("Paravirtual", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                ? $"the active adapter is '{n}', a virtualised GPU that renders some effects as a no-op, "
                    + "and this test asserts an effect is visible rather than comparing a golden"
                : null;

        static string? ProbeDeviceName()
        {
            try
            {
                using var ctx = KhaozEngine.Gpu.GpuDeviceContext.CreateHeadless();
                return ctx.Capabilities.DeviceName;
            }
            catch
            {
                return null;
            }
        }

        static (string Backend, bool Fences)? ProbeCapabilities()
        {
            try
            {
                using var ctx = KhaozEngine.Gpu.GpuDeviceContext.CreateHeadless();
                return (ctx.GpuDevice.Backend.ToString(), ctx.Capabilities.SupportsCompletionFences);
            }
            catch
            {
                return null;
            }
        }

        static string? ProbeHeadlessDevice()
        {
            try
            {
                using var ctx = KhaozEngine.Gpu.GpuDeviceContext.CreateHeadless();
                return null;
            }
            catch (Exception ex)
            {
                return $"KE_GPU_TESTS=probe: no headless GPU device ({ex.GetType().Name}: {ex.Message})";
            }
        }
    }
}
