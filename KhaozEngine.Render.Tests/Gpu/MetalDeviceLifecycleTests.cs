using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        /// The capability read as the seam sees it, which row 16
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/582) completed. The constants are asserted here
        /// because this is the row that owns "a real device came up and answered", and
        /// <c>MaxMsaaSampleCount</c> is asserted as a POWER OF TWO OF AT LEAST FOUR, because its exact value above
        /// that is a property of the machine. Four is the floor rather than the walk's own 1: an Apple M2 Max
        /// reports 4, every Metal device the engine supports answers at least 4, and a 1 here means the walk found
        /// nothing rather than that the machine is unusual. What pins the number against the incumbent's own is
        /// <c>NativeVsVeldridMetalCapabilityParityTests</c>, which reads both devices in one process.
        /// </summary>
        [GpuFact]
        public void Capabilities_AnswerEveryMemberOfTheSeam()
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
            Assert.True(capabilities.SupportsShadowMaps);
            Assert.True(capabilities.SupportsCompute);
            Assert.True(capabilities.SupportsCompletionFences);

            int msaa = capabilities.MaxMsaaSampleCount;
            _output.WriteLine($"MaxMsaaSampleCount {msaa}");
            Assert.True((msaa & (msaa - 1)) == 0,
                $"the sample-count walk asks only powers of two, so {msaa} could not have come out of it.");
            Assert.True(msaa >= 4,
                $"this device reported {msaa} as its highest sample count. An Apple M2 Max reports 4 and every "
                + "Metal device the engine supports answers at least 4, so a lower number is the walk having "
                + "found nothing rather than a machine fact, and the scene3d_hdr_msaa golden is baked at 4.");
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
        /// NOTHING ON THIS DEVICE IS UNBUILT ANY MORE, AND THE QUESTION CHANGED WITH THAT.
        /// <para>
        /// This row used to assert that the last unbuilt member named the row that would build it and said what
        /// WAS live, which is what stopped the ledger paragraph on the device from rotting silently: it failed
        /// the day a member started working and nobody updated the message, which it duly did at row 6
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/572), again at row 7
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), and a last time at row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581), which took the swapchain and left the message
        /// with no caller at all.
        /// </para>
        /// <para>
        /// <b>SO IT ASKS THE OTHER QUESTION NOW: is ANYTHING unbuilt.</b> That is the same transformation row 14
        /// made on <c>MetalCommandList</c>'s own unbuilt ledger, for the same reason: answering it needs EVERY
        /// member called, where a row that walked one member forward would pass forever the day somebody put a
        /// stub back on a different one. The refusals that remain are refusals about the CALLER (a foreign
        /// command list) rather than about the package, and telling those two apart is what the row is for.
        /// </para>
        /// <para>
        /// <b>AND "EVERY MEMBER" IS CHECKED BY REFLECTION RATHER THAN BELIEVED</b>, which is the same guard
        /// <c>NativeVsVeldridMetalCapabilityParityTests</c> puts on its hand-written comparer. The first version
        /// of this row drove twelve of the interface's members and claimed all of them: the whole data half
        /// (both <c>Map</c> overloads, both <c>Unmap</c>, all three <c>UpdateBuffer</c>, both
        /// <c>UpdateTexture</c>) and <c>Submit(list, fence)</c> were never called, so a stub put back on
        /// <c>Map</c> would have passed this forever. Every call is recorded by name, and
        /// <see cref="RequireEveryMemberDriven"/> compares that against
        /// <c>typeof(IGpuDevice)</c>'s own public members INCLUDING their overload counts, so a member appended
        /// to the seam fails this row until somebody drives it.
        /// </para>
        /// </summary>
        [GpuFact]
        public void NoMemberOfTheDeviceIsUnbuilt()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            // THE MINIMUM RESOURCES THE DATA MEMBERS NEED. Staging on both, because Map is the member that
            // refuses anything else on this backend (M-M2: every other texture is Private and has no CPU-visible
            // memory at all), and a buffer big enough for the writes below.
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.Staging));
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using IGpuFence fence = device.Factory.CreateFence();

            var texels = new byte[2 * 2 * 4];
            byte one = 1;

            // EVERY MEMBER, called, and RECORDED as it is called. A NotSupportedException out of any of them is
            // the unbuilt shape, and the helper below is what names which member produced one.
            var driven = new List<string>();

            void Drive(Action call, string member)
            {
                driven.Add(member);
                NothingUnbuilt(call, member);
            }

            Drive(() => _ = device.Backend, nameof(device.Backend));
            Drive(() => _ = device.Capabilities, nameof(device.Capabilities));
            Drive(() => _ = device.Counters, nameof(device.Counters));
            Drive(() => _ = device.Diagnostics, nameof(device.Diagnostics));
            Drive(() => _ = device.Factory, nameof(device.Factory));
            Drive(() => _ = device.PointSampler, nameof(device.PointSampler));
            Drive(() => _ = device.LinearSampler, nameof(device.LinearSampler));
            Drive(() => _ = device.SwapchainFramebuffer, nameof(device.SwapchainFramebuffer));
            Drive(() => device.SyncToVerticalBlank = !device.SyncToVerticalBlank,
                nameof(device.SyncToVerticalBlank));
            Drive(() => device.ResizeSwapchain(640u, 480u), nameof(device.ResizeSwapchain));
            Drive(() => device.Present(), nameof(device.Present));
            Drive(device.WaitForIdle, nameof(device.WaitForIdle));

            // THE THREE UpdateBuffer OVERLOADS, spelled so each one really is a different overload: an array, an
            // explicit span, and a single value by readonly reference.
            Drive(() => device.UpdateBuffer(buffer, 0u, new byte[16]), nameof(device.UpdateBuffer));
            Drive(() => device.UpdateBuffer(buffer, 16u, (ReadOnlySpan<byte>)new byte[16]),
                nameof(device.UpdateBuffer));
            Drive(() => device.UpdateBuffer(buffer, 32u, in one), nameof(device.UpdateBuffer));

            // BOTH UpdateTexture OVERLOADS. The six-argument one forwards to the eight-argument one, and both are
            // driven anyway: which of them a caller reaches is the seam's business and a stub could be put on
            // either.
            Drive(() => device.UpdateTexture(staging, texels, 0u, 0u, 2u, 2u), nameof(device.UpdateTexture));
            Drive(() => device.UpdateTexture(staging, texels, 0u, 0u, 2u, 2u, 0u, 0u),
                nameof(device.UpdateTexture));

            // BOTH Map AND BOTH Unmap, on staging resources, which is the row 6 API. Map(Read) additionally
            // flushes the setup batch and drains (M-C6), so this is the one pair here that does real work.
            Drive(() => _ = device.Map(staging, GpuMapMode.Read), nameof(device.Map));
            Drive(() => device.Unmap(staging), nameof(device.Unmap));
            Drive(() => _ = device.Map(buffer, GpuMapMode.Read), nameof(device.Map));
            Drive(() => device.Unmap(buffer), nameof(device.Unmap));

            // AND THE SECOND Submit OVERLOAD, on a real recording, because the fence arm is where the timeline
            // signal is encoded and a device that could submit but not arm a fence would pass a list-only call.
            list.Begin();
            list.End();
            Drive(() => device.Submit(list, fence), nameof(device.Submit));

            // AND THE REFUSAL THAT IS LEFT IS ABOUT THE CALLER, not about the package. Asserted by its message
            // rather than by its type, because both shapes are exceptions and only the message tells a reader
            // whether to fix their code or wait for a row. It drives the one-argument Submit at the same time.
            using var foreign = new NullGpuCommandList();
            driven.Add(nameof(device.Submit));
            string refusal = Refusal(() => device.Submit(foreign));
            _output.WriteLine(refusal);
            Assert.Contains("not created by this native Metal device", refusal, StringComparison.Ordinal);

            RequireEveryMemberDriven(driven);
        }

        /// <summary>
        /// THE COMPLETENESS BACKSTOP: the set of member names the row above drives, against the seam's own
        /// public members and their overload counts. Without it the claim in that row's title is a comment.
        /// <para>
        /// COUNTS AND NOT JUST NAMES, because five of the fourteen methods on this interface are overload pairs
        /// or triples and a name-only check is satisfied by driving one of each. Declared members are filtered
        /// to those this interface itself declares, so <c>IDisposable.Dispose</c> is not in the ledger (teardown
        /// has its own rows in this file), and property accessors are dropped so a property counts once.
        /// </para>
        /// </summary>
        void RequireEveryMemberDriven(IEnumerable<string> driven)
        {
            Dictionary<string, int> declared = typeof(IGpuDevice)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType == typeof(IGpuDevice))
                .Where(m => m is not MethodInfo { IsSpecialName: true })
                .GroupBy(m => m.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            Dictionary<string, int> called = driven
                .GroupBy(name => name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            _output.WriteLine($"IGpuDevice declares {declared.Values.Sum()} members over {declared.Count} names, "
                + $"and this row drove {called.Values.Sum()}.");

            Assert.Equal(declared.Keys.OrderBy(name => name, StringComparer.Ordinal),
                called.Keys.OrderBy(name => name, StringComparer.Ordinal));

            foreach ((string member, int overloads) in declared)
            {
                Assert.True(called[member] >= overloads,
                    "IGpuDevice." + member + " declares " + overloads
                    + " overloads and this row drives only " + called[member]
                    + ". Drive the missing one, or a stub put back on it passes this row forever.");
            }
        }

        /// <summary>A headless device has no swapchain BY DEFINITION, so null is the correct answer rather than
        /// an unbuilt one. The windowed path is live as of row 15 and builds a real one over the request's Cocoa
        /// window.</summary>
        [GpuFact]
        public void AHeadlessDevice_HasNoSwapchainFramebuffer()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            Assert.Null(device.SwapchainFramebuffer);

            // AND ITS PRESENT-BOUNDARY COUNTERS ARE ZERO, which is literally true rather than a placeholder: a
            // device with no swapchain opens no frame at this seam and never waits on a drawable.
            Assert.True(device.Counters.HasValue);
            Assert.Equal(0L, device.Counters.FramesBegun);
            Assert.Equal(0L, device.Counters.AcquireWaitCount);
        }

        static IGpuDevice CreateHeadless() => new MetalBackendProvider().CreateHeadless().Device;

        static string Refusal(Action call) => Assert.ThrowsAny<Exception>(call).Message;

        // A member that is UNBUILT raises NotSupportedException, which is the shape MetalGpuDevice.NotBuiltYet
        // produced until row 15 deleted it with its last caller. Anything else is left to propagate, because a
        // member that fails for a real reason is a different fact and should fail this row loudly rather than be
        // swallowed by a catch that was only looking for one type.
        static void NothingUnbuilt(Action call, string member)
        {
            NotSupportedException? unbuilt = Record.Exception(call) as NotSupportedException;
            Assert.True(unbuilt is null,
                "IGpuDevice." + member + " on the native Metal backend still refuses as unbuilt: "
                + unbuilt?.Message);
        }

        // [SupportedOSPlatformGuard] rather than an inline check at every call site, which is the same mechanism
        // KhaozEngineMetal.IsPlatformSupported uses one level down. It is honest: the first thing this asks is
        // that guard, so a true answer really does imply macOS, and CA1416 then lets a row read the probe.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                // KE_METAL_REQUIRED=1 turns every dormant answer in this assembly into a throw, and the variable
                // has exactly one reader (MetalDormancy) however many places go dormant.
                MetalDormancy.ThrowIfRequired("this is not macOS at all");
                _output.WriteLine("dormant: not macOS, so there is no Metal device to create.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            MetalDormancy.ThrowIfRequired(missing);
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

        // Throws instead where KE_METAL_REQUIRED=1 says this leg has a device, so a dormant row on the one leg
        // built to run these cannot report green having asserted nothing (MetalDormancy).
        void Dormant(string why)
        {
            MetalDormancy.ThrowIfRequired(why);
            _output.WriteLine("dormant: " + why + ".");
        }

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
