using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE COMPLETION HANDLER THE SHARED EVENT DID NOT DELETE (M-F2), and the only thing it does is read
    /// <c>status</c> and <c>error</c> and hand them to the device's latch (M-G4).
    /// <para>
    /// <b>ONE GLOBAL BLOCK FOR THE PROCESS, WITH AN <c>[UnmanagedCallersOnly]</c> INVOKE (M-F3).</b> No
    /// delegate, no <c>Marshal.GetFunctionPointerForDelegate</c>, no GC handle and nothing to keep alive, which
    /// is what keeps the completion path AOT-clean. A block with no captures needs no copy helper and no dispose
    /// helper, so it lives in static native memory for the life of the process, <c>Block_copy</c> on it is a
    /// no-op, and Metal may hold it as long as it likes. Row 1's spike RAN exactly this shape against a real
    /// command buffer, so the named fallback (the incumbent's delegate-and-dictionary block) is not taken.
    /// </para>
    /// <para>
    /// <b>THE BLOCK CARRIES NO STATE, SO THE DEVICE IS READ OFF THE COMMAND BUFFER.</b> A block with a captured
    /// device pointer would be a heap block with copy and dispose helpers, and it would have to outlive every
    /// buffer that references it, so the invoke asks the buffer for its own <c>device</c> instead and looks that
    /// up in the small fixed table below. <b>That table is not the dictionary M-F1 removed.</b> The thing the
    /// design rejects is a lock plus a hash lookup inside the driver's callback, once per FENCE. This is a
    /// lock-free scan of at most four pointer slots, once per COMMAND BUFFER, and it exists because a process
    /// can have more than one live native Metal device (a test assembly creating and disposing headless devices
    /// is the ordinary case) and delivering device A's failure to device B's latch would flip the wrong liveness
    /// token.
    /// </para>
    /// <para>
    /// <b>IT CARRIES NO ORDERING RESPONSIBILITY AT ALL</b>, which is the answer to the fact that Metal delivers
    /// completion handlers on an arbitrary internal thread in no guaranteed order. It takes no lock, sets no
    /// event, advances no counter and returns nothing. Every ordering question on this backend is answered by
    /// <see cref="MetalTimeline"/>'s shared event.
    /// </para>
    /// <para>
    /// <b>NOTHING ESCAPES IT.</b> An exception crossing back into Objective-C terminates the process rather than
    /// unwinding to anything that could report it, so the whole body is wrapped, including the report of the
    /// failure to report.
    /// </para>
    /// </summary>
    internal static unsafe class MetalCompletionHandler
    {
        static readonly ILogger log = Log.For<MetalTimeline>();

        // BLOCK_IS_GLOBAL. See the class note: it is what makes Block_copy a no-op on a literal in static
        // native memory.
        const int BlockIsGlobal = 1 << 28;

        /// <summary>
        /// How many live native Metal devices can have a latch registered at once. Four rather than one because
        /// a test assembly creates and disposes headless devices and row 19's <c>NativeDeviceLifecycle</c>
        /// collection serialises their LIFETIMES rather than proving only one exists, and four rather than
        /// unbounded because the lookup is a linear scan on the completion path and a process with five live GPU
        /// devices has a different problem.
        /// </summary>
        internal const int MaxRegisteredDevices = 4;

        // Registration is cold (device creation and teardown) so it takes a lock. The completion path is hot and
        // takes none: it scans the same two arrays with volatile reads. Pointer-sized elements cannot tear, so
        // the only race left is a completion arriving while a device unregisters, which resolves to "delivered
        // to nobody" because the device slot is cleared first.
        static readonly object _gate = new();
        static readonly IntPtr[] _devices = new IntPtr[MaxRegisteredDevices];
        static readonly IMetalCommandBufferErrorSink?[] _sinks =
            new IMetalCommandBufferErrorSink?[MaxRegisteredDevices];

        static IntPtr _block;
        static int _blockFailureReported;

        /// <summary>
        /// Route completions from <paramref name="device"/>'s command buffers to <paramref name="sink"/>. Called
        /// by row 4's device creation (https://github.com/APKiwiOrg/KhaozEngine/issues/570), inside the
        /// lifecycle lock, and by nothing else.
        /// </summary>
        /// <param name="device">The <c>MTLDevice</c> the sink latches for.</param>
        /// <param name="sink">The latch. See <see cref="IMetalCommandBufferErrorSink"/>.</param>
        internal static void Register(IntPtr device, IMetalCommandBufferErrorSink sink)
        {
            if (device == IntPtr.Zero) throw new ArgumentNullException(nameof(device));
            ArgumentNullException.ThrowIfNull(sink);

            lock (_gate)
            {
                int free = -1;
                for (int i = 0; i < MaxRegisteredDevices; i++)
                {
                    if (_devices[i] == device)
                    {
                        throw new InvalidOperationException(
                            "A native Metal device registered its command-buffer error latch twice. The second "
                            + "registration is refused rather than replacing the first, because a latch that "
                            + "was replaced would stop hearing about the failures of buffers already in flight "
                            + "against it.");
                    }

                    if (free < 0 && _devices[i] == IntPtr.Zero) free = i;
                }

                if (free < 0)
                {
                    throw new InvalidOperationException(
                        $"More than {MaxRegisteredDevices} native Metal devices tried to register a "
                        + "command-buffer error latch at once. The completion path scans this table per "
                        + "command buffer, so it is deliberately small. A process holding this many live GPU "
                        + "devices is leaking them.");
                }

                _sinks[free] = sink;
                Volatile.Write(ref _devices[free], device);
            }
        }

        /// <summary>
        /// Stop routing completions from <paramref name="device"/>. Called by row 4's teardown after the drain,
        /// and safe to call for a device that never registered.
        /// <para>
        /// THE DEVICE SLOT IS CLEARED FIRST so a completion racing the teardown finds no match and is dropped,
        /// which is the correct direction: a buffer completing after its device has been torn down has nothing
        /// left to latch on to.
        /// </para>
        /// </summary>
        /// <param name="device">The <c>MTLDevice</c> that is going away.</param>
        internal static void Unregister(IntPtr device)
        {
            lock (_gate)
            {
                for (int i = 0; i < MaxRegisteredDevices; i++)
                {
                    if (_devices[i] != device) continue;

                    Volatile.Write(ref _devices[i], IntPtr.Zero);
                    _sinks[i] = null;
                    return;
                }
            }
        }

        /// <summary>
        /// Hand <paramref name="outcome"/> to whatever sink is registered for <paramref name="device"/>, or
        /// drop it when none is. The DECIDING half of the completion path, so it is device-free and a
        /// <c>[Fact]</c> drives it with an opaque handle on a machine with no Metal.
        /// <para>
        /// A sink that throws is swallowed and reported, because the caller above is an Objective-C callback and
        /// letting it escape kills the process.
        /// </para>
        /// </summary>
        /// <param name="device">The device the completed buffer belonged to.</param>
        /// <param name="outcome">What its <c>status</c> and <c>error</c> said.</param>
        internal static void Deliver(IntPtr device, in MetalCommandBufferOutcome outcome)
        {
            IMetalCommandBufferErrorSink? sink = null;
            for (int i = 0; i < MaxRegisteredDevices; i++)
            {
                if (Volatile.Read(ref _devices[i]) != device) continue;

                sink = Volatile.Read(ref _sinks[i]);
                break;
            }

            if (sink == null) return;

            try
            {
                sink.CommandBufferCompleted(outcome);
            }
            catch (Exception ex)
            {
                Swallow(ex, "the command-buffer error latch threw while being told a buffer had completed");
            }
        }

        /// <summary>
        /// Register the completion handler on <paramref name="commandBuffer"/>
        /// (<c>addCompletedHandler:</c>). Called by row 7's submit path
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573) for every buffer it commits, before the commit.
        /// </summary>
        /// <param name="commandBuffer">The buffer about to be committed.</param>
        /// <returns>True when the handler was attached. False only when the block could not be built at all, in
        /// which case this device's command-buffer failures will be invisible and the error line naming that has
        /// already been written.</returns>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool AttachTo(IntPtr commandBuffer)
        {
            if (commandBuffer == IntPtr.Zero) throw new ArgumentNullException(nameof(commandBuffer));

            IntPtr block = GlobalBlock();
            if (block == IntPtr.Zero) return false;

            MetalTimelineNative.MsgSendVoidPtr(commandBuffer, Selectors.AddCompletedHandler, block);
            return true;
        }

        /// <summary>
        /// The block's invoke slot (M-F3). Reached only from Objective-C.
        /// <para>
        /// The pool is M-N5's rule arriving on the one path that needs it most: <c>error</c> and
        /// <c>localizedDescription</c> both hand back autoreleased objects, and this runs on a driver thread
        /// whose implicit pool drains at a moment nobody here controls.
        /// </para>
        /// </summary>
        [UnmanagedCallersOnly]
        [SupportedOSPlatform("macos")]
        static void Completed(IntPtr block, IntPtr commandBuffer)
        {
            _ = block;

            IntPtr pool = IntPtr.Zero;
            try
            {
                pool = MetalTimelineNative.AutoreleasePoolPush();
                Deliver(
                    MetalTimelineNative.MsgSend(commandBuffer, Selectors.Device),
                    ReadOutcome(commandBuffer));
            }
            catch (Exception ex)
            {
                Swallow(ex, "the native Metal completion handler threw while reading a command buffer's status");
            }
            finally
            {
                if (pool != IntPtr.Zero) MetalTimelineNative.AutoreleasePoolPop(pool);
            }
        }

        /// <summary>
        /// <c>status</c> and <c>error</c>, copied out into managed values while the Objective-C objects are
        /// still alive (M-G4). Read in EVERY configuration and never behind a <c>[Conditional]</c>, which is
        /// phase 3's lesson: a latch built on checks that compile away in Release never fires.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalCommandBufferOutcome ReadOutcome(IntPtr commandBuffer)
        {
            nint status = MetalTimelineNative.MsgSendNInt(commandBuffer, Selectors.Status);
            IntPtr error = MetalTimelineNative.MsgSend(commandBuffer, Selectors.Error);
            if (error == IntPtr.Zero) return new MetalCommandBufferOutcome(status, 0, "");

            nint code = MetalTimelineNative.MsgSendNInt(error, Selectors.Code);
            string description = MetalTimelineNative.NSStringToManaged(
                MetalTimelineNative.MsgSend(error, Selectors.LocalizedDescription));

            return new MetalCommandBufferOutcome(status, code, description);
        }

        // The literal itself: static native memory, never freed, which is correct for a global block rather than
        // sloppy. Metal can hold it for the life of the process and Block_release on it is a no-op.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static IntPtr GlobalBlock()
        {
            IntPtr existing = Volatile.Read(ref _block);
            if (existing != IntPtr.Zero) return existing;

            lock (_gate)
            {
                if (_block != IntPtr.Zero) return _block;

                IntPtr isa = IntPtr.Zero;
                if (NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out IntPtr system))
                    NativeLibrary.TryGetExport(system, "_NSConcreteGlobalBlock", out isa);

                if (isa == IntPtr.Zero)
                {
                    ReportBlockFailure();
                    return IntPtr.Zero;
                }

                var descriptor = (MetalTimelineNative.BlockDescriptor*)NativeMemory.AllocZeroed(
                    (nuint)sizeof(MetalTimelineNative.BlockDescriptor));
                descriptor->Reserved = 0;
                descriptor->Size = (nuint)sizeof(MetalTimelineNative.BlockLiteral);

                var literal = (MetalTimelineNative.BlockLiteral*)NativeMemory.AllocZeroed(
                    (nuint)sizeof(MetalTimelineNative.BlockLiteral));
                literal->Isa = isa;
                literal->Flags = BlockIsGlobal;
                literal->Reserved = 0;
                literal->Invoke = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Completed;
                literal->Descriptor = (IntPtr)descriptor;

                Volatile.Write(ref _block, (IntPtr)literal);
                return _block;
            }
        }

        // Once per process rather than once per submit, because a per-buffer error line on a path that runs
        // thousands of times a frame is how a real message gets scrolled past.
        static void ReportBlockFailure()
        {
            if (Interlocked.Exchange(ref _blockFailureReported, 1) != 0) return;

            log.Error(
                "The native Metal backend could not resolve _NSConcreteGlobalBlock, so no command-buffer "
                + "completion handler can be attached. Every command buffer still runs and the timeline is "
                + "unaffected, because ordering is the shared event's job, but a command-buffer failure on this "
                + "device will be invisible to the engine and to telemetry. The design's named fallback for "
                + "this is the incumbent's delegate-and-dictionary block shape.");
        }

        // The report of a failure to report. Its own try, because a logger that throws inside a catch inside an
        // Objective-C callback is still a dead process.
        static void Swallow(Exception ex, string what)
        {
            try
            {
                log.Error("The native Metal backend swallowed an exception on the completion path: " + what
                    + ". It is swallowed rather than propagated because this path is entered from Objective-C, "
                    + "where an escaping exception terminates the process.", ex);
            }
            catch (Exception)
            {
                // Nothing left to report with.
            }
        }

        /// <summary>
        /// The selectors, resolved once, in a NESTED type on purpose: the CLR runs a type's initializer on first
        /// access to THAT type, so <see cref="Deliver"/> and the registration members stay reachable on Linux
        /// and Windows while these never resolve there. Putting them on the outer type would run
        /// <c>sel_registerName</c> the first time any member of it was touched, on every platform.
        /// </summary>
        [SupportedOSPlatform("macos")]
        static class Selectors
        {
            internal static readonly IntPtr Device = MetalTimelineNative.Sel("device");
            internal static readonly IntPtr Status = MetalTimelineNative.Sel("status");
            internal static readonly IntPtr Error = MetalTimelineNative.Sel("error");
            internal static readonly IntPtr Code = MetalTimelineNative.Sel("code");
            internal static readonly IntPtr LocalizedDescription =
                MetalTimelineNative.Sel("localizedDescription");
            internal static readonly IntPtr AddCompletedHandler =
                MetalTimelineNative.Sel("addCompletedHandler:");
        }
    }
}
