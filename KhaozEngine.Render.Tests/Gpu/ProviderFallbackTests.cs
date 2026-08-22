using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// What a provider-backed creation does when the provider itself REFUSES, which 17.40.0 made reachable on
    /// every machine rather than only on one somebody deliberately mis-set. The OS probe answers a
    /// provider-backed kind now, so a registered provider that throws
    /// (<c>MetalSupportProbe.MissingRequirement</c> on a Mac that cannot meet the feature floor, a loader that
    /// is not installed) is the DEFAULT path failing rather than a request nobody would have made.
    /// <para>
    /// THE HEADLESS PATH IS THE ONE THAT MOVED. Before this release a provider failure propagated out of
    /// <c>CreateHeadless</c>, so a <c>Render2DSnapshot.Capture</c> that worked before a repin threw after it.
    /// It falls back to the platform's Veldrid incumbent now, with the same WARN the windowed path has always
    /// printed. What did NOT move is the environment-pinned request, which still propagates everything: a soak
    /// session and each of the five cross-platform GPU legs pin their backend that way and then capture
    /// goldens, and a quiet change of backend under a golden capture is the one failure this whole seam exists
    /// to prevent.
    /// </para>
    /// <para>
    /// In <c>GraphicsBackendGlobalState</c> because every row mutates the provider registry, the environment, or
    /// both. That collection disables parallelization outright, so the one row here that builds a real device is
    /// serialized against the whole pool exactly as it would be in <c>NativeDeviceLifecycle</c>, and it goes
    /// here rather than there because the registry is the state it has to hold alone.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class ProviderFallbackTests
    {
        // A value no GpuBackendKind member will plausibly take, distinct from the 9001 the registry tests and the
        // 9002 the default-flip tests use, so no two files can collide over one registry entry.
        const GpuBackendKind SentinelKind = (GpuBackendKind)9003;

        /// <summary>
        /// The half that did not move, through the provenance that decides it: <c>KE_GRAPHICS_BACKEND</c> pinned
        /// the backend, so the provider's own exception comes out whole. Anything else here would let a leg that
        /// pinned <c>metal-native</c> capture its goldens on Veldrid Metal and file them under the native name.
        /// </summary>
        [Fact]
        public void CreateHeadless_ForAnEnvironmentPinnedBackend_PropagatesTheProvidersFailure()
        {
            var boom = new NotSupportedException("MTLDevice does not meet the feature floor");
            var provider = new FakeBackendProvider(GpuBackendKind.MetalNative) { CreationThrows = boom };

            using (new EnvScope(GpuBackendSelector.EnvVarName, "metal-native"))
            using (new BackendProviderScope(GpuBackendKind.MetalNative, provider))
            {
                NotSupportedException thrown =
                    Assert.Throws<NotSupportedException>(() => GpuDeviceContext.CreateHeadless());

                Assert.Same(boom, thrown);
                Assert.Equal(1, provider.HeadlessCreations);
            }
        }

        /// <summary>
        /// And the same for a backend named in the call signature, which turns fallback off outright. Two
        /// different levers, one contract: a caller that said which implementation it wanted is never quietly
        /// given the other one.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnANamedBackend_PropagatesTheProvidersFailure()
        {
            var boom = new InvalidOperationException("vkCreateInstance returned VK_ERROR_INITIALIZATION_FAILED");
            var provider = new FakeBackendProvider(SentinelKind) { CreationThrows = boom };

            using (new BackendProviderScope(SentinelKind, provider))
            {
                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => GpuDeviceContext.CreateHeadless(SentinelKind));

                Assert.Same(boom, thrown);
            }
        }

        /// <summary>
        /// The half that DID move, and the only row here that needs a device, because the fallback ends in a real
        /// creation on the platform's Veldrid incumbent. A DEFAULTED provider-backed backend whose registered
        /// provider throws lands on the incumbent and reports it, instead of taking the process down: this is a
        /// machine that cannot run its own default, which is a fact about the hardware and exactly what the
        /// reported fallback was built to carry.
        /// <para>
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/> and NOT the 17.40.0 member beside it: the provider
        /// was registered and a creation really did fail, so this is the ordinary failure the member has always
        /// meant. The missing-registration case is a different member and is pinned device-free in
        /// <c>NativeDefaultTests</c>.
        /// </para>
        /// </summary>
        [GpuFact]
        public void CreateHeadless_ForADefaultedBackend_FallsBackToTheIncumbent_WhenTheProviderThrows()
        {
            GpuBackendKind incumbent = GpuBackendSelector.IncumbentFor(GpuBackendSelector.DetectOS());
            var boom = new NotSupportedException("this machine reports no support for it");
            var provider = new FakeBackendProvider(SentinelKind) { CreationThrows = boom };

            using (new BackendProviderScope(SentinelKind, provider))
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null));

                Assert.Equal(incumbent, ctx.Backend);
                Assert.Equal(GpuBackendSource.FallbackAfterFailure, ctx.Selection.Source);
                Assert.Equal(SentinelKind, ctx.Selection.RequestedBackend);
                Assert.Equal(1, provider.HeadlessCreations);
            }
        }

        /// <summary>
        /// THE DOUBLE FALL, which is the case that used to lose the first failure entirely. The requested backend
        /// fails, the engine falls back, and the fallback fails too, so there is no device and the app cannot
        /// render. What comes out names BOTH attempts, because on a native backend and its Veldrid twin the two
        /// failures usually share one underlying cause and the one worth reading is the first.
        /// <para>
        /// Device-free and deterministic on every OS: the fallback is made to fail by handing in a window whose
        /// <see cref="GpuWindowKind"/> no swapchain source exists for, which is refused before any driver is
        /// touched. That stands in for the real double fall (a machine with no working driver for either
        /// implementation of the API) without needing such a machine.
        /// </para>
        /// </summary>
        [Fact]
        public void CreateForWindow_ReportsBothFailures_WhenTheFallbackFailsToo()
        {
            GpuBackendKind incumbent = GpuBackendSelector.IncumbentFor(GpuBackendSelector.DetectOS());
            var boom = new InvalidOperationException("D3D11CreateDevice returned E_FAIL");
            var provider = new FakeBackendProvider(GpuBackendKind.Direct3D11Native) { CreationThrows = boom };
            var unbuildable = new GpuWindowHandle((GpuWindowKind)99, new IntPtr(0x1234));

            using (new EnvScope(GpuBackendSelector.EnvVarName, "d3d11-native"))
            using (new BackendProviderScope(GpuBackendKind.Direct3D11Native, provider))
            {
                GpuNoUsableBackendException ex = Assert.Throws<GpuNoUsableBackendException>(
                    () => GpuDeviceContext.CreateForWindow(unbuildable, 640, 480, syncToVerticalBlank: true));

                Assert.Equal(GpuBackendKind.Direct3D11Native, ex.RequestedBackend);
                Assert.Equal(incumbent, ex.FallbackBackend);

                // Both exceptions stay reachable as OBJECTS, so a debugger stops on the attempt that started
                // this rather than on the recovery from it, and neither stack has to be recovered from a log.
                Assert.Same(boom, ex.InnerException);
                Assert.IsType<NotSupportedException>(ex.FallbackFailure);

                // And both reasons are in the one line a support paste carries.
                Assert.Contains("Direct3D11Native", ex.Message);
                Assert.Contains(incumbent.ToString(), ex.Message);
                Assert.Contains("D3D11CreateDevice returned E_FAIL", ex.Message);
                Assert.Contains("Unknown GpuWindowKind", ex.Message);
            }
        }

        /// <summary>
        /// The same shape when the requested backend never threw at all: the machine simply reported no support,
        /// so there is a reason and no exception behind it. The inner exception then has to be the fallback's
        /// own, because carrying null would leave a reader with an exception whose InnerException says the first
        /// attempt never happened.
        /// </summary>
        [Fact]
        public void CreateForWindow_UsesTheFallbackFailureAsTheInner_WhenTheRequestNeverThrew()
        {
            var provider = new FakeBackendProvider(GpuBackendKind.Direct3D11Native) { Supported = false };
            var unbuildable = new GpuWindowHandle((GpuWindowKind)99, new IntPtr(0x1234));

            using (new EnvScope(GpuBackendSelector.EnvVarName, "d3d11-native"))
            using (new BackendProviderScope(GpuBackendKind.Direct3D11Native, provider))
            {
                GpuNoUsableBackendException ex = Assert.Throws<GpuNoUsableBackendException>(
                    () => GpuDeviceContext.CreateForWindow(unbuildable, 640, 480, syncToVerticalBlank: true));

                Assert.Same(ex.FallbackFailure, ex.InnerException);
                Assert.Contains("this machine reports no support for it", ex.Message);
                Assert.Equal(0, provider.WindowedCreations);
            }
        }
    }
}
