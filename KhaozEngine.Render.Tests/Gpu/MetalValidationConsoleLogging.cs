using System;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE METAL HALF OF THE TEST HOST'S VALIDATION LOGGING (https://github.com/APKiwiOrg/KhaozEngine/issues/617),
    /// and the DECISION only. <see cref="GpuValidationConsoleLogging"/> owns the single
    /// <c>Log.Configure</c> call this assembly is allowed to make.
    ///
    /// <para><b>THE BLINDNESS THIS REMOVES.</b> Metal's two tiers are process-launch variables rather than a
    /// message stream the engine pumps, so it is easy to conclude that the engine has nothing to say about them.
    /// It has three things, all of them through the ambient facade and all of them lost until now:
    /// <c>MetalGpuDevice</c> logs WHICH TIER IS REALLY ARMED and the device's Objective-C class at Info,
    /// <c>MetalGpuDevice</c> WARNS when the armed environment did not produce an <c>MTLDebugDevice</c> after all,
    /// and <c>MetalDeviceLossLatch</c> logs EVERY FAILED COMMAND BUFFER at Error, with the driver's own
    /// localized description and the site that first saw it. That last one is the reading #617 needed and could
    /// not have: a run where 186 rows read back nothing but the pass clear colour is a completely different fault
    /// depending on whether the command buffers failed or completed clean, and the artifact could not tell the
    /// two apart.</para>
    ///
    /// <para><b>ARMED MEANS WHAT THE METAL RUNTIME ACTUALLY READ, not what the environment says now.</b> The
    /// answer comes from the backend's own <see cref="MetalValidationReader"/>, which compares the managed
    /// environment against <c>getenv</c> precisely because a variable set in-process was never seen by the Metal
    /// runtime and arms nothing. A log configured for a tier that is not running would imply an instrument that
    /// is not there, which is the failure the Vulkan half of this seam already refuses to make.</para>
    ///
    /// <para><b>CAPTURE RATHER THAN CURRENT, and the difference is timing.</b>
    /// <see cref="MetalValidationReader.Current"/> memoizes the first reading for the process, and the backend
    /// takes it when the first device is created. Calling it here would move that memo to module-initializer
    /// time, which is EARLIER and no more correct, and would change what the reader rows in
    /// <c>MetalValidationTests</c> are measuring. <see cref="MetalValidationReader.Capture"/> answers the same
    /// question without touching the memo, and at module-initializer time no test has mutated the environment
    /// yet, so it is reading the launch environment by construction.</para>
    /// </summary>
    internal static class MetalValidationConsoleLogging
    {
        /// <summary>Every type in the native Metal backend is named <c>Metal*</c> and <c>Log.For&lt;T&gt;</c> uses
        /// <c>typeof(T).Name</c>, so this prefix admits exactly that backend's categories:
        /// <c>MetalGpuDevice</c>, <c>MetalDeviceLossLatch</c>, <c>MetalCompletionHandler</c>,
        /// <c>MetalPresentBoundary</c>, <c>MetalTimeline</c>, <c>MetalBackendProvider</c> and their
        /// siblings.</summary>
        internal const string CategoryPrefix = "Metal";

        /// <summary>The category this seam announces itself under. Prefixed, so the one line it writes survives
        /// its own filter.</summary>
        internal const string HostCategory = "MetalValidationLogHost";

        /// <summary>
        /// Which Metal validation tier this PROCESS really has AND which variables armed it, read through the
        /// backend's own reader. Pure in the sense that matters: it reads the environment and decides nothing
        /// else, and it never configures anything.
        /// <para>
        /// THE VARIABLES COME BACK BESIDE THE TIER RATHER THAN BEING DERIVED FROM IT. The tier is a merge, and
        /// the merge loses the distinction: <c>MTL_SHADER_VALIDATION</c> alone and both variables together both
        /// report <see cref="MetalValidationMode.Shaders"/>. Deriving the pair back out of the tier is what made
        /// this host's own announcement name a variable nobody had set, which is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/628 one process further out.
        /// </para>
        /// </summary>
        internal static MetalValidationArming ArmedReading() => MetalValidationReader.Capture();

        /// <summary>The tier alone, for the callers that only need the rung.</summary>
        internal static MetalValidationMode ArmedTier() => ArmedReading().Armed;

        /// <summary>Whether <paramref name="armed"/> is a rung worth configuring a sink for, which is any rung
        /// above <see cref="MetalValidationMode.Off"/>. Both tiers count: the API layer alone already produces
        /// the device-class line and every command-buffer failure, and those are the readings a Metal artifact
        /// exists for.</summary>
        internal static bool IsArmed(MetalValidationMode armed) => armed != MetalValidationMode.Off;

        /// <summary>
        /// The one line this seam writes on its own account, at the top of an armed run's log.
        /// <para>
        /// A LOG WITH NO METAL LINES IN IT IS AMBIGUOUS, and this is the line that resolves it. Zero Metal lines
        /// is what a clean run looks like AND what a run looks like when the sink was never configured, which is
        /// exactly the state #617's artifact was in and could not report. This says the sink existed on the run
        /// being read, so the two are tellable apart from the artifact alone.
        /// </para>
        /// <para>
        /// IT NAMES ONLY THE VARIABLES THAT WERE ACTUALLY ARMED, through the backend's own
        /// <see cref="MetalValidation.ArmedVariables"/>, so a shader-only run is not described as having set
        /// <c>MTL_DEBUG_LAYER</c>. Naming both on every armed run is the header half of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/628, and it is worse here than in the backend: this
        /// is the FIRST line of the artifact, so it sets the reader's belief about the launch environment before
        /// any engine line has had a chance to correct it.
        /// </para>
        /// </summary>
        internal static string ArmedAnnouncement(MetalValidationArming arming)
        {
            string armed = MetalValidation.ArmedVariables(arming.DebugLayerArmed, arming.ShaderValidationArmed);
            string verb = arming.DebugLayerArmed && arming.ShaderValidationArmed ? "arm" : "arms";

            return $"{armed} {verb} tier '{arming.Armed}' in "
                + "this process's LAUNCH environment, so this test host configured a console sink for log "
                + $"categories starting with '{CategoryPrefix}' at Info and above. The native Metal backend's own "
                + "lines therefore reach this log: the armed-tier and device-class report from MetalGpuDevice, "
                + "and every failed command buffer from MetalDeviceLossLatch ('A native Metal command buffer "
                + "FAILED'). Metal's own validation output is independent of this and arrives on the runtime's "
                + "stream whether or not the sink was configured. An unarmed run configures nothing and this "
                + "line is absent.";
        }
    }
}
