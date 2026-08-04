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
    /// </summary>
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
}
