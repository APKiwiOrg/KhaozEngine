using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// What <see cref="MetalCaptureDisplacementTripwire.Check"/> saw. The default value is the stood-down
    /// reading, which is every machine and every trigger the check does not apply to.
    /// </summary>
    /// <param name="Live">Whether the question was live at all, which is an unattended CI run with a Metal
    /// validation tier armed in the launch environment. False means nothing was read off the machine.</param>
    /// <param name="DeviceClassName">The Objective-C class of the device the native backend would use, read only
    /// on a live reading. Null when this machine has no Metal device.</param>
    /// <param name="Verdict">The failure message, or null to pass.</param>
    internal readonly record struct MetalTripwireReading(bool Live, string? DeviceClassName, string? Verdict);

    /// <summary>
    /// THE RUNTIME HALF OF THE #614 EXCLUSION GUARD
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/614): on an unattended CI run, a Metal validation tier
    /// armed in the launch environment MUST have produced a device that actually validates.
    ///
    /// <para><b>THE MEASURED FACT THIS DEFENDS.</b> On Apple hardware <c>MTL_CAPTURE_ENABLED</c> and
    /// <c>MTL_DEBUG_LAYER</c> are mutually exclusive in practice. A process launched with the debug layer alone
    /// gets an <c>MTLDebugDevice</c>, the class that performs the API validation. Add the capture and the device
    /// comes back as <c>CaptureMTLDevice</c> instead, which validates nothing, so the run reports a tier it does
    /// not have. That is not a hypothetical: the arrangement in place before the re-tier armed the capture on
    /// every push, pull request and cron of the native Metal leg, and #614 spent a fortnight reading debug-layer
    /// artifacts in which the layer had never been in a position to object to anything.</para>
    ///
    /// <para><b>WHY A SECOND LAYER AT ALL.</b> The primary guard is a step in
    /// <c>.github/workflows/cross-platform-gpu.yml</c>, which asserts the tier plumbing at authoring time and
    /// fails the job on the first push that gets it wrong. That guard can only ever see what THIS repository's
    /// workflow arms. This one reads the DEVICE, so it also catches a displacement the workflow did not cause: a
    /// runner image that starts injecting a variable, a wrapper the toolchain adds, a future macOS that stops
    /// honouring the lever. The engine already logs a warning for exactly this state
    /// (<c>MetalGpuDevice.ReportDeviceClass</c>), and a warning in a six-thousand-row log is the thing nobody
    /// reads. This promotes it to a failure on the one trigger where nobody is reading anything.</para>
    ///
    /// <para><b>UNATTENDED CI ONLY, AND THE ATTENDED CASES ARE NOT AN OVERSIGHT.</b> Off CI a developer may
    /// legitimately arm both to see what happens, and the engine's own warning is the right answer there. On an
    /// attended <c>workflow_dispatch</c> the same is true with more force: the <c>capture</c> tier arms both
    /// variables ON PURPOSE, because starting a real GPU-trace capture is what that tier is for and the
    /// debug-layer variable is also what configures the device-class line and the <c>MetalDeviceLossLatch</c>
    /// stream its artifact wants. Failing that dispatch would make the tier unusable. What must never happen is
    /// the pair arriving on a trigger nobody chose, which is push, pull request and the cron.</para>
    ///
    /// <para><b>IT READS THE LAUNCH ENVIRONMENT FOR THE LEVER, NOT THE MANAGED ONE.</b> Rows in this assembly
    /// set and restore <c>MTL_DEBUG_LAYER</c> in process while other collections run beside them, and a variable
    /// set after launch was never read by the Metal runtime and arms nothing. Reading the managed value would
    /// turn that window into a red leg caused by the guard rather than by the plumbing, so the arming comes from
    /// <see cref="MetalValidationReader"/>, which compares <c>getenv</c> against the CLR's snapshot for this
    /// exact reason.</para>
    /// </summary>
    internal static class MetalCaptureDisplacementTripwire
    {
        /// <summary>GitHub Actions sets this to <c>true</c> on every hosted job, and it is the conventional CI
        /// signal. Nothing else in this repository read it from C# before, so it is named here rather than
        /// inherited from a house helper that does not exist.</summary>
        internal const string CiVar = "GITHUB_ACTIONS";

        /// <summary>The trigger that started the workflow run, set by GitHub Actions beside
        /// <see cref="CiVar"/>. It is what tells an unattended trigger from a dispatch a human aimed.</summary>
        internal const string TriggerVar = "GITHUB_EVENT_NAME";

        /// <summary>The frame-capture lever. Not one of <see cref="MetalValidation"/>'s variables, because it is
        /// not a validation tier: it is the thing that DISPLACES one.</summary>
        internal const string CaptureVar = "MTL_CAPTURE_ENABLED";

        /// <summary>The one trigger value that stands this check down, because it is the only attended one this
        /// workflow has and it is where the <c>capture</c> tier lives.</summary>
        internal const string AttendedTrigger = "workflow_dispatch";

        /// <summary>
        /// Whether this process is a CI run on a trigger NOBODY IS WATCHING, which is the only condition under
        /// which a disarmed instrument is worth failing a suite over.
        /// <para>
        /// AN UNRECOGNISED TRIGGER COUNTS AS UNATTENDED, deliberately. The failure this exists to catch is a
        /// gate going quiet, so the ambiguous case resolves towards saying something rather than towards
        /// silence. The cost of being wrong is bounded: the whole check is behind an armed Metal validation
        /// tier, which off this repository's macOS legs is never set at all.
        /// </para>
        /// </summary>
        internal static bool IsAnUnattendedCiRun(string? ciValue, string? triggerValue)
        {
            // MetalValidation.IsArmed is a flag-value parser rather than a Metal fact: it reads anything that is
            // not blank and not an explicit off word as armed, which is exactly the reading GITHUB_ACTIONS=true
            // wants, and it gets "false" and "0" handled for free.
            if (!MetalValidation.IsArmed(ciValue)) return false;

            return !string.Equals(triggerValue?.Trim(), AttendedTrigger, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether an Objective-C device class is one that actually performs validation.
        /// <list type="bullet">
        ///   <item><description><c>MTLDebugDevice</c>: the API validation class, which is
        ///   <c>MTL_DEBUG_LAYER=1</c> alone.</description></item>
        ///   <item><description><c>MTLLegacySVDevice</c> and <c>MTLGPUDebugDevice</c>: the two measured classes a
        ///   process gets when <c>MTL_SHADER_VALIDATION=1</c> armed the tier, one spelling per machine. Both
        ///   validate, so both are a PASS here.</description></item>
        ///   <item><description><c>CaptureMTLDevice</c>: the capture wrapper. Validates nothing.</description></item>
        ///   <item><description>anything else is the driver's own class (<c>AGXG14CDevice</c> on Apple silicon),
        ///   which is what an unvalidated process gets.</description></item>
        /// </list>
        /// <para>
        /// IT DELEGATES TO THE ENGINE'S OWN CLASSIFIER, AND THAT IS THE POINT. This began as a private substring
        /// test because the engine's helper of the day, <c>LooksLikeADebugDevice</c>, was a bare "Debug" match
        /// that read <c>MTLLegacySVDevice</c> as a displacement, so delegating would have redded the deep tier
        /// for arming the deep tier. https://github.com/APKiwiOrg/KhaozEngine/issues/628 replaced that helper
        /// with <c>MetalValidation.ClassifyDevice</c> over four classes, which is where the second shader-
        /// validation spelling came from as well. Delegating now leaves ONE predicate for what "validating"
        /// means, so the tripwire and the backend's own warning cannot answer differently about the same device.
        /// </para>
        /// </summary>
        internal static bool IsAValidatingDeviceClass(string? deviceClassName)
            => deviceClassName is { Length: > 0 } name
                && MetalValidation.IsValidationDevice(MetalValidation.ClassifyDevice(name));

        /// <summary>
        /// THE DECISION, pure: the environment values and the device's class in, the failure message out, or
        /// null to pass. Every gate is a separate early return so a headless test can drive one at a time.
        /// <para>
        /// A device class that could not be read PASSES. A machine with no Metal device is a different fault
        /// with its own instrument (<c>KE_METAL_REQUIRED</c> makes it a throw on the leg that declared one
        /// mandatory), and reporting it here would be this guard claiming a failure it cannot substantiate.
        /// </para>
        /// </summary>
        internal static string? Verdict(string? ciValue, string? triggerValue, string? debugLayerValue,
            string? captureValue, string? deviceClassName)
        {
            if (!IsAnUnattendedCiRun(ciValue, triggerValue)) return null;
            if (!MetalValidation.IsArmed(debugLayerValue)) return null;
            if (deviceClassName is not { Length: > 0 } className) return null;
            if (IsAValidatingDeviceClass(className)) return null;

            return MetalValidation.IsArmed(captureValue)
                ? DisplacedByTheCapture(className)
                : DisplacedBySomethingElse(className);
        }

        static string DisplacedByTheCapture(string deviceClassName)
            => $"{MetalValidation.DebugLayerVar} is armed in this process's launch environment and the Metal "
                + $"device came back as {deviceClassName} rather than MTLDebugDevice. {CaptureVar} is armed "
                + "beside it, and that is the displacement: the capture wrapper takes the device, so the layer "
                + "this run reports as active validated nothing. This is an UNATTENDED CI run, so the whole "
                + "suite would otherwise have gone green under an instrument that was not there, which is "
                + "precisely the fortnight of empty debug-layer artifacts recorded on "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/614. THE FIX IS THE WORKFLOW TIER, NOT THIS "
                + "ROW: the capture belongs to the attended `capture` dispatch alone, and the leg description in "
                + "docs/CROSS-PLATFORM.md carries the measurement.";

        static string DisplacedBySomethingElse(string deviceClassName)
            => $"{MetalValidation.DebugLayerVar} is armed in this process's launch environment and the Metal "
                + $"device came back as {deviceClassName} rather than MTLDebugDevice, with {CaptureVar} NOT "
                + "armed, so the usual displacement does not explain it. Believe the device: this run carries "
                + "no Metal API validation whatever the environment says, and on an UNATTENDED CI trigger that "
                + "means a validation gate reported itself green while disarmed. Something outside this "
                + "repository's workflow took the device, so this one is worth reporting with the macOS version "
                + "and the runner image attached, on "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/614. The exclusion this checks is described "
                + "on the Metal native leg in docs/CROSS-PLATFORM.md.";

        /// <summary>
        /// THE WHOLE CHECK, run against this process.
        /// <para>
        /// NOTHING IS READ OFF THE MACHINE UNTIL THE LEVERS SAY THE QUESTION IS LIVE, so an ordinary
        /// <c>dotnet test</c> on a developer's Mac creates no device here and costs three environment reads.
        /// That is also why the reading carries <see cref="MetalTripwireReading.Live"/> rather than leaving a
        /// caller to ask for the class again: asking twice would acquire a device on every machine that runs
        /// this assembly, including the ones the whole check stood down on.
        /// </para>
        /// </summary>
        internal static MetalTripwireReading Check()
        {
            string? ciValue = Environment.GetEnvironmentVariable(CiVar);
            string? triggerValue = Environment.GetEnvironmentVariable(TriggerVar);
            if (!IsAnUnattendedCiRun(ciValue, triggerValue)) return default;

            // The LAUNCH answer, for the reason on the type: a lever set in process was never read by the Metal
            // runtime, and this assembly has rows that set one and put it back. Capture rather than Current, so
            // the backend's own memo is not taken here (the same argument MetalValidationConsoleLogging makes).
            // The reader answers with a tier rather than a value, and the value is what the pure decision speaks
            // in, so the tier is turned back into the armed spelling for it.
            string? launchDebugLayer =
                MetalValidationReader.Capture().Armed == MetalValidationMode.Off ? null : "1";
            if (!MetalValidation.IsArmed(launchDebugLayer)) return default;

            string? deviceClassName = DeviceClassName();
            return new MetalTripwireReading(true, deviceClassName,
                Verdict(ciValue, triggerValue, launchDebugLayer,
                    Environment.GetEnvironmentVariable(CaptureVar), deviceClassName));
        }

        /// <summary>
        /// The Objective-C class of the device this process's native Metal backend would use, or null when this
        /// machine has none. Never throws: an unreadable class is a pass, not a failure.
        /// </summary>
        internal static string? DeviceClassName()
        {
            if (!KhaozEngineMetal.IsPlatformSupported) return null;

            try
            {
                return ReadSelectedDeviceClassName();
            }
            catch
            {
                return null;
            }
        }

        // Through the backend's OWN acquisition, so the class read here is the class the device under test has
        // rather than the class some other path to Metal would produce. The device arrives at +1 and is released
        // straight back: on Apple silicon the whole process shares one MTLDevice, so this neither creates a
        // second one nor keeps the first alive.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string? ReadSelectedDeviceClassName()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MetalSelectedDevice selected = MetalDeviceEnumeration.AcquireSelected();
            if (selected.Device.IsNull) return null;

            try
            {
                return selected.Device.ClassName();
            }
            finally
            {
                selected.Device.Release();
            }
        }
    }
}
