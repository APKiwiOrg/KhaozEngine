using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
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
    /// </summary>
    internal sealed partial class MetalGpuDevice
    {
        /// <summary>
        /// Create an OFFSCREEN device with no swapchain, for the headless snapshot and golden paths. The whole of
        /// this row's creation path: everything a windowed device would additionally need (a
        /// <c>CAMetalLayer</c>, a drawable, a present) belongs to row 15.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GpuProviderDevice CreateHeadless()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ReportValidation();

            MetalSelectedDevice selected = MetalDeviceEnumeration.AcquireSelected();
            if (selected.Device.IsNull) throw NoEligibleDevice(selected);

            log.Info(selected.LogLine);
            if (selected.Warning is not null) log.Warn(selected.Warning);

            try
            {
                return Create(selected);
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
        static GpuProviderDevice Create(in MetalSelectedDevice selected)
        {
            MTLDevice device = selected.Device;

            // ONE queue (M-N2), created once. Metal documents MTLCommandQueue as thread-safe, which is what makes
            // lock-free recording true later, and committing under the device's submit lock is what makes submit
            // order the observable order. No second queue and no async compute: #534's argument transfers with
            // the FFT ocean as the same named consumer.
            MTLCommandQueue queue = device.NewCommandQueue();
            if (queue.IsNull) throw NoQueue(selected.Facts.DeviceName);

            var liveness = new MetalDeviceLiveness();
            var loss = new MetalDeviceLossLatch(liveness);

            MetalTimeline? timeline = null;
            bool registered = false;

            try
            {
                // MM4's DEPTH, resolved once per device and reported, so a capture proves the number its
                // backpressure counter was measured against rather than resting on the tester believing they set
                // the variable. Row 8 reads it for the uniform ring and row 15 for maximumDrawableCount.
                int framesInFlight = MetalFramesInFlight.FromEnvironment(out string? unrecognizedFrames);
                if (unrecognizedFrames is not null)
                    log.Warn(MetalFramesInFlight.UnrecognizedWarning(unrecognizedFrames));
                log.Info(MetalFramesInFlight.ActiveDescription(framesInFlight));

                // THE DEVICE'S ONE COMPLETION TIMELINE (M-F1), created before anything can submit. Row 5 built
                // it and left the wiring to the first row with a submit path, which is this one.
                timeline = new MetalTimeline(new MetalSharedEvent(device.Handle), liveness);

                // AND THE ROUTE M-F2's HANDLER DELIVERS THROUGH, keyed on the QUEUE rather than the MTLDevice,
                // which is a measurement rather than a preference: MTLCreateSystemDefaultDevice is a per-GPU
                // process singleton, so two engine devices on one GPU are indistinguishable by device pointer.
                MetalCompletionHandler.Register(queue.Handle, new MetalCompletionErrorRoute(loss));
                registered = true;

                MetalGpuDevice created = new(device, queue, ReadCapabilities(selected.Facts), liveness, loss,
                    timeline, new MetalUncommittedBuffers(framesInFlight));

                // THE ARMING IS CHECKED AGAINST THE DEVICE ITSELF, through row 1's own control: a validated
                // device is an MTLDebugDevice rather than the driver's class. Done here because it is the first
                // moment a real device exists to ask, and skipped entirely on an unvalidated run so an ordinary
                // session gains no line.
                ReportDeviceClass(device);

                // No threading probe and no threading failure. That pair exists because a natively created
                // Direct3D 11 device has no Veldrid GraphicsDevice for D3D11ThreadingProbe to read a raw pointer
                // off. Metal has no D3D11_FEATURE_DATA_THREADING analogue at all, so there is nothing to ask and
                // two nulls are the honest answer rather than a gap (4.2's first two rows).
                return new GpuProviderDevice(created, ThreadingCaps: null, ThreadingProbeFailure: null);
            }
            catch
            {
                // Between -newCommandQueue and the constructor taking over, this method holds a +1 queue nothing
                // else knows about, a +1 shared event behind the timeline, and possibly a slot in the four-slot
                // completion table. The device is the caller's to release on this path. Unwound in the reverse
                // order of acquisition, and the registration goes first because it is the only one of the three
                // that another thread can reach.
                if (registered) MetalCompletionHandler.Unregister(queue.Handle);
                timeline?.Dispose();
                queue.Release();
                throw;
            }
        }

        // Section 14's table, filled to the extent a device with no renderer on it can answer honestly. Row 16
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/582) owns the rest and the ZERO-permitted-difference
        // parity test that pins all of it.
        static GpuCapabilities ReadCapabilities(in MetalDeviceFacts facts)
            => new(
                // FALSE, and with no viewport trick needed, unlike the Vulkan sibling: Metal's clip space matches
                // the engine's already (7.3).
                clipSpaceYInverted: false,
                depthRangeZeroToOne: true,
                // VERBATIM and never trimmed. Section 14 inherits that from phase 3 by name: the incumbent takes
                // -name as it comes, so a trim on the native path alone would fail parity on any device whose
                // reported name carries padding.
                deviceName: facts.DeviceName,
                // TRUE, hardcoded, reproducing the incumbent's GraphicsDeviceFeatures(samplerAnisotropy: true).
                samplerAnisotropy: true,
                // FALSE, and it is the one capability that differs from BOTH other native backends, because
                // MTLSamplerDescriptor has no LOD bias at all. Identical to the incumbent, which is the bar.
                samplerLodBias: false,
                // PINNED TO 1 rather than computed. M-C3 says the incumbent's own computation is what row 16
                // reproduces, and a formula invented here would be a silent lie AntiAliasing.ResolveFor acts on.
                maxMsaaSampleCount: 1,
                // The incumbent's own QUESTION is "is R32_Float usable as both render target and sampled", and
                // row 16 asks exactly that. Until then this reports the seam's own default rather than an answer
                // derived from what the member's NAME suggests, which is the phase-3 correction section 14
                // inherits by name.
                supportsShadowMaps: true,
                supportsCompute: true,
                // TRUE, and it was already true: VeldridMap answers true for GraphicsBackend.Metal, so M-F4 is
                // parity here rather than the upgrade it was on Direct3D 11. The mechanism behind it is the
                // timeline row's (https://github.com/APKiwiOrg/KhaozEngine/issues/571).
                supportsCompletionFences: true);

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
            log.Info(MetalValidation.ActiveDescription(arming.Armed, className));

            if (!MetalValidation.LooksLikeADebugDevice(className))
                log.Warn(MetalValidation.ArmedButNotADebugDeviceWarning(className));
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
