using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The explicit backend-provider registry (decisions P4 and I2, section 4.1 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>): how a backend that lives in an opt-in
    /// package outside <c>KhaozEngine.Gpu</c> gets created, and the split between the two ways that can fail.
    /// Device-free, so these run under a plain <c>dotnet test</c> on any OS.
    /// <para>
    /// The property worth stating plainly, because everything else here serves it: a FORGOTTEN registration and an
    /// INCAPABLE machine must never produce the same outcome. The first throws, the second falls back and reports
    /// itself. Collapsing them is how a soak session measures one backend and files the numbers under another,
    /// which is precisely the attribution the whole rollout depended on.
    /// </para>
    /// </summary>
    [Collection("GpuBackendProvidersSerial")]
    public sealed class GpuBackendProvidersTests
    {
        // The appended member itself, which decision I1 reserved ordinal 4 for and which now exists. Used ONLY in
        // the read-only assertions here: once KhaozEngine.Gpu.D3D11 ships, registering a fake provider under this
        // kind would fight the real one the test assembly registers for it.
        const GpuBackendKind AppendedKind = GpuBackendKind.Direct3D11Native;

        // A value no GpuBackendKind member will plausibly ever take, for every test that REGISTERS something. It
        // behaves identically to an appended kind (the registry is keyed by value and knows nothing else about
        // it) while staying out of the way of the real backends this file outlives.
        const GpuBackendKind SentinelKind = (GpuBackendKind)9001;

        // --- which kinds go through the registry at all ---

        /// <summary>
        /// EVERY kind goes through the registry since 18.0.0. The four members that used to be built in place by
        /// the Veldrid incumbent are retired, and the three native backends have always been provider-backed, so
        /// there is no "built-in" arm left for a kind to fall into. Stated as a walk over the whole enum plus a
        /// value no member will take, because the rule is now membership-free: the answer does not depend on
        /// which kind is asked, which is exactly what makes a forgotten registration impossible to mistake for a
        /// path that already works.
        /// </summary>
        [Fact]
        public void RequiresProvider_IsTrue_ForEveryKind()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                Assert.True(GpuBackendProviders.RequiresProvider(kind));
            }

            Assert.True(GpuBackendProviders.RequiresProvider(AppendedKind));
            Assert.True(GpuBackendProviders.RequiresProvider(SentinelKind));
        }

        // --- registration ---

        [Fact]
        public void Register_ThenRequire_HandsBackTheSameProvider()
        {
            var provider = new FakeBackendProvider(SentinelKind);
            using (Registered(SentinelKind, provider))
            {
                Assert.True(GpuBackendProviders.IsRegistered(SentinelKind));
                Assert.Same(provider, GpuBackendProviders.Require(SentinelKind));
                Assert.True(GpuBackendProviders.TryGet(SentinelKind, out IGpuBackendProvider? found));
                Assert.Same(provider, found);
            }

            Assert.False(GpuBackendProviders.IsRegistered(SentinelKind));
            Assert.False(GpuBackendProviders.TryGet(SentinelKind, out IGpuBackendProvider? gone));
            Assert.Null(gone);
        }

        /// <summary>
        /// A repeated startup call must be harmless. Registering the entry point twice (two composition roots, a
        /// test host that re-runs it) is not an error worth taking an app down for.
        /// </summary>
        [Fact]
        public void Register_Twice_ReplacesTheEarlierProvider()
        {
            var first = new FakeBackendProvider(SentinelKind);
            var second = new FakeBackendProvider(SentinelKind);
            using (Registered(SentinelKind, first))
            {
                GpuBackendProviders.Register(SentinelKind, second);
                Assert.Same(second, GpuBackendProviders.Require(SentinelKind));
            }
        }

        [Fact]
        public void Register_RejectsANullProvider()
            => Assert.Throws<ArgumentNullException>(() => GpuBackendProviders.Register(SentinelKind, null!));

        [Fact]
        public void Require_ThrowsNamingTheBackend_WhenNothingIsRegistered()
        {
            GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuBackendProviders.Require(SentinelKind));

            Assert.Equal(SentinelKind, ex.Backend);
            // The message has to be actionable on its own: the reader is a consumer whose app just refused to
            // start, and the fix is one line they have never written before.
            Assert.Contains("KhaozEngineD3D11.Register()", ex.Message);
        }

        // --- the machine-capability probe, which is the OTHER failure mode ---

        /// <summary>
        /// Veldrid cannot answer for a backend it does not implement, so the provider's own functional probe is
        /// what <see cref="GpuBackendSelector.IsBackendSupported"/> reports. On the native Direct3D11 backend that
        /// probe is the <c>ConstantBufferOffsetting</c> and <c>MapNoOverwriteOnDynamicConstantBuffer</c> check
        /// (R7, U2), and this is the seam it answers through.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void IsBackendSupported_AsksTheRegisteredProvider(bool supported)
        {
            var provider = new FakeBackendProvider(SentinelKind) { Supported = supported };
            using (Registered(SentinelKind, provider))
            {
                Assert.Equal(supported, GpuBackendSelector.IsBackendSupported(SentinelKind));
                Assert.Equal(1, provider.SupportProbes);
            }
        }

        /// <summary>
        /// The probe may create and destroy a real device to find out, so a settings screen asking every frame
        /// must pay for it once.
        /// </summary>
        [Fact]
        public void IsBackendSupported_ProbesOnce_AndCachesTheAnswer()
        {
            var provider = new FakeBackendProvider(SentinelKind) { Supported = true };
            using (Registered(SentinelKind, provider))
            {
                Assert.True(GpuBackendSelector.IsBackendSupported(SentinelKind));
                Assert.True(GpuBackendSelector.IsBackendSupported(SentinelKind));
                Assert.True(GpuBackendSelector.IsBackendSupported(SentinelKind));
                Assert.Equal(1, provider.SupportProbes);
            }
        }

        /// <summary>The interface says the probe must never throw, and this is what happens when one does anyway:
        /// "we could not even ask" and "no" are the same answer to the settings screen that asked.</summary>
        [Fact]
        public void IsBackendSupported_IsFalse_WhenTheProbeThrows()
        {
            var provider = new FakeBackendProvider(SentinelKind)
            {
                SupportProbeThrows = new DllNotFoundException("d3d11.dll"),
            };
            using (Registered(SentinelKind, provider))
            {
                Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));
            }
        }

        /// <summary>
        /// With no provider the honest answer to "may a settings screen offer this" is no, because the code that
        /// would run it is not in the process. It must not be CACHED as no, though: registration happens at
        /// startup and a screen that asked first would otherwise be stuck with that answer for the whole run.
        /// </summary>
        [Fact]
        public void IsBackendSupported_IsFalseWithNoProvider_ButIsNotFrozenByThatAnswer()
        {
            Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));

            using (Registered(SentinelKind, new FakeBackendProvider(SentinelKind) { Supported = true }))
            {
                Assert.True(GpuBackendSelector.IsBackendSupported(SentinelKind));
            }

            // And unregistering puts it back, rather than leaving the cached true behind for a backend that can no
            // longer answer at all.
            Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));
        }

        [Fact]
        public void IsBackendSupported_FollowsAReplacedProvider()
        {
            using (Registered(SentinelKind, new FakeBackendProvider(SentinelKind) { Supported = true }))
            {
                Assert.True(GpuBackendSelector.IsBackendSupported(SentinelKind));

                GpuBackendProviders.Register(SentinelKind, new FakeBackendProvider(SentinelKind) { Supported = false });
                Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));
            }
        }

        /// <summary>
        /// AND IT FOLLOWS ONE REPLACED WHILE ITS PREDECESSOR'S PROBE WAS STILL RUNNING, which the row above
        /// cannot see (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/472">#472</see>).
        ///
        /// <para><b>THE HOLE WAS AN ORDERING ONE AND IT LASTED THE WHOLE PROCESS.</b> The probe used to publish
        /// through <c>ConcurrentDictionary.GetOrAdd</c>, and <c>Register</c> invalidates by REMOVING the entry.
        /// A thread that had already resolved provider P1 and was inside its <c>IsSupported</c> when a concurrent
        /// <c>Register(P2)</c> removed the entry then completed its <c>GetOrAdd</c> and wrote P1's answer back
        /// under P2's registration, where nothing would ever drop it again. A settings screen would offer, or
        /// refuse, a backend on the strength of an answer from code that is no longer the answerer.</para>
        ///
        /// <para><b>THE ROW IS DETERMINISTIC, not a stress loop.</b> The outgoing provider blocks inside its own
        /// probe until this thread says go, so the replacement lands at exactly the moment that used to lose.
        /// Without the gates the window is a few instructions wide and a repeat-until-it-happens test would be
        /// flaky in the useless direction: green on a bad build.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> The cache is trusting an answer without checking who produced it,
        /// so the process is stuck on the outgoing provider's verdict.</para>
        /// </summary>
        [Fact]
        public async Task IsBackendSupported_DoesNotKeepTheAnswerOfAProviderReplacedMidProbe()
        {
            // Not disposed on purpose. The probe thread is still parked on `release` if the wait below ever
            // fails, and disposing an event out from under a waiter turns a clear test failure into an
            // ObjectDisposedException on a pool thread. Two events for one test method is not worth that.
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);

            var outgoing = new FakeBackendProvider(SentinelKind)
            {
                Supported = true,
                ProbeEntered = entered,
                ProbeRelease = release,
            };
            var replacement = new FakeBackendProvider(SentinelKind) { Supported = false };

            GpuBackendProviders.Register(SentinelKind, outgoing);
            try
            {
                Task<bool> probing = Task.Run(() => GpuBackendSelector.IsBackendSupported(SentinelKind));

                // Past the registry lookup and inside the outgoing provider's probe, which is the only moment
                // the race exists at.
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the probe never started");

                GpuBackendProviders.Register(SentinelKind, replacement);
                release.Set();
                await probing.WaitAsync(TimeSpan.FromSeconds(10));

                // The answer a later caller gets must come from whoever is registered NOW.
                Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));
                Assert.Equal(1, replacement.SupportProbes);

                // And it is still cached, so the fix did not turn the probe into a per-call cost.
                Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));
                Assert.Equal(1, replacement.SupportProbes);
            }
            finally
            {
                release.Set();
                GpuBackendProviders.Unregister(SentinelKind);
            }
        }

        // --- decision I2: the two failure modes, told apart at the one point that decides ---

        /// <summary>
        /// The invariant, stated as the two different outcomes the SAME request gets. A registered provider that
        /// says the machine cannot run it returns a reason, which the caller warns with and falls back on. Remove
        /// the registration and the identical request throws instead, even though the support probe would also
        /// have answered false. Ordering is the whole point: the throw comes first, so a forgotten one-line
        /// registration can never be read as an incapable machine and quietly run a different backend.
        /// </summary>
        [Fact]
        public void Preflight_TellsAMissingProviderApartFromAnIncapableMachine()
        {
            var provider = new FakeBackendProvider(SentinelKind) { Supported = false };
            using (Registered(SentinelKind, provider))
            {
                string? reason = GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null), allowFallback: true, out _);
                Assert.NotNull(reason);
                Assert.Contains("no support", reason);
            }

            // Same call, same false answer available from the probe, entirely different outcome.
            Assert.False(GpuBackendSelector.IsBackendSupported(SentinelKind));
            Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null), allowFallback: true, out _));
        }

        /// <summary>
        /// A capable machine with a registered provider is cleared to create, and the provider it hands back is the
        /// registered one rather than a second lookup that could disagree with the one just checked.
        /// </summary>
        [Fact]
        public void Preflight_ClearsCreation_WhenTheProviderReportsSupport()
        {
            var provider = new FakeBackendProvider(SentinelKind) { Supported = true };
            using (Registered(SentinelKind, provider))
            {
                Assert.Null(GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null),
                    allowFallback: true, out IGpuBackendProvider? found));
                Assert.Same(provider, found);
            }
        }

        /// <summary>
        /// A caller that named one backend outright is not asking to be given a different one, so there is nothing
        /// to fall back to and therefore nothing to probe for. That is what keeps the "retry as X" lever honest:
        /// it tries, and a real failure propagates.
        /// </summary>
        [Fact]
        public void Preflight_SkipsTheProbeEntirely_WhenFallbackIsNotAllowed()
        {
            var provider = new FakeBackendProvider(SentinelKind) { Supported = false };
            using (Registered(SentinelKind, provider))
            {
                Assert.Null(GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null), allowFallback: false, out _));
                Assert.Equal(0, provider.SupportProbes);
            }
        }

        /// <summary>A missing provider throws even where a fallback WOULD have been allowed, for every provenance
        /// except the STORED PREFERENCE row below. 17.40.0 let a DEFAULTED one fall back to the platform's Veldrid
        /// incumbent, and there is no incumbent left to fall back to, so a wiring gap in the app's own startup is a
        /// wiring gap. See <c>NativeDefaultTests</c>.</summary>
        [Theory]
        [InlineData(GpuBackendSource.OsProbe)]
        [InlineData(GpuBackendSource.EnvironmentOverride)]
        [InlineData(GpuBackendSource.UnrecognizedOverride)]
        [InlineData(GpuBackendSource.FallbackAfterFailure)]
        public void Preflight_ThrowsForAMissingProvider_EvenWhenFallbackIsAllowed(GpuBackendSource source)
            => Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, source, null), allowFallback: true, out _));

        /// <summary>
        /// THE ONE PROVENANCE THAT ROUTES AROUND A MISSING PROVIDER: a stored
        /// <see cref="GpuBackendSource.UserPreference"/>. A settings file synced from another machine, or written
        /// by a build that registered all three natives before the game dropped its explicit registrations, names
        /// a kind this process has no provider for. Throwing there locks the player out of the very screen that
        /// would clear it, so the preflight reports the reason and the caller falls back and self-heals.
        /// <para>
        /// The reason names the BUILD rather than the machine, because the same file on the same hardware boots
        /// fine against a build that registered the backend, and the fallback warning a player reads is written
        /// from this string.
        /// </para>
        /// </summary>
        [Fact]
        public void Preflight_FallsBackForAMissingProvider_WhenAStoredPreferenceAskedForIt()
        {
            string? reason = GpuDeviceContext.PreflightProvider(
                new GpuBackendSelection(SentinelKind, GpuBackendSource.UserPreference, null),
                allowFallback: true, out IGpuBackendProvider? provider);

            Assert.NotNull(reason);
            Assert.Contains("registered in this build", reason, StringComparison.Ordinal);
            Assert.Null(provider);
        }

        /// <summary>
        /// And the boundary that keeps the exemption from swallowing the "retry as X" lever. The explicitly named
        /// <c>Create*(kind)</c> overloads report <see cref="GpuBackendSource.UserPreference"/> too, since naming a
        /// backend from outside the engine is the same provenance class, and they arrive here with no fallback
        /// allowance. Without the allowance the exemption does not apply and the missing provider throws.
        /// </summary>
        /// <summary>
        /// The retirement half of the same exemption. A stored member retired in 18.0.0 is redirected by the
        /// selector onto its API's native backend and already reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, so by the time the preflight sees it the
        /// UserPreference provenance is gone. A stored <c>Direct3D11</c> on a Mac lands on a kind whose package
        /// cannot register there, and throwing would put the boot failure back exactly where the retirement was
        /// designed to prevent it.
        /// <para>
        /// The row below it is the narrowing that keeps this from swallowing the engine's own last attempt: the
        /// selection a fallback re-enters with reports the same source and carries the LIVE backend that failed
        /// on <c>RequestedBackend</c>, and that one has nothing left to route to.
        /// </para>
        /// </summary>
        [Fact]
        public void Preflight_FallsBackForAMissingProvider_WhenARetiredPreferenceWasRedirectedOntoIt()
        {
            string? reason = GpuDeviceContext.PreflightProvider(
                new GpuBackendSelection(SentinelKind, GpuBackendSource.FallbackAfterFailure, null,
                    GpuBackendKind.Direct3D11), allowFallback: true, out _);

            Assert.NotNull(reason);
            Assert.Contains("registered in this build", reason, StringComparison.Ordinal);
        }

        /// <summary>A fallback selection carrying a LIVE requested backend is the engine's own second attempt,
        /// not a player's stored choice, so a missing provider on it still throws.</summary>
        [Fact]
        public void Preflight_ThrowsForAMissingProvider_OnTheEnginesOwnFallbackSelection()
            => Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.FallbackAfterFailure, null,
                        GpuBackendKind.VulkanNative), allowFallback: true, out _));

        [Fact]
        public void Preflight_StillThrowsForAStoredPreference_WhenNoFallbackIsAllowed()
            => Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.UserPreference, null),
                    allowFallback: false, out _));

        // --- the creation path itself, end to end and still device-free ---

        /// <summary>
        /// The public path a consumer actually calls, on a backend whose provider was never registered. It throws
        /// before it touches the window handle or any device at all, which is why this runs with a default handle
        /// on a machine that has no such backend at all.
        /// </summary>
        [Fact]
        public void CreateForWindow_ThrowsForAMissingProvider_InsteadOfCreatingSomethingElse()
        {
            GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, SentinelKind));

            Assert.Equal(SentinelKind, ex.Backend);
        }

        /// <summary>
        /// The whole seam in one call: the registry supplies the provider, the provider supplies the device and
        /// its threading diagnostics, and the context adopts all three. Nothing here loads a driver, so the wiring
        /// this backend needs is pinned on every machine that runs the suite, not only on a Windows one.
        /// </summary>
        [Fact]
        public void CreateForWindow_AdoptsTheDeviceTheProviderBuilt()
        {
            var device = new FakeGpuDevice(SentinelKind);
            var caps = new GpuThreadingCaps(DriverCommandLists: true, DriverConcurrentCreates: false);
            var provider = new FakeBackendProvider(SentinelKind) { Device = device, ThreadingCaps = caps };

            using (Registered(SentinelKind, provider))
            {
                var window = new GpuWindowHandle(GpuWindowKind.Win32, new IntPtr(0x1234));
                using GpuDeviceContext ctx =
                    GpuDeviceContext.CreateForWindow(window, 1280, 720, false, SentinelKind);

                Assert.Same(device, ctx.GpuDevice);
                Assert.Equal(SentinelKind, ctx.Backend);
                Assert.Equal(caps, ctx.ThreadingCaps);
                // Named outright, so it is not probed first, exactly as on the Veldrid path.
                Assert.Equal(0, provider.SupportProbes);

                Assert.Equal(1, provider.WindowedCreations);
                Assert.Equal(0, provider.HeadlessCreations);
                Assert.Equal(1280u, provider.LastRequest.Width);
                Assert.Equal(720u, provider.LastRequest.Height);
                Assert.False(provider.LastRequest.SyncToVerticalBlank);
                Assert.Equal(GpuWindowKind.Win32, provider.LastRequest.Window.Kind);
                Assert.Equal(new IntPtr(0x1234), provider.LastRequest.Window.Handle);
            }
        }

        /// <summary>
        /// The context owns a provider-built device exactly as it owned a Veldrid one, so disposing the context is
        /// what destroys it. A provider path that left the device alive would leak a real D3D11 device per created
        /// context.
        /// </summary>
        [Fact]
        public void CreateForWindow_LeavesTheContextOwningTheProvidersDevice()
        {
            var device = new RecordingGpuDevice(SentinelKind);
            using (Registered(SentinelKind, new FakeBackendProvider(SentinelKind) { Device = device }))
            {
                GpuDeviceContext ctx = GpuDeviceContext.CreateForWindow(default, 8, 8, true, SentinelKind);
                Assert.Empty(device.Calls);

                ctx.Dispose();

                Assert.Equal(new[] { "MarkDeviceDisposed", "Dispose" }, device.Calls);
            }
        }

        /// <summary>
        /// A provider handing back an empty result is a programming error in the provider, and it is caught here
        /// rather than surfacing later as a null device inside a renderer. A provider that cannot create one is
        /// required to throw, because only an exception carries a reason the fallback can log.
        /// </summary>
        [Fact]
        public void CreateForWindow_RejectsAProviderThatReturnsNoDevice()
        {
            using (Registered(SentinelKind, new FakeBackendProvider(SentinelKind) { ReturnsNothing = true }))
            {
                Assert.Throws<InvalidOperationException>(
                    () => GpuDeviceContext.CreateForWindow(default, 8, 8, true, SentinelKind));
            }
        }

        /// <summary>
        /// A creation failure on an explicitly named backend propagates rather than becoming a different backend,
        /// which is the same contract the explicit-backend Veldrid overload had.
        /// </summary>
        [Fact]
        public void CreateForWindow_PropagatesAProviderFailure_WhenTheBackendWasNamedOutright()
        {
            var boom = new InvalidOperationException("D3D11CreateDevice returned E_FAIL");
            using (Registered(SentinelKind, new FakeBackendProvider(SentinelKind) { CreationThrows = boom }))
            {
                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => GpuDeviceContext.CreateForWindow(default, 8, 8, true, SentinelKind));

                Assert.Same(boom, thrown);
            }
        }

        // Registers a provider for the duration of a test and takes it back out again, because the registry is
        // process-wide static state and a leaked fake would follow every later test in the run.
        static ProviderScope Registered(GpuBackendKind backend, IGpuBackendProvider provider)
            => new(backend, provider);

        sealed class ProviderScope : IDisposable
        {
            readonly GpuBackendKind _backend;

            internal ProviderScope(GpuBackendKind backend, IGpuBackendProvider provider)
            {
                _backend = backend;
                GpuBackendProviders.Register(backend, provider);
            }

            public void Dispose() => GpuBackendProviders.Unregister(_backend);
        }
    }

    /// <summary>
    /// An <see cref="IGpuBackendProvider"/> that touches no driver: it counts what it was asked, remembers the
    /// request it was handed, and hands back an inert <see cref="FakeGpuDevice"/>. Enough to drive the whole
    /// registry and creation seam on a machine with no such backend.
    /// </summary>
    internal sealed class FakeBackendProvider : IGpuBackendProvider
    {
        readonly GpuBackendKind _backend;

        internal FakeBackendProvider(GpuBackendKind backend) => _backend = backend;

        /// <summary>What the functional probe answers.</summary>
        internal bool Supported { get; set; } = true;

        /// <summary>Thrown out of the probe, for the contract that says a probe blowing up reads as "no".</summary>
        internal Exception? SupportProbeThrows { get; set; }

        /// <summary>Signalled once <see cref="IsSupported"/> has been entered, for a test that needs to act at
        /// the moment a probe is in flight. Null on every provider that does not care.</summary>
        internal ManualResetEventSlim? ProbeEntered { get; set; }

        /// <summary>Waited on inside <see cref="IsSupported"/>, so a test can hold a probe open across another
        /// registry call. Null means the probe returns straight away.</summary>
        internal ManualResetEventSlim? ProbeRelease { get; set; }

        /// <summary>Thrown out of creation, standing in for a driver that fails after passing the probe.</summary>
        internal Exception? CreationThrows { get; set; }

        /// <summary>Returns a default (device-less) result, standing in for a misbehaving provider.</summary>
        internal bool ReturnsNothing { get; set; }

        /// <summary>The device to hand back, or null for a fresh <see cref="FakeGpuDevice"/> per call.</summary>
        internal IGpuDevice? Device { get; set; }

        internal GpuThreadingCaps? ThreadingCaps { get; set; }
        internal string? ThreadingProbeFailure { get; set; }

        internal int SupportProbes { get; private set; }
        internal int WindowedCreations { get; private set; }
        internal int HeadlessCreations { get; private set; }
        internal GpuWindowedDeviceRequest LastRequest { get; private set; }

        public bool IsSupported()
        {
            SupportProbes++;
            ProbeEntered?.Set();
            ProbeRelease?.Wait(TimeSpan.FromSeconds(30));
            if (SupportProbeThrows != null) throw SupportProbeThrows;
            return Supported;
        }

        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        {
            WindowedCreations++;
            LastRequest = request;
            return Build();
        }

        public GpuProviderDevice CreateHeadless()
        {
            HeadlessCreations++;
            return Build();
        }

        GpuProviderDevice Build()
        {
            if (CreationThrows != null) throw CreationThrows;
            if (ReturnsNothing) return default;
            return new GpuProviderDevice(Device ?? new FakeGpuDevice(_backend), ThreadingCaps, ThreadingProbeFailure);
        }
    }
}
