using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE CREATION HALF OF THE NATIVE DEVICE: the adapter choice of decision G2, the <c>D3D11CreateDevice</c>
    /// call with the debug-layer arm of decision G4, and the assembly of every subsystem in dependency order.
    /// Split from the seam surface because the two are different concerns and because a device that must stay
    /// under the file-size cap has no room for both.
    /// </summary>
    internal sealed partial class D3D11GpuDevice
    {
        /// <summary>
        /// Create a WINDOWED device on <paramref name="hwnd"/>, with its swapchain. Windows-only, and the caller
        /// has already made the platform check: this is reached from
        /// <see cref="D3D11BackendProvider.CreateForWindow"/>, which guards on
        /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/> first so no body naming a Direct3D type is ever
        /// JIT-compiled off Windows.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static GpuProviderDevice CreateForWindowWindows(IntPtr hwnd, uint width, uint height,
            bool syncToVerticalBlank)
        {
            if (hwnd == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The native Direct3D 11 backend was asked for a windowed device with a null window handle. A "
                    + "swapchain needs an HWND to present into, and the windowing layer supplies one through "
                    + "GpuWindowHandle.", nameof(hwnd));
            }

            return CreateWindows(hwnd, width, height, syncToVerticalBlank);
        }

        /// <summary>
        /// Create an OFFSCREEN device with no swapchain, for the headless snapshot and golden paths. Everything
        /// else is identical to the windowed path, which is what keeps a golden rendered headlessly comparable
        /// with a frame rendered in a window.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static GpuProviderDevice CreateHeadlessWindows()
            => CreateWindows(IntPtr.Zero, 0u, 0u, syncToVerticalBlank: false);

        // THE CONSTRUCTION ORDER, STEPS 1 AND 2 (issue #497). Everything from the emitter down is the instance
        // constructor below, because those subsystems need one another rather than needing this.
        //
        // OWNERSHIP IS THE DELICATE PART AND THE finally IS WHY. Between the D3D11CreateDevice call and the
        // constructor taking over, this method holds COM references nothing else knows about, so a throw anywhere
        // in the middle (a runtime with no ID3D11DeviceContext1, a swapchain a window handle refuses) would leak
        // an ID3D11Device and keep every driver allocation behind it alive until the process exits. The locals
        // are NULLED on the success path, which is what makes the finally both the failure cleanup and the
        // ordinary release of the references this method is done with.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static GpuProviderDevice CreateWindows(IntPtr hwnd, uint width, uint height, bool syncToVerticalBlank)
        {
            uint creationFlags = ResolveCreationFlags();

            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IReadOnlyList<D3D11AdapterInfo> adapters = D3D11DxgiQueries.DescribeAdaptersWindows(factory);
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.FromEnvironment(), adapters, out string? adapterWarning);
            if (adapterWarning != null) log.Warn(adapterWarning);
            log.Info(D3D11AdapterSelection.Describe(choice, adapters));

            IDXGIAdapter1? requested = ResolveAdapterWindows(factory, choice);
            IDXGIAdapter? deviceAdapter = null;
            ID3D11Device? device = null;
            ID3D11DeviceContext? immediate = null;
            ID3D11DeviceContext1? context = null;

            try
            {
                device = CreateDeviceWindows(requested, DriverTypeFor(choice, requested), ref creationFlags,
                    out immediate);

                // DECISION R7 NEEDS THE VERSIONED CONTEXT, because every constant-buffer bind goes through
                // *SetConstantBuffers1 and those six methods live on ID3D11DeviceContext1. Asking once here is
                // what turns a runtime too old to answer into a refusal with a message, rather than a cast that
                // fails on the first draw of the first frame.
                context = immediate.QueryInterfaceOrNull<ID3D11DeviceContext1>()
                    ?? throw new NotSupportedException(
                        "The native Direct3D 11 backend needs ID3D11DeviceContext1 and this runtime does not "
                        + "offer it. Every constant-buffer bind goes through *SetConstantBuffers1 with an "
                        + "explicit first constant and constant count, which exists only on the versioned "
                        + "context. Select GpuBackendKind.Direct3D11 on this machine.");

                bool debugLayerActive = (creationFlags & D3D11DebugLayer.CreateDeviceDebug) != 0;
                if (debugLayerActive) log.Info(D3D11DebugLayer.ActiveDescription);

                // The adapter the device ACTUALLY runs on, which is right on every path including the default
                // enumeration where nothing in the engine picked one. It feeds the capability read and the
                // swapchain's factory, and it is released by the finally once the constructor has used both.
                deviceAdapter = AdapterOfWindows(device);

                // Decision G2's telemetry half: the flag off the created device, OR'd with "the selection asked
                // for WARP", because a WARP device whose adapter does not carry the flag still ran on a software
                // rasterizer and a header that said otherwise would misattribute the whole capture.
                bool softwareAdapter = D3D11DxgiQueries.IsSoftwareAdapterWindows(device)
                    || D3D11AdapterSelection.IsSoftwareChoice(choice, adapters);

                // The threading probe runs HERE rather than in GpuDeviceContext, because a natively created
                // device has no Veldrid GraphicsDevice to read a raw ID3D11Device pointer off. Both halves travel
                // out through GpuProviderDevice, so the native leg logs the same threading line the Veldrid leg
                // does instead of going dark on the backend it was written to diagnose.
                GpuThreadingCaps? threadingCaps = D3D11ThreadingProbe.TryQuery(device.NativePointer,
                    out string? threadingProbeFailure);

                D3D11RecordMode recordMode = D3D11RecordModes.FromEnvironment(out string? recordValue);
                if (recordValue != null) log.Warn(D3D11RecordModes.UnrecognizedWarning(recordValue));

                var created = new D3D11GpuDevice(device, context, deviceAdapter, threadingCaps, softwareAdapter,
                    recordMode, debugLayerActive, hwnd, width, height, syncToVerticalBlank);

                // The device owns both from here, so the finally must not release them.
                device = null;
                context = null;
                return new GpuProviderDevice(created, threadingCaps, threadingProbeFailure);
            }
            finally
            {
                requested?.Dispose();
                deviceAdapter?.Dispose();
                // ALWAYS released: the versioned context above is a second reference to the same object, and it
                // is the one the device keeps.
                immediate?.Dispose();
                context?.Dispose();
                device?.Dispose();
            }
        }

        /// <summary>
        /// STEPS 2 TO 9: every subsystem this device owns, in the order their dependencies force. The order the
        /// issue lists is the order the SUBSYSTEMS were designed in, and it differs here in two places, both
        /// because a later listed piece is an input to an earlier listed one: the capability read comes before
        /// the resource factory, which takes the capabilities to validate a sample count against, and the one
        /// device state comes after the ring, because the state COMPOSES the bind flush and the bind flush takes
        /// the ring on the immediate driver. Neither changes what is built, only when.
        /// </summary>
        /// <param name="device">The created device. Taken over: <see cref="Dispose"/> releases it.</param>
        /// <param name="context">Its immediate context as <c>ID3D11DeviceContext1</c>. Taken over too.</param>
        /// <param name="adapter">The adapter the device runs on. BORROWED, for the capability read and the
        /// swapchain's factory, and released by the caller once this returns.</param>
        /// <param name="threadingCaps">What the driver-threading probe answered, or null for no answer, which
        /// takes the conservative arm of both the creation gate and the R7 workaround.</param>
        /// <param name="softwareAdapter">Whether this device is on a software rasterizer, for the header.</param>
        /// <param name="recordMode">Which recording driver <c>KE_D3D11_RECORD</c> selected.</param>
        /// <param name="debugLayerActive">Whether the device was created WITH the debug layer, which is the one
        /// thing that decides whether an info-queue pump is built at all.</param>
        /// <param name="hwnd">The window to present into, or <see cref="IntPtr.Zero"/> for headless.</param>
        /// <param name="width">The initial backbuffer width, ignored when headless.</param>
        /// <param name="height">The initial backbuffer height, ignored when headless.</param>
        /// <param name="syncToVerticalBlank">The initial vsync setting.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        D3D11GpuDevice(ID3D11Device device, ID3D11DeviceContext1 context, IDXGIAdapter adapter,
            GpuThreadingCaps? threadingCaps, bool softwareAdapter, D3D11RecordMode recordMode,
            bool debugLayerActive, IntPtr hwnd, uint width, uint height, bool syncToVerticalBlank)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(adapter);

            _device = device;
            _context = context;
            _recordMode = recordMode;
            _softwareAdapter = softwareAdapter;
            _syncToVerticalBlank = syncToVerticalBlank;

            // The liveness token and the loss latch first: every subsystem below holds one, the other, or both,
            // and the latch is what a fault site at any depth reaches. It takes THIS device as its one native
            // call, which is safe in a constructor because the call is never made until a fault happens.
            _liveness = new D3D11DeviceLiveness();
            _loss = new D3D11DeviceLossLatch(_liveness, this);

            // 3. FENCES. The timeline picks its own mechanism (ID3D11Fence via ID3D11Device5, or the event-query
            // pool), which is the one place that decision is taken, and the subsystem adds the engine logic on
            // top of it: the submit lock, the liveness adapter and the KE_D3D11_REAL_DRAIN kill switch.
            bool realDrain = D3D11RealDrain.FromEnvironment(out string? realDrainValue);
            if (realDrainValue != null) log.Warn(D3D11RealDrain.UnrecognizedWarning(realDrainValue));
            _fences = new D3D11FenceSubsystem(
                D3D11FenceTimelines.CreateWindows(device, context), _submitLock, _liveness, realDrain);
            log.Info($"D3D11 completion fences: {_fences.Mechanism}. This device reports "
                + "SupportsCompletionFences true, which is the one permitted capability difference from the "
                + "Veldrid Direct3D 11 backend (decision C5).");
            if (!realDrain)
            {
                log.Warn($"{D3D11RealDrain.EnvVarName} is OFF, so WaitForIdle on this device does NOTHING, which "
                    + "is the empty method body the Veldrid Direct3D 11 backend has always had. Every drain in "
                    + "the engine is a no-op on this run, and any measurement of one reads zero because the call "
                    + "is empty rather than because the GPU was idle.");
            }

            // 4. THE RING. One allocator for the device, over the fence subsystem's read half, so a segment is
            // recycled against GPU COMPLETION rather than against a submit receipt (decision U5).
            int framesInFlight = D3D11FramesInFlight.FromEnvironment(out string? framesValue);
            if (framesValue != null) log.Warn(D3D11FramesInFlight.UnrecognizedWarning(framesValue));
            _rings = new D3D11RingAllocator(framesInFlight, _fences, _submitLock,
                D3D11RingAllocator.MapScopeFor(recordMode));
            log.Info(D3D11FramesInFlight.ActiveDescription(framesInFlight));

            // 2. ONE STATE AND ONE EMITTER CONTEXT PER DEVICE, which is the enforcement half of issue #476: the
            // DEVICE constructs exactly one of each and every emitter value it hands out receives them, so the
            // redundancy caches describe the context rather than one command list's recording.
            //
            // NO ANSWER FROM THE PROBE TAKES THE WORKAROUND, which is the same rule the creation gate below
            // applies to the same silence: an unknown answer is not a licence. The two arms are not symmetric in
            // what being wrong costs. Skipping the unset on a runtime that IS emulating command lists is the
            // documented way to bind a *SetConstantBuffers1 range at the wrong first constant, which renders
            // wrong and throws nothing, and issuing it on a runtime that is not is one extra call with the same
            // span immediately before the bind, which changes no state and costs call count.
            _state = new D3D11DeviceState(new D3D11BindFlush(
                unsetConstantBuffersBeforeSet: threadingCaps?.CommandListsAreEmulated ?? true,
                ringsUnmappedBeforeCommands: D3D11BindFlush.RingsFor(recordMode, _rings)));
            _emitterContext = new D3D11EmitterContext(context);
            _emitter = new D3D11NativeEmitter(_state, _emitterContext);
            log.Info(D3D11RecordModes.ActiveDescription(recordMode));

            // 7a. CAPABILITIES, read off the live device and the adapter it runs on, and the single source both
            // GpuDeviceContext.Capabilities and IGpuDevice.Capabilities take on this backend. It is here rather
            // than after the factory because the factory validates a requested sample count against it.
            Capabilities = D3D11DxgiQueries.ReadCapabilitiesWindows(device, adapter,
                _fences.SupportsCompletionFences);

            // 5. THE FACTORY, with the creation gate the threading probe earned: a driver reporting
            // DriverConcurrentCreates gets no lock at all, and no answer takes the serialized arm.
            _factory = new D3D11ResourceFactory(device, context, _liveness, _rings, CreateCommandList,
                Capabilities, D3D11CreationGate.For(threadingCaps), _fences.CreateFence, _loss);
            log.Info(_factory.SerializesCreation
                ? "D3D11 resource creation: SERIALIZED behind one lock, because this driver does not report "
                    + "DriverConcurrentCreates (or the threading probe had no answer)."
                : "D3D11 resource creation: FREE-THREADED, because this driver reports DriverConcurrentCreates. "
                    + "No creation lock is taken at all.");

            // The two shared samplers the seam promises, built through the factory so they carry the same
            // hardcodes every other sampler on this backend does (decision G1), and from D3D11SharedSamplers
            // rather than from the engine's identically named GpuSamplerDescription.Point / .Linear statics. The
            // statics are CLAMPED and the seam's shared pair is WRAP, which is the collision that cost two
            // goldens. See D3D11SharedSamplers for the record.
            _pointSampler = _factory.CreateSampler(D3D11SharedSamplers.Point);
            _linearSampler = _factory.CreateSampler(D3D11SharedSamplers.Linear);

            // The staging map path, with decision G3's second check site wired: the latch arrives here so a
            // failed map asks it BEFORE it builds an exception message.
            _staging = new D3D11StagingAccess(new D3D11ContextStagingMemory(context), _submitLock, _loss);

            // 6. THE SWAPCHAIN, on the windowed path only. The engine half owns the present boundary and the
            // queued resize, and the native half is the four calls it makes.
            if (hwnd != IntPtr.Zero)
            {
                _swapchain = new D3D11Swapchain(
                    D3D11DxgiSwapchain.CreateWindows(device, context, adapter, hwnd, width, height,
                        depthFormat: null, _liveness),
                    _submitLock, width, height, syncToVerticalBlank);
            }

            // 9. THE DEBUG-LAYER PUMP (decision G4), and ONLY when the device was actually created with the
            // layer. A queue asked for on a device without it answers null, which is not a fault: it is what a
            // machine with no Graphics Tools installed does, and the retry above already warned about it.
            if (debugLayerActive)
            {
                ID3D11InfoQueueSource? source = D3D11InfoQueueMessages.TryCreateWindows(device);
                if (source is null)
                {
                    log.Warn("The Direct3D 11 debug layer is active on this device but it exposes no "
                        + "ID3D11InfoQueue, so no debug-layer messages are pumped into this log. Rendering is "
                        + "unaffected.");
                }
                else
                {
                    _infoQueue = new D3D11InfoQueuePump(source);
                }
            }
        }

        // 8. THE RECORDING DRIVER, and the one place KE_D3D11_RECORD is consulted after creation. The emitter
        // travels BY VALUE, one copy per list, which is safe precisely because it is a readonly struct over the
        // device's one state and one context: every copy addresses the same caches. The deferred driver ignores
        // it entirely and meets an emitter only at submit.
        IGpuCommandList CreateCommandList() => D3D11CommandDrivers.Create(_recordMode, _emitter);

        // The two environment levers that reach D3D11CreateDevice, OR'd exactly as decision G4 states, each
        // warning about its own unrecognized value. GpuD3D11DeviceFlags is the engine-wide lever and is logged
        // here rather than by GpuDeviceContext, whose logging of it belongs to the Veldrid creation path.
        static uint ResolveCreationFlags()
        {
            uint flags = GpuD3D11DeviceFlags.FromEnvironment(out string? preventValue);
            if (preventValue != null) log.Warn(GpuD3D11DeviceFlags.UnrecognizedWarning(preventValue));
            else if (flags != 0) log.Info(GpuD3D11DeviceFlags.ActiveDescription);

            uint debug = D3D11DebugLayer.FromEnvironment(out string? debugValue);
            if (debugValue != null) log.Warn(D3D11DebugLayer.UnrecognizedWarning(debugValue));

            return flags | debug;
        }

        // The adapter an ENUMERATED choice names, re-fetched at its index because the enumeration handed the
        // policy plain descriptions and released its own objects. A failed enumeration is not a fault: the index
        // was valid when the list was taken and an adapter can be removed between the two, so this falls back to
        // letting DXGI pick, which is what every other unsatisfiable request does.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static IDXGIAdapter1? ResolveAdapterWindows(IDXGIFactory1 factory, in D3D11AdapterChoice choice)
        {
            if (choice.Kind != D3D11AdapterChoiceKind.Enumerated) return null;

            SharpGen.Runtime.Result result = factory.EnumAdapters1(choice.Index, out IDXGIAdapter1? adapter);
            if (result.Success && adapter is not null) return adapter;

            adapter?.Dispose();
            log.Warn($"Adapter {choice.Index} was enumerated a moment ago and is no longer there, so "
                + $"{D3D11AdapterSelection.EnvVarName} could not be honoured after all. Letting DXGI pick.");
            return null;
        }

        // Direct3D requires DriverType.Unknown when an adapter is supplied and refuses an adapter alongside
        // Hardware or Warp, so the two halves of the choice are one decision rather than two arguments a caller
        // could pair up wrongly.
        static DriverType DriverTypeFor(in D3D11AdapterChoice choice, IDXGIAdapter1? adapter)
        {
            if (adapter is not null) return DriverType.Unknown;
            return choice.Kind == D3D11AdapterChoiceKind.WarpDriver ? DriverType.Warp : DriverType.Hardware;
        }

        // THE CREATION CALL AND DECISION G4's RETRY ARM. Feature level 11_0 and nothing higher, for the reason
        // D3D11FeatureProbe states: the two features this backend requires are 11.1 RUNTIME features that 11_0
        // hardware reports through D3D11_FEATURE_D3D11_OPTIONS, and asking for 11_1 is the classic way to get a
        // blanket E_INVALIDARG out of an older runtime.
        //
        // THE FLAGS TRAVEL BY REF because the retry CHANGES them: a machine without the Windows graphics tools
        // answers DXGI_ERROR_SDK_COMPONENT_MISSING to a debug-layer request, and the caller has to know the
        // second attempt dropped the flag, since that is what decides whether an info-queue pump is built.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static ID3D11Device CreateDeviceWindows(IDXGIAdapter1? adapter, DriverType driverType, ref uint flags,
            out ID3D11DeviceContext immediateContext)
        {
            FeatureLevel[] featureLevels = { FeatureLevel.Level_11_0 };
            IntPtr adapterPointer = adapter?.NativePointer ?? IntPtr.Zero;

            SharpGen.Runtime.Result result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(adapterPointer, driverType,
                (DeviceCreationFlags)flags, featureLevels, out ID3D11Device? device,
                out ID3D11DeviceContext? context);

            if (D3D11DebugLayer.ShouldRetryWithoutDebugLayer(flags, result.Code))
            {
                // A partial success would leak: the call can hand a device back on a non-success HRESULT, and
                // the retry below overwrites both locals.
                context?.Dispose();
                device?.Dispose();

                log.Warn(D3D11DebugLayer.UnavailableWarning());
                flags &= ~D3D11DebugLayer.CreateDeviceDebug;
                result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(adapterPointer, driverType, (DeviceCreationFlags)flags,
                    featureLevels, out device, out context);
            }

            if (result.Failure || device is null || context is null)
            {
                context?.Dispose();
                device?.Dispose();
                result.CheckError();
                throw new InvalidOperationException(
                    "D3D11CreateDevice reported success and handed back no device. The native Direct3D 11 "
                    + "backend cannot run on this machine. Select GpuBackendKind.Direct3D11.");
            }

            immediateContext = context;
            return device;
        }

        // The adapter a created device is actually on: device to IDXGIDevice to its adapter. The caller owns the
        // result and releases it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static IDXGIAdapter AdapterOfWindows(ID3D11Device device)
        {
            using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
            return dxgi.GetAdapter();
        }
    }
}
