using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decision V-N1: ONE <c>VkInstance</c> for the process, refcounted, created on the first device and gone
    /// with the last. The golden assertion of the row ("create and destroy many devices and the instance is
    /// gone") plus the two edges around it.
    /// <para>
    /// DEVICE-FREE, over the refcount's injected factory, which is what makes the assertion possible at all.
    /// Creating a real <c>VkInstance</c> needs a loader, an ICD and a driver, and the machines this suite runs on
    /// outside the Linux leg have none. The policy is a separate type from the native calls precisely so the
    /// lifecycle can be driven from nothing, and the real creation path is exercised on the <c>vulkan-native</c>
    /// CI leg (https://github.com/APKiwiOrg/KhaozEngine/issues/529).
    /// </para>
    /// </summary>
    public sealed class VulkanInstanceLifecycleTests
    {
        static readonly VulkanInstanceKey Headless =
            new(Windowed: false, Window: default, Validation: VulkanValidationMode.Off);

        /// <summary>
        /// THE ROW'S GOLDEN ASSERTION. Many devices, created and destroyed the way the golden suite creates and
        /// destroys them, produce exactly ONE instance, and it is gone when the last one goes.
        /// <para>
        /// Both halves matter and they are asserted separately. A refcount that reached zero without destroying
        /// would leak the instance for the process lifetime, which on a soak run is the failure that looks like a
        /// slow driver rather than like a leak, and a count of zero with a live instance is exactly what
        /// <see cref="VulkanInstanceRefCount{T}.IsLive"/> exists to make visible.
        /// </para>
        /// </summary>
        [Fact]
        public void ManyDevices_ShareOneInstance_AndItIsGoneAtZero()
        {
            var created = new List<FakeInstance>();
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key => { var made = new FakeInstance(key); created.Add(made); return made; },
                instance => instance.Destroyed = true);

            var leases = new List<VulkanInstanceLease<FakeInstance>>();
            for (int i = 0; i < 32; i++)
            {
                leases.Add(refCount.Acquire(Headless));
                Assert.Equal(i + 1, refCount.Count);
            }

            // ONE instance for thirty-two devices, which is the claim V-N1 makes, and every device holding the
            // same object rather than an equal one.
            Assert.Single(created);
            foreach (VulkanInstanceLease<FakeInstance> lease in leases) Assert.Same(created[0], lease.Value);

            for (int i = 0; i < leases.Count; i++)
            {
                // Still alive on every release but the last, because another device still holds it. A destroy
                // here would be the crash the whole refcount exists to prevent.
                Assert.True(refCount.IsLive);
                Assert.False(created[0].Destroyed);
                leases[i].Dispose();
            }

            Assert.Equal(0, refCount.Count);
            Assert.False(refCount.IsLive);
            Assert.True(created[0].Destroyed);
        }

        /// <summary>
        /// A second device AFTER the last one went creates a second instance, rather than handing back the
        /// destroyed one. Worth pinning because it is the sequence the golden suite actually runs (one device per
        /// test class, serially), so a refcount that could not recreate would work for exactly one test.
        /// </summary>
        [Fact]
        public void AfterTheLastRelease_TheNextDevice_CreatesAFreshInstance()
        {
            var created = new List<FakeInstance>();
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key => { var made = new FakeInstance(key); created.Add(made); return made; },
                instance => instance.Destroyed = true);

            refCount.Acquire(Headless).Dispose();
            using VulkanInstanceLease<FakeInstance> second = refCount.Acquire(Headless);

            Assert.Equal(2, created.Count);
            Assert.True(created[0].Destroyed);
            Assert.False(created[1].Destroyed);
            Assert.Same(created[1], second.Value);
        }

        /// <summary>
        /// Releasing twice drops the count ONCE. This is the row's most consequential edge: a device disposed
        /// twice (which a consumer is entitled to do, and which <c>GpuDeviceContext</c> itself does on the
        /// rejected-device path) would otherwise destroy an instance another live device is still calling
        /// through, and destroying a live <c>VkInstance</c> aborts the process through the Vulkan loader rather
        /// than failing quietly.
        /// </summary>
        [Fact]
        public void ReleasingALeaseTwice_DropsTheCountOnce()
        {
            var created = new List<FakeInstance>();
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key => { var made = new FakeInstance(key); created.Add(made); return made; },
                instance => instance.Destroyed = true);

            VulkanInstanceLease<FakeInstance> first = refCount.Acquire(Headless);
            using VulkanInstanceLease<FakeInstance> second = refCount.Acquire(Headless);

            first.Dispose();
            first.Dispose();
            first.Dispose();

            Assert.True(first.IsReleased);
            Assert.Equal(1, refCount.Count);
            Assert.True(refCount.IsLive);
            Assert.False(created[0].Destroyed);
        }

        /// <summary>
        /// A failed creation leaves the refcount at zero with no instance, rather than at one with nothing behind
        /// it. Without this the next acquire would hand out a lease on null and the failure would surface as a
        /// null reference several calls later, with the real reason gone.
        /// </summary>
        [Fact]
        public void AFailedCreation_LeavesNothingClaimed()
        {
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                _ => throw new InvalidOperationException("no loader"),
                _ => Assert.Fail("nothing was created, so nothing may be destroyed"));

            Assert.Throws<InvalidOperationException>(() => refCount.Acquire(Headless));

            Assert.Equal(0, refCount.Count);
            Assert.False(refCount.IsLive);
        }

        /// <summary>
        /// A second configuration while the first is live REFUSES, and the message says what is live and what was
        /// asked for. It is the one case the single-instance model cannot serve, because a live instance's
        /// extension and layer lists are fixed at creation, and refusing loudly beats the two silent alternatives:
        /// a second instance abandons V-N1 and reopens the loader race MV7 measures, and creating every instance
        /// with the surface extensions takes down a golden leg that runs with no display server.
        /// </summary>
        [Fact]
        public void ADifferentConfiguration_WhileOneIsLive_RefusesByName()
        {
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key => new FakeInstance(key), _ => { });

            using VulkanInstanceLease<FakeInstance> headless = refCount.Acquire(Headless);

            var windowed = new VulkanInstanceKey(Windowed: true, Window: GpuWindowKind.X11,
                Validation: VulkanValidationMode.Off);
            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => refCount.Acquire(windowed));

            Assert.Contains("headless", ex.Message, StringComparison.Ordinal);
            Assert.Contains("windowed", ex.Message, StringComparison.Ordinal);
            // The refusal must not have disturbed what was already there.
            Assert.Equal(1, refCount.Count);
            Assert.True(refCount.IsLive);
        }

        /// <summary>
        /// THE OTHER ORDER, WHICH IS THE ONE THE REFUSAL MESSAGE PRESCRIBES AND WHICH USED TO BE REFUSED TOO. A
        /// windowed instance's extension list is the headless one plus <c>VK_KHR_surface</c> and one platform
        /// surface extension, so a headless device asked for while a windowed one is live already has everything
        /// it needs. It shares the live instance rather than creating a second one, which is what makes a Linux
        /// client that opens a window and then takes a headless capture work at all.
        /// <para>
        /// The strict key match this replaces refused both directions, so "create the windowed device first"
        /// resolved nothing (https://github.com/APKiwiOrg/KhaozEngine/issues/543). Pure and device-free, over
        /// the key rather than over a driver.
        /// </para>
        /// </summary>
        [Fact]
        public void AHeadlessRequest_WhileAWindowedInstanceIsLive_SharesIt()
        {
            var created = new List<FakeInstance>();
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key => { var made = new FakeInstance(key); created.Add(made); return made; }, _ => { });

            var windowed = new VulkanInstanceKey(Windowed: true, Window: GpuWindowKind.X11,
                Validation: VulkanValidationMode.Off);

            using VulkanInstanceLease<FakeInstance> window = refCount.Acquire(windowed);
            using VulkanInstanceLease<FakeInstance> headless = refCount.Acquire(Headless);

            Assert.Same(window.Value, headless.Value);
            Assert.Single(created);
            Assert.Equal(2, refCount.Count);
            // The instance stays what it was CREATED as, so a later windowed device on the same surface still
            // matches and a Wayland one still does not.
            Assert.Equal(windowed, window.Value.Key);
        }

        /// <summary>
        /// The rule itself, stated once over the key, because it is the input to every acquire above and it is
        /// asymmetric in a way equality is not. A headless request is served by ANY live instance on the same
        /// validation rung. A windowed one is served only by a windowed instance on the SAME platform surface,
        /// and the validation rung is a hard match in both directions because the layer is a
        /// <c>vkCreateInstance</c> argument a live instance cannot gain.
        /// </summary>
        [Theory]
        // live windowed X11: serves headless, serves X11, not Wayland.
        [InlineData(true, GpuWindowKind.X11, false, default(GpuWindowKind), true)]
        [InlineData(true, GpuWindowKind.X11, true, GpuWindowKind.X11, true)]
        [InlineData(true, GpuWindowKind.X11, true, GpuWindowKind.Wayland, false)]
        // live headless: serves headless only.
        [InlineData(false, default(GpuWindowKind), false, default(GpuWindowKind), true)]
        [InlineData(false, default(GpuWindowKind), true, GpuWindowKind.X11, false)]
        public void Satisfies_IsWiderThanEquality_InExactlyOneDirection(
            bool liveWindowed, GpuWindowKind liveWindow,
            bool askedWindowed, GpuWindowKind askedWindow, bool expected)
        {
            var live = new VulkanInstanceKey(liveWindowed, liveWindow, VulkanValidationMode.Off);
            var asked = new VulkanInstanceKey(askedWindowed, askedWindow, VulkanValidationMode.Off);

            Assert.Equal(expected, VulkanInstanceRefCount<FakeInstance>.Satisfies(live, asked));
            // And the rung is a hard match whatever the surfaces say.
            Assert.False(VulkanInstanceRefCount<FakeInstance>.Satisfies(
                live, asked with { Validation = VulkanValidationMode.Strict }));
        }

        /// <summary>
        /// The validation rung is part of the key, so a device asking for a different one while an instance is
        /// live is refused too. Not pedantry: the layer is a <c>vkCreateInstance</c> argument, so a session cannot
        /// turn validation on for its second device, and silently handing back the unvalidated instance would
        /// produce a clean run that proved nothing.
        /// </summary>
        [Fact]
        public void TheValidationRung_IsPartOfTheKey()
        {
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key => new FakeInstance(key), _ => { });

            using VulkanInstanceLease<FakeInstance> plain = refCount.Acquire(Headless);

            Assert.Throws<NotSupportedException>(() => refCount.Acquire(
                new VulkanInstanceKey(Windowed: false, Window: default,
                    Validation: VulkanValidationMode.Strict)));
        }

        /// <summary>
        /// Concurrent acquires produce ONE instance, which is the property the whole single-instance hypothesis
        /// rests on: the racing operation MV7 is about is <c>vkCreateInstance</c> and the loader's ICD enumeration
        /// underneath it, so a refcount that could create two under contention would reopen exactly the race the
        /// model is meant to close.
        /// </summary>
        [Fact]
        public void ConcurrentAcquires_CreateExactlyOne()
        {
            var created = new List<FakeInstance>();
            var gate = new object();
            var refCount = new VulkanInstanceRefCount<FakeInstance>(
                key =>
                {
                    var made = new FakeInstance(key);
                    lock (gate) created.Add(made);
                    return made;
                },
                instance => instance.Destroyed = true);

            var leases = new VulkanInstanceLease<FakeInstance>[64];
            Parallel.For(0, leases.Length, i => leases[i] = refCount.Acquire(Headless));

            Assert.Single(created);
            Assert.Equal(leases.Length, refCount.Count);

            Parallel.ForEach(leases, lease => lease.Dispose());

            Assert.Equal(0, refCount.Count);
            Assert.True(created[0].Destroyed);
        }

        /// <summary>
        /// THE PROBE IS NOT A HOLDER. Asking whether this machine supports the backend must leave the shared
        /// refcount exactly where it found it, because the probe creates and destroys its OWN throwaway instance:
        /// it has to answer before any device exists, which is before the shared one is allowed to. A probe that
        /// took a lease would hold the process instance alive from the first settings-screen query until exit, on
        /// a machine that may never create a device at all.
        /// <para>
        /// Runs for real on whatever machine the suite is on, which on a developer Mac means the probe answers
        /// "no loader" before it creates anything. Either answer is a pass: what is asserted is the count, not the
        /// support.
        /// </para>
        /// </summary>
        [Fact]
        public void AskingTheProbe_DoesNotClaimTheSharedInstance()
        {
            int before = VulkanInstance.LeaseCount;

            _ = new VulkanBackendProvider().IsSupported();

            Assert.Equal(before, VulkanInstance.LeaseCount);
        }

        // The refcount's payload, standing in for the real VkInstance. It records what it was created FOR, so a
        // test can assert the key travelled, and whether it was destroyed, which is the half a count cannot say.
        sealed class FakeInstance
        {
            internal FakeInstance(VulkanInstanceKey key) => Key = key;

            internal VulkanInstanceKey Key { get; }

            internal bool Destroyed { get; set; }
        }
    }

    /// <summary>
    /// Decision V-N6: exactly which extensions and layers an instance is created with. THAT IS THE ENTIRE LIST,
    /// and these rows are what stops it growing by analogy with a backend that needed more.
    /// </summary>
    public sealed class VulkanInstanceLayoutTests
    {
        /// <summary>
        /// THE HEADLESS PATH ENABLES NO SURFACE EXTENSION AT ALL, not even <c>VK_KHR_surface</c>. That is what
        /// lets the whole golden suite run on a machine with no display server, and it is not a harmless extra: a
        /// loader without the Xlib libraries fails instance creation outright on a surface extension it cannot
        /// supply, so an extension added here "just in case" takes the golden leg down.
        /// </summary>
        [Fact]
        public void TheHeadlessPath_AsksForNoSurfaceExtension()
        {
            IReadOnlyList<string> extensions =
                VulkanInstanceLayout.HeadlessInstanceExtensions(VulkanValidationMode.Off);

            Assert.Empty(extensions);
        }

        /// <summary>Validation adds the debug-utils extension and nothing else, on the headless path too: the
        /// messenger is not a surface concern.</summary>
        [Theory]
        [InlineData((int)VulkanValidationMode.On)]
        [InlineData((int)VulkanValidationMode.Strict)]
        [InlineData((int)VulkanValidationMode.Sync)]
        public void ValidationAddsDebugUtils_AndNothingElse(int mode)
        {
            IReadOnlyList<string> extensions =
                VulkanInstanceLayout.HeadlessInstanceExtensions((VulkanValidationMode)mode);

            Assert.Equal(new[] { "VK_EXT_debug_utils" }, extensions);
            // NEVER the deprecated one. The incumbent uses VK_EXT_debug_report, which has been deprecated for six
            // years, and this row's whole departure from it is the newer extension.
            Assert.DoesNotContain("VK_EXT_debug_report", extensions);
        }

        /// <summary>
        /// EXACTLY ONE platform surface extension on the windowed path, beside <c>VK_KHR_surface</c>. Requesting
        /// Xlib and Wayland together is the shape that works on a developer machine carrying both and fails on a
        /// container carrying one.
        /// </summary>
        [Theory]
        [InlineData(GpuWindowKind.Win32, "VK_KHR_win32_surface")]
        [InlineData(GpuWindowKind.X11, "VK_KHR_xlib_surface")]
        [InlineData(GpuWindowKind.Wayland, "VK_KHR_wayland_surface")]
        public void TheWindowedPath_AsksForExactlyOneSurfaceExtension(GpuWindowKind window, string expected)
        {
            IReadOnlyList<string> extensions =
                VulkanInstanceLayout.WindowedInstanceExtensions(window, VulkanValidationMode.Off);

            Assert.Equal(new[] { "VK_KHR_surface", expected }, extensions);
            Assert.Equal(expected, VulkanInstanceLayout.SurfaceExtensionFor(window));
        }

        /// <summary>
        /// macOS is refused BY NAME rather than falling through to a wrong extension. Vulkan there is MoltenVK
        /// over Metal, which needs <c>VK_EXT_metal_surface</c> and a translation layer this backend deliberately
        /// does not carry, and phase 4 of the program brings a real Metal backend instead.
        /// </summary>
        [Fact]
        public void ACocoaWindow_IsRefusedByName()
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => VulkanInstanceLayout.SurfaceExtensionFor(GpuWindowKind.Cocoa));

            Assert.Contains("MoltenVK", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// ONE layer, only under the knob. The incumbent additionally requests
        /// <c>VK_LAYER_LUNARG_standard_validation</c>, removed from the SDK in 2020, which a modern loader answers
        /// by failing instance creation outright.
        /// </summary>
        [Fact]
        public void OneValidationLayer_UnderTheKnobOnly()
        {
            Assert.Empty(VulkanInstanceLayout.InstanceLayers(VulkanValidationMode.Off));

            IReadOnlyList<string> layers = VulkanInstanceLayout.InstanceLayers(VulkanValidationMode.On);

            Assert.Equal(new[] { "VK_LAYER_KHRONOS_validation" }, layers);
            Assert.DoesNotContain("VK_LAYER_LUNARG_standard_validation", layers);
        }
    }
}
