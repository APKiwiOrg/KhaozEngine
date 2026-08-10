using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE AND THE QUEUE, ON REAL HARDWARE. Row 4 of the Metal design creates an <c>MTLDevice</c> and one
    /// <c>MTLCommandQueue</c>, drains before teardown (M-F6) and reports through the command-buffer error latch
    /// (M-G4), and every one of those is a claim only a real device can settle.
    /// <para>
    /// DORMANT OFF macOS RATHER THAN SKIPPED, which is phase 3's row-19 lesson: under <c>KE_GPU_TESTS=1</c> the
    /// Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a failure, so each row
    /// returns early with the platform recorded instead.
    /// </para>
    /// <para>
    /// THE COMPLEMENT TO THESE ROWS IS ENTIRELY DEVICE-FREE and lives beside them:
    /// <c>MetalDeviceSelectionTests</c> drives <c>KE_METAL_DEVICE</c> over hand-written candidates,
    /// <c>MetalDeviceLossLatchTests</c> drives the latch over a hand-written fault, and
    /// <c>MetalAutoreleaseArchitectureTests</c> walks the IL. What is left for a device is what a device is
    /// actually needed for.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalDeviceLifecycleTests
    {
        readonly ITestOutputHelper _output;

        public MetalDeviceLifecycleTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// A real device comes back, names itself the way the probe does, and reports no loss. The name check is
        /// the load-bearing one: it is the same reading through two paths, which is what says the probe's device
        /// and the created device are one device rather than two that happen to agree.
        /// </summary>
        [GpuFact]
        public void TheDevice_NamesTheSameDeviceTheProbeRead()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            _output.WriteLine($"backend {device.Backend}, device '{device.Capabilities.DeviceName}'");

            Assert.Equal(GpuBackendKind.MetalNative, device.Backend);
            Assert.False(string.IsNullOrWhiteSpace(device.Capabilities.DeviceName));
            Assert.Equal(MetalSupportProbe.ReadFacts().DeviceName, device.Capabilities.DeviceName);
        }

        /// <summary>
        /// <c>softwareAdapter</c> is FALSE with confidence rather than null (M-G2), because Apple ships no
        /// software Metal rasterizer at all. That is a genuinely different answer from "nobody asked", which is
        /// what the struct documents null as meaning and what the Veldrid Metal path correctly keeps because it
        /// cannot answer. And <c>deviceLossReason</c> is null on a healthy device, which is the state #427's
        /// header field reports from.
        /// </summary>
        [GpuFact]
        public void Diagnostics_AnswerBothFieldsRatherThanLeavingThemNull()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            GpuDeviceDiagnostics diagnostics = device.Diagnostics;

            Assert.False(diagnostics.SoftwareAdapter);
            Assert.Null(diagnostics.DeviceLossReason);
        }

        /// <summary>
        /// The partial capability read, asserted for the parts this row can answer HONESTLY. Row 16 owns the
        /// rest and the zero-permitted-difference parity test that pins all of it, and
        /// <c>MaxMsaaSampleCount</c> is pinned to 1 here rather than guessed, because a formula invented at this
        /// row would be a silent lie <c>AntiAliasing.ResolveFor</c> acts on.
        /// </summary>
        [GpuFact]
        public void Capabilities_AnswerTheMembersThisRowCanAnswer()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            GpuCapabilities capabilities = device.Capabilities;

            Assert.False(capabilities.ClipSpaceYInverted);
            Assert.True(capabilities.DepthRangeZeroToOne);
            Assert.True(capabilities.SamplerAnisotropy);
            // The one capability that differs from BOTH other native backends: MTLSamplerDescriptor has no LOD
            // bias at all, and the incumbent answers the same way, which is the bar.
            Assert.False(capabilities.SamplerLodBias);
            Assert.True(capabilities.SupportsCompute);
            Assert.True(capabilities.SupportsCompletionFences);
            Assert.Equal(1, capabilities.MaxMsaaSampleCount);
        }

        /// <summary>
        /// <c>WaitForIdle</c> is a REAL DRAIN even without the timeline: an empty command buffer committed and
        /// waited on covers everything committed to the queue before it, because a Metal queue executes in
        /// enqueue order. Metal has no device-level wait, so there is no <c>vkDeviceWaitIdle</c> to call, and
        /// this is how M-F6's "drain BEFORE teardown" is honoured with the timeline row still in flight.
        /// </summary>
        [GpuFact]
        public void WaitForIdle_Drains_AndIsASafeNoOpAfterTeardown()
        {
            if (!Available()) return;

            IGpuDevice device = CreateHeadless();

            device.WaitForIdle();
            device.WaitForIdle();

            device.Dispose();

            // After teardown it must RETURN rather than wait, because a torn-down device has no outstanding work
            // and waiting would wait on a queue nothing can advance (M-F6). Getting this wrong is a hang at
            // shutdown rather than a wrong pixel.
            device.WaitForIdle();
        }

        /// <summary>Disposal is idempotent, which matters because teardown order is exactly where a consumer
        /// disposing twice is normal rather than a defect.</summary>
        [GpuFact]
        public void Dispose_IsIdempotent()
        {
            if (!Available()) return;

            IGpuDevice device = CreateHeadless();

            device.Dispose();
            device.Dispose();
            device.Dispose();
        }

        /// <summary>
        /// MANY DEVICES IN SEQUENCE, because the golden suite creates and destroys one per test class and a
        /// backend that leaked its queue or its device would show up as a slow crawl rather than as a failure.
        /// This is the cheapest place to notice, and it is the row that would have caught an ownership mistake
        /// in the acquisition path where the enumerated device is BORROWED out of an <c>NSArray</c>.
        /// </summary>
        [GpuFact]
        public void ManyDevicesInSequence_CreateAndTearDownCleanly()
        {
            if (!Available()) return;

            for (int i = 0; i < 8; i++)
            {
                using IGpuDevice device = CreateHeadless();
                Assert.Equal(GpuBackendKind.MetalNative, device.Backend);
            }
        }

        /// <summary>
        /// EVERY UNBUILT MEMBER NAMES THE ROW THAT BUILDS IT, and says what IS live, because a reader who hits
        /// one needs to know whether the backend is unfinished or their machine is wrong and those have different
        /// answers. This row is what stops the ledger paragraph on the device from rotting silently: it fails the
        /// day a member starts working and nobody updated the message, which is exactly what it did when row 6
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/572) landed the resource factory and the shared
        /// sampler pair, and again when row 7
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573) landed the submit. All four moved from the
        /// refusal list to the live one below, which leaves the swapchain row carrying the message on its own.
        /// </summary>
        [GpuFact]
        public void EveryUnbuiltMember_NamesItsRowAndWhatIsLive()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            // LIVE as of row 6. Named here rather than deleted, because the whole value of this row is that the
            // ledger and the code disagree loudly, and a member silently dropped from the list is a member
            // nothing checks in either direction.
            Assert.NotNull(device.Factory);
            Assert.NotNull(device.PointSampler);
            Assert.NotNull(device.LinearSampler);

            // AND SUBMIT IS LIVE AS OF ROW 7, so what replaces its refusal is the one that member actually has,
            // which is a foreign list rather than an unbuilt path.
            using var foreign = new NullGpuCommandList();
            Assert.Contains("not created by this native Metal device",
                Refusal(() => device.Submit(foreign)), StringComparison.Ordinal);

            // The swapchain is the last row on this device that still refuses, so it is the one member left
            // carrying the NotBuiltYet message, and the message has to name BOTH landed rows as live.
            string present = Refusal(() => device.Present());
            _output.WriteLine(present);
            Assert.Contains("581", present, StringComparison.Ordinal);
            Assert.Contains("MTLCommandQueue", present, StringComparison.Ordinal);
            Assert.Contains("not about this machine", present, StringComparison.Ordinal);
            Assert.Contains("572", present, StringComparison.Ordinal);
            Assert.Contains("573", present, StringComparison.Ordinal);
        }

        /// <summary>A headless device has no swapchain BY DEFINITION, so null is the correct answer rather than
        /// an unbuilt one, and the windowed path refuses at creation instead of handing back a device that
        /// cannot present.</summary>
        [GpuFact]
        public void AHeadlessDevice_HasNoSwapchainFramebuffer()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            Assert.Null(device.SwapchainFramebuffer);
        }

        static IGpuDevice CreateHeadless() => new MetalBackendProvider().CreateHeadless().Device;

        static string Refusal(Action call) => Assert.ThrowsAny<Exception>(call).Message;

        // [SupportedOSPlatformGuard] rather than an inline check at every call site, which is the same mechanism
        // KhaozEngineMetal.IsPlatformSupported uses one level down. It is honest: the first thing this asks is
        // that guard, so a true answer really does imply macOS, and CA1416 then lets a row read the probe.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no Metal device to create.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }

    /// <summary>
    /// <c>KE_METAL_DEVICE</c> DRIVEN AGAINST A REAL MACHINE, which is the half the device-free policy tests
    /// cannot reach: <c>MTLCopyAllDevices()</c>, the ownership rule that an element read out of an
    /// <c>NSArray</c> is BORROWED and must be retained before the array goes, and the fact that the enumerated
    /// path and the default path land on the same device on a machine with one GPU.
    /// <para>
    /// In the non-parallel collection because it mutates a process environment variable, and every value is put
    /// back including "nothing".
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class MetalDeviceSelectionOnHardwareTests
    {
        readonly ITestOutputHelper _output;

        public MetalDeviceSelectionOnHardwareTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Pinning the device by a substring of its own name takes the ENUMERATED path and lands on the same
        /// device the default path does, on a machine with one GPU. That exercises MTLCopyAllDevices, the
        /// per-device requirement read, the retain-before-release ownership rule and the selection, none of
        /// which an ordinary run reaches at all.
        /// </summary>
        [GpuFact]
        public void PinningByName_TakesTheEnumeratedPathAndFindsTheSameDevice()
        {
            if (!KhaozEngineMetal.IsPlatformSupported) { Dormant("not macOS"); return; }
            if (MetalSupportProbe.MissingRequirement() is not null) { Dormant("no usable device"); return; }

            string name = MetalSupportProbe.ReadFacts().DeviceName;
            _output.WriteLine("default device: " + name);

            using var _ = new EnvVar(MetalDeviceSelection.EnvVarName, name);

            using IGpuDevice device = new MetalBackendProvider().CreateHeadless().Device;
            Assert.Equal(name, device.Capabilities.DeviceName);
        }

        /// <summary>
        /// A pin nothing matches WARNS and falls back rather than failing, which is the shape every lever in this
        /// fleet has. A name substring is machine-specific by nature, so turning a stale value into a refusal to
        /// start would make a diagnostic lever into a way of bricking a session.
        /// </summary>
        [GpuFact]
        public void APinNothingMatches_FallsBackToAnEligibleDevice()
        {
            if (!KhaozEngineMetal.IsPlatformSupported) { Dormant("not macOS"); return; }
            if (MetalSupportProbe.MissingRequirement() is not null) { Dormant("no usable device"); return; }

            using var _ = new EnvVar(MetalDeviceSelection.EnvVarName, "a-gpu-this-machine-does-not-have");

            using IGpuDevice device = new MetalBackendProvider().CreateHeadless().Device;
            Assert.False(string.IsNullOrWhiteSpace(device.Capabilities.DeviceName));
        }

        /// <summary>An index past the end does the same, and it is the other arm because the warning it produces
        /// names the count rather than the list.</summary>
        [GpuFact]
        public void AnIndexPastTheEnd_FallsBackToAnEligibleDevice()
        {
            if (!KhaozEngineMetal.IsPlatformSupported) { Dormant("not macOS"); return; }
            if (MetalSupportProbe.MissingRequirement() is not null) { Dormant("no usable device"); return; }

            using var _ = new EnvVar(MetalDeviceSelection.EnvVarName, "99");

            using IGpuDevice device = new MetalBackendProvider().CreateHeadless().Device;
            Assert.False(string.IsNullOrWhiteSpace(device.Capabilities.DeviceName));
        }

        void Dormant(string why) => _output.WriteLine("dormant: " + why + ".");

        sealed class EnvVar : IDisposable
        {
            readonly string _name;
            readonly string? _original;

            internal EnvVar(string name, string? value)
            {
                _name = name;
                _original = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
        }
    }
}
