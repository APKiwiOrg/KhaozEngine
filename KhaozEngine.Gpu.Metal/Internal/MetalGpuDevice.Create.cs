using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE CREATION HALF OF THE NATIVE DEVICE: the <c>KE_METAL_DEVICE</c> selection of M-N1, the ONE queue of
    /// M-N2, the validation report of M-G3, and the liveness token and error latch every later row is handed.
    /// Split from the seam surface because the two are different concerns and because a device that must stay
    /// under the file-size cap has no room for both, which is the split both sibling devices take.
    /// <para>
    /// EVERY BODY HERE SITS INSIDE ONE AUTORELEASE POOL (M-N5). Device creation touches <c>-name</c>,
    /// <c>-isLowPower</c> and the class-name read, all of which return autoreleased objects, and the architecture
    /// test is what keeps that true rather than this paragraph.
    /// </para>
    /// <para>
    /// THE HEADLESS AND WINDOWED PATHS DIFFER BY ONE ARGUMENT, which row 15 chose over two creation methods:
    /// selecting a device, making its queue, resolving MM4's depth and registering the completion route are
    /// identical for both, and a swapchain is one more thing a device may HAVE rather than a different kind of
    /// device. The one asymmetry is where the host view is resolved, which is before any device exists, so a
    /// window this backend cannot present to is refused without allocating anything.
    /// </para>
    /// </summary>
    internal sealed partial class MetalGpuDevice
    {
        /// <summary>
        /// Create an OFFSCREEN device with no swapchain, for the headless snapshot and golden paths. Everything a
        /// windowed device additionally needs (a <c>CAMetalLayer</c>, a drawable, a present) is
        /// <see cref="CreateForWindow"/>'s, and the two share every line except that one.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GpuProviderDevice CreateHeadless() => CreateWith(host: null, syncToVerticalBlank: false);

        /// <summary>
        /// Create a WINDOWED device, with its swapchain over the request's Cocoa window (row 15). The host view is
        /// resolved FIRST, before any device exists, so a window this backend cannot present to costs nothing to
        /// find out about and leaks nothing when it is refused.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        {
            MetalSwapchainHost host = MetalLayerHost.Resolve(request.Window);
            return CreateForHost(host, request.SyncToVerticalBlank);
        }

        /// <summary>
        /// Create a device with a swapchain over an ALREADY RESOLVED host, which is the seam between "turn a
        /// window into a layer" and "build a device over a layer".
        /// <para>
        /// <b>IT EXISTS BECAUSE OF MM7, AND IT IS THE FURTHEST THAT OBSERVATION CAN BE PUSHED.</b> The design
        /// records that not one line of the incumbent's swapchain ran in CI on any leg, ever. A headless runner
        /// cannot produce an <c>NSWindow</c>, and it CAN produce a <c>CAMetalLayer</c>, which row 1's spike
        /// established on a real device. So the window resolution is the only part that has to wait for a windowed
        /// playtest, and everything from the layer down (the configuration, the acquire, the present, the resize
        /// apply, the counters and the teardown) runs against a REAL layer on a real device in the
        /// <c>[GpuFact]</c> suite. Splitting here is what makes that possible.
        /// </para>
        /// </summary>
        /// <param name="host">The layer and its size. OWNERSHIP OF THE LAYER TRANSFERS HERE unconditionally: on
        /// success the device releases it at teardown, and on a throw this method releases it before rethrowing,
        /// so a caller never releases it either way.</param>
        /// <param name="syncToVerticalBlank">The initial vsync value (M-W2).</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GpuProviderDevice CreateForHost(in MetalSwapchainHost host, bool syncToVerticalBlank)
        {
            try
            {
                return CreateWith(host, syncToVerticalBlank);
            }
            catch
            {
                // The resolve handed back a layer at +1 and this method owns it until the swapchain api takes
                // over, which is the last thing construction does. A throw before that would leak the layer, and
                // on the ADOPT path it would leak a reference to the host view's own layer for the life of the
                // process.
                host.Layer.Release();
                throw;
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static GpuProviderDevice CreateWith(MetalSwapchainHost? host, bool syncToVerticalBlank)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ReportValidation();

            MetalSelectedDevice selected = MetalDeviceEnumeration.AcquireSelected();
            if (selected.Device.IsNull) throw NoEligibleDevice(selected);

            log.Info(selected.LogLine);
            if (selected.Warning is not null) log.Warn(selected.Warning);

            try
            {
                return Create(selected, host, syncToVerticalBlank);
            }
            catch
            {
                // The acquisition handed back +1 and this method owns it until the device takes over, so a throw
                // anywhere below would leak the device and everything the driver holds behind it for the rest of
                // the process. Ownership transfers on a SUCCESSFUL construction only.
                selected.Device.Release();
                throw;
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static GpuProviderDevice Create(in MetalSelectedDevice selected, MetalSwapchainHost? host,
            bool syncToVerticalBlank)
        {
            MTLDevice device = selected.Device;

            // ONE queue (M-N2), created once. Metal documents MTLCommandQueue as thread-safe, which is what makes
            // lock-free recording true later, and committing under the device's submit lock is what makes submit
            // order the observable order. No second queue and no async compute: #534's argument transfers with
            // the FFT ocean as the same named consumer.
            MTLCommandQueue queue = device.NewCommandQueue();
            if (queue.IsNull) throw NoQueue(selected.Facts.DeviceName);

            var liveness = new DeviceLiveness();
            var loss = new MetalDeviceLossLatch(liveness);

            // INSIDE THE TRY, and that is not tidiness. MetalSharedEvent's constructor throws when -newSharedEvent
            // answers nil, and built above the try it would throw with the +1 queue held by nobody: the catch that
            // releases the queue would never run and the queue would leak for the life of the process. The whole
            // window between -newCommandQueue and the constructor taking over belongs to this try.
            MetalTimeline? timeline = null;
            bool registered = false;

            try
            {
                // MM4's DEPTH, resolved once per device and reported, so a capture proves the number its
                // backpressure counter was measured against rather than resting on the tester believing they set
                // the variable. The uniform ring's segments and each list's staging arena slots are cut to it,
                // and row 15 reads it for maximumDrawableCount.
                int framesInFlight = MetalFramesInFlight.FromEnvironment(out string? unrecognizedFrames);
                if (unrecognizedFrames is not null)
                    log.Warn(MetalFramesInFlight.UnrecognizedWarning(unrecognizedFrames));
                log.Info(MetalFramesInFlight.ActiveDescription(framesInFlight));

                // THE DEVICE'S ONE COMPLETION TIMELINE (M-F1), created before anything can submit. Row 5 built
                // it and left the wiring to the first row with a submit path, which is row 7.
                timeline = new MetalTimeline(new MetalSharedEvent(device.Handle), liveness);

                // AND THE ROUTE M-F2's HANDLER DELIVERS THROUGH, keyed on the QUEUE rather than the MTLDevice,
                // which is a measurement rather than a preference: MTLCreateSystemDefaultDevice is a per-GPU
                // process singleton, so two engine devices on one GPU are indistinguishable by device pointer.
                // REGISTERED BEFORE THE DEVICE EXISTS, which is what makes the setup batch's own handler live:
                // MetalSetupCommands attaches one at BeginBatch, and until this call that handler had nowhere to
                // deliver, so a failed device-level upload was indistinguishable from a completed one.
                MetalCompletionHandler.Register(queue.Handle, new MetalCompletionErrorRoute(loss));
                registered = true;

                MetalGpuDevice created = new(device, queue, ReadCapabilities(selected.Facts, device), liveness, loss,
                    timeline, new MetalUncommittedBuffers(framesInFlight), new MetalSetupNative(device, queue),
                    framesInFlight, new MetalStagingSource(device),
                    // M-N4's BUFFER-OFFSET ALIGNMENT, carried rather than re-read. The selection already refused
                    // a device reporting 0 or a value M-M3's 256-byte stride is not a multiple of, so this is a
                    // power of two no wider than the stride and the cast cannot lose anything.
                    (uint)selected.Facts.BufferOffsetAlignment,
                    // AND THE BORDER-COLOUR ANSWER, from the same snapshot for the same reason: the probe already
                    // asked supportsFamily: for Mac2 to clear the floor, and a sampler cannot ask a device this
                    // question safely (building one to find out IS the abort under MTL_DEBUG_LAYER=1).
                    MetalSamplerPolicy.DeviceSupportsBorderColor(selected.Facts));

                // THE SHARED SAMPLER PAIR, from MetalSharedSamplers and not from the engine's same-named
                // GpuSamplerDescription statics. Both are WRAP on all three axes, and reading the engine statics
                // instead (which clamp) cost two goldens on the Direct3D 11 leg.
                created.CreateSharedSamplers();

                // THE ARMING IS CHECKED AGAINST THE DEVICE ITSELF, through row 1's own control: a validated
                // device is an MTLDebugDevice rather than the driver's class. Done here because it is the first
                // moment a real device exists to ask, and skipped entirely on an unvalidated run so an ordinary
                // session gains no line.
                ReportDeviceClass(device);

                // THE SWAPCHAIN LAST (row 15), INSIDE THIS TRY, and the position is the same argument the
                // timeline's is: it configures the layer and takes the first drawable, so a throw anywhere in it
                // has to unwind the queue, the timeline and the completion registration exactly as any other
                // failure here does. The layer itself is the CALLER's to release on this path, which is what
                // CreateForWindow's own catch does, because ownership of it transfers to the api this builds.
                if (host is { } resolved) created.AttachSwapchain(resolved, syncToVerticalBlank, framesInFlight);

                // No threading probe and no threading failure. That pair exists because the GPU package holds
                // no device object of its own for D3D11ThreadingProbe to read a raw Direct3D 11 pointer
                // off. Metal has no D3D11_FEATURE_DATA_THREADING analogue at all, so there is nothing to ask and
                // two nulls are the honest answer rather than a gap (4.2's first two rows).
                return new GpuProviderDevice(created, ThreadingCaps: null, ThreadingProbeFailure: null);
            }
            catch
            {
                // Between -newCommandQueue and the constructor taking over, this method holds a +1 queue nothing
                // else knows about, a +1 shared event behind the timeline, and possibly a slot in the completion
                // table. The device is the caller's to release on this path. Unwound in the reverse
                // order of acquisition, and the registration goes first because it is the only one of the three
                // that another thread can reach. The timeline is null when the shared event itself is what threw.
                if (registered) MetalCompletionHandler.Unregister(queue.Handle);
                timeline?.Dispose();
                queue.Release();
                throw;
            }
        }

        // Created at device creation rather than lazily, because the seam exposes them as properties with no
        // failure mode and a lazy pair would need a lock on a path every renderer touches on its first frame.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void CreateSharedSamplers()
        {
            // BOTH ARE WRAP ON ALL THREE AXES, so neither can reach the border-colour refusal whatever the device
            // answers. The flag is passed rather than hardcoded true because this path must not be the one place
            // that decides, and MetalSharedSamplers is where the wrap is stated.
            _pointSampler = MetalSampler.Create(_device, _liveness, MetalSharedSamplers.Point, _supportsBorderColor);

            try
            {
                _linearSampler = MetalSampler.Create(_device, _liveness, MetalSharedSamplers.Linear,
                    _supportsBorderColor);
            }
            catch
            {
                // The point sampler is already a live +1 object nothing else has a reference to.
                _pointSampler.Dispose();
                throw;
            }
        }

        // Section 14's table, and the whole of it as of row 16
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/582). Every DECISION is in MetalCapabilityRead, which
        // has no device in it and is driven device-free on every leg. What is left here is the two reads a device
        // actually answers: its own -name, already in the probe's snapshot, and M-C3's sample-count walk.
        //
        // THE WALK IS TAKEN OFF THE DEVICE RATHER THAN OUT OF THE FACTS SNAPSHOT, deliberately. The snapshot is
        // read for EVERY device on the machine by the KE_METAL_DEVICE enumeration, and a capability nothing in
        // the selection decides on has no business costing six selector sends per candidate. The facts already
        // carry the one sample-count answer the SELECTION needs (supportsTextureSampleCount:1, the floor this
        // walk would otherwise fall through to).
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static GpuCapabilities ReadCapabilities(in MetalDeviceFacts facts, MTLDevice device)
            => MetalCapabilityRead.Assemble(
                facts.DeviceName,
                MetalCapabilityRead.HighestSupportedSampleCount(
                    count => device.SupportsTextureSampleCount((nuint)count)));

        // M-G3's report, taken before any device exists so it describes the PROCESS rather than the moment.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReportValidation()
        {
            MetalValidationArming arming = MetalValidationReader.Current();

            if (arming.UnrecognizedValue is not null)
                log.Warn(MetalValidation.UnrecognizedWarning(arming.UnrecognizedValue));

            if (arming.DebugLayerSetInProcessOnly)
                log.Warn(MetalValidation.SetInProcessWarning(MetalValidation.DebugLayerVar));

            if (arming.ShaderValidationSetInProcessOnly)
                log.Warn(MetalValidation.SetInProcessWarning(MetalValidation.ShaderValidationVar));

            if (arming.RequestedMoreThanArmed) log.Warn(MetalValidation.NotArmedWarning(arming.Requested));
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReportDeviceClass(MTLDevice device)
        {
            MetalValidationArming arming = MetalValidationReader.Current();
            if (arming.Armed == MetalValidationMode.Off) return;

            string className = device.ClassName();
            log.Info(MetalValidation.ActiveDescription(
                arming.DebugLayerArmed, arming.ShaderValidationArmed, className));

            // ONLY WHEN THE CLASS DISAGREES WITH WHAT WAS ARMED. It used to be "the class does not contain
            // Debug", which fired on every device creation of a MTL_SHADER_VALIDATION-only run (99 of them on
            // run 31874140088) and told the reader to disbelieve a run that was validating
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/628).
            if (MetalValidation.DisagreesWithArming(
                    arming.DebugLayerArmed, arming.ShaderValidationArmed, className))
            {
                log.Warn(MetalValidation.ArmedButWrongDeviceClassWarning(
                    arming.DebugLayerArmed, arming.ShaderValidationArmed, className));
            }
        }

        static InvalidOperationException NoEligibleDevice(in MetalSelectedDevice selected)
            => new("No Metal device on this machine can run the native Metal backend, on a machine whose support "
                + "probe answered yes. " + (selected.NoDeviceDetail ?? "No reason was recorded.")
                + " The probe and this call go through the same acquisition and ask the same requirement method, "
                + "so a disagreement means the machine changed between them.");

        static InvalidOperationException NoQueue(string deviceName)
            => new("The Metal device '" + deviceName + "' would not create a command queue. That is a device "
                + "under resource pressure rather than a device below the floor, since -newCommandQueue is the "
                + "first allocation this backend makes and it takes no arguments to get wrong.");
    }
}
