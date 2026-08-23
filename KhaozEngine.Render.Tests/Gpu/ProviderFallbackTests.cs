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
    /// It falls back to the platform's own default now, with the same WARN the windowed path has always
    /// printed. What did NOT move is the environment-pinned request, which still propagates everything: a soak
    /// session and each of the three cross-platform GPU legs pin their backend that way and then capture
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
        /// A native kind that is never THIS platform's default, with its <c>KE_GRAPHICS_BACKEND</c> token. Every
        /// row that wants the FALLBACK has to request one, because the windowed path skips the fallback outright
        /// when the request already IS the platform default (there is exactly one default per platform since
        /// 18.0.0, so falling back onto it would warn about a change that is not one). A row that hardcoded
        /// <c>d3d11-native</c> therefore passed on every Mac and went red on the <c>direct3d11-native</c> CI leg,
        /// where that token IS the default and the provider's own exception came out raw.
        /// </summary>
        static (GpuBackendKind Kind, string Token) NonDefaultNative()
            => GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS()) == GpuBackendKind.Direct3D11Native
                ? (GpuBackendKind.MetalNative, "metal-native")
                : (GpuBackendKind.Direct3D11Native, "d3d11-native");

        /// <summary>
        /// The half that did not move, through the provenance that decides it: <c>KE_GRAPHICS_BACKEND</c> pinned
        /// the backend, so the provider's own exception comes out whole. Anything else here would let a leg that
        /// pinned <c>metal-native</c> capture its goldens on another backend and file them under that name.
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
        /// creation on the platform's own default. A DEFAULTED provider-backed backend whose registered
        /// provider throws lands on that default and reports it, instead of taking the process down: this is a
        /// machine that cannot run its own default, which is a fact about the hardware and exactly what the
        /// reported fallback was built to carry.
        /// <para>
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/>: the provider was registered and a creation really
        /// did fail, so this is the ordinary failure the member has always meant. A MISSING registration is a
        /// hard throw since 18.0.0 and is pinned device-free in <c>NativeDefaultTests</c>.
        /// </para>
        /// </summary>
        [GpuFact]
        public void CreateHeadless_ForADefaultedBackend_FallsBackToThePlatformDefault_WhenTheProviderThrows()
        {
            // The platform default IS what a fallback lands on since 18.0.0. It used to be a second map,
            // IncumbentFor, deleted with the Veldrid backend it named.
            GpuBackendKind fallbackTo = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            var boom = new NotSupportedException("this machine reports no support for it");
            var provider = new FakeBackendProvider(SentinelKind) { CreationThrows = boom };

            using (new BackendProviderScope(SentinelKind, provider))
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null));

                Assert.Equal(fallbackTo, ctx.Backend);
                Assert.Equal(GpuBackendSource.FallbackAfterFailure, ctx.Selection.Source);
                Assert.Equal(SentinelKind, ctx.Selection.RequestedBackend);
                Assert.Equal(1, provider.HeadlessCreations);
            }
        }

        /// <summary>
        /// THE DOUBLE FALL, which is the case that used to lose the first failure entirely. The requested backend
        /// fails, the engine falls back, and the fallback fails too, so there is no device and the app cannot
        /// render. What comes out names BOTH attempts, because the two failures usually share one underlying
        /// cause and the one worth reading is the first.
        /// <para>
        /// Device-free and deterministic on every OS: the fallback is made to fail by handing in a window whose
        /// <see cref="GpuWindowKind"/> no swapchain source exists for, which every backend refuses before any
        /// driver is touched. That stands in for the real double fall (a machine with no working driver at all)
        /// without needing such a machine. The fallback's exception TYPE is deliberately not pinned: each native
        /// backend refuses a foreign window handle in its own words, and this row is about neither failure being
        /// lost rather than about which type either one is.
        /// </para>
        /// </summary>
        [Fact]
        public void CreateForWindow_ReportsBothFailures_WhenTheFallbackFailsToo()
        {
            GpuBackendKind fallbackTo = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            (GpuBackendKind requested, string token) = NonDefaultNative();
            var boom = new InvalidOperationException("device creation returned a hard failure");
            var provider = new FakeBackendProvider(requested) { CreationThrows = boom };
            var unbuildable = new GpuWindowHandle((GpuWindowKind)99, new IntPtr(0x1234));

            using (new EnvScope(GpuBackendSelector.EnvVarName, token))
            using (new BackendProviderScope(requested, provider))
            {
                GpuNoUsableBackendException ex = Assert.Throws<GpuNoUsableBackendException>(
                    () => GpuDeviceContext.CreateForWindow(unbuildable, 640, 480, syncToVerticalBlank: true));

                Assert.Equal(requested, ex.RequestedBackend);
                Assert.Equal(fallbackTo, ex.FallbackBackend);

                // Both exceptions stay reachable as OBJECTS, so a debugger stops on the attempt that started
                // this rather than on the recovery from it, and neither stack has to be recovered from a log.
                Assert.Same(boom, ex.InnerException);
                Assert.NotNull(ex.FallbackFailure);

                // And both reasons are in the one line a support paste carries.
                Assert.Contains(requested.ToString(), ex.Message);
                Assert.Contains(fallbackTo.ToString(), ex.Message);
                Assert.Contains("device creation returned a hard failure", ex.Message);
                Assert.Contains(ex.FallbackFailure!.Message, ex.Message);
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
            (GpuBackendKind requested, string token) = NonDefaultNative();
            var provider = new FakeBackendProvider(requested) { Supported = false };
            var unbuildable = new GpuWindowHandle((GpuWindowKind)99, new IntPtr(0x1234));

            using (new EnvScope(GpuBackendSelector.EnvVarName, token))
            using (new BackendProviderScope(requested, provider))
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
