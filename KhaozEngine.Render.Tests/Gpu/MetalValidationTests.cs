using System;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-G3's knob, parsed and reported. Pure rows, so the whole ladder is decided on every leg.
    /// <para>
    /// THE KNOB ARMS NOTHING AND THAT IS THE MEASUREMENT, NOT A LIMITATION OF THIS CODE. Metal API validation is
    /// a process-launch mechanism, and row 1's spike proved with a control that an in-process
    /// <c>setenv("MTL_DEBUG_LAYER", "1")</c> ahead of any Metal use leaves the device class
    /// <c>AGXG14CDevice</c> while the same run launched with the variable set gets <c>MTLDebugDevice</c>. So this
    /// row's job is to REPORT what is armed and to WARN loudly when a tester asked for a tier the process cannot
    /// have, because a run that believes it is validating and is not produces a clean result that proves nothing.
    /// </para>
    /// </summary>
    public sealed class MetalValidationTests
    {
        [Theory]
        [InlineData(null, "Off")]
        [InlineData("", "Off")]
        [InlineData("0", "Off")]
        [InlineData("off", "Off")]
        [InlineData("1", "On")]
        [InlineData("TRUE", "On")]
        [InlineData("shaders", "Shaders")]
        [InlineData("Shader", "Shaders")]
        public void Parse_ReadsTheLadder(string? value, string expected)
        {
            Assert.Equal(expected, MetalValidation.Parse(value, out string? unrecognized).ToString());
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// There is deliberately no "anything else means on" reading, unlike the device selector's substring arm.
        /// A device name is free text by nature, while this knob has three values and a fourth is a typo, and
        /// reading a typo as a level would be the worst outcome available: a session that believes it is running
        /// shader validation and is running nothing.
        /// </summary>
        [Fact]
        public void AnUnrecognizedValue_StaysOffAndIsQuotedVerbatim()
        {
            Assert.Equal("Off", MetalValidation.Parse(" ful1 ", out string? unrecognized).ToString());

            Assert.Equal(" ful1 ", unrecognized);
            Assert.Contains(" ful1 ", MetalValidation.UnrecognizedWarning(unrecognized!),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The process-level variables are FLAGS to the Metal runtime, so anything that is not an explicit off
        /// value counts. An empty value counts as unset, because a shell that exported the name with no value did
        /// not ask for validation.
        /// </summary>
        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("0", false)]
        [InlineData("off", false)]
        [InlineData("1", true)]
        [InlineData("yes", true)]
        [InlineData("2", true)]
        public void IsArmed_TreatsTheProcessVariableAsAFlag(string? value, bool expected)
            => Assert.Equal(expected, MetalValidation.IsArmed(value));

        /// <summary>
        /// ROW 1'S CONTROL, REUSED AS A RUNTIME CHECK AND WIDENED TO ALL FOUR MEASURED CLASSES. A device created
        /// under Metal API validation is an <c>MTLDebugDevice</c> rather than the driver's own class, which is
        /// how the spike proved the mechanism in the first place. #614 added the capture class and #628 added
        /// <c>MTLLegacySVDevice</c>, which is a VALIDATION device and read as an unvalidated one for a release.
        /// </summary>
        [Theory]
        [InlineData("MTLDebugDevice", "Debug")]
        // Shader validation alone has TWO measured spellings: the hosted runner's and real Apple silicon's.
        // MTLGPUDebugDevice contains "Debug", so it also pins the order inside the classifier.
        [InlineData("MTLLegacySVDevice", "ShaderValidation")]
        [InlineData("MTLGPUDebugDevice", "ShaderValidation")]
        [InlineData("CaptureMTLDevice", "Capture")]
        [InlineData("AGXG14CDevice", "Driver")]
        [InlineData("", "Driver")]
        public void ClassifyDevice_ReadsEveryClassThatHasBeenMeasured(string className, string expected)
            => Assert.Equal(expected, MetalValidation.ClassifyDevice(className).ToString());

        /// <summary>
        /// THE DEBUG LAYER WINS WHEN BOTH ARE SET, which is measured rather than assumed and is what makes the
        /// shader-only case a different expectation rather than the same one.
        /// </summary>
        [Theory]
        [InlineData(true, true, "Debug")]
        [InlineData(true, false, "Debug")]
        [InlineData(false, true, "ShaderValidation")]
        [InlineData(false, false, "Driver")]
        public void ExpectedDeviceClass_FollowsWhichVariableIsArmed(
            bool debug, bool shaders, string expected)
            => Assert.Equal(expected, MetalValidation.ExpectedDeviceClass(debug, shaders).ToString());

        /// <summary>
        /// #628'S CASE, AS THE ROW THAT WOULD HAVE CAUGHT IT. A process armed with <c>MTL_SHADER_VALIDATION</c>
        /// alone gets a shader-validation device, which IS validated, and the old substring check called that a
        /// disagreement 99 times on one run. Every armed row here is a launch environment that has actually been
        /// measured, so the table is a record rather than a prediction.
        /// <para>
        /// THE QUESTION IS "IS ANYTHING VALIDATING", not "did one exact class come back". Shader validation
        /// answers <c>MTLLegacySVDevice</c> on the hosted runner and <c>MTLGPUDebugDevice</c> on real Apple
        /// silicon, so a class-equality check would warn on whichever machine it was not written against, which
        /// is the same defect one machine further along.
        /// </para>
        /// </summary>
        [Theory]
        // Armed and something is validating: no warning at all, whichever wrapper answered.
        [InlineData(true, false, "MTLDebugDevice", false)]
        [InlineData(true, true, "MTLDebugDevice", false)]
        [InlineData(false, true, "MTLLegacySVDevice", false)]
        [InlineData(false, true, "MTLGPUDebugDevice", false)]
        // Armed and displaced by a capture, which is #614's row and stays a warning.
        [InlineData(true, false, "CaptureMTLDevice", true)]
        // Armed and nothing at all is holding the device, which is the case nobody has observed.
        [InlineData(true, false, "AGXG14CDevice", true)]
        [InlineData(false, true, "AGXG14CDevice", true)]
        // Nothing armed is never a disagreement, whatever came back.
        [InlineData(false, false, "AGXG14CDevice", false)]
        [InlineData(false, false, "MTLDebugDevice", false)]
        public void DisagreesWithArming_FiresOnlyWhenNothingIsValidatingAfterAll(
            bool debug, bool shaders, string className, bool expected)
            => Assert.Equal(expected, MetalValidation.DisagreesWithArming(debug, shaders, className));

        /// <summary>
        /// THE WARNING NAMES THE VARIABLE THAT WAS SET AND NO OTHER. Naming <c>MTL_DEBUG_LAYER</c> on a run that
        /// only ever set the shader variable sends the reader to look at something they never touched, which is
        /// the second half of #628.
        /// </summary>
        [Fact]
        public void TheDisagreementWarning_NamesOnlyTheVariableThatWasArmed()
        {
            string shaderOnly = MetalValidation.ArmedButWrongDeviceClassWarning(
                false, true, "AGXG14CDevice");

            Assert.Contains(MetalValidation.ShaderValidationVar, shaderOnly, StringComparison.Ordinal);
            Assert.DoesNotContain(MetalValidation.DebugLayerVar, shaderOnly, StringComparison.Ordinal);
            Assert.Contains("MTLLegacySVDevice", shaderOnly, StringComparison.Ordinal);

            string capture = MetalValidation.ArmedButWrongDeviceClassWarning(true, false, "CaptureMTLDevice");

            Assert.Contains(MetalValidation.DebugLayerVar, capture, StringComparison.Ordinal);
            Assert.DoesNotContain(MetalValidation.ShaderValidationVar, capture, StringComparison.Ordinal);
            // The capture cause is MEASURED, so it names it instead of asking for a bug report.
            Assert.Contains("MTL_CAPTURE_ENABLED", capture, StringComparison.Ordinal);
            Assert.DoesNotContain("has not been observed", capture, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE WARN THAT MATTERS MOST NAMES THE EXACT PREFIX TO RE-RUN WITH. A tester mid-diagnosis who is told
        /// only "validation is off" goes looking for a bug in the knob, and the answer is that the knob cannot
        /// arm it and never could.
        /// </summary>
        [Fact]
        public void TheNotArmedWarning_NamesTheLaunchPrefixForBothTiers()
        {
            string one = MetalValidation.NotArmedWarning(MetalValidationMode.On);
            string shaders = MetalValidation.NotArmedWarning(MetalValidationMode.Shaders);

            Assert.Contains("MTL_DEBUG_LAYER=1 <your command>", one, StringComparison.Ordinal);
            Assert.Contains("MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1 <your command>", shaders,
                StringComparison.Ordinal);
            Assert.Contains("BEFORE the first device exists", one, StringComparison.Ordinal);
        }

        /// <summary>The in-process case gets its own sentence rather than folding into the one above, because the
        /// reader is looking at a variable that is demonstrably set and needs to be told why it did not count.</summary>
        [Fact]
        public void TheInProcessWarning_SaysTheRuntimeNeverSawIt()
        {
            string warning = MetalValidation.SetInProcessWarning(MetalValidation.DebugLayerVar);

            Assert.Contains("was NOT in the environment at launch", warning, StringComparison.Ordinal);
            Assert.Contains("MTLDebugDevice", warning, StringComparison.Ordinal);
        }

        /// <summary>A run on the default says NOTHING, because a line on every session is a line nobody
        /// reads.</summary>
        [Fact]
        public void AnUnvalidatedRun_LogsNoLineAtAll()
            => Assert.Equal("", MetalValidation.ActiveDescription(false, false, "AGXG14CDevice"));

        [Fact]
        public void AValidatedRun_NamesTheTierAndTheDeviceClass()
        {
            string line = MetalValidation.ActiveDescription(true, true, "MTLDebugDevice");

            Assert.Contains("SHADER VALIDATION", line, StringComparison.Ordinal);
            Assert.Contains("MTLDebugDevice", line, StringComparison.Ordinal);
            // Section 16 is explicit that neither tier is a synchronisation validator, and the API tier's line is
            // where a reader would otherwise assume it was.
            Assert.Contains("synchronisation validator",
                MetalValidation.ActiveDescription(true, false, "MTLDebugDevice"),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// THE SHADER-ONLY RUN READS AS VALIDATED, which is the line a reader of a #628 artifact was looking at
        /// while 99 warnings underneath told them the opposite. It names the shader variable and not the debug
        /// one, and it says what <c>MTLLegacySVDevice</c> IS rather than printing the class bare.
        /// </summary>
        [Theory]
        [InlineData("MTLLegacySVDevice")]
        [InlineData("MTLGPUDebugDevice")]
        public void AShaderOnlyRun_ReadsAsValidatedAndNamesItsOwnVariable(string className)
        {
            string line = MetalValidation.ActiveDescription(false, true, className);

            Assert.Contains("SHADER VALIDATION is ACTIVE", line, StringComparison.Ordinal);
            Assert.Contains(className, line, StringComparison.Ordinal);
            Assert.Contains("SHADER validation layer holding the device", line, StringComparison.Ordinal);
            Assert.Contains(MetalValidation.ShaderValidationVar, line, StringComparison.Ordinal);
            Assert.DoesNotContain(MetalValidation.DebugLayerVar, line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The reading half, which mutates process environment variables and therefore sits in the non-parallel
    /// collection beside the other rows that touch process-wide state.
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class MetalValidationReaderTests
    {
        readonly ITestOutputHelper _output;

        public MetalValidationReaderTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// The default: nothing asked for, nothing armed, nothing to warn about. Run against the environment as
        /// it stands, so it also records what the machine running the suite actually has.
        /// </summary>
        [Fact]
        public void TheProcessReports_WhateverThisRunWasLaunchedWith()
        {
            MetalValidationArming arming = MetalValidationReader.Capture();
            _output.WriteLine($"requested {arming.Requested}, armed {arming.Armed}");

            // The memo answers the same way, which is the property the ordering hazard depends on: what the Metal
            // runtime read is fixed at launch, so a second reading must not be able to say something else.
            Assert.Equal(arming, MetalValidationReader.Current());
        }

        /// <summary>
        /// ASKING FOR A TIER THE PROCESS CANNOT HAVE IS THE CASE THE WARN EXISTS FOR, and it is the one a tester
        /// hits: they set the engine's knob, the run starts, and nothing is validated because Metal read its
        /// variables before the process had a managed environment at all.
        /// </summary>
        [Fact]
        public void AskingForATierTheLaunchDidNotArm_IsReportedAsAMismatch()
        {
            using var _ = new EnvironmentValue(MetalValidation.EnvVarName, "1");
            using var __ = new EnvironmentValue(MetalValidation.DebugLayerVar, null);
            using var ___ = new EnvironmentValue(MetalValidation.ShaderValidationVar, null);

            MetalValidationArming arming = MetalValidationReader.Capture();

            Assert.Equal(MetalValidationMode.On, arming.Requested);
            Assert.Equal(MetalValidationMode.Off, arming.Armed);
            Assert.True(arming.RequestedMoreThanArmed);
        }

        /// <summary>
        /// THE DETECTION M-G3's LOG LINE ASKS FOR, and the reason it is possible at all. On Unix the CLR keeps
        /// its own copy of the environment and <c>Environment.SetEnvironmentVariable</c> never writes through, so
        /// a variable the managed side reports and <c>getenv</c> does not was set after launch and the Metal
        /// runtime never saw it. That is a read rather than a guess.
        /// <para>
        /// The assertion splits by platform because the mechanism does. Off macOS there is no Metal runtime to
        /// have read anything, so the managed answer is the whole answer and the code deliberately has no second
        /// path there to drift.
        /// </para>
        /// <para>
        /// AND IT GOES DORMANT IN A PROCESS THAT WAS LAUNCHED WITH THE VARIABLE, which is the `metal-native` CI
        /// leg, where `MTL_DEBUG_LAYER=1` is a job variable
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/591). The property under test is "the managed
        /// environment carries it and the native one does not", and there is no way to observe that in a process
        /// whose native environment carries it already: the in-process set becomes a no-op over a value that was
        /// there at launch, and the reader is RIGHT to report the launch. Dormant rather than a skip, for the
        /// reason every other dormant row here is, and NOT covered by `KE_METAL_REQUIRED`, which is about a
        /// machine having a device rather than about a property being observable.
        /// </para>
        /// </summary>
        [Fact]
        public void AVariableSetInProcess_IsTellableApartFromOneSetAtLaunch()
        {
            // Read BEFORE anything is set in-process. Armed with the in-process flag clear is exactly the shape
            // of a launch-armed process, and it is the only shape this row cannot measure.
            MetalValidationArming launch = MetalValidationReader.Capture();
            if (launch.Armed != MetalValidationMode.Off && !launch.DebugLayerSetInProcessOnly)
            {
                _output.WriteLine(
                    "dormant: this process was LAUNCHED with Metal validation armed (" + launch.Armed
                    + "), so an in-process set of MTL_DEBUG_LAYER cannot be told apart from the launch's own.");
                return;
            }

            using var _ = new EnvironmentValue(MetalValidation.EnvVarName, "1");
            using var __ = new EnvironmentValue(MetalValidation.DebugLayerVar, "1");

            MetalValidationArming arming = MetalValidationReader.Capture();

            if (KhaozEngineMetal.IsPlatformSupported)
            {
                Assert.True(arming.DebugLayerSetInProcessOnly,
                    "MTL_DEBUG_LAYER was set from managed code inside this test, so the native environment does "
                    + "not carry it and the reader must say so. If this fails, the CLR started writing through "
                    + "to the native environment and the whole in-process detection needs re-measuring.");
                Assert.Equal(MetalValidationMode.Off, arming.Armed);
            }
            else
            {
                Assert.False(arming.DebugLayerSetInProcessOnly);
                Assert.Equal(MetalValidationMode.On, arming.Armed);
            }
        }

        /// <summary>The shader variable is the higher rung and implies the API layer, so a process carrying only
        /// it still reports the higher tier. Reporting Off there would be the same failure this type exists to
        /// prevent, arriving from the other direction.</summary>
        [Fact]
        public void TheShaderVariableAlone_ReportsTheHigherTier()
        {
            if (KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: on macOS a managed set does not reach the native environment, which "
                    + "is the point of the row above, so the tier arithmetic is asserted off macOS instead.");
                return;
            }

            using var _ = new EnvironmentValue(MetalValidation.ShaderValidationVar, "1");
            using var __ = new EnvironmentValue(MetalValidation.DebugLayerVar, null);

            Assert.Equal(MetalValidationMode.Shaders, MetalValidationReader.Capture().Armed);
        }

        /// <summary>Sets an environment variable for the length of a test and puts back exactly what was there,
        /// including "nothing".</summary>
        sealed class EnvironmentValue : IDisposable
        {
            readonly string _name;
            readonly string? _original;

            internal EnvironmentValue(string name, string? value)
            {
                _name = name;
                _original = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
        }
    }
}
