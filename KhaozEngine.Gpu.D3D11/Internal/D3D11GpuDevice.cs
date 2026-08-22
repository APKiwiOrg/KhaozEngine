using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE NATIVE DIRECT3D 11 DEVICE: the <see cref="IGpuDevice"/> seam over the sixteen subsystems the rest of
    /// this package builds, and the one object that owns the <c>ID3D11Device</c>, its immediate context and the
    /// single submit lock of decision W4. Everything here is wiring: every rule it applies is decided in a type
    /// that has no device in it, which is why the whole package is testable on macOS and this file is the part
    /// that is not.
    /// <para>
    /// ONE OF EVERYTHING, AND THAT IS THE POINT (issue #476). One <see cref="D3D11DeviceState"/>, one
    /// <see cref="D3D11EmitterContext"/>, and therefore one emitter VALUE every command list copies, so the
    /// redundancy caches of R6 describe what is bound on the context rather than what one list recorded. One
    /// submit lock, covering replay, present and the resize apply together. One
    /// <see cref="D3D11RingAllocator"/>, one <see cref="D3D11FenceSubsystem"/>, one
    /// <see cref="D3D11DeviceLossLatch"/>, one liveness token.
    /// </para>
    /// <para>
    /// THE CONSTRUCTION ORDER IS A DEPENDENCY ORDER and it is written out in
    /// <c>D3D11GpuDevice.Create.cs</c>, which also holds the adapter selection and the device creation itself.
    /// This file is the seam surface and the teardown.
    /// </para>
    /// <para>
    /// WINDOWS-ONLY AT THE TYPE LEVEL, the same contract <see cref="D3D11ResourceFactory"/> and
    /// <see cref="D3D11NativeEmitter"/> carry: nothing off Windows constructs one, so no body here is ever
    /// JIT-compiled on a machine with no Direct3D, and the platform-compatibility analyzer gates every caller at
    /// the provider rather than method by method. The load-path rule that still binds is the FIELD one: every
    /// field below is a REFERENCE, so loading this type by reflection (which the suite does, deliberately, on
    /// macOS) resolves no Vortice layout. Every value-typed Direct3D argument is a LOCAL inside a body.
    /// </para>
    /// <para>
    /// DECISION G3'S FOUR CHECK SITES ARE ALL HERE, because the latch is the device's:
    /// <see cref="Present"/> hands it the present HRESULT, <see cref="Submit(IGpuCommandList)"/> asks after every
    /// replay, <see cref="D3D11StagingAccess"/> was handed the latch for the staging map, and the resize apply
    /// (which throws rather than returning an HRESULT) is wrapped in
    /// <see cref="D3D11DeviceLossLatch.CheckAfterFault"/> at the present that applies it.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed partial class D3D11GpuDevice : IGpuDevice, IGpuDeviceLifecycle, ID3D11RemovedReason
    {
        static readonly ILogger log = Log.For<D3D11GpuDevice>();

        // The site names the device-loss latch records. Constants rather than literals at the call, so the string
        // a telemetry session header carries and the string a test asserts are one thing.
        internal const string PresentSite = "the swapchain Present";
        internal const string ReplaySite = "a submit replay";
        internal const string ResizeSite = "the swapchain resize apply";

        // Decision W4's ONE lock, covering replay, present and the resize apply. Created here because it is the
        // device's: every subsystem that needs it is handed this one rather than making its own.
        readonly object _submitLock = new();

        readonly ID3D11Device _device;
        readonly ID3D11DeviceContext1 _context;

        readonly DeviceLiveness _liveness;
        readonly D3D11DeviceLossLatch _loss;
        readonly D3D11FenceSubsystem _fences;
        readonly D3D11RingAllocator _rings;
        readonly D3D11DeviceState _state;
        readonly D3D11EmitterContext _emitterContext;
        readonly D3D11ResourceFactory _factory;
        readonly D3D11StagingAccess _staging;
        readonly D3D11Swapchain? _swapchain;
        readonly D3D11InfoQueuePump? _infoQueue;
        readonly D3D11RecordMode _recordMode;
        readonly IGpuSampler _pointSampler;
        readonly IGpuSampler _linearSampler;
        readonly bool _softwareAdapter;

        // NOT readonly, and it cannot be: D3D11CommandDrivers.Submit takes the emitter by ref, so a readonly
        // field would be copied to a temporary and the call would replay into a copy. The struct itself is
        // readonly and holds two class references, so the copy would still address the same state, but a ref to a
        // defensive copy is exactly the shape that stops being true the day the emitter grows a field.
        D3D11NativeEmitter _emitter;

        bool _syncToVerticalBlank;
        bool _tornDown;
        bool _released;

        /// <inheritdoc/>
        public GpuBackendKind Backend => GpuBackendKind.Direct3D11Native;

        /// <inheritdoc/>
        public GpuCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public IGpuResourceFactory Factory => _factory;

        /// <summary>
        /// The swapchain's framebuffer, and NULL on a headless device, which is the same answer the Veldrid path
        /// gives when there is no main swapchain. Decision W2 makes this the SAME object for the life of the
        /// device: a resize changes its size and its views, never its identity, so anything may cache it.
        /// </summary>
        public IGpuFramebuffer? SwapchainFramebuffer => _swapchain?.Framebuffer;

        /// <inheritdoc/>
        public IGpuSampler PointSampler => _pointSampler;

        /// <inheritdoc/>
        public IGpuSampler LinearSampler => _linearSampler;

        /// <summary>
        /// The two facts only a live device can report (decision G2's telemetry half and G3's). The
        /// software-adapter flag is fixed at creation, because the adapter a device runs on cannot change under
        /// it, and the loss reason is read LIVE off the latch, because a device loss happens at an arbitrary
        /// moment long after creation and a captured value would always say the device was fine.
        /// </summary>
        public GpuDeviceDiagnostics Diagnostics => new(_softwareAdapter, _loss.HeaderValue);

        /// <summary>Which recording driver this device runs, from <c>KE_D3D11_RECORD</c>. For the session log and
        /// for a test, never consulted to decide anything outside <see cref="CreateCommandList"/>.</summary>
        internal D3D11RecordMode RecordMode => _recordMode;

        /// <summary>The device's one state object. Every emitter value this device hands out addresses it, which
        /// is the invariant issue #476 exists for.</summary>
        internal D3D11DeviceState State => _state;

        /// <summary>The device's one emitter context, beside the one state.</summary>
        internal D3D11EmitterContext EmitterContext => _emitterContext;

        /// <summary>The device's one submit lock (decision W4), for the tests that assert what is and is not
        /// held inside it.</summary>
        internal object SubmitLock => _submitLock;

        /// <summary>
        /// THE M2 AND M3 COUNTERS PER FRAME, and the pending-patch pair of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/499. These are the rolled per-frame readings, for a
        /// debug overlay and for the tests that assert the roll. What a TELEMETRY SESSION carries is
        /// <see cref="Counters"/>, whose cumulative shape is the one that survives being sampled on the
        /// consumer's own cadence.
        /// </summary>
        internal D3D11BackpressureStats LastFrameBackpressure => _rings.LastFrameBackpressure;

        /// <inheritdoc cref="LastFrameBackpressure"/>
        internal RingPatchStats OffTimelinePatches => _rings.OffTimelinePatches;

        /// <inheritdoc cref="LastFrameBackpressure"/>
        internal D3D11DrainStats LastFrameDrain => _fences.LastFrameDrain;

        /// <summary>
        /// THE SEAM'S VIEW OF THE SAME COUNTERS, cumulative since this device was created, which is what gate 4 of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/460 judges the field soak on. This is the only backend
        /// that answers all seven fields, because it is the only one with a fence drain and a segment ring to
        /// count. The Vulkan native device is the second backend to answer anything but the default: it reports
        /// the drain pair off its own timeline and leaves the rest at zero until its segment ring lands.
        /// <para>
        /// BOTH BACKPRESSURE READINGS CROSS, on separate members, which is the requirement of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/499. The ring's frame-boundary stalls are M3's number.
        /// The off-timeline deferrals are a device-level <c>UpdateBuffer</c> meeting a segment an earlier frame is
        /// still reading, which blocks nobody, usually happens at load time, and would make M3's zero-stall
        /// criterion unreachable if it were folded in.
        /// </para>
        /// <para>
        /// THE DENOMINATOR COMES OFF THE RING, and that is only sound because of where the two are advanced.
        /// <c>FramesBegun</c> is the ring allocator's frame index, while the drain it divides is the fence
        /// subsystem's, so M2's per-frame figure holds only while the two count the same frames. They do because
        /// <see cref="Present"/> advances both at the same boundary, in adjacent calls. Separating them, or
        /// advancing one somewhere else, would skew the per-frame drain figure without failing anything.
        /// </para>
        /// </summary>
        public GpuDeviceCounters Counters
        {
            get
            {
                WaitTotals drain = _fences.TotalDrain;
                WaitTotals stalls = _rings.TotalBackpressure;
                RingPatchStats patches = _rings.OffTimelinePatches;

                // Named, because the two longs and the two doubles sit next to each other: a transposed pair here
                // compiles, passes every test, and reports a stall count as a drain count in the field.
                return new GpuDeviceCounters(
                    framesBegun: (long)_rings.FrameIndex,
                    drainCount: drain.Count,
                    drainMs: drain.TotalMs,
                    backpressureStallCount: stalls.Count,
                    backpressureStallMs: stalls.TotalMs,
                    offTimelineDeferred: patches.Deferred,
                    offTimelineOutstanding: patches.Outstanding,
                    // ZERO IS THE READING HERE RATHER THAN A GAP. A Direct3D 11 present hands the frame to the
                    // runtime and returns: there is no acquire step and therefore no acquire the CPU can wait on,
                    // so this backend can never move either half of the pair. Leaving them out was not available,
                    // since the populating constructor takes every field, and passing zero says the honest thing.
                    acquireWaitCount: 0,
                    acquireWaitMs: 0d);
            }
        }

        // ---- Submission ----

        /// <inheritdoc/>
        public void Submit(IGpuCommandList cl) => SubmitCore(cl, null);

        /// <inheritdoc/>
        public void Submit(IGpuCommandList cl, IGpuFence fence) => SubmitCore(cl, fence);

        /// <summary>
        /// THE SUBMIT BOUNDARY: the driver's replay under the submit lock, the end-of-replay signal, the ring
        /// bracket, then decision G3's after-replay check and decision G4's debug-layer pump.
        /// <para>
        /// THE CHECK IS IN A <c>finally</c>, so it runs whether the replay returned or threw. A replay that threw
        /// is exactly the case where the reason matters most and the case where nothing else would ask for it:
        /// the native calls inside a replay end in <c>CheckError</c>, so a device that dies mid-replay arrives as
        /// a <c>SharpGenException</c> and never as an HRESULT anything reads. The check answers one question and
        /// never handles the exception, which keeps on unwinding.
        /// </para>
        /// <para>
        /// THE PUMP RUNS PER SUBMIT rather than per frame, which is a deliberate reading of "at the submit
        /// boundary" rather than a departure from it: a frame makes a handful of submissions, the pump is null
        /// unless <c>KE_D3D11_DEBUG</c> is on, and a message that lands in the log beside the submission that
        /// raised it is worth more during a crash investigation than one batched to the present. Its rate limit
        /// is what bounds the volume either way.
        /// </para>
        /// </summary>
        void SubmitCore(IGpuCommandList list, IGpuFence? fence)
        {
            try
            {
                D3D11CommandDrivers.Submit(_submitLock, list, ref _emitter, _fences, fence, _rings);
            }
            finally
            {
                _loss.CheckAfterFault(ReplaySite);
                _infoQueue?.Pump();
            }
        }

        /// <summary>
        /// The real drain of decision C6, behind <c>KE_D3D11_REAL_DRAIN</c>. Called WITHOUT the submit lock: the
        /// drain signals and flushes under it and then releases it to wait, so the work it is waiting for can
        /// still be submitted, and a caller that already held it is refused by name.
        /// </summary>
        public void WaitForIdle() => _fences.WaitForIdle();

        // ---- Device-level writes ----

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => WriteBuffer(b, offsetBytes, MemoryMarshal.AsBytes(data));

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => WriteBuffer(b, offsetBytes, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(data)));

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => WriteBuffer(b, offsetBytes,
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1)));

        /// <summary>
        /// THE OFF-TIMELINE WRITE OF SECTION 6.4, ROUTED BY THE DESTINATION (decisions U4 and U5). A ring-backed
        /// uniform buffer goes to the allocator, which writes EVERY segment (or queues a patch for the ones the
        /// GPU is still reading) so a value written once persists exactly as it does on the Veldrid leg, which is
        /// the resolution of https://github.com/APKiwiOrg/KhaozEngine/issues/484. Everything else is an
        /// <c>UpdateSubresource</c> with a box, through the same emitter member the record path uses, so the two
        /// levels cannot disagree about what a bulk write does.
        /// <para>
        /// THE LOCK IS SCOPED TO THE WRITE AND NEVER TO A FRAME (decision W4), so an off-timeline write cannot
        /// land in the middle of a replay. The ring branch takes the same lock inside the allocator, which is why
        /// it is not taken here as well: re-entering is free, but taking it around a call that takes it itself
        /// would say the two scopes were different when they are not.
        /// </para>
        /// </summary>
        void WriteBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> bytes)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (buffer is ID3D11RingBacked { Ring: { } ring })
            {
                _rings.UpdateBuffer(ring, offsetBytes, bytes);
                return;
            }

            lock (_submitLock)
            {
                _emitter.UpdateBuffer(buffer, offsetBytes, bytes);
            }
        }

        /// <inheritdoc/>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => UpdateTexture(texture, data, x, y, width, height, 0u, 0u);

        /// <summary>
        /// THE DEVICE-LEVEL TEXTURE WRITE, and the third clause of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/494: it takes the SAME short submit lock the buffer
        /// write takes, scoped to the write and never to a frame, so it cannot land in the middle of a replay.
        /// There is no command-list texture write on this seam at all, so this is the only path a texture upload
        /// can take and there is nothing on the emitter to share with.
        /// <para>
        /// THE ROW PITCH IS DERIVED FROM THE DATA rather than from a format table, and that is exact rather than
        /// approximate: the seam's contract is a tightly packed <paramref name="width"/> by
        /// <paramref name="height"/> region, so its rows are <c>data.Length / height</c> bytes apart by
        /// definition. A format table here would be a second source for the same number, and it is the one that
        /// would drift, because the seam admits a caller that packs its own rows.
        /// </para>
        /// <para>
        /// AN ARRAY LAYER THE TEXTURE DOES NOT HAVE IS REFUSED HERE, by name, before anything native runs. See
        /// <see cref="D3D11UploadBounds"/> for why D3D11 itself will not do it (#695).
        /// </para>
        /// </summary>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        {
            ArgumentNullException.ThrowIfNull(texture);
            ArgumentNullException.ThrowIfNull(data);
            if (width == 0 || height == 0 || data.Length == 0) return;

            D3D11Texture destination = texture as D3D11Texture
                ?? throw new ArgumentException(
                    $"A {texture.GetType().Name} was handed to the native Direct3D 11 device as an upload target. "
                    + "A texture this backend created carries the ID3D11Texture2D an update names, and a texture "
                    + "from another backend carries another backend's.", nameof(texture));

            // THE PHANTOM LAYER IS REFUSED HERE AND NOT BY D3D11 (#695). UpdateSubresource drops a subresource
            // index past the end of the resource without an HRESULT, so nothing downstream of this line would
            // ever notice, and the seam's contract is the refusal at the call site that native Metal already
            // gives. The bound is slices rather than logical layers, which is what a cubemap makes different.
            D3D11UploadBounds.RequireArrayLayer(arrayLayer, destination.ArraySlices);

            lock (_submitLock)
            {
                UpdateTextureWindows(destination, data, x, y, width, height, mipLevel, arrayLayer);
            }
        }

        // The one native call the device-level texture write makes. D3D11CalcSubresource is the mip index plus
        // the layer's stride through the chain, the same arithmetic the emitter's copies use, and the box is the
        // requested region at the requested origin.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void UpdateTextureWindows(D3D11Texture destination, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        {
            int subresource = (int)(mipLevel + (arrayLayer * destination.MipLevels));
            int rowPitch = data.Length / (int)height;
            var region = new Box((int)x, (int)y, 0, (int)(x + width), (int)(y + height), 1);

            _context.UpdateSubresource<byte>(data, destination.DeviceTexture, subresource, rowPitch,
                data.Length, region);
        }

        // ---- Staging map and unmap ----

        /// <inheritdoc/>
        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _staging.Map(staging, mode);

        /// <inheritdoc/>
        public void Unmap(IGpuTexture staging) => _staging.Unmap(staging);

        /// <inheritdoc/>
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _staging.Map(staging, mode);

        /// <inheritdoc/>
        public void Unmap(IGpuBuffer staging) => _staging.Unmap(staging);

        // ---- Present, resize, vsync ----

        /// <summary>
        /// DECISION W3's QUEUE: store the size and return. Takes no lock, makes no native call and never blocks,
        /// so a window callback on any thread is safe even while the submit thread holds the lock for a replay.
        /// The submit thread applies it at the next present boundary, where it provably owns the context. A
        /// headless device has nothing to resize and says so by doing nothing, which is what the incumbent does.
        /// </summary>
        public void ResizeSwapchain(uint w, uint h) => _swapchain?.QueueResize(w, h);

        /// <summary>
        /// THE PRESENT BOUNDARY, in the one order that works (issue #494, clause 2): present and apply the queued
        /// resize under the submit lock, RELEASE it, and only then roll the frame counters. Both
        /// <see cref="D3D11FenceSubsystem.BeginFrame"/> and <see cref="D3D11RingAllocator.BeginFrame"/> refuse a
        /// caller holding the lock by name, and the ring's can block for up to a frame while the GPU finishes
        /// with the segment it is opening, which is exactly the hold decision W4 caps at microseconds.
        /// <para>
        /// THE PRESENT HRESULT GOES STRAIGHT TO THE LATCH (decision G3's first site), because the swapchain
        /// returns it rather than checking it precisely so the reason is read at the site that noticed. A THROW
        /// out of the present is the resize apply's (#489): <c>ResizeBuffers</c>, <c>GetBuffer</c> and
        /// <c>CreateRenderTargetView</c> all end in <c>CheckError</c>, so a device that dies during a resize
        /// arrives as a <c>SharpGenException</c>, which is what <see cref="D3D11DeviceLossLatch.CheckAfterFault"/>
        /// is for.
        /// </para>
        /// <para>
        /// A HEADLESS DEVICE STILL ROLLS THE FRAME. There is nothing to present, but the frame boundary is what
        /// advances the ring's segment and closes the drain telemetry, so a headless render loop that presents
        /// gets the same pipelining a windowed one does.
        /// </para>
        /// </summary>
        public void Present()
        {
            if (_swapchain is not null)
            {
                int hresult;
                try
                {
                    hresult = _swapchain.Present();
                }
                catch
                {
                    _loss.CheckAfterFault(ResizeSite);
                    throw;
                }

                _loss.Check(hresult, PresentSite);
            }

            _fences.BeginFrame();
            _rings.BeginFrame();
        }

        /// <summary>
        /// Whether presentation syncs to the vertical blank. On Direct3D 11 this is an ARGUMENT of
        /// <c>Present</c>, so the setter reconfigures nothing: there is no swapchain to recreate, none to leak,
        /// and no size or depth to preserve. A headless device keeps the mirrored value so the round trip still
        /// answers what it was set to, exactly as the Veldrid path does.
        /// </summary>
        public bool SyncToVerticalBlank
        {
            get => _swapchain?.SyncToVerticalBlank ?? _syncToVerticalBlank;
            set
            {
                _syncToVerticalBlank = value;
                if (_swapchain is not null) _swapchain.SyncToVerticalBlank = value;
            }
        }

        // ---- Device loss ----

        /// <summary>
        /// Decision G3's one native call at a fault site, implemented by the device itself because it already
        /// holds the <c>ID3D11Device</c> and the body is one expression. NEVER throws: a reason read that faulted
        /// during a device loss would replace the diagnostic with a second, less informative failure at exactly
        /// the moment the first one mattered.
        /// </summary>
        int ID3D11RemovedReason.GetDeviceRemovedReason()
        {
            try
            {
                return ReadRemovedReasonWindows();
            }
            catch
            {
                return D3D11DeviceLossCodes.Ok;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        int ReadRemovedReasonWindows() => _device.DeviceRemovedReason.Code;

        // ---- Teardown ----

        /// <summary>
        /// THE ENGINE-OWNED HALF OF TEARDOWN, and the hook <see cref="GpuDeviceContext"/> calls immediately
        /// before it disposes the device. Idempotent, and it never throws.
        /// <para>
        /// IT DOES THE ORDERED RELEASE RATHER THAN JUST FLIPPING THE TOKEN, which is where this device differs
        /// from the Veldrid wrapper and has to. There, destroying the Veldrid <c>GraphicsDevice</c> frees every
        /// child object, so latching first and destroying second is the whole of it. Here the children are COM
        /// objects held by reference count: a token flipped before the swapchain, the samplers and the fence
        /// timeline were released would turn every one of those releases into a no-op (they all read the token),
        /// and the <c>ID3D11Device</c> would then stay alive holding all of them. So the releases happen here,
        /// while the token still says the device is alive, and the token is flipped LAST.
        /// </para>
        /// <para>
        /// THE ORDER IS THE TWO LOCK GUARDS AND THE TIMELINE'S. The drain is called with no lock held, because it
        /// refuses a caller holding the submit lock by name and it is the one member that can block. The fence
        /// subsystem is disposed after the swapchain, because disposing it releases the timeline's fence and its
        /// event objects, and nothing may signal after that. A device already LOST has flipped the token itself
        /// (latch-then-MarkDead), so every release below is already a no-op and the drain returns immediately,
        /// which is the correct behaviour rather than a special case.
        /// </para>
        /// </summary>
        public void MarkDeviceDisposed()
        {
            if (_tornDown) return;
            _tornDown = true;

            // A drain that throws must not stop the rest of the teardown: the exception has nowhere to go from a
            // disposal path and the releases below are what keep the device from leaking.
            try
            {
                _fences.WaitForIdle();
            }
            catch (Exception ex)
            {
                log.Warn($"Draining the native Direct3D 11 device at teardown threw {ex.GetType().Name}: "
                    + $"{ex.Message}. Teardown continues, since the releases below are what free the device.");
            }

            _infoQueue?.Dispose();
            _swapchain?.Dispose();
            _pointSampler.Dispose();
            _linearSampler.Dispose();
            _fences.Dispose();

            // LAST, so every release above ran against a live device and every straggler after this one is the
            // quiet no-op decision X3 promises.
            _liveness.MarkDead();
        }

        /// <summary>
        /// Release the device. Runs the engine-owned teardown first (idempotent, so the ordinary path where
        /// <see cref="GpuDeviceContext"/> already called <see cref="MarkDeviceDisposed"/> pays nothing) and then
        /// destroys the immediate context and the <c>ID3D11Device</c>.
        /// <para>
        /// Both halves are here rather than only the second, because this type is also disposed on paths that
        /// never reach the lifecycle hook: adoption refusing a device it was handed, and a creation that fails
        /// after the device object exists.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            MarkDeviceDisposed();

            if (_released) return;
            _released = true;

            ReleaseNativeWindows();
        }

        // ClearState before the release, so the context is not holding a reference to anything the release is
        // about to drop, and Flush so the driver is handed whatever is still buffered rather than discovering it
        // during destruction. This is the incumbent's shape (Veldrid's D3D11 device does the same pair) and it is
        // the last thing this device does.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void ReleaseNativeWindows()
        {
            try
            {
                _context.ClearState();
                _context.Flush();
            }
            catch (Exception ex)
            {
                log.Warn($"Clearing the native Direct3D 11 immediate context at teardown threw "
                    + $"{ex.GetType().Name}: {ex.Message}. The device is released anyway.");
            }

            _context.Dispose();
            _device.Dispose();
        }
    }
}
