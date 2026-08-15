using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DECISION, DRIVEN HEADLESSLY. <see cref="MetalCaptureDisplacementTripwire.Verdict"/> takes the
    /// environment values and a device class name and answers with a failure message or null, so every case this
    /// guard can meet is reachable on a Linux box with no Metal anywhere: all four device classes crossed with
    /// the lever combinations, and the gating that keeps it off a developer's machine and off an attended
    /// dispatch.
    /// <para>
    /// The row that runs it against THIS process is <see cref="MetalCaptureDisplacementTripwireLiveTests"/>,
    /// which is the only part that needs a real device and the only part that can go red on a leg.
    /// </para>
    /// </summary>
    public sealed class MetalCaptureDisplacementTripwireTests
    {
        // ci, trigger, MTL_DEBUG_LAYER, MTL_CAPTURE_ENABLED, device class, expected to fail.
        [Theory]
        // THE FOUR DEVICE CLASSES on an unattended CI run with the debug layer armed and no capture. The two
        // validating classes pass, and the two that do not validate fail whether or not anything explains them.
        [InlineData("true", "push", "1", null, "MTLDebugDevice", false)]
        [InlineData("true", "push", "1", null, "MTLLegacySVDevice", false)]
        [InlineData("true", "push", "1", null, "CaptureMTLDevice", true)]
        [InlineData("true", "push", "1", null, "AGXG14CDevice", true)]
        // THE SAME FOUR WITH THE CAPTURE ARMED TOO, which is the #614 shape. A device that still validates is not
        // a failure however the levers look: the guard believes the device, not the environment.
        [InlineData("true", "push", "1", "1", "MTLDebugDevice", false)]
        [InlineData("true", "push", "1", "1", "MTLLegacySVDevice", false)]
        [InlineData("true", "push", "1", "1", "CaptureMTLDevice", true)]
        [InlineData("true", "push", "1", "1", "AGXG14CDevice", true)]
        // NO TIER CLAIMED, NO COMPLAINT. Without the debug layer this run never said it was validating, so the
        // driver's own class and even a capture wrapper are the correct outcome rather than a displacement.
        [InlineData("true", "push", null, "1", "CaptureMTLDevice", false)]
        [InlineData("true", "push", null, null, "AGXG14CDevice", false)]
        [InlineData("true", "push", "0", "1", "CaptureMTLDevice", false)]
        [InlineData("true", "push", "off", "1", "CaptureMTLDevice", false)]
        // A CLASS THAT COULD NOT BE READ PASSES. No Metal device is a different fault with its own instrument.
        [InlineData("true", "push", "1", "1", null, false)]
        [InlineData("true", "push", "1", "1", "", false)]
        // THE GATING. Off CI it stays the engine's warning, because a developer may arm both deliberately.
        [InlineData(null, "push", "1", "1", "CaptureMTLDevice", false)]
        [InlineData("false", "push", "1", "1", "CaptureMTLDevice", false)]
        [InlineData("0", "push", "1", "1", "CaptureMTLDevice", false)]
        // An attended dispatch stands down, in either spelling, because the `capture` tier arms both on purpose.
        [InlineData("true", "workflow_dispatch", "1", "1", "CaptureMTLDevice", false)]
        [InlineData("true", "WORKFLOW_DISPATCH", "1", "1", "CaptureMTLDevice", false)]
        // Every other trigger is unattended, including one this build does not recognise: an ambiguous CI run
        // resolves towards saying something, because going quiet is the failure being guarded.
        [InlineData("true", "pull_request", "1", "1", "CaptureMTLDevice", true)]
        [InlineData("true", "schedule", "1", "1", "CaptureMTLDevice", true)]
        [InlineData("true", null, "1", "1", "CaptureMTLDevice", true)]
        [InlineData("true", "merge_group", "1", "1", "CaptureMTLDevice", true)]
        public void TheVerdictFailsOnlyWhereAnArmedTierDidNotProduceAValidatingDevice(
            string? ci, string? trigger, string? debugLayer, string? capture, string? deviceClass, bool fails)
        {
            string? verdict = MetalCaptureDisplacementTripwire.Verdict(ci, trigger, debugLayer, capture,
                deviceClass);

            Assert.Equal(fails, verdict is not null);
        }

        /// <summary>The two classes that validate and the two that do not, as their own row, because the
        /// predicate is the one piece of Metal knowledge this file carries and a change to it should fail
        /// something named after it.</summary>
        [Theory]
        [InlineData("MTLDebugDevice", true)]
        [InlineData("MTLLegacySVDevice", true)]
        [InlineData("CaptureMTLDevice", false)]
        [InlineData("AGXG14CDevice", false)]
        [InlineData("MTLIGAccelDevice", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyTheValidatingDeviceClassesAreRecognised(string? deviceClass, bool validating)
            => Assert.Equal(validating, MetalCaptureDisplacementTripwire.IsAValidatingDeviceClass(deviceClass));

        /// <summary>
        /// The message names the class it actually saw and points at both the issue and the living doc, because
        /// a guard that fires on a leg nobody was watching has one chance to explain itself.
        /// </summary>
        [Fact]
        public void TheCaptureArmedMessageNamesTheCaptureAndWhereToReadWhy()
        {
            string verdict = Assert.IsType<string>(MetalCaptureDisplacementTripwire.Verdict(
                "true", "push", "1", "1", "CaptureMTLDevice"));

            Assert.Contains("CaptureMTLDevice", verdict, System.StringComparison.Ordinal);
            Assert.Contains(MetalCaptureDisplacementTripwire.CaptureVar, verdict, System.StringComparison.Ordinal);
            Assert.Contains("issues/614", verdict, System.StringComparison.Ordinal);
            Assert.Contains("docs/CROSS-PLATFORM.md", verdict, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// With the capture NOT armed the message must not blame it, because sending the next reader to the
        /// workflow tier when the workflow tier is innocent is worse than saying nothing.
        /// </summary>
        [Fact]
        public void TheUnexplainedMessageSaysTheCaptureIsNotWhatTookTheDevice()
        {
            string verdict = Assert.IsType<string>(MetalCaptureDisplacementTripwire.Verdict(
                "true", "push", "1", null, "AGXG14CDevice"));

            Assert.Contains("AGXG14CDevice", verdict, System.StringComparison.Ordinal);
            Assert.Contains("NOT armed", verdict, System.StringComparison.Ordinal);
            Assert.Contains("issues/614", verdict, System.StringComparison.Ordinal);
        }

        /// <summary>The CI gate on its own, so the two halves of "unattended CI" are readable separately from
        /// everything the verdict then does with them.</summary>
        [Theory]
        [InlineData(null, "push", false)]
        [InlineData("", "push", false)]
        [InlineData("false", "push", false)]
        [InlineData("true", "push", true)]
        [InlineData("true", "pull_request", true)]
        [InlineData("true", "schedule", true)]
        [InlineData("true", "workflow_dispatch", false)]
        [InlineData("true", " workflow_dispatch ", false)]
        [InlineData("true", null, true)]
        public void TheGateIsCiMinusTheOneTriggerAHumanAimed(string? ci, string? trigger, bool unattended)
            => Assert.Equal(unattended, MetalCaptureDisplacementTripwire.IsAnUnattendedCiRun(ci, trigger));
    }

    /// <summary>
    /// THE TRIPWIRE ITSELF, against this process.
    ///
    /// <para><b>WHAT IT ASSERTS.</b> On an unattended CI run with a Metal validation tier armed at launch, the
    /// device the native backend would use has to be a class that validates. Anywhere else (a developer box, an
    /// attended dispatch, any leg with no Metal lever set) it reads three environment variables, decides the
    /// question is not live, and passes without touching the machine. So this row is inert on the Windows and
    /// Linux legs, inert on the incumbent Metal leg, and inert under an ordinary <c>dotnet test</c>.</para>
    ///
    /// <para><b>A ROW RATHER THAN A MODULE INITIALIZER, deliberately.</b> Throwing before the suite starts would
    /// stop the run harder, and it would also destroy the run's evidence: on the leg this exists for, the six
    /// thousand results and the golden artifacts are what #614 is read out of, and a leg that produced no
    /// results at all would be a worse outcome than a red leg with a named row in it. Red is red either way, and
    /// the property that matters is that the suite cannot go GREEN under a disarmed instrument.</para>
    ///
    /// <para><b>IN <c>NativeDeviceLifecycle</c></b> because it acquires an <c>MTLDevice</c> through the backend's
    /// own selection, which is that collection's whole membership rule, even though this row releases it again
    /// immediately and creates no queue.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalCaptureDisplacementTripwireLiveTests
    {
        readonly ITestOutputHelper _output;

        public MetalCaptureDisplacementTripwireLiveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void AnArmedValidationTierReallyHoldsTheDeviceOnAnUnattendedCiRun()
        {
            MetalTripwireReading reading = MetalCaptureDisplacementTripwire.Check();

            // Written out whatever the verdict, so a GREEN leg's log still says which device class held it, and
            // says so from the row that checked rather than from a warning that may or may not have been
            // configured. That line is the first thing #614 reads on every occurrence, and a guard that only
            // speaks when it fails leaves a passing run indistinguishable from one where it never fired.
            _output.WriteLine(reading.Live
                ? "unattended CI with a Metal validation tier armed at launch. Device class: "
                    + (reading.DeviceClassName ?? "(no Metal device on this machine)")
                : "stood down: not an unattended CI run with a Metal validation tier armed at launch, so "
                    + "nothing was read off this machine.");

            Assert.True(reading.Verdict is null, reading.Verdict);
        }
    }
}
