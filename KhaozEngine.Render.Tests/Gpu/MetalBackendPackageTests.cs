using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <c>KhaozEngine.Gpu.Metal</c> package as work-breakdown row 2 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> leaves it: a real registration, a real
    /// functional probe, and creation that refuses by naming the row that builds it.
    /// <para>
    /// Every test here runs on macOS, Linux and Windows alike, which takes ARRANGING in a way the Vulkan
    /// sibling's did not. Metal is an OS-specific API (M-P1), so the probe rows below assert the SHAPE of the
    /// answer on every leg and the CONTENT only where there is a device to have one. What is asserted
    /// everywhere is that asking never throws, that the provider's boolean tracks the probe's sentence, and that
    /// each of the three refusals stays tellable apart from the other two.
    /// </para>
    /// </summary>
    public sealed class MetalBackendPackageTests
    {
        readonly ITestOutputHelper _output;

        public MetalBackendPackageTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE CONTRACT THAT MATTERS MOST, because the probe reaches libobjc, the Metal framework and a driver,
        /// and any of the three can fail in ways that are not a return value. A probe that blows up and a probe
        /// that answers no are the same answer to the settings screen and the fallback that consume it, so the
        /// exception never escapes.
        /// </summary>
        [Fact]
        public void TheSupportProbe_NeverThrows_AndAnswersTheSameWayTwice()
        {
            var provider = new MetalBackendProvider();

            bool supported = provider.IsSupported();

            // Asking twice must not change the answer. The selector caches per backend, so a probe that answered
            // differently on a second call would make the cached value depend on who asked first. It also means
            // the device really was released: a probe that leaked one every time would be a slow leak nothing
            // else in the suite would notice.
            Assert.Equal(supported, provider.IsSupported());
        }

        /// <summary>
        /// The probe's own half, run for real, which is the integration this row owes. It asserts the SHAPE of
        /// the answer rather than the answer: null means this machine can run the backend, and anything else is
        /// a sentence a tester can act on. An empty or whitespace rejection would read in a session log as a
        /// machine that failed for no reason, which is why null is the only yes.
        /// </summary>
        [Fact]
        public void TheProbe_AnswersWithAReason_OnWhateverMachineThisIs()
        {
            string? missing = TryProbe();
            _output.WriteLine(missing is null ? "this machine can run the native Metal backend" : missing);

            if (missing is null) return;
            Assert.False(string.IsNullOrWhiteSpace(missing));
        }

        /// <summary>
        /// The provider's answer IS the probe's answer, with the swallow in between. Worth pinning because those
        /// are the two halves decision M-I4 keeps apart: a machine that cannot run the backend must be reported
        /// through <see cref="GpuBackendSource.FallbackAfterFailure"/>, and the only way that stays true is if
        /// the provider's boolean tracks the probe's sentence rather than acquiring an opinion of its own.
        /// </summary>
        [Fact]
        public void TheProviderAnswer_IsExactlyTheProbeAnswer()
            => Assert.Equal(TryProbe() is null, new MetalBackendProvider().IsSupported());

        /// <summary>
        /// THE PLATFORM GUARD IS THE FIRST ANSWER, and off macOS it is the whole of it (M-P1). The point is not
        /// that the answer is false, which would be true of a Mac with no device too, but that the reason names
        /// the OPERATING SYSTEM: a platform that has no Metal is not a fault, and reporting it as a machine
        /// failure would send a Linux reader looking for a driver to install.
        /// </summary>
        [Fact]
        public void OffMacOs_TheAnswerNamesThePlatformRatherThanTheMachine()
        {
            if (KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: this IS macOS, so the platform guard is not the answer here.");
                return;
            }

            Assert.False(new MetalBackendProvider().IsSupported());

            string? missing = MetalSupportProbe.MissingRequirement();
            Assert.NotNull(missing);
            Assert.Contains("operating system", missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// Creation is not built yet, and the refusal is asserted rather than left implicit because of what the
        /// creation path does with it: it catches, WARNs with the message and falls back to the incumbent, so
        /// this text is what a tester who named the native backend actually reads.
        /// <para>
        /// Which of the three refusals is correct depends on the machine, and asserting that split is the point.
        /// Off macOS it is a <see cref="PlatformNotSupportedException"/> about the PLATFORM (M-P1, and the one
        /// place this package differs from the Vulkan sibling, which has no guard to raise). On a Mac whose
        /// probe answers no it is about the MACHINE. On a Mac whose probe answers yes it is about the PACKAGE
        /// and names row 4. None of the three may be
        /// <see cref="GpuBackendProviderMissingException"/>, which is about the WIRING and is thrown by the
        /// registry before this type is reached at all.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Creation_RefusesWithTheRightOneOfThreeReasons(bool windowed)
        {
            var provider = new MetalBackendProvider();

            Exception ex = windowed
                ? Assert.ThrowsAny<Exception>(() => provider.CreateForWindow(default))
                : Assert.ThrowsAny<Exception>(() => provider.CreateHeadless());
            _output.WriteLine(ex.GetType().Name + ": " + ex.Message);

            Assert.IsNotType<GpuBackendProviderMissingException>(ex);

            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                Assert.IsType<PlatformNotSupportedException>(ex);
                Assert.Contains("operating system", ex.Message, StringComparison.Ordinal);
                return;
            }

            Assert.IsType<NotSupportedException>(ex);
            if (MetalSupportProbe.MissingRequirement() is null)
            {
                // The PACKAGE refusal, which must name the row that ends it. A message that only said
                // "unsupported" would be indistinguishable in a log from the machine refusal below.
                Assert.Contains("570", ex.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("about the MACHINE", ex.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The provider returns a real device or throws, and never hands back an empty result. Pinned here
        /// because the adopting path checks for a null device and produces its own message for it, and that
        /// guard only stays meaningful while no provider actually relies on it.
        /// </summary>
        [Fact]
        public void CreationNeverReturnsAnEmptyResult()
        {
            var provider = new MetalBackendProvider();

            Assert.ThrowsAny<Exception>(() => provider.CreateForWindow(default));
            Assert.ThrowsAny<Exception>(() => provider.CreateHeadless());
        }

        /// <summary>
        /// M-N4's four reads taken off a REAL device, with the whole reading printed, because the numbers are as
        /// much the deliverable as the pass is. This is the row that says the probe measures the machine rather
        /// than reciting the OS, and the alignment answer in particular is the one that would silently corrupt
        /// every ring bind if a future device raised it (row 2's own regression evidence).
        /// <para>
        /// DORMANT OFF macOS RATHER THAN SKIPPED, which is phase 3's row-19 lesson: under
        /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip
        /// is a failure, so this returns early with the platform recorded instead.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheFourReads_AnswerOnARealDevice()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no Metal device to read.");
                return;
            }

            MetalDeviceFacts facts = MetalSupportProbe.ReadFacts();
            string report = "device created: " + facts.DeviceCreated + "\n"
                + "name: " + facts.DeviceName + "\n"
                + "highest Apple family: " + facts.HighestAppleFamily + "\n"
                + "Mac2: " + facts.SupportsMac2 + ", Common1: " + facts.SupportsCommon1 + "\n"
                + "buffer-offset alignment: " + facts.BufferOffsetAlignment
                + " (from " + facts.BufferOffsetAlignmentSource + ")\n"
                + "supportsTextureSampleCount:1: " + facts.SupportsTextureSampleCount1;
            _output.WriteLine(report);

            Assert.Null(MetalDeviceRequirements.MissingRequirement(facts));

            // And the reads are reads rather than constants. Each of these is a value that came back from the
            // device, so a probe quietly returning a fabricated snapshot would pass the line above and fail
            // here.
            Assert.True(facts.DeviceCreated, report);
            Assert.False(string.IsNullOrWhiteSpace(facts.DeviceName), report);
            Assert.True(facts.SupportsCommon1, "every Metal device answers the shared baseline family:\n" + report);
            Assert.NotEqual("(no device)", facts.BufferOffsetAlignmentSource);
        }

        // The probe, with the same swallow the provider applies, so a machine whose interop throws out of the
        // P/Invoke layer is a "no" here too rather than a red test. Reaching past the provider is deliberate:
        // the rows above compare the two, and comparing them through the same catch is what makes the
        // comparison mean something on a machine where the probe cannot even ask.
        static string? TryProbe()
        {
            try
            {
                return MetalSupportProbe.MissingRequirement();
            }
            catch (Exception ex)
            {
                return "the probe threw " + ex.GetType().Name + ", which the provider reports as unsupported";
            }
        }
    }

    /// <summary>
    /// That the test process actually has the REAL native Metal provider registered, through
    /// <c>KhaozEngine.TestSupport.Gpu/MetalBackendRegistration.cs</c>, which every assembly taking
    /// <c>[GpuFact]</c> reaches from that attribute's static constructor and which this assembly also calls from
    /// the thin module initializer in <c>MetalBackendRegistrationInitializer.cs</c>. These rows are exactly why
    /// that second one exists: they carry no <c>[GpuFact]</c>, so a filtered run of them alone would never touch
    /// the attribute and never fire the hook.
    /// <para>
    /// In the non-parallel collection because it reads the process-wide registry, which the Direct3D 11
    /// append-audit rows temporarily empty. Worth asserting at all because the registration is invisible: no GPU
    /// test runs on this backend yet, so nothing else in the suite fails if it silently stops happening.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class MetalBackendRegistrationTests
    {
        [Fact]
        public void TheTestAssembly_RegistersTheRealNativeBackend()
        {
            Assert.True(GpuBackendProviders.IsRegistered(KhaozEngineMetal.MetalNativeKind));

            IGpuBackendProvider provider = GpuBackendProviders.Require(KhaozEngineMetal.MetalNativeKind);
            Assert.Same(typeof(KhaozEngineMetal).Assembly, provider.GetType().Assembly);
        }

        /// <summary>
        /// A repeated startup call is harmless, which matters because "call it once at startup" is advice rather
        /// than something the type system enforces, and a game with two entry points can easily call it twice.
        /// </summary>
        [Fact]
        public void Register_IsIdempotent()
        {
            IGpuBackendProvider first = GpuBackendProviders.Require(KhaozEngineMetal.MetalNativeKind);

            KhaozEngineMetal.Register();
            KhaozEngineMetal.Register();

            Assert.Same(first, GpuBackendProviders.Require(KhaozEngineMetal.MetalNativeKind));
        }

        /// <summary>
        /// The append site answered for a THIRD backend the same way it was for the first two and with no edit
        /// at all: <see cref="GpuBackendProviders.RequiresProvider"/> is stated as "everything the built-in path
        /// does not build", so an appended kind is provider-backed by default. That is the safe direction, and
        /// it is what makes registering under the pinned ordinal below reach the provider registry rather than
        /// the Veldrid creation switch, whose discard arm would ask Veldrid for a Metal device.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsProviderBacked_WithNoEditToTheRegistry()
        {
            Assert.True(GpuBackendProviders.RequiresProvider(KhaozEngineMetal.MetalNativeKind));
            Assert.False(GpuBackendProviders.RequiresProvider(GpuBackendKind.Metal));
        }

        /// <summary>
        /// The registration is what makes the machine question askable at all, so the selector must route the
        /// pinned kind to THIS provider's functional probe rather than to Veldrid, which cannot answer for a
        /// backend it does not implement. Compared against the provider's own answer rather than pinned to a
        /// constant, because the right answer is a fact about the machine the suite is running on.
        /// </summary>
        [Fact]
        public void IsBackendSupported_RoutesToTheMetalProvidersOwnProbe()
        {
            IGpuBackendProvider provider = GpuBackendProviders.Require(KhaozEngineMetal.MetalNativeKind);

            Assert.Equal(provider.IsSupported(),
                GpuBackendSelector.IsBackendSupported(KhaozEngineMetal.MetalNativeKind));
        }
    }
}
