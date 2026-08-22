using System;
using Veldrid;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// HOW A DEVICE IS CREATED: every static entry point, the backend resolution behind them, the two-guard
    /// fallback both paths share, the adoption step a provider-built device goes through, and the Veldrid
    /// factory switches. Split from the live-device surface in <c>GpuDeviceContext.cs</c>, which owns the
    /// instance state, the boot log and disposal.
    /// <para>
    /// A CONCERN BOUNDARY RATHER THAN A LINE COUNT, and it is the same split the two native backends already
    /// take (<c>VulkanGpuDevice.Create.cs</c>, and <c>D3D11GpuDevice</c> before it). Creation reasons about
    /// what a machine can do, what a caller asked for, and what to do when those disagree. The other half
    /// reasons about a device that already exists. Nothing here runs again after construction, and nothing
    /// there runs before it.
    /// </para>
    /// </summary>
    public sealed partial class GpuDeviceContext
    {
        // The Direct3D11 device options for both creation paths, plus the one-time log that proves whether the
        // opt-in diagnostic flag is on. Shared so the windowed and headless sites cannot drift: a lever that works
        // on only one of them is worse than no lever, because a tester setting it sees it do nothing in half their
        // runs and concludes the flag is irrelevant.
        static D3D11DeviceOptions BuildD3D11Options()
        {
            uint flags = GpuD3D11DeviceFlags.FromEnvironment(out string? unrecognized);
            if (unrecognized != null) log.Warn(GpuD3D11DeviceFlags.UnrecognizedWarning(unrecognized));
            else if (flags != 0) log.Info(GpuD3D11DeviceFlags.ActiveDescription);

            return new D3D11DeviceOptions
            {
                UseImmediateContext = true,
                DeviceCreationFlags = flags,
            };
        }

        /// <summary>
        /// Create a windowed graphics device on the selected backend (via <see cref="GpuBackendSelector"/>) from a
        /// platform-native window handle. The window/input platform (KhaozEngine.Windowing, on Silk.NET) creates the
        /// native window and passes its handle here as a <see cref="GpuWindowHandle"/>, so this package needs no
        /// windowing dependency of its own. Builds the Veldrid <c>SwapchainSource</c> for the handle's
        /// <see cref="GpuWindowKind"/>, then creates the backend device with a main swapchain (so
        /// <see cref="IGpuDevice.SwapchainFramebuffer"/> is non-null). <paramref name="syncToVerticalBlank"/> selects
        /// vsync (default true, unchanged) vs immediate presentation; it feeds both the device options and the
        /// swapchain description. The returned context owns the device's disposal: dispose this context, not the
        /// underlying device.
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
            // A backend this package cannot reference is created by its registered provider instead, and the
            // branch is taken up here rather than inside CreateOrFallBack because the two paths share none of
            // their inputs: a native device wants no SwapchainSource and no GraphicsDeviceOptions, and building
            // them anyway would put Veldrid work on the creation path of a backend whose premise is having none.
            if (GpuBackendProviders.RequiresProvider(selection.Backend))
                return CreateFromProvider(window, width, height, syncToVerticalBlank, selection, allowFallback);

            SwapchainSource source = window.Kind switch
            {
                GpuWindowKind.Cocoa => SwapchainSource.CreateNSWindow(window.Handle),
                GpuWindowKind.Win32 => SwapchainSource.CreateWin32(window.Handle, IntPtr.Zero),
                GpuWindowKind.X11 => SwapchainSource.CreateXlib(window.Display, window.Handle),
                GpuWindowKind.Wayland => SwapchainSource.CreateWayland(window.Display, window.Handle),
                _ => throw new NotSupportedException($"Unknown GpuWindowKind '{window.Kind}'."),
            };

            // Engine-owned default windowed device options (no swapchain depth attachment, Improved binding,
            // linear non-sRGB swapchain) - the same options the previous windowed CreateWindow path passed, with
            // the vsync flag now caller-selected. Veldrid's GraphicsDeviceOptions stays internal to this package
            // so consumers never reference a Veldrid type.
            var opts = new GraphicsDeviceOptions(false, null, syncToVerticalBlank, ResourceBindingModel.Improved, true, true);
            var scDesc = new SwapchainDescription(source, width, height, null, syncToVerticalBlank, false);

            GraphicsDevice gd;
            GpuBackendSelection actual;
            lock (_lifecycleGate)
            {
                (gd, actual) = CreateOrFallBack(opts, scDesc, selection, allowFallback);
            }
            // Constructed OUTSIDE the gate, as before: the gate exists to serialize Veldrid device creation and
            // disposal, and capability reads / the D3D11 threading probe were never inside it.
            return new GpuDeviceContext(gd, actual, ownsDevice: true);
        }

        /// <summary>
        /// Creates the requested device, falling back to the platform's Veldrid incumbent rather than propagating when the
        /// requested one cannot be had. This is what stops a player from choosing a backend their machine cannot
        /// run and ending up with a client that will not start and cannot be fixed from inside the game.
        /// </summary>
        /// <remarks>
        /// Two guards, because neither alone is enough. The functional probe rules out the backend up front (no
        /// Vulkan ICD, no required surface extension), and the try/catch covers the case the probe cannot see: a
        /// broken or partial driver that answers "supported" and then fails at device creation anyway.
        /// <para>
        /// Retrying needs NO new window. The native window is already created and initialized by the time
        /// <see cref="GpuWindowHandle"/> is built, and that handle is a plain readonly struct of native pointers
        /// holding no device state, so the second attempt reuses it as-is.
        /// </para>
        /// </remarks>
        static (GraphicsDevice Device, GpuBackendSelection Selection) CreateOrFallBack(
            GraphicsDeviceOptions opts, SwapchainDescription scDesc, GpuBackendSelection selection, bool allowFallback)
        {
            GpuBackendKind requested = selection.Backend;
            GpuBackendKind fallback = GpuBackendSelector.IncumbentFor(GpuBackendSelector.DetectOS());

            // Nothing to fall back TO when the request already IS the fallback. That is the platform's Veldrid
            // INCUMBENT since 17.40.0, where it used to be the OS-probe default and the two were the same
            // backend. Naming the incumbent keeps this arm's behaviour byte-for-byte what it was for every
            // Veldrid request, and it is also what the fallback has to be now: the OS probe answers a
            // provider-backed kind, which this Veldrid-only path cannot create.
            if (!allowFallback || requested == fallback)
                return (CreateWindowed(requested, opts, scDesc), selection);

            // The exception is KEPT, not only its rendering, because it becomes the inner exception if the
            // fallback fails too. Null when the machine simply reports no support, where there is no exception.
            Exception? cause = null;
            string? failure = GpuBackendSelector.IsBackendSupported(requested) ? null : NoMachineSupport;

            if (failure is null)
            {
                try
                {
                    return (CreateWindowed(requested, opts, scDesc), selection);
                }
                catch (Exception ex)
                {
                    // Deliberately broad. The Vulkan leg throws VeldridException, the Direct3D11 leg surfaces
                    // SharpGen.Runtime.SharpGenException out of Vortice's Result.CheckError (whose only common
                    // ancestor with VeldridException is System.Exception), and a machine missing a loader library
                    // outright throws DllNotFoundException or TypeInitializationException from the P/Invoke layer
                    // before either type is reached. Naming the two known types would miss exactly the
                    // no-driver-installed case this fallback exists for.
                    cause = ex;
                    failure = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            WarnFallback(requested, failure, fallback);

            try
            {
                return (CreateWindowed(fallback, opts, scDesc),
                    GpuBackendSelector.AfterFallback(selection, fallback));
            }
            catch (Exception ex)
            {
                throw GpuNoUsableBackendException.Build(requested, failure, fallback, ex, cause);
            }
        }

        // The reason a requested backend could not be had, when the machine itself says so. Shared by the Veldrid
        // and provider paths: the two probe different things (Veldrid's own loader check, and a registered
        // provider's functional probe) but they are the SAME answer to a reader, and two wordings would read as
        // two different problems in a session log.
        const string NoMachineSupport = "this machine reports no support for it";

        // The one fallback warning, in one place, for the same reason. It is the line that tells a player their
        // stored graphics choice does not work here, and a provider-backed backend that fell back has to say it
        // identically or a support reply is written against wording that depends on which backend was asked for.
        //
        // Built as a string first, the way SelectionLine and UnrecognizedOverrideWarning are, so a test reads
        // exactly what a session log gets rather than a reconstruction of it.
        internal static string FallbackWarning(GpuBackendKind requested, string failure, GpuBackendKind fallback)
            => $"Could not create a {requested} graphics device ({failure}). Falling back to {fallback}. "
                + "If this backend was chosen in the game's graphics settings, that stored choice does not work "
                + "on this machine and should be cleared.";

        static void WarnFallback(GpuBackendKind requested, string failure, GpuBackendKind fallback)
            => log.Warn(FallbackWarning(requested, failure, fallback));

        // WHICH of the two warnings a fallback prints, decided from the selection it produced rather than from
        // the reason string, so the log line and the reported GpuBackendSource can never disagree. A default
        // whose provider was never registered is a developer's problem and reads as one. Everything else is the
        // player-facing line above, including a STORED PREFERENCE for an unregistered native kind: that player
        // really does have a saved choice this build cannot honour, and clearing it is the fix.
        static void WarnFallbackFor(GpuBackendSelection fell, GpuBackendKind requested, string failure,
            GpuBackendKind fallback)
        {
            if (fell.Source == GpuBackendSource.DefaultProviderMissing)
                log.Warn(UnregisteredNativeDefault.Warning(requested, fallback));
            else
                WarnFallback(requested, failure, fallback);
        }

        /// <summary>
        /// The decision a provider-backed request gets BEFORE anything is created, and the single place decision
        /// I2's two failure modes are told apart. Pure enough to pin headlessly, which matters because the
        /// alternative is only reachable on a machine that has the backend.
        /// <para>
        /// A backend with NO registered provider throws <see cref="GpuBackendProviderMissingException"/> here, and
        /// it throws FIRST, before the support probe below can answer false for the same request and turn a
        /// forgotten one-line registration into a run on a quietly different backend. That ordering is the whole
        /// invariant: a missing registration is a wiring fault in the app, an unsupported machine is a fact about
        /// the hardware, and only the second one is allowed to fall back.
        /// </para>
        /// <para>
        /// Returns null when creation should be attempted, or the reason to warn with and fall back on. With
        /// <paramref name="allowFallback"/> false there is nothing to fall back to, so the probe is skipped
        /// entirely and a real failure throws, exactly as the Veldrid path treats a caller that named one backend
        /// outright.
        /// </para>
        /// </summary>
        internal static string? PreflightProvider(GpuBackendKind backend, bool allowFallback,
            bool pinnedByEnvironment, out IGpuBackendProvider? provider, out bool providerMissing)
        {
            // The 17.40.0 split of decision I2, whose whole reasoning lives on the type that decides it: a
            // provider-backed backend the ENVIRONMENT did not pin, with no provider registered, falls back
            // rather than throwing, because the OS probe answers one on every platform now.
            providerMissing = false;
            if (allowFallback && UnregisteredNativeDefault.Applies(backend, pinnedByEnvironment))
            {
                provider = null;
                providerMissing = true;
                return UnregisteredNativeDefault.Reason;
            }

            provider = GpuBackendProviders.Require(backend);
            if (!allowFallback) return null;
            return GpuBackendSelector.IsBackendSupported(backend) ? null : NoMachineSupport;
        }

        // The provider-backed half of windowed creation. Same two guards as the Veldrid path and in the same
        // order: rule the backend out up front with the functional probe, then catch what the probe cannot see (a
        // driver that answers "supported" and fails at device creation anyway).
        static GpuDeviceContext CreateFromProvider(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendSelection selection, bool allowFallback)
        {
            GpuBackendKind fallback = GpuBackendSelector.IncumbentFor(GpuBackendSelector.DetectOS());
            // The same "nothing to fall back TO" guard the Veldrid path carries. It reads IncumbentFor rather
            // than ProbeOS since 17.40.0, and that is the edit that keeps this whole path alive: the probe now
            // answers a provider-backed kind, so a fallback aimed at it would land on the backend that just
            // refused, warn about a change that is not one, then fail again for the same reason.
            bool canFallBack = allowFallback && selection.Backend != fallback;

            string? failure = PreflightProvider(selection.Backend, canFallBack,
                selection.WasPinnedByEnvironment, out IGpuBackendProvider? provider, out bool providerMissing);
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
                    // Inside the same process-wide gate the Veldrid path uses. Device creation is serialized on
                    // every backend, so a provider needs no lifecycle lock of its own and cannot race one.
                    lock (_lifecycleGate)
                    {
                        created = provider!.CreateForWindow(request);
                    }
                }
                catch (Exception ex) when (canFallBack)
                {
                    // Deliberately broad, for the reason the Veldrid path spells out: the failure can be anything
                    // from a driver HRESULT wrapper to a DllNotFoundException out of the P/Invoke layer, and the
                    // no-driver case is exactly the one this fallback exists for.
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

            // WHAT gets reported is the missing-provider split of 17.40.0: a stored preference and a real
            // creation failure both read as FallbackAfterFailure, and only a DEFAULT with no registered
            // provider reads as DefaultProviderMissing.
            GpuBackendSelection fell = providerMissing
                ? UnregisteredNativeDefault.Report(selection, fallback)
                : GpuBackendSelector.AfterFallback(selection, fallback);
            WarnFallbackFor(fell, selection.Backend, failure, fallback);

            // Back through the ordinary entry with the fallback's own selection and no further fallback, so the
            // fallback device is created by whichever path owns it and the post-fallback report is the same
            // record a Veldrid-path fallback produces.
            try
            {
                return CreateForWindow(window, width, height, syncToVerticalBlank, fell, allowFallback: false);
            }
            catch (Exception ex)
            {
                throw GpuNoUsableBackendException.Build(selection.Backend, failure, fallback, ex, cause);
            }
        }

        // The provider path's construction step, shared by the windowed and headless entries so the guard and the
        // ownership decision cannot drift apart between them.
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
                // The provider built it, so this context owns its disposal, exactly as it owns the raw Veldrid
                // device on the other path.
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

        // Creates a windowed device on `kind`, with no probing, no fallback, and no resolution. The single place
        // that maps a backend onto a Veldrid factory for the windowed path.
        static GraphicsDevice CreateWindowed(GpuBackendKind kind, GraphicsDeviceOptions opts, SwapchainDescription scDesc)
            => kind switch
            {
                GpuBackendKind.Metal => GraphicsDevice.CreateMetal(opts, scDesc),
                GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(opts, scDesc),
                GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(opts, BuildD3D11Options(), scDesc),
                GpuBackendKind.OpenGL => throw new NotSupportedException(
                    "Windowed OpenGL device-from-handle is not supported (Silk would need to own the GL context)."),
                GpuBackendKind.Direct3D11Native => throw NotCreatedByVeldrid(kind),
                _ => throw NotCreatedByVeldrid(kind),
            };

        // The arm an appended member used to fall into, and the reason this audit exists. A switch expression over
        // an enum does NOT throw SwitchExpressionException for an unlisted member when it carries a discard, and
        // both of these carried one that read `GraphicsDevice.CreateMetal(...)`. So a new backend did not fail
        // here: it silently asked Veldrid for a METAL device, which on Windows fails naming an API the caller
        // never selected, from a stack that says nothing about the backend actually requested.
        static NotSupportedException NotCreatedByVeldrid(GpuBackendKind kind)
            => new($"{kind} is not created here. It is a provider-backed backend, built by the "
                + "IGpuBackendProvider registered for it (GpuBackendProviders) and adopted through the "
                + "IGpuDevice constructor, and every entry into this path branches on "
                + "GpuBackendProviders.RequiresProvider before reaching the Veldrid switch. Reaching this means "
                + "that branch was bypassed.");

        // The engine-owned headless device options, in ONE place so the resolved and the backend-named entries
        // cannot drift apart. Verbatim what the 2D snapshot path passed (no depth, no main-swapchain depth, debug
        // off, Improved binding, sRGB on, sync off), which is what keeps the golden images pixel-identical.
        static GraphicsDeviceOptions DefaultHeadlessOptions
            => new(false, null, false, ResourceBindingModel.Improved, true, true);

        /// <summary>
        /// Veldrid-free headless device for migrated consumers (Render2D) that must not reference Veldrid, on the
        /// backend <see cref="GpuBackendSelector"/> resolves from the environment. Uses the SAME device options the
        /// 2D snapshot path passed verbatim (no depth, no main-swapchain depth, debug off, Improved binding, sRGB
        /// on, sync off) so the golden image stays pixel-identical.
        /// </summary>
        public static GpuDeviceContext CreateHeadless() => CreateHeadless(DefaultHeadlessOptions);

        /// <summary>
        /// Create a headless device on EXACTLY <paramref name="backend"/>: no environment override, no stored
        /// preference, no OS probe, and no fallback, the headless twin of
        /// <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool, GpuBackendKind)"/>. A provider-backed
        /// backend with no registered provider throws <see cref="GpuBackendProviderMissingException"/> (decision
        /// I2), and every other failure propagates, because a caller that named one backend outright is not asking
        /// to be quietly given a different one.
        /// <para>
        /// PUBLIC because comparing two backends in ONE process is a first-class need rather than a test trick.
        /// Backend-parity work drives the incumbent and the native Direct3D 11 implementations A against B, and
        /// phase 3 of the native backend program replaces one with the other under the same measurements. The
        /// alternative is what those callers reach for when this does not exist: pulling the provider out of
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
            => CreateHeadless(DefaultHeadlessOptions,
                new GpuBackendSelection(backend, GpuBackendSource.UserPreference, null), allowFallback: false);

        internal static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options)
            => CreateHeadless(options, GpuBackendSelector.Resolve(), allowFallback: true);

        // The RESOLVED headless entry with the selection supplied, which is how a test drives a provenance the
        // live environment cannot produce on the machine it runs on. Identical to the entry above in every other
        // respect, so nothing here is a path only a test takes.
        internal static GpuDeviceContext CreateHeadless(GpuBackendSelection selection)
            => CreateHeadless(DefaultHeadlessOptions, selection, allowFallback: true);

        // The one headless creation path, so the resolved entry and the backend-named entry share the provider
        // branch, the lifecycle gate and the adoption step rather than each routing its own way to a device.
        static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options, GpuBackendSelection selection,
            bool allowFallback)
        {
            if (GpuBackendProviders.RequiresProvider(selection.Backend))
                return CreateHeadlessFromProvider(options, selection, allowFallback);

            GraphicsDevice gd;
            lock (_lifecycleGate)
            {
                gd = selection.Backend switch
                {
                    GpuBackendKind.Metal => GraphicsDevice.CreateMetal(options),
                    GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(options),
                    GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(options, BuildD3D11Options()),
                    GpuBackendKind.OpenGL => throw new NotSupportedException(
                        "Headless OpenGL device creation is not supported in Phase 3a (needs a context surface)."),
                    GpuBackendKind.Direct3D11Native => throw NotCreatedByVeldrid(selection.Backend),
                    _ => throw NotCreatedByVeldrid(selection.Backend),
                };
            }
            return new GpuDeviceContext(gd, selection, ownsDevice: true);
        }

        /// <summary>
        /// The provider-backed half of headless creation, and the two ways it is allowed to end somewhere else.
        /// <para>
        /// THE HEADLESS PATH IS STRICTER THAN THE WINDOWED ONE, deliberately. A headless run that quietly
        /// changed backend would file its golden images under a backend that never rendered them, and each of
        /// the five cross-platform GPU legs pins its backend in <c>KE_GRAPHICS_BACKEND</c> and then captures
        /// goldens through here. So a PINNED backend still propagates everything, exactly as it always did, and
        /// so does the explicitly-named <see cref="CreateHeadless(GpuBackendKind)"/> overload, which turns
        /// fallback off outright.
        /// </para>
        /// <para>
        /// What falls back is the DEFAULT, in both of the ways it can fail, because the OS probe answers a
        /// provider-backed kind on every platform since 17.40.0. An unregistered provider is a game that never
        /// took the native package. A creation that THROWS is a registered provider refusing this machine, which
        /// <c>MetalSupportProbe.MissingRequirement</c> can do on a real Mac, and before this it propagated: a
        /// <c>Render2DSnapshot.Capture</c> or <c>Render3DSnapshot.Capture</c> that worked before the repin threw
        /// after it. Both now land on the incumbent with the same WARN the windowed path prints.
        /// </para>
        /// </summary>
        static GpuDeviceContext CreateHeadlessFromProvider(GraphicsDeviceOptions options,
            GpuBackendSelection selection, bool allowFallback)
        {
            bool canFallBack = allowFallback && !selection.WasPinnedByEnvironment;
            GpuBackendKind incumbent = GpuBackendSelector.IncumbentFor(GpuBackendSelector.DetectOS());

            if (canFallBack && UnregisteredNativeDefault.Applies(selection.Backend, pinnedByEnvironment: false))
            {
                return HeadlessFallback(options, selection, incumbent,
                    UnregisteredNativeDefault.Reason, cause: null, providerMissing: true);
            }

            IGpuBackendProvider provider = GpuBackendProviders.Require(selection.Backend);
            GpuProviderDevice created;
            try
            {
                lock (_lifecycleGate)
                {
                    created = provider.CreateHeadless();
                }
            }
            catch (Exception ex) when (canFallBack)
            {
                // Broad for the reason both windowed paths give, and the machine-refusal case is the one this
                // arm was added for: a provider that answers "this machine cannot" raises NotSupportedException
                // from creation, and a machine with no loader raises out of the P/Invoke layer before that.
                return HeadlessFallback(options, selection, incumbent,
                    $"{ex.GetType().Name}: {ex.Message}", ex, providerMissing: false);
            }

            // Adoption sits OUTSIDE the catch, exactly as it does on the windowed path: every throw out of it is
            // a bug in the PROVIDER, and turning one into "this machine cannot run the backend" is the
            // misattribution both guards exist to prevent.
            return Adopt(created, selection);
        }

        // The headless fallback itself, in one place so the unregistered-provider arm and the creation-failure
        // arm report, warn and re-enter identically. allowFallback goes false on the way back in: the incumbent
        // is a Veldrid backend and there is nothing further to fall back to.
        static GpuDeviceContext HeadlessFallback(GraphicsDeviceOptions options, GpuBackendSelection selection,
            GpuBackendKind incumbent, string failure, Exception? cause, bool providerMissing)
        {
            GpuBackendSelection fell = providerMissing
                ? UnregisteredNativeDefault.Report(selection, incumbent)
                : GpuBackendSelector.AfterFallback(selection, incumbent);
            WarnFallbackFor(fell, selection.Backend, failure, incumbent);

            try
            {
                return CreateHeadless(options, fell, allowFallback: false);
            }
            catch (Exception ex)
            {
                throw GpuNoUsableBackendException.Build(selection.Backend, failure, incumbent, ex, cause);
            }
        }
    }
}
