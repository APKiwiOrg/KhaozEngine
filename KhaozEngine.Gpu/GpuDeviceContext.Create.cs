using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// HOW A DEVICE IS CREATED: every static entry point, the backend resolution behind them, the two-guard
    /// fallback both paths share, and the adoption step a provider-built device goes through. Split from the
    /// live-device surface in <c>GpuDeviceContext.cs</c>, which owns the instance state, the boot log and
    /// disposal.
    /// <para>
    /// A CONCERN BOUNDARY RATHER THAN A LINE COUNT, and it is the same split the three native backends already
    /// take (<c>VulkanGpuDevice.Create.cs</c>, and <c>D3D11GpuDevice</c> before it). Creation reasons about
    /// what a machine can do, what a caller asked for, and what to do when those disagree. The other half
    /// reasons about a device that already exists. Nothing here runs again after construction, and nothing
    /// there runs before it.
    /// </para>
    /// <para>
    /// EVERY BACKEND IS PROVIDER-BACKED SINCE 18.0.0. This package builds no device of its own any more: it
    /// resolves a <see cref="GpuBackendKind"/>, asks <see cref="GpuBackendProviders"/> for the registered
    /// <see cref="IGpuBackendProvider"/>, and adopts what comes back. There is no second creation path beside
    /// this one, which is what removed the switch whose discard arm used to ask for a Metal device on Windows.
    /// </para>
    /// </summary>
    public sealed partial class GpuDeviceContext
    {
        /// <summary>
        /// Create a windowed graphics device on the selected backend (via <see cref="GpuBackendSelector"/>) from a
        /// platform-native window handle. The window/input platform (KhaozEngine.Windowing, on Silk.NET) creates the
        /// native window and passes its handle here as a <see cref="GpuWindowHandle"/>, so this package needs no
        /// windowing dependency of its own. The registered provider for the resolved backend builds the device with
        /// a main swapchain (so <see cref="IGpuDevice.SwapchainFramebuffer"/> is non-null).
        /// <paramref name="syncToVerticalBlank"/> selects vsync (default true, unchanged) vs immediate
        /// presentation. The returned context owns the device's disposal: dispose this context, not the underlying
        /// device.
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank = true)
            => CreateForWindow(window, width, height, syncToVerticalBlank, preferredBackend: null);

        /// <summary>
        /// The same windowed device creation as <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool)"/>,
        /// with a stored USER PREFERENCE (the consuming game's in-game graphics setting) sitting between the
        /// <c>KE_GRAPHICS_BACKEND</c> override and the OS probe. Null (the default path) resolves exactly as
        /// before. The preference arrives as data: this package does no file IO and gains no settings dependency.
        /// <para>Note the pairing with the <see cref="GpuBackendKind"/> overload below: a NULLABLE argument is a
        /// preference that may be absent and is resolved against the environment, while a NON-NULLABLE argument
        /// names the backend outright and skips resolution entirely.</para>
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendKind? preferredBackend)
            => CreateForWindow(window, width, height, syncToVerticalBlank,
                GpuBackendSelector.Resolve(preferredBackend));

        /// <summary>
        /// Create a windowed device on EXACTLY <paramref name="backend"/>: no environment override, no stored
        /// preference, no OS probe, and no fallback. This is the "retry as X" lever, for a consumer driving its
        /// own recovery (the engine's built-in fallback does not need it). A failure propagates, because a caller
        /// that named one backend outright is not asking to be quietly given a different one.
        /// <para>Contrast the <see cref="GpuBackendKind"/>? overload above: nullable means "a preference, maybe
        /// absent" and is resolved against the environment WITH fallback, non-nullable means "this one" and is
        /// not. The resulting <see cref="Selection"/> reports
        /// <see cref="GpuBackendSource.UserPreference"/>, since naming a backend from outside the engine is the
        /// same provenance class as a stored preference: neither the environment nor the probe chose it.</para>
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendKind backend)
            => CreateForWindow(window, width, height, syncToVerticalBlank,
                new GpuBackendSelection(backend, GpuBackendSource.UserPreference, null), allowFallback: false);

        static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendSelection selection, bool allowFallback = true)
        {
            GpuBackendKind fallback = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            // Nothing to fall back TO when the request already IS this platform's default. Falling back onto the
            // backend that just refused would warn about a change that is not one, then fail again for the same
            // reason.
            bool canFallBack = allowFallback && selection.Backend != fallback;

            string? failure = PreflightProvider(selection, canFallBack,
                out IGpuBackendProvider? provider);
            var request = new GpuWindowedDeviceRequest(window, width, height, syncToVerticalBlank);
            Exception? cause = null;

            if (failure is null)
            {
                // Seeded so the catch below needs no assignment of its own. Nothing ever adopts this value: the
                // only path past the guard below is the one where creation returned, and creation either assigns
                // or throws.
                GpuProviderDevice created = default;
                try
                {
                    // Inside the process-wide gate. Device creation is serialized on every backend, so a provider
                    // needs no lifecycle lock of its own and cannot race one.
                    lock (_lifecycleGate)
                    {
                        created = provider!.CreateForWindow(request);
                    }
                }
                catch (Exception ex) when (canFallBack)
                {
                    // Deliberately broad. The failure can be anything from a driver HRESULT wrapper to a
                    // DllNotFoundException out of the P/Invoke layer, and the no-driver case is exactly the one
                    // this fallback exists for.
                    cause = ex;
                    failure = $"{ex.GetType().Name}: {ex.Message}";
                }

                // Adoption sits OUTSIDE that try, and the CREATION call is the only thing inside it. The catch
                // answers one question, "can this machine run the backend", and the fallback shape it produces (a
                // WARN telling a player their stored graphics choice does not work here, then a boot on another
                // backend) is the answer to that question and to nothing else. Adopt validates what the provider
                // HANDED BACK, so both of its throws report a bug in the provider instead. Inside the try they
                // would come out as the machine-incapability answer, which is the exact misattribution both of
                // those guards exist to prevent, and it would ship as a green run on a different backend.
                if (failure is null) return Adopt(created, selection);
            }

            GpuBackendSelection fell = GpuBackendSelector.AfterFallback(selection, fallback);
            WarnFallback(selection.Backend, failure, fallback);

            // Back through the ordinary entry with the fallback's own selection and no further fallback.
            try
            {
                return CreateForWindow(window, width, height, syncToVerticalBlank, fell, allowFallback: false);
            }
            catch (Exception ex)
            {
                throw GpuNoUsableBackendException.Build(selection.Backend, failure, fallback, ex, cause);
            }
        }

        // The reason a requested backend could not be had, when the machine itself says so: the registered
        // provider's own functional probe answered no.
        const string NoMachineSupport = "this machine reports no support for it";

        // The reason a STORED preference could not be had because this build has no provider for it at all. Worded
        // as a fact about the build rather than the machine, because that is what it is: the same settings file on
        // the same hardware boots fine against a build that registered the backend.
        const string NoRegisteredProvider = "no graphics backend provider for it is registered in this build";

        // The one fallback warning, in one place. It is the line that tells a player their stored graphics choice
        // does not work here, and every backend that fell back has to say it identically or a support reply is
        // written against wording that depends on which backend was asked for.
        //
        // Built as a string first, the way SelectionLine and UnrecognizedOverrideWarning are, so a test reads
        // exactly what a session log gets rather than a reconstruction of it.
        internal static string FallbackWarning(GpuBackendKind requested, string failure, GpuBackendKind fallback)
            => $"Could not create a {requested} graphics device ({failure}). Falling back to {fallback}. "
                + "If this backend was chosen in the game's graphics settings, that stored choice does not work "
                + "on this machine and should be cleared.";

        static void WarnFallback(GpuBackendKind requested, string failure, GpuBackendKind fallback)
            => log.Warn(FallbackWarning(requested, failure, fallback));

        /// <summary>
        /// The decision a request gets BEFORE anything is created, and the single place decision I2's two failure
        /// modes are told apart. Pure enough to pin headlessly, which matters because the alternative is only
        /// reachable on a machine that has the backend.
        /// <para>
        /// A backend with NO registered provider throws <see cref="GpuBackendProviderMissingException"/> here, and
        /// it throws FIRST, before the support probe below can answer false for the same request and turn a
        /// forgotten one-line registration into a run on a quietly different backend. That ordering is the whole
        /// invariant: a missing registration is a wiring fault in the app, an unsupported machine is a fact about
        /// the hardware, and only the second one is allowed to fall back.
        /// </para>
        /// <para>
        /// ONE PROVENANCE IS EXEMPT FROM THAT THROW, and it is the one a wiring fault is not: a STORED
        /// <see cref="GpuBackendSource.UserPreference"/> with a fallback available. A settings file written on
        /// another machine, or by a build that registered all three natives before the game dropped its explicit
        /// registrations, names a kind this process has no provider for, and refusing to boot leaves the player
        /// with the setting that caused it unreachable from inside the game. It reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/> instead, which is exactly the signal a consuming
        /// game clears a stored preference on. An <see cref="GpuBackendSource.EnvironmentOverride"/> still throws,
        /// because a soak session that pinned a variable must never quietly measure something else, and so does an
        /// explicitly named <c>Create*(kind)</c>, which arrives here with no fallback allowance at all.
        /// </para>
        /// <para>
        /// A RETIRED backend throws <see cref="GpuBackendRetiredException"/> here, and it throws AHEAD of the
        /// registry lookup. That ordering is decision 5.2 of the removal design rather than tidiness: the four
        /// members retired in 18.0.0 have no provider, so leaving them to
        /// <see cref="GpuBackendProviders.Require"/> would report a retirement as a forgotten registration and
        /// send a reader off to add a package reference that would not help. Nothing a PLAYER can store reaches
        /// this, because <see cref="GpuBackendSelector.Resolve(string?, OSPlatformKind, GpuBackendKind?)"/>
        /// redirects a stored preference and an environment token onto the API's native backend first.
        /// </para>
        /// <para>
        /// Returns null when creation should be attempted, or the reason to warn with and fall back on. With
        /// <paramref name="allowFallback"/> false there is nothing to fall back to, so the probe is skipped
        /// entirely and a real failure throws.
        /// </para>
        /// </summary>
        internal static string? PreflightProvider(GpuBackendSelection selection, bool allowFallback,
            out IGpuBackendProvider? provider)
        {
            GpuBackendKind backend = selection.Backend;
            if (GpuBackendSelector.IsRetired(backend))
            {
                throw new GpuBackendRetiredException(backend,
                    GpuBackendSelector.NativeReplacementFor(backend, GpuBackendSelector.DetectOS()));
            }

            if (!GpuBackendProviders.TryGet(backend, out provider) || provider is null)
            {
                if (allowFallback && selection.Source is GpuBackendSource.UserPreference) return NoRegisteredProvider;
                throw new GpuBackendProviderMissingException(backend);
            }

            if (!allowFallback) return null;
            return GpuBackendSelector.IsBackendSupported(backend) ? null : NoMachineSupport;
        }

        // The construction step, shared by the windowed and headless entries so the guard and the ownership
        // decision cannot drift apart between them.
        //
        // Every throw out of here is a BUG IN THE PROVIDER, never a machine that cannot run the backend, which is
        // why the windowed entry calls this outside its fallback catch. See the comment at that call site.
        static GpuDeviceContext Adopt(in GpuProviderDevice created, GpuBackendSelection selection)
        {
            if (created.Device is null)
            {
                throw new InvalidOperationException(
                    $"The {selection.Backend} backend provider returned no device. A provider that cannot create "
                    + "one must throw, so the failure carries a reason the fallback can log, instead of handing "
                    + "back an empty result the caller has to guess at.");
            }

            try
            {
                // The provider built it, so this context owns its disposal.
                return new GpuDeviceContext(created.Device, created.ThreadingCaps, created.ThreadingProbeFailure,
                    selection, ownsDevice: true);
            }
            catch
            {
                // Ownership transfers on a SUCCESSFUL construction only. A rejected device has no context to
                // dispose it and no other reference anywhere, so without this its adapter, swapchain and driver
                // allocations live until the process exits. Rejecting the device is exactly the case where the
                // provider is already misbehaving, so it is also the case least likely to have cleaned up after
                // itself.
                DisposeRejected(created.Device);
                throw;
            }
        }

        // Releases a device that adoption refused, without letting the release replace the reason for the refusal.
        // A provider handing back a device the engine will not adopt is misbehaving by definition, so its Dispose
        // may be equally broken, and an exception thrown here would unwind in place of the provider-bug exception
        // the caller has to see. Under the same gate the ordinary teardown uses, because it is the same
        // destruction.
        static void DisposeRejected(IGpuDevice device)
        {
            try
            {
                lock (_lifecycleGate)
                {
                    device.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Disposing the device adoption refused threw {ex.GetType().Name}: {ex.Message}. This is "
                    + "the cleanup, not the fault: the refusal it was disposed for is the exception coming out of "
                    + "device creation, and that is the one to act on.");
            }
        }

        /// <summary>
        /// Headless device on the backend <see cref="GpuBackendSelector"/> resolves from the environment, built by
        /// that backend's registered provider.
        /// </summary>
        public static GpuDeviceContext CreateHeadless() => CreateHeadless(GpuBackendSelector.Resolve(),
            allowFallback: true);

        /// <summary>
        /// Create a headless device on EXACTLY <paramref name="backend"/>: no environment override, no stored
        /// preference, no OS probe, and no fallback, the headless twin of
        /// <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool, GpuBackendKind)"/>. A backend with no
        /// registered provider throws <see cref="GpuBackendProviderMissingException"/> (decision I2), and every
        /// other failure propagates, because a caller that named one backend outright is not asking to be quietly
        /// given a different one.
        /// <para>
        /// PUBLIC because comparing two backends in ONE process is a first-class need rather than a test trick.
        /// The alternative is what those callers reach for when this does not exist: pulling the provider out of
        /// <see cref="GpuBackendProviders"/> and calling <see cref="IGpuBackendProvider.CreateHeadless"/>
        /// directly. That skips the process-wide creation gate this class owns, and the gate is not optional
        /// bookkeeping. Concurrent device creation races the Vulkan loader's dispatch setup, and every provider is
        /// written on the promise that the engine serializes creation for it, so a device made around the outside
        /// of it races every device made through it.
        /// </para>
        /// <para>
        /// The resulting <see cref="Selection"/> reports <see cref="GpuBackendSource.UserPreference"/>, the same
        /// provenance the windowed named-backend overload reports and for the same reason: naming a backend from
        /// outside the engine is one provenance class, and neither the environment nor the probe chose it.
        /// </para>
        /// </summary>
        public static GpuDeviceContext CreateHeadless(GpuBackendKind backend)
            => CreateHeadless(new GpuBackendSelection(backend, GpuBackendSource.UserPreference, null),
                allowFallback: false);

        // The RESOLVED headless entry with the selection supplied, which is how a test drives a provenance the
        // live environment cannot produce on the machine it runs on. Identical to CreateHeadless() in every other
        // respect, so nothing here is a path only a test takes.
        internal static GpuDeviceContext CreateHeadless(GpuBackendSelection selection)
            => CreateHeadless(selection, allowFallback: true);

        /// <summary>
        /// The one headless creation path, and the way it is allowed to end somewhere else.
        /// <para>
        /// THE HEADLESS PATH IS STRICTER THAN THE WINDOWED ONE, deliberately. A headless run that quietly
        /// changed backend would file its golden images under a backend that never rendered them, and each of
        /// the cross-platform GPU legs pins its backend in <c>KE_GRAPHICS_BACKEND</c> and then captures goldens
        /// through here. So a PINNED backend still propagates everything, exactly as it always did, and so does
        /// the explicitly-named <see cref="CreateHeadless(GpuBackendKind)"/> overload, which turns fallback off
        /// outright.
        /// </para>
        /// </summary>
        static GpuDeviceContext CreateHeadless(GpuBackendSelection selection, bool allowFallback)
        {
            GpuBackendKind fallback = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            bool canFallBack = allowFallback && !selection.WasPinnedByEnvironment
                && selection.Backend != fallback;

            string? failure = PreflightProvider(selection, canFallBack,
                out IGpuBackendProvider? provider);
            if (failure is null)
            {
                GpuProviderDevice created;
                try
                {
                    lock (_lifecycleGate)
                    {
                        created = provider!.CreateHeadless();
                    }
                }
                catch (Exception ex) when (canFallBack)
                {
                    // Broad for the reason the windowed path gives, and the machine-refusal case is the one this
                    // arm was added for: a provider that answers "this machine cannot" raises
                    // NotSupportedException from creation, and a machine with no loader raises out of the
                    // P/Invoke layer before that.
                    return HeadlessFallback(selection, fallback, $"{ex.GetType().Name}: {ex.Message}", ex);
                }

                // Adoption sits OUTSIDE the catch, exactly as it does on the windowed path: every throw out of it
                // is a bug in the PROVIDER, and turning one into "this machine cannot run the backend" is the
                // misattribution both guards exist to prevent.
                return Adopt(created, selection);
            }

            return HeadlessFallback(selection, fallback, failure, cause: null);
        }

        // The headless fallback itself, in one place so the probe-refusal arm and the creation-failure arm
        // report, warn and re-enter identically. allowFallback goes false on the way back in: the platform
        // default is where a fallback lands, and there is nothing further to fall back to.
        static GpuDeviceContext HeadlessFallback(GpuBackendSelection selection, GpuBackendKind fallback,
            string failure, Exception? cause)
        {
            GpuBackendSelection fell = GpuBackendSelector.AfterFallback(selection, fallback);
            WarnFallback(selection.Backend, failure, fallback);

            try
            {
                return CreateHeadless(fell, allowFallback: false);
            }
            catch (Exception ex)
            {
                throw GpuNoUsableBackendException.Build(selection.Backend, failure, fallback, ex, cause);
            }
        }
    }
}
