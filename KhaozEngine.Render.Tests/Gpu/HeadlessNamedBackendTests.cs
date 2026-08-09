using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="GpuDeviceContext.CreateHeadless(GpuBackendKind)"/>, the headless entry that takes the backend as
    /// an argument instead of resolving one. Its whole reason to exist is a caller that wants two backends in one
    /// process (a parity A/B, or phase 3's replacement of one implementation by the other) and must still go
    /// through the process-wide creation gate to get them, so these rows pin the two halves of that: the named
    /// backend beats the environment, and a missing provider throws rather than falling anywhere.
    /// <para>
    /// Device-free, and in the non-parallel collection because both rows mutate process-wide graphics state (the
    /// provider registry under a REAL kind, plus <c>KE_GRAPHICS_BACKEND</c>, which <c>GoldenCompare</c> also reads).
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class HeadlessNamedBackendTests
    {
        /// <summary>
        /// The named backend is taken as given: the environment says <c>metal</c> and the call still creates on
        /// the kind it was handed, through that kind's registered provider, with no support probe and therefore no
        /// opportunity to fall back. That is the same no-probe, no-fallback contract the resolved headless entry
        /// has always had, which is what keeps a headless run from filing its images under a backend that never
        /// rendered them.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnANamedBackend_UsesItRatherThanTheEnvironment()
        {
            var device = new FakeGpuDevice(GpuBackendKind.Direct3D11Native);
            var caps = new GpuThreadingCaps(DriverCommandLists: true, DriverConcurrentCreates: false);
            var provider = new FakeBackendProvider(GpuBackendKind.Direct3D11Native)
            {
                Device = device,
                ThreadingCaps = caps,
            };

            using (new EnvScope(GpuBackendSelector.EnvVarName, "metal"))
            using (new BackendProviderScope(GpuBackendKind.Direct3D11Native, provider))
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);

                Assert.Same(device, ctx.GpuDevice);
                Assert.Equal(GpuBackendKind.Direct3D11Native, ctx.Backend);
                Assert.Equal(caps, ctx.ThreadingCaps);
                // Named from outside the engine, so the same provenance the windowed named-backend overload
                // reports. Neither the environment nor the OS probe chose this one.
                Assert.Equal(GpuBackendSource.UserPreference, ctx.Selection.Source);

                Assert.Equal(1, provider.HeadlessCreations);
                Assert.Equal(0, provider.WindowedCreations);
                Assert.Equal(0, provider.SupportProbes);
            }
        }

        /// <summary>
        /// And with nothing registered for the named kind it throws the wiring-fault exception naming the one line
        /// that fixes it (decision I2), rather than falling back to the backend the environment happens to name.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnANamedBackend_ThrowsWhenNothingIsRegistered()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, "metal"))
            using (new BackendProviderScope(GpuBackendKind.Direct3D11Native, provider: null))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    () => GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native));

                Assert.Equal(GpuBackendKind.Direct3D11Native, ex.Backend);
                Assert.DoesNotContain("Metal", ex.Message);
            }
        }
    }

    /// <summary>
    /// The same overload against a REAL device, on whatever backend this run is on, which is the half the fakes
    /// above cannot answer: that a device created by name comes up, and that disposing it releases the creation
    /// gate rather than holding it. The second create is what pins the release, since a gate left held would hang
    /// here instead of quietly working for the rest of the suite.
    /// <para>
    /// In <c>NativeDeviceLifecycle</c> rather than <c>GraphicsBackendGlobalState</c>: it mutates neither the
    /// registry nor the environment, it just brings a device up and down beside the suite's own.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class HeadlessNamedBackendDeviceGpuTests
    {
        [GpuFact]
        public void CreateHeadless_OnThisRunsBackend_CreatesAndDisposesThroughTheGate()
        {
            GpuBackendKind kind = GpuBackendSelector.Select();

            GpuDeviceContext first = GpuDeviceContext.CreateHeadless(kind);
            Assert.Equal(kind, first.Backend);
            Assert.Equal(kind, first.GpuDevice.Backend);
            Assert.Equal(GpuBackendSource.UserPreference, first.Selection.Source);
            first.Dispose();

            using GpuDeviceContext second = GpuDeviceContext.CreateHeadless(kind);
            Assert.Equal(kind, second.Backend);
        }
    }

    /// <summary>
    /// Groups the tests that BUILD AND TEAR DOWN whole GPU devices beside the suite's own, so no two of them are
    /// ever doing it at once and none of them is doing it while the rest of the pool is mid-frame.
    ///
    /// <para><b>THE COST IS MEASURED, NOT SUSPECTED.</b> The first WARP run of the direct3d11-native leg took 49
    /// minutes where that leg normally takes 17 (run 30955744945), with these tests creating their devices in
    /// parallel with everything else. A software rasterizer pays for a device in real seconds, the suite's primary
    /// device is busy rendering goldens throughout, and the two contend for the same driver.</para>
    ///
    /// <para><b>WHY NOT <c>GraphicsBackendGlobalState</c>,</b> which is the other non-parallel graphics collection
    /// and was the obvious candidate. That one is named for the state it protects and means it: the
    /// <c>KE_GRAPHICS_BACKEND</c> variable and the <c>GpuBackendProviders</c> registry, both of which its members
    /// temporarily set to something the run was not launched with. The members here mutate neither. They contend
    /// for the DEVICE, and for the creation gate underneath it, which is a different resource with a different
    /// reason to be serialized. Folding them in would put slow device work behind fast registry work for no
    /// reason, and would leave that collection's doc describing something it no longer only does. Its own
    /// <c>D3D11BackendRegistrationTests</c> sibling stays there, correctly, because that one really does read the
    /// registry the append-audit rows empty.</para>
    ///
    /// <para>Per-assembly, like every xUnit collection definition, and there are TWO copies now.
    /// <c>KhaozEngine.MapEditor.Tests</c> carries an identical definition of its own, the way
    /// <c>AllocSensitive</c> already did, because four of its rows build a whole device across two classes and
    /// three of those do it invisibly through <c>Render3DSnapshot.Capture</c>. The
    /// <c>vulkan-native</c> leg (https://github.com/APKiwiOrg/KhaozEngine/issues/529) is what made the second
    /// copy due: that contention meets a lavapipe suite already serialised at roughly twenty-odd minutes.</para>
    /// </summary>
    [CollectionDefinition("NativeDeviceLifecycle", DisableParallelization = true)]
    public sealed class NativeDeviceLifecycleCollection { }
}
