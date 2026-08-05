using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <c>KhaozEngine.Gpu.Vulkan</c> package as work-breakdown row 2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> leaves it: a real registration, a real
    /// functional probe, and creation that refuses by naming the row that builds it.
    /// <para>
    /// Every test here runs on macOS, Linux and Windows alike, and unlike the Direct3D 11 package that needs no
    /// arranging at all (decision V-P1: no OS-suffixed TFM, no platform guard, no <c>NoInlining</c> bodies). The
    /// probe rows below therefore run the REAL probe on whatever machine the suite is on, which on a developer
    /// Mac with no Vulkan loader means the "no loader" answer and on the Linux leg means lavapipe. Both are a
    /// pass: what is asserted is that the probe answers rather than what it answers, because the answer is a fact
    /// about the machine and the contract is that asking never throws.
    /// </para>
    /// </summary>
    public sealed class VulkanBackendPackageTests
    {
        /// <summary>
        /// THE CONTRACT THAT MATTERS MOST on this backend, because the probe reaches a native loader, a driver
        /// and an ICD and any of the three can fail in ways that are not a return value. A probe that blows up
        /// and a probe that answers no are the same answer to the settings screen and the fallback that consume
        /// it, so the exception never escapes.
        /// </summary>
        [Fact]
        public void TheSupportProbe_NeverThrows_AndAnswersTheSameWayTwice()
        {
            var provider = new VulkanBackendProvider();

            bool supported = provider.IsSupported();

            // Asking twice must not change the answer. The selector caches per backend, so a probe that answered
            // differently on a second call would make the cached value depend on who asked first. It also means
            // the throwaway instance really was destroyed: a probe that leaked one would eventually stop being
            // able to create another.
            Assert.Equal(supported, provider.IsSupported());
        }

        /// <summary>
        /// The probe's own half, run for real, which is the integration this row owes. It asserts the SHAPE of
        /// the answer rather than the answer: null means this machine can run the backend, and anything else is a
        /// sentence a tester can act on. An empty or whitespace rejection would read in a session log as a
        /// machine that failed for no reason, which is why null is the only yes.
        /// </summary>
        [Fact]
        public void TheProbe_AnswersWithAReason_OnWhateverMachineThisIs()
        {
            string? missing = TryProbe();

            if (missing is null) return;   // this machine can run the native Vulkan backend
            Assert.False(string.IsNullOrWhiteSpace(missing));
        }

        /// <summary>
        /// The provider's answer IS the probe's answer, with the swallow in between. Worth pinning because those
        /// are the two halves decision V-I4 keeps apart: a machine that cannot run the backend must be reported
        /// through <see cref="GpuBackendSource.FallbackAfterFailure"/>, and the only way that stays true is if the
        /// provider's boolean tracks the probe's sentence rather than acquiring an opinion of its own.
        /// </summary>
        [Fact]
        public void TheProviderAnswer_IsExactlyTheProbeAnswer()
            => Assert.Equal(TryProbe() is null, new VulkanBackendProvider().IsSupported());

        /// <summary>
        /// Creation is not built yet, and the refusal is asserted rather than left implicit because of what the
        /// creation path does with it: it catches, WARNs with the message and falls back to the incumbent, so
        /// this text is what a tester who named the native backend actually reads. It must name the row that
        /// builds the device, and it must not read as a machine problem, which is a different failure with a
        /// different answer (<see cref="VulkanBackendProvider.IsSupported"/>) and its own sentence.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Creation_RefusesByNamingTheRowThatBuildsIt(bool windowed)
        {
            var provider = new VulkanBackendProvider();

            NotSupportedException ex = windowed
                ? Assert.Throws<NotSupportedException>(() => provider.CreateForWindow(default))
                : Assert.Throws<NotSupportedException>(() => provider.CreateHeadless());

            Assert.Contains("514", ex.Message, StringComparison.Ordinal);
            // Not the OTHER failure mode. A missing registration is a wiring fault with its own exception type
            // and its own message, and decision V-I4 exists to keep the two tellable apart.
            Assert.IsNotType<GpuBackendProviderMissingException>(ex);
            // And not the Direct3D 11 package's answer either. There is no platform guard here (V-P1), so a
            // PlatformNotSupportedException would mean somebody added one back by analogy.
            Assert.IsNotType<PlatformNotSupportedException>(ex);
        }

        /// <summary>
        /// The provider returns a real device or throws, and never hands back an empty result. Pinned here
        /// because the adopting path checks for a null device and produces its own message for it, and that guard
        /// only stays meaningful while no provider actually relies on it.
        /// </summary>
        [Fact]
        public void CreationNeverReturnsAnEmptyResult()
        {
            var provider = new VulkanBackendProvider();

            Assert.ThrowsAny<Exception>(() => provider.CreateForWindow(default));
            Assert.ThrowsAny<Exception>(() => provider.CreateHeadless());
        }

        // The probe, with the same swallow the provider applies, so a machine whose loader throws out of the
        // P/Invoke layer is a "no" here too rather than a red test. Reaching past the provider is deliberate: the
        // rows above compare the two, and comparing them through the same catch is what makes the comparison mean
        // something on a machine where the probe cannot even ask.
        static string? TryProbe()
        {
            try
            {
                return VulkanSupportProbe.MissingRequirement();
            }
            catch (Exception ex)
            {
                return "the probe threw " + ex.GetType().Name + ", which the provider reports as unsupported";
            }
        }
    }

    /// <summary>
    /// That the test process actually has the REAL native Vulkan provider registered, through
    /// <c>KhaozEngine.TestSupport.Gpu/VulkanBackendRegistration.cs</c>, which every assembly taking
    /// <c>[GpuFact]</c> reaches from that attribute's static constructor and which this assembly also calls from
    /// the thin module initializer in <c>VulkanBackendRegistrationInitializer.cs</c>. These rows are exactly why
    /// that second one exists: they carry no <c>[GpuFact]</c>, so a filtered run of them alone would never touch
    /// the attribute and never fire the hook.
    /// <para>
    /// In the non-parallel collection because it reads the process-wide registry, which the Direct3D 11
    /// append-audit rows temporarily empty. Worth asserting at all because the registration is invisible: no GPU
    /// test runs on this backend yet, so nothing else in the suite fails if it silently stops happening.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class VulkanBackendRegistrationTests
    {
        [Fact]
        public void TheTestAssembly_RegistersTheRealNativeBackend()
        {
            Assert.True(GpuBackendProviders.IsRegistered(KhaozEngineVulkan.VulkanNativeKind));

            IGpuBackendProvider provider = GpuBackendProviders.Require(KhaozEngineVulkan.VulkanNativeKind);
            Assert.Same(typeof(KhaozEngineVulkan).Assembly, provider.GetType().Assembly);
        }

        /// <summary>
        /// A repeated startup call is harmless, which matters because "call it once at startup" is advice rather
        /// than something the type system enforces, and a game with two entry points can easily call it twice.
        /// </summary>
        [Fact]
        public void Register_IsIdempotent()
        {
            IGpuBackendProvider first = GpuBackendProviders.Require(KhaozEngineVulkan.VulkanNativeKind);

            KhaozEngineVulkan.Register();
            KhaozEngineVulkan.Register();

            Assert.Same(first, GpuBackendProviders.Require(KhaozEngineVulkan.VulkanNativeKind));
        }

        /// <summary>
        /// The fourteenth append site, answered for Vulkan the same way it was for Direct3D 11 and with no edit
        /// at all: <see cref="GpuBackendProviders.RequiresProvider"/> is stated as "everything the built-in path
        /// does not build", so an appended kind is provider-backed by default. That is the safe direction, and it
        /// is what makes registering under the pinned ordinal below reach the provider registry rather than the
        /// Veldrid creation switch.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsProviderBacked_WithNoEditToTheRegistry()
        {
            Assert.True(GpuBackendProviders.RequiresProvider(KhaozEngineVulkan.VulkanNativeKind));
            Assert.False(GpuBackendProviders.RequiresProvider(GpuBackendKind.Vulkan));
        }

        /// <summary>
        /// THE TRIPWIRE ON THE PINNED ORDINAL, and it is meant to go red exactly once.
        /// <para>
        /// Row 2 registers under <c>(GpuBackendKind)5</c> because row 3
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/513) owns the <c>VulkanNative = 5</c> append and
        /// parallelises with this row rather than preceding it. The registry keys by VALUE, so the registration
        /// is correct today and becomes correct BY NAME the moment that member lands. What must not happen is the
        /// cast surviving the append, because a magic number nobody is forced to remove is how a temporary shim
        /// becomes permanent.
        /// </para>
        /// <para>
        /// So this row fails the moment ordinal 5 gains a name, and its message is the instruction. If you are
        /// reading this because it just went red: that is the design working. Do what it says.
        /// </para>
        /// </summary>
        [Fact]
        public void ThePinnedOrdinal_IsStillAShim_AndItsReplacementIsRow3sJob()
        {
            Assert.Equal(5, (int)KhaozEngineVulkan.VulkanNativeKind);

            Assert.False(Enum.IsDefined(typeof(GpuBackendKind), KhaozEngineVulkan.VulkanNativeKind),
                "GpuBackendKind ordinal 5 now has a name, so row 3's append has landed and the shim it was "
                + "written around is done. Two edits and this is finished: replace the (GpuBackendKind)5 cast on "
                + "KhaozEngineVulkan.VulkanNativeKind with GpuBackendKind.VulkanNative (or drop the constant and "
                + "name the member directly in Register), then delete this test. The rest of this class keeps "
                + "holding either way, because it never spells the ordinal anywhere but here.");
        }

        /// <summary>
        /// The registration is what makes the machine question askable at all, so the selector must route the
        /// pinned kind to THIS provider's functional probe rather than to Veldrid, which cannot answer for a
        /// backend it does not implement. Compared against the provider's own answer rather than pinned to a
        /// constant, because the right answer is a fact about the machine the suite is running on.
        /// </summary>
        [Fact]
        public void IsBackendSupported_RoutesToTheVulkanProvidersOwnProbe()
        {
            IGpuBackendProvider provider = GpuBackendProviders.Require(KhaozEngineVulkan.VulkanNativeKind);

            Assert.Equal(provider.IsSupported(),
                GpuBackendSelector.IsBackendSupported(KhaozEngineVulkan.VulkanNativeKind));
        }
    }
}
