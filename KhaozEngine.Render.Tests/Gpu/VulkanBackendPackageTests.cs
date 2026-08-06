using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <c>KhaozEngine.Gpu.Vulkan</c> package as work-breakdown row 4 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> leaves it: a real registration, a real
    /// functional probe, real HEADLESS device creation, and a windowed path that refuses by naming the row that
    /// builds the swapchain.
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
        /// The WINDOWED path is not built yet, and the refusal is asserted rather than left implicit because of
        /// what the creation path does with it: it catches, WARNs with the message and falls back to the
        /// incumbent, so this text is what a tester who named the native backend actually reads. It must name the
        /// row that builds the swapchain, it must say the device itself IS built (so the reader does not conclude
        /// the whole backend is absent), and it must not read as a machine problem, which is a different failure
        /// with a different answer (<see cref="VulkanBackendProvider.IsSupported"/>) and its own sentence.
        /// </summary>
        [Fact]
        public void WindowedCreation_RefusesByNamingTheSwapchainRow()
        {
            var provider = new VulkanBackendProvider();

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => provider.CreateForWindow(default));

            Assert.Contains("527", ex.Message, StringComparison.Ordinal);
            Assert.Contains("514", ex.Message, StringComparison.Ordinal);
            // Not the OTHER failure mode. A missing registration is a wiring fault with its own exception type
            // and its own message, and decision V-I4 exists to keep the two tellable apart.
            Assert.IsNotType<GpuBackendProviderMissingException>(ex);
            // And not the Direct3D 11 package's answer either. There is no platform guard here (V-P1), so a
            // PlatformNotSupportedException would mean somebody added one back by analogy.
            Assert.IsNotType<PlatformNotSupportedException>(ex);
        }

        /// <summary>
        /// HEADLESS CREATION IS REAL FROM ROW 4, and this row asserts whichever half of that this machine can
        /// show. On a machine the probe accepts, a device is created, reports the native backend, answers its
        /// capability read and disposes cleanly, and the shared instance goes with it. On a machine the probe
        /// refuses, which is every developer Mac in this fleet, creation fails with a message about the MACHINE
        /// rather than about the package: no loader is not an unfinished row, and the old "the device lands in
        /// row 4" refusal must be gone from this path entirely.
        /// <para>
        /// THE REAL PATH IS CI-DEFERRED to the <c>vulkan-native</c> Linux leg row 19 brings up
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/529), which is the only machine in the net with a
        /// loader. Everything about the device that CAN be asserted without one is asserted device-free in
        /// <see cref="VulkanInstanceLifecycleTests"/>, <see cref="VulkanFeatureChainTests"/>,
        /// <see cref="VulkanDeviceSelectionTests"/> and <see cref="VulkanDeviceLossLatchTests"/>. This row is the
        /// one that will start meaning something the day the leg exists, and it is deliberately not skippable so
        /// it cannot go quiet.
        /// </para>
        /// </summary>
        [Fact]
        public void HeadlessCreation_BuildsARealDevice_OrFailsAboutTheMachine()
        {
            var provider = new VulkanBackendProvider();

            if (TryProbe() is not null)
            {
                NotSupportedException ex = Assert.Throws<NotSupportedException>(() => provider.CreateHeadless());

                Assert.Contains("this machine", ex.Message, StringComparison.Ordinal);
                // The row-4 refusal is retired from this path. A message naming 514 here would mean the device
                // row did not actually wire itself in.
                Assert.DoesNotContain("514", ex.Message, StringComparison.Ordinal);
                // And nothing was claimed on the way out, which is what stops a failed creation holding the
                // process instance alive for the rest of the run.
                Assert.Equal(0, VulkanInstance.LeaseCount);
                return;
            }

            GpuProviderDevice created = provider.CreateHeadless();
            try
            {
                Assert.NotNull(created.Device);
                Assert.Equal(GpuBackendKind.VulkanNative, created.Device.Backend);
                Assert.False(string.IsNullOrWhiteSpace(created.Device.Capabilities.DeviceName));
                // A headless device has no swapchain, which is a fact about the path rather than an unbuilt
                // member.
                Assert.Null(created.Device.SwapchainFramebuffer);
                // Nothing is latched on a healthy device, so the header field stays absent.
                Assert.Null(created.Device.Diagnostics.DeviceLossReason);
                Assert.False(created.Device.Diagnostics.IsDeviceLost);
                // There is no Vulkan analogue of the Direct3D 11 driver-threading query, so both halves are null
                // rather than a probe that failed.
                Assert.Null(created.ThreadingCaps);
                Assert.Null(created.ThreadingProbeFailure);

                Assert.True(VulkanInstance.LeaseCount >= 1);
                Assert.True(VulkanInstance.IsLive);

                // Safe after the device is alive and safe again after it is dead, which is the V-F10 contract.
                created.Device.WaitForIdle();
            }
            finally
            {
                created.Device.Dispose();
            }

            // Disposed twice on purpose: a consumer is entitled to, and a second release that dropped the
            // refcount again would destroy an instance a concurrent device is still calling through.
            created.Device.Dispose();
            created.Device.WaitForIdle();
        }

        /// <summary>
        /// The members later rows own throw a message naming their row rather than returning something that fails
        /// later somewhere less informative, which is the discipline <c>D3D11ResourceFactory</c> established
        /// between its own row and the ones that filled it in. Asserted through the seam type so the list cannot
        /// drift from what <see cref="IGpuDevice"/> actually declares.
        /// </summary>
        [Fact]
        public void TheUnbuiltMembers_NameTheirOwnRow()
        {
            if (TryProbe() is not null) return;   // no loader here, and the row above already asserted that

            GpuProviderDevice created = new VulkanBackendProvider().CreateHeadless();
            try
            {
                IGpuDevice device = created.Device;

                Assert.Contains("519", Assert.Throws<NotSupportedException>(() => device.Factory).Message,
                    StringComparison.Ordinal);
                Assert.Contains("519", Assert.Throws<NotSupportedException>(() => device.PointSampler).Message,
                    StringComparison.Ordinal);
                Assert.Contains("517", Assert.Throws<NotSupportedException>(() => device.Submit(null!)).Message,
                    StringComparison.Ordinal);
                Assert.Contains("527", Assert.Throws<NotSupportedException>(() => device.Present()).Message,
                    StringComparison.Ordinal);
            }
            finally
            {
                created.Device.Dispose();
            }
        }

        /// <summary>
        /// The provider returns a real device or throws, and never hands back an empty result. Pinned here
        /// because the adopting path checks for a null device and produces its own message for it, and that guard
        /// only stays meaningful while no provider actually relies on it.
        /// </summary>
        [Fact]
        public void WindowedCreationNeverReturnsAnEmptyResult()
            => Assert.ThrowsAny<Exception>(() => new VulkanBackendProvider().CreateForWindow(default));

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
    /// In the non-parallel collection because it reads the process-wide registry, which BOTH append audits
    /// temporarily empty, this backend's own included. Worth asserting at all because the registration is
    /// invisible: no GPU test runs on this backend yet, so nothing else in the suite fails if it silently stops
    /// happening.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class VulkanBackendRegistrationTests
    {
        [Fact]
        public void TheTestAssembly_RegistersTheRealNativeBackend()
        {
            Assert.True(GpuBackendProviders.IsRegistered(GpuBackendKind.VulkanNative));

            IGpuBackendProvider provider = GpuBackendProviders.Require(GpuBackendKind.VulkanNative);
            Assert.Same(typeof(KhaozEngineVulkan).Assembly, provider.GetType().Assembly);
        }

        /// <summary>
        /// A repeated startup call is harmless, which matters because "call it once at startup" is advice rather
        /// than something the type system enforces, and a game with two entry points can easily call it twice.
        /// </summary>
        [Fact]
        public void Register_IsIdempotent()
        {
            IGpuBackendProvider first = GpuBackendProviders.Require(GpuBackendKind.VulkanNative);

            KhaozEngineVulkan.Register();
            KhaozEngineVulkan.Register();

            Assert.Same(first, GpuBackendProviders.Require(GpuBackendKind.VulkanNative));
        }

        // The fourteenth append site, RequiresProvider answering provider-backed with no edit at all, is asserted
        // once in GpuBackendKindVulkanAppendAuditTests where the audit narrative reads it. It was duplicated
        // verbatim here, which is a row that can only ever go red in two places at once.

        /// <summary>
        /// The registration is what makes the machine question askable at all, so the selector must route the
        /// native kind to THIS provider's functional probe rather than to Veldrid, which cannot answer for a
        /// backend it does not implement. Compared against the provider's own answer rather than pinned to a
        /// constant, because the right answer is a fact about the machine the suite is running on.
        /// </summary>
        [Fact]
        public void IsBackendSupported_RoutesToTheVulkanProvidersOwnProbe()
        {
            IGpuBackendProvider provider = GpuBackendProviders.Require(GpuBackendKind.VulkanNative);

            Assert.Equal(provider.IsSupported(),
                GpuBackendSelector.IsBackendSupported(GpuBackendKind.VulkanNative));
        }
    }
}
