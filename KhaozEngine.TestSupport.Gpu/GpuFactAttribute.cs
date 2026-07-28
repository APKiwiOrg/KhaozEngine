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
    /// </summary>
    public sealed class GpuFactAttribute : FactAttribute
    {
        // One headless device-creation attempt per process. null = a device was created (and disposed) fine, so
        // probe mode may run; a non-null string is the reason probe mode skips.
        static readonly Lazy<string?> ProbeReason = new(ProbeHeadlessDevice);

        /// <summary>The shared probe accessor <see cref="GpuTheoryAttribute"/> passes to <see cref="SkipReason"/>,
        /// so both attributes hit the SAME once-per-process device probe instead of each keeping their own.</summary>
        internal static string? ProbeReasonValue() => ProbeReason.Value;

        public GpuFactAttribute()
        {
            string? reason = SkipReason(
                Environment.GetEnvironmentVariable("KE_GPU_TESTS"),
                ProbeReasonValue);
            if (reason != null) Skip = reason;
        }

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
