using System;
using System.Collections.Concurrent;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Thrown when a device is asked for on a backend whose <see cref="IGpuBackendProvider"/> was never registered.
    /// <para>
    /// This is a WIRING fault in the consuming app, not a fact about the machine, and the two are deliberately kept
    /// apart (decision I2 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>). It THROWS and never
    /// falls back to another backend. A run that quietly used a different backend than the one it was asked for
    /// would report its frame times, its telemetry session header and its golden images under the wrong name, and
    /// attributing a measurement to the right backend is the entire reason the requested backend was named.
    /// </para>
    /// </summary>
    public sealed class GpuBackendProviderMissingException : InvalidOperationException
    {
        /// <summary>The backend that was asked for and has no registered provider.</summary>
        public GpuBackendKind Backend { get; }

        /// <summary>The exception as the creation path throws it, with the actionable message built from
        /// <paramref name="backend"/>.</summary>
        public GpuBackendProviderMissingException(GpuBackendKind backend)
            : base(BuildMessage(backend))
            => Backend = backend;

        /// <summary>Standard message constructor. <see cref="Backend"/> is left at its default.</summary>
        public GpuBackendProviderMissingException(string message) : base(message)
        {
        }

        /// <summary>Standard message plus inner-exception constructor. <see cref="Backend"/> is left at its
        /// default.</summary>
        public GpuBackendProviderMissingException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        // The naming CONVENTION rather than one package's name, and rather than a switch. When this message was
        // written there was one provider-backed backend, so naming its entry point read as generic. The second
        // one arrived and the same sentence started telling a Vulkan tester to call KhaozEngineD3D11.Register().
        // A switch on the kind would fix that and would add another append site to the audit, for a diagnostic
        // string. The convention is real and holds for every package (KhaozEngine.Gpu.<Backend> exposes
        // KhaozEngine<Backend>.Register()), so stating it degrades correctly for a backend added later: that
        // reader is told the rule, not somebody else's entry point.
        static string BuildMessage(GpuBackendKind backend)
            => $"No graphics backend provider is registered for {backend}. That backend lives in an opt-in package "
                + "outside KhaozEngine.Gpu, one package per backend, and registering it is one explicit call the "
                + "consuming app makes at startup: KhaozEngine.Gpu.<Backend> exposes a single static "
                + "KhaozEngine<Backend>.Register(), so KhaozEngine.Gpu.D3D11 exposes KhaozEngineD3D11.Register() "
                + "and KhaozEngine.Gpu.Vulkan exposes KhaozEngineVulkan.Register(). Referencing the package is "
                + "not enough on its own: the CLR loads an assembly lazily on first type reference, so a "
                + "self-registering module initializer would run on some machines and not others. A windowed game "
                + "gets this for free, because AppWindow calls GpuBackends.RegisterResolvedIfUnregistered() at "
                + "boot, and a headless host calls that same member once itself. This does not fall back to another "
                + "backend, on purpose. A run that quietly used a backend other than the one asked for would "
                + "report its measurements under the wrong name.";
    }

    /// <summary>
    /// The registry of graphics backends that live outside this package, keyed by <see cref="GpuBackendKind"/>.
    /// <c>KhaozEngine.Gpu</c> cannot reference a backend package without a dependency cycle, so
    /// <see cref="GpuDeviceContext"/> cannot construct one. A consuming app closes that gap with one call at
    /// startup, and the backend then arrives here as data.
    /// <para>
    /// See section 4.1 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>. Referencing the package
    /// plus one line is the whole opt-in, and it is compile-time visible, trim-safe and testable, which is exactly
    /// what a <c>[ModuleInitializer]</c> or a reflection probe would not be.
    /// </para>
    /// <para>
    /// TWO FAILURE MODES, KEPT APART (decision I2). A missing registration is a wiring fault and
    /// <see cref="Require"/> throws for it, always, with no fallback. An incapable MACHINE is the other case
    /// entirely, is answered by the provider's own functional probe through
    /// <see cref="GpuBackendSelector.IsBackendSupported"/>, and is reported through the ordinary
    /// <see cref="GpuBackendSource.FallbackAfterFailure"/> path. Collapsing the two would let a soak session
    /// silently measure the incumbent backend and file the numbers under the new one.
    /// </para>
    /// </summary>
    public static class GpuBackendProviders
    {
        static readonly ConcurrentDictionary<GpuBackendKind, IGpuBackendProvider> _providers = new();

        /// <summary>
        /// Register the provider that creates devices for <paramref name="backend"/>. Called once at consumer
        /// startup, from the backend package's own entry point (<c>KhaozEngineD3D11.Register()</c>).
        /// <para>
        /// The kind is a parameter rather than a property of the provider so there is exactly one statement of
        /// which backend this is. The remaining half of that invariant is enforced where it matters: adopting a
        /// device whose own <see cref="IGpuDevice.Backend"/> disagrees with the selection it is adopted with
        /// throws, because the two halves are read by different consumers downstream and a mismatched pair
        /// misattributes a session instead of failing it.
        /// </para>
        /// <para>
        /// Registering twice for one backend REPLACES the earlier provider, so a repeated startup call is
        /// harmless. Thread-safe.
        /// </para>
        /// </summary>
        public static void Register(GpuBackendKind backend, IGpuBackendProvider provider)
        {
            if (provider is null) throw new ArgumentNullException(nameof(provider));

            _providers[backend] = provider;
            // The support probe caches its answer per backend for the process lifetime, and for a provider-backed
            // kind that answer comes from THIS provider. Registering or replacing one therefore drops the cached
            // value, so a settings screen that asked before registration is not stuck forever with the answer it
            // got when the code that answers was not yet in the process.
            GpuBackendSelector.InvalidateSupportCache(backend);
        }

        /// <summary>The registered provider for <paramref name="backend"/>, or false with a null
        /// <paramref name="provider"/> when there is none.</summary>
        public static bool TryGet(GpuBackendKind backend, out IGpuBackendProvider? provider)
            => _providers.TryGetValue(backend, out provider);

        /// <summary>Whether a provider is registered for <paramref name="backend"/>.</summary>
        public static bool IsRegistered(GpuBackendKind backend) => _providers.ContainsKey(backend);

        /// <summary>
        /// The registered provider for <paramref name="backend"/>, or a
        /// <see cref="GpuBackendProviderMissingException"/> naming what is missing and how to register it. This is
        /// the throw half of decision I2, and the creation path calls it BEFORE the machine-capability probe
        /// precisely so a forgotten registration can never be read as an incapable machine and turned into a quiet
        /// fallback onto a different backend.
        /// <para>
        /// A RETIRED backend throws <see cref="GpuBackendRetiredException"/> instead, and the check sits AHEAD of
        /// the registry lookup for the reason decision 5.2 gives: the four members retired in 18.0.0 have no
        /// provider and never will, so the missing-provider message would send a reader off to add a package
        /// reference that cannot help. <c>GpuDeviceContext.PreflightProvider</c> makes the same check first, and
        /// this one is what covers a consumer calling <see cref="Require"/> directly to drive a backend
        /// comparison in one process.
        /// </para>
        /// </summary>
        public static IGpuBackendProvider Require(GpuBackendKind backend)
        {
            if (GpuBackendSelector.IsRetired(backend))
            {
                throw new GpuBackendRetiredException(backend,
                    GpuBackendSelector.NativeReplacementFor(backend, GpuBackendSelector.DetectOS()));
            }

            return _providers.TryGetValue(backend, out IGpuBackendProvider? provider)
                ? provider
                : throw new GpuBackendProviderMissingException(backend);
        }

        /// <summary>
        /// Whether <paramref name="backend"/> is created by a registered provider. CONSTANT TRUE since 18.0.0:
        /// this package builds no device of its own any more, so every live backend arrives through the registry
        /// and an APPENDED <see cref="GpuBackendKind"/> is provider-backed with nothing to remember.
        /// <para>
        /// Kept as a member rather than deleted because it is the question the creation path and the support
        /// probe ask, and because a build-it-here path could return. What it must NEVER become again is a list
        /// somebody has to add a new kind to: forgetting that is what used to send a new backend into a switch
        /// whose discard arm asked for a Metal device on Windows.
        /// </para>
        /// </summary>
        public static bool RequiresProvider(GpuBackendKind backend) => true;

        // Test seam. The registry is process-wide static state, so a test that registers a fake provider has to be
        // able to put it back. Internal because a consuming app has no reason to unregister a backend mid-run:
        // devices already created from it keep running, and nothing rechecks.
        internal static bool Unregister(GpuBackendKind backend)
        {
            bool removed = _providers.TryRemove(backend, out _);
            GpuBackendSelector.InvalidateSupportCache(backend);
            return removed;
        }
    }
}
