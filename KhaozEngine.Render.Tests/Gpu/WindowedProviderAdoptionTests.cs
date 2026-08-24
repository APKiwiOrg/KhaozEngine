using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The line between the TWO ways provider-backed windowed creation can fail, which
    /// <c>GpuDeviceContext.CreateFromProvider</c> draws by what it puts inside its fallback catch. A driver that
    /// cannot create a device is a fact about the MACHINE, and it is answered with the reported fallback: a WARN
    /// telling a player their stored graphics choice does not work here, then a boot on another backend. A provider
    /// that hands back nothing, or hands back a device belonging to a different backend, is a BUG IN THE PROVIDER,
    /// and it has to reach the caller as one.
    /// <para>
    /// Collapsing the second into the first is worse than a crash, because it ships. The run continues on another
    /// backend, the WARN blames the player's settings, and the two guards written specifically to stop a session
    /// being attributed to the wrong backend are what produced the misattribution. These tests are the pin, and
    /// they matter now rather than later: creation on the native provider currently throws before adoption is
    /// reached, so the moment it starts returning a real device the misattribution is live and silent.
    /// </para>
    /// <para>
    /// Device-free, so they run under a plain <c>dotnet test</c> on any OS. The window handle is never read: on
    /// every path here the throw lands before a swapchain source is built. The opposite leg, a genuine provider
    /// creation failure actually FALLING BACK, is deliberately not pinned here, because it ends in a real device
    /// creation on the platform's default backend.
    /// </para>
    /// <para>
    /// Registry and environment are both process-wide, hence the non-parallel collection: the backend is asked
    /// for as a STORED PREFERENCE, which reaches the provider path with the fallback still allowed, and the fake
    /// is registered under the REAL appended kind. The other two levers cannot serve here. Naming a backend in
    /// the call signature turns fallback off, and since #719 so does a <c>KE_GRAPHICS_BACKEND</c> pin, which is
    /// what these rows used to use. <c>KE_GRAPHICS_BACKEND</c> is CLEARED for the duration instead, because each
    /// cross-platform GPU leg sets it and a leg's own pin would outrank the preference and disarm the fallback
    /// on that leg alone.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class WindowedProviderAdoptionTests
    {
        const GpuBackendKind NativeKind = GpuBackendKind.Direct3D11Native;

        /// <summary>
        /// A provider that creates a device successfully and hands back one belonging to a DIFFERENT backend. The
        /// pair chosen is the realistic one: the incumbent <see cref="GpuBackendKind.Direct3D11"/> under a
        /// selection that says <see cref="GpuBackendKind.Direct3D11Native"/>, two implementations of the same API,
        /// which is both the bug a half-wired provider actually produces and the pair a reader is least likely to
        /// catch in a session log.
        /// </summary>
        [Fact]
        public void CreateForWindow_SurfacesAMismatchedAdoptedDevice_RatherThanFallingBack()
        {
            var device = new RecordingGpuDevice(GpuBackendKind.Direct3D11);
            var provider = new FakeBackendProvider(NativeKind) { Supported = true, Device = device };

            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            using (new BackendProviderScope(NativeKind, provider))
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, (GpuBackendKind?)NativeKind));

                // The adoption guard, named by its own parameter, not something a driver threw.
                Assert.Equal("selection", ex.ParamName);
                Assert.Contains("adopted device", ex.Message);

                // Asked once and not retried, which is the other half of "no fallback happened".
                Assert.Equal(1, provider.WindowedCreations);

                // The refused device is released here or it is released never: construction failed, so no context
                // exists to own it and nothing else holds a reference to it.
                Assert.Contains(nameof(IDisposable.Dispose), device.Calls);
            }
        }

        /// <summary>
        /// The other adoption guard, on the same path. A provider that cannot create a device is required to
        /// THROW, because only an exception carries a reason, and handing back an empty result instead is a
        /// programming error in the provider rather than a machine that cannot run the backend.
        /// </summary>
        [Fact]
        public void CreateForWindow_SurfacesAProviderThatReturnedNoDevice_RatherThanFallingBack()
        {
            var provider = new FakeBackendProvider(NativeKind) { Supported = true, ReturnsNothing = true };

            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            using (new BackendProviderScope(NativeKind, provider))
            {
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, (GpuBackendKind?)NativeKind));

                Assert.Contains("returned no device", ex.Message);
                Assert.Contains(NativeKind.ToString(), ex.Message);
                Assert.Equal(1, provider.WindowedCreations);
            }
        }

        /// <summary>
        /// The setup both tests above depend on, asserted separately so a red there cannot be read as "the
        /// fallback was never reachable anyway". Fallback IS allowed on this call: the backend arrives as a
        /// stored preference rather than from the call signature or a <c>KE_GRAPHICS_BACKEND</c> pin, the kind
        /// differs from the one a failure falls back TO, and the provider answers the support probe with yes, so
        /// creation is attempted and its failure would have had somewhere to fall back to.
        /// </summary>
        [Fact]
        public void CreateForWindow_ThroughAStoredPreference_LeavesTheFallbackArmed()
        {
            var provider = new FakeBackendProvider(NativeKind) { Supported = true };

            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            using (new BackendProviderScope(NativeKind, provider))
            {
                GpuBackendSelection selection = GpuBackendSelector.Resolve((GpuBackendKind?)NativeKind);
                Assert.Equal(NativeKind, selection.Backend);
                Assert.False(selection.WasPinnedByEnvironment);
                // The fallback is ARMED only when the requested kind differs from the backend a failure falls
                // back TO, which since 18.0.0 is the platform default itself. So this row exercises the probe
                // path on every OS except Windows, where Direct3D11Native IS the default and there is nothing to
                // fall back to. The assertion is written that way round rather than skipped, because the
                // interesting half is that the support probe ran at all.
                if (GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS()) == NativeKind) return;

                using GpuDeviceContext ctx =
                    GpuDeviceContext.CreateForWindow(default, 640, 480, true, (GpuBackendKind?)NativeKind);

                // Probed rather than taken on trust, which is what the fallback-allowed path does and the
                // named-backend path does not.
                Assert.Equal(1, provider.SupportProbes);
                Assert.Equal(NativeKind, ctx.Backend);
            }
        }
    }
}
