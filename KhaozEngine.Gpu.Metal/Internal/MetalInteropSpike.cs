using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// What the interop spike measured. Every field is an answer to a question section 3.1 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> asks and refuses to assert, because a wrong
    /// ABI assumption in hand-rolled Objective-C interop is a memory corruption rather than a compile error.
    /// </summary>
    internal sealed record MetalInteropSpikeResult
    {
        internal bool DeviceCreated { get; init; }
        internal string DeviceName { get; init; } = "";
        internal string DeviceClassName { get; init; } = "";
        internal string SupportedFamilies { get; init; } = "";
        internal bool BoolIsOneByte { get; init; }
        internal bool CGFloatIsDouble { get; init; }
        internal bool ArraySettersRecorded { get; init; }
        internal bool OffsetSettersRecorded { get; init; }
        internal bool ByValueStructsRecorded { get; init; }
        internal IReadOnlyList<byte> ClearedPixelBgra { get; init; } = Array.Empty<byte>();
        internal bool CompletionHandlerFired { get; init; }
        internal nint CommandBufferStatus { get; init; }
        internal bool CommandBufferErrorWasNil { get; init; }
        internal bool SharedEventCreated { get; init; }
        internal bool SharedEventWaitSucceeded { get; init; }
        internal ulong SharedEventSignaledValue { get; init; }
        internal bool MetalLayerCreated { get; init; }
        internal nuint MaximumDrawableCountReadBack { get; init; }
        internal bool DebugLayerAlreadyInEnvironment { get; init; }
        internal bool InProcessEnvReachesValidationLayer { get; init; }
        internal string ValidationProbeDeviceClassName { get; init; } = "";
        internal IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        /// <summary>A flat, readable transcript of the whole measurement, for the test's failure message and for
        /// the record the design asks row 1 to leave behind.</summary>
        internal string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("device created: " + DeviceCreated);
            sb.AppendLine("device name: " + DeviceName);
            sb.AppendLine("device class: " + DeviceClassName);
            sb.AppendLine("supportsFamily: " + SupportedFamilies);
            sb.AppendLine("BOOL is one byte: " + BoolIsOneByte);
            sb.AppendLine("CGFloat is double: " + CGFloatIsDouble);
            sb.AppendLine("array setters recorded: " + ArraySettersRecorded);
            sb.AppendLine("offset setters recorded: " + OffsetSettersRecorded);
            sb.AppendLine("by-value structs recorded: " + ByValueStructsRecorded);
            sb.AppendLine("cleared pixel read back (B,G,R,A): " + (ClearedPixelBgra.Count == 0
                ? "(not read)"
                : string.Join(", ", ClearedPixelBgra)));
            sb.AppendLine("UnmanagedCallersOnly completion handler fired: " + CompletionHandlerFired);
            sb.AppendLine("command buffer status: " + CommandBufferStatus + " (4 = Completed)");
            sb.AppendLine("command buffer error was nil: " + CommandBufferErrorWasNil);
            sb.AppendLine("MTLSharedEvent created: " + SharedEventCreated);
            sb.AppendLine("MTLSharedEvent wait succeeded: " + SharedEventWaitSucceeded);
            sb.AppendLine("MTLSharedEvent signaledValue: " + SharedEventSignaledValue);
            sb.AppendLine("CAMetalLayer created: " + MetalLayerCreated);
            sb.AppendLine("maximumDrawableCount read back: " + MaximumDrawableCountReadBack);
            sb.AppendLine("MTL_DEBUG_LAYER already in environment: " + DebugLayerAlreadyInEnvironment);
            sb.AppendLine("in-process setenv reaches the validation layer: " + InProcessEnvReachesValidationLayer);
            sb.AppendLine("validation probe device class: " + ValidationProbeDeviceClassName);
            foreach (string note in Notes) sb.AppendLine("note: " + note);
            return sb.ToString();
        }
    }

    /// <summary>
    /// VERIFICATION TASK ONE of work-breakdown row 1 in
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, compiled and RUN against a real Metal
    /// device. What it covers is every ABI SHAPE the design names, which is not the same as every selector: the
    /// point is that each distinct <c>objc_msgSend</c> prototype crosses correctly, so one representative of a
    /// shape stands for the rest of that shape and row 4's full selector list is row 4's. It is also two files
    /// rather than one, this half and <c>MetalInteropSpike.Native.cs</c>, with the compile-options probe beside
    /// them as its own verification task.
    /// <para>
    /// It is not the interop layer. Row 4 builds that, as a file family under <c>Internal/ObjC/</c> with one file
    /// per Objective-C class. This is the measurement that has to come first, because section 3.1 refuses to
    /// assert any of it: a hand-rolled <c>objc_msgSend</c> layer that gets an argument class wrong does not fail
    /// to compile, it corrupts memory, and the failure surfaces somewhere else entirely. Phase 3's equivalent
    /// spike could only be a COMPILE-time inventory, because the machine that wrote it had no Vulkan loader.
    /// This one runs.
    /// </para>
    /// <para>
    /// WHAT IT ANSWERS, each with the fallback section 3.1 names if the answer is no. The <c>objc_msgSend</c>
    /// return and argument classes, including three by-value struct shapes chosen to cover BOTH paths the arm64
    /// ABI has for a composite argument (registers for an HFA, which is at most four members, and indirect for
    /// everything else). An <c>[UnmanagedCallersOnly]</c> completion handler firing on a real command buffer
    /// (M-F3, fallback: the incumbent's delegate-and-dictionary block shape, losing AOT-cleanliness).
    /// <c>MTLSharedEvent</c>'s four members (M-F1, fallback: a completion-counter timeline, which also removes
    /// M-P4's fifth extraction). The array setters and the offset setters (M-R6 and M-R7, fallback: per-element
    /// binds, losing the whole argument and the budget test's headline marginal). <c>supportsFamily:</c> (M-N3).
    /// <c>maximumDrawableCount</c> (M-W4). And whether in-process environment mutation reaches the validation
    /// layer (M-G3, fallback: a job-level environment variable in CI plus a documented local prefix).
    /// </para>
    /// <para>
    /// THE AUTORELEASE RULE IS HONOURED HERE RATHER THAN LEFT TO ROW 4 (M-N4's family). Most of what this method
    /// touches comes back autoreleased (<c>commandBuffer</c>, <c>renderCommandEncoderWithDescriptor:</c>,
    /// <c>renderPassDescriptor</c>, the texture descriptor), so the whole body sits inside one
    /// <c>objc_autoreleasePoolPush</c> and <c>Pop</c> pair, and everything that arrives at +1 through a
    /// <c>new*</c> or <c>alloc</c> selector is released by hand. Autorelease discipline that is a habit rather
    /// than a rule accumulates under a frame loop, and a spike that leaked would be teaching the wrong habit to
    /// the row that copies it.
    /// </para>
    /// </summary>
    internal static unsafe partial class MetalInteropSpike
    {
        // Metal enum values the spike needs, by number rather than through an enum type, because the enums
        // themselves are row 4's and declaring half of one here would be the start of a second copy.
        const nuint PixelFormatBgra8Unorm = 80;
        const nuint TextureUsageShaderRead = 1;
        const nuint TextureUsageRenderTarget = 4;
        const nuint StorageModePrivate = 2;
        const nuint LoadActionClear = 2;
        const nuint StoreActionStore = 1;

        // The render target's edge, and the clear colour the pass writes into it. Both are read back through the
        // blit below, so they are named once rather than repeated at the two ends of the round-trip.
        const nuint TargetSize = 64;
        const double ClearRed = 0.25, ClearGreen = 0.5, ClearBlue = 0.75, ClearAlpha = 1.0;

        // BLOCK_IS_GLOBAL. A block with no captures needs no copy helper and no dispose helper, so it can live
        // in static native memory for the life of the process and Block_copy on it is a no-op.
        const int BlockIsGlobal = 1 << 28;

        static int _completionCount;

        /// <summary>
        /// The block's invoke slot (M-F3). No delegate, no <c>Marshal.GetFunctionPointerForDelegate</c>, no GC
        /// handle and nothing to keep alive. Metal calls completion handlers on an arbitrary internal thread in
        /// no guaranteed order, which is why this one carries no ordering responsibility and does nothing but
        /// count.
        /// </summary>
        [UnmanagedCallersOnly]
        static void CompletedHandler(IntPtr block, IntPtr commandBuffer)
        {
            _ = block;
            _ = commandBuffer;
            Interlocked.Increment(ref _completionCount);
        }

        /// <summary>
        /// Run the spike against the system default Metal device. Never throws: every question it cannot answer
        /// comes back as a note, because the point is a transcript rather than a pass.
        /// </summary>
        [SupportedOSPlatform("macos")]
        internal static MetalInteropSpikeResult Run()
        {
            var notes = new List<string>();
            IntPtr pool = AutoreleasePoolPush();
            try
            {
                return RunInsidePool(notes);
            }
            finally
            {
                AutoreleasePoolPop(pool);
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalInteropSpikeResult RunInsidePool(List<string> notes)
        {
            // M-G3 FIRST, before anything else in this method touches Metal, because the answer is only
            // meaningful ahead of the framework's own initialisation: MTL_DEBUG_LAYER is read when Metal first
            // comes up in a process, so a probe run after a device already exists can only ever answer no and
            // would be measuring the ordering rather than the mechanism.
            (bool envReaches, string probeClass, bool alreadySet) = ProbeValidationLayer(notes);

            IntPtr device = MTLCreateSystemDefaultDevice();
            if (device == IntPtr.Zero)
            {
                notes.Add("MTLCreateSystemDefaultDevice returned nil, so nothing below could be measured.");
                return new MetalInteropSpikeResult { Notes = notes };
            }

            Interlocked.Exchange(ref _completionCount, 0);

            string deviceClass = CString(ObjectGetClassName(device));
            string deviceName = NSStringToManaged(MsgSend(device, Sel("name")));
            string families = ProbeFamilies(device);

            IntPtr queue = MsgSend(device, Sel("newCommandQueue"));
            IntPtr bufferA = MsgSendPtrNUInt2(device, Sel("newBufferWithLength:options:"), 256, 0);
            IntPtr bufferB = MsgSendPtrNUInt2(device, Sel("newBufferWithLength:options:"), 256, 0);
            IntPtr renderTarget = NewTexture(device, TargetSize, TextureUsageRenderTarget);
            IntPtr readback = MsgSendPtrNUInt2(device, Sel("newBufferWithLength:options:"),
                TargetSize * TargetSize * 4, 0);
            IntPtr sampled = NewTexture(device, 8, TextureUsageShaderRead);
            IntPtr samplerDesc = MsgSend(MsgSend(Cls("MTLSamplerDescriptor"), Sel("alloc")), Sel("init"));
            IntPtr sampler = MsgSendPtr(device, Sel("newSamplerStateWithDescriptor:"), samplerDesc);
            IntPtr sharedEvent = MsgSend(device, Sel("newSharedEvent"));
            if (sharedEvent == IntPtr.Zero) notes.Add("newSharedEvent returned nil (M-F1's fallback applies).");

            IntPtr commandBuffer = MsgSend(queue, Sel("commandBuffer"));
            IntPtr block = GlobalCompletionBlock(notes);
            if (block != IntPtr.Zero)
                MsgSendVoidPtr(commandBuffer, Sel("addCompletedHandler:"), block);

            bool structs = RecordPass(commandBuffer, renderTarget, notes, out IntPtr renderEncoder);
            bool arrays = RecordBinds(commandBuffer, renderEncoder, bufferA, bufferB, sampled, sampler, notes,
                out bool offsets);
            bool blitted = BlitTargetToBuffer(commandBuffer, renderTarget, readback, notes);

            if (sharedEvent != IntPtr.Zero)
                MsgSendVoidPtrULong(commandBuffer, Sel("encodeSignalEvent:value:"), sharedEvent, 42);

            MsgSendVoid(commandBuffer, Sel("commit"));
            MsgSendVoid(commandBuffer, Sel("waitUntilCompleted"));

            nint status = MsgSendNInt(commandBuffer, Sel("status"));
            IntPtr error = MsgSend(commandBuffer, Sel("error"));
            if (error != IntPtr.Zero)
            {
                notes.Add("command buffer error code " + MsgSendNInt(error, Sel("code")) + ": "
                    + NSStringToManaged(MsgSend(error, Sel("localizedDescription"))));
            }

            // The clear colour, read back as bytes. Every OTHER by-value struct answer in this spike is "the
            // device did not reject the call", which cannot tell a correctly passed struct from one whose
            // members landed in the wrong registers and happened not to crash. This one is a VALUE, and it is
            // the register path (MTLClearColor is the only HFA here), so it closes the round-trip on the shape
            // the other two cannot check.
            byte[] cleared = Array.Empty<byte>();
            if (blitted)
            {
                byte* contents = MsgSendBytePtr(readback, Sel("contents"));
                if (contents == null) notes.Add("MTLBuffer.contents came back null, so no pixel was read back.");
                else cleared = new[] { contents[0], contents[1], contents[2], contents[3] };
            }

            bool waited = false;
            ulong signaled = 0;
            if (sharedEvent != IntPtr.Zero)
            {
                waited = MsgSendBoolULongUInt(sharedEvent, Sel("waitUntilSignaledValue:timeoutMS:"), 42, 2000) != 0;
                signaled = MsgSendULong(sharedEvent, Sel("signaledValue"));
            }

            (bool layerMade, nuint drawables, bool boolByte, bool cgFloat) = ProbeMetalLayer(notes);

            ObjcRelease(sharedEvent);
            ObjcRelease(sampler);
            ObjcRelease(samplerDesc);
            ObjcRelease(sampled);
            ObjcRelease(renderTarget);
            ObjcRelease(readback);
            ObjcRelease(bufferB);
            ObjcRelease(bufferA);
            ObjcRelease(queue);
            ObjcRelease(device);

            return new MetalInteropSpikeResult
            {
                DeviceCreated = true,
                DeviceName = deviceName,
                DeviceClassName = deviceClass,
                SupportedFamilies = families,
                BoolIsOneByte = boolByte,
                CGFloatIsDouble = cgFloat,
                ArraySettersRecorded = arrays,
                OffsetSettersRecorded = offsets,
                ByValueStructsRecorded = structs,
                ClearedPixelBgra = cleared,
                CompletionHandlerFired = Volatile.Read(ref _completionCount) > 0,
                CommandBufferStatus = status,
                CommandBufferErrorWasNil = error == IntPtr.Zero,
                SharedEventCreated = sharedEvent != IntPtr.Zero,
                SharedEventWaitSucceeded = waited,
                SharedEventSignaledValue = signaled,
                MetalLayerCreated = layerMade,
                MaximumDrawableCountReadBack = drawables,
                DebugLayerAlreadyInEnvironment = alreadySet,
                InProcessEnvReachesValidationLayer = envReaches,
                ValidationProbeDeviceClassName = probeClass,
                Notes = notes,
            };
        }

        // The three by-value struct shapes, covering BOTH paths the arm64 ABI has for them, which is the whole
        // reason more than one is here. The rule is that a homogeneous floating-point aggregate is at most FOUR
        // members: MTLClearColor is four doubles, so it is an HFA and rides d0 to d3. MTLViewport is SIX doubles,
        // which is one too many to be an HFA however homogeneous it looks, so it is an ordinary composite over
        // 16 bytes and goes indirectly. MTLScissorRect is four NSUIntegers, not floating point at all, so it
        // goes indirectly for the other reason. Two paths, both covered.
        // A layer that gets one right can still get the other wrong, and the debug layer catches neither: what
        // catches them is the command buffer completing without an error, which is what the caller checks.
        //
        // The open encoder is handed OUT rather than parked in a static. Row 4 declares this file its template
        // and row 7 records N encoders concurrently, so a static holding "the" encoder is a shape that has to be
        // unpicked exactly once someone copies it. It is also the difference between a compile error and a
        // silent cross-thread overwrite the day that happens.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool RecordPass(IntPtr commandBuffer, IntPtr renderTarget, List<string> notes, out IntPtr encoder)
        {
            encoder = IntPtr.Zero;
            IntPtr descriptor = MsgSend(Cls("MTLRenderPassDescriptor"), Sel("renderPassDescriptor"));
            IntPtr attachments = MsgSend(descriptor, Sel("colorAttachments"));
            IntPtr attachment = MsgSendPtrNUInt(attachments, Sel("objectAtIndexedSubscript:"), 0);
            if (attachment == IntPtr.Zero)
            {
                notes.Add("colorAttachments[0] came back nil, so no pass could be recorded.");
                return false;
            }

            MsgSendVoidPtr(attachment, Sel("setTexture:"), renderTarget);
            MsgSendVoidNUInt(attachment, Sel("setLoadAction:"), LoadActionClear);
            MsgSendVoidNUInt(attachment, Sel("setStoreAction:"), StoreActionStore);
            MsgSendVoidClearColor(attachment, Sel("setClearColor:"),
                new MTLClearColor { Red = ClearRed, Green = ClearGreen, Blue = ClearBlue, Alpha = ClearAlpha });

            encoder = MsgSendPtr(commandBuffer, Sel("renderCommandEncoderWithDescriptor:"), descriptor);
            if (encoder == IntPtr.Zero)
            {
                notes.Add("renderCommandEncoderWithDescriptor: came back nil.");
                return false;
            }

            MsgSendVoidViewport(encoder, Sel("setViewport:"),
                new MTLViewport { OriginX = 0, OriginY = 0, Width = TargetSize, Height = TargetSize, ZNear = 0, ZFar = 1 });
            MsgSendVoidScissor(encoder, Sel("setScissorRect:"),
                new MTLScissorRect { X = 0, Y = 0, Width = 32, Height = 32 });
            return true;
        }

        // The array setters (M-R6) and the offset setters (M-R7). The array form is the whole argument for the
        // bind flush: one native call per (kind, stage) instead of one per resource per stage, which is the
        // #418 fan-out defect arriving on a second API. The offsets-only form is the shadow pass's shape
        // thousands of times a frame. Neither is declared by the vendored bindings the design rejected, so
        // neither has a reference implementation to copy and both are measured here instead.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool RecordBinds(IntPtr commandBuffer, IntPtr encoder, IntPtr bufferA, IntPtr bufferB,
            IntPtr sampled, IntPtr sampler, List<string> notes, out bool offsetsRecorded)
        {
            offsetsRecorded = false;
            if (encoder == IntPtr.Zero) { notes.Add("no render encoder, so no binds were recorded."); return false; }

            IntPtr* objects = stackalloc IntPtr[2];
            nuint* offsets = stackalloc nuint[2];
            objects[0] = bufferA;
            objects[1] = bufferB;
            offsets[0] = 0;
            offsets[1] = 0;
            var twoSlots = new NSRange { Location = 0, Length = 2 };
            var oneSlot = new NSRange { Location = 0, Length = 1 };

            MsgSendVoidArrayRange(encoder, Sel("setVertexBuffers:offsets:withRange:"), objects, offsets, twoSlots);
            MsgSendVoidArrayRange(encoder, Sel("setFragmentBuffers:offsets:withRange:"), objects, offsets, oneSlot);

            IntPtr* one = stackalloc IntPtr[1];
            one[0] = sampled;
            MsgSendVoidObjectsRange(encoder, Sel("setFragmentTextures:withRange:"), one, oneSlot);
            one[0] = sampler;
            MsgSendVoidObjectsRange(encoder, Sel("setFragmentSamplerStates:withRange:"), one, oneSlot);

            MsgSendVoidNUInt2(encoder, Sel("setVertexBufferOffset:atIndex:"), 128, 0);
            MsgSendVoidNUInt2(encoder, Sel("setFragmentBufferOffset:atIndex:"), 128, 0);
            MsgSendVoid(encoder, Sel("endEncoding"));

            // The compute siblings, on their own encoder because Metal allows exactly one open at a time.
            IntPtr compute = MsgSend(commandBuffer, Sel("computeCommandEncoder"));
            if (compute == IntPtr.Zero) { notes.Add("computeCommandEncoder came back nil."); return false; }
            objects[0] = bufferA;
            objects[1] = bufferB;
            MsgSendVoidArrayRange(compute, Sel("setBuffers:offsets:withRange:"), objects, offsets, twoSlots);
            MsgSendVoidNUInt2(compute, Sel("setBufferOffset:atIndex:"), 128, 0);
            MsgSendVoid(compute, Sel("endEncoding"));

            offsetsRecorded = true;
            return true;
        }

        // The blit that turns the clear colour from a call that was accepted into a value that was measured.
        // MTLClearColor rides d0 to d3 as an HFA, so nothing on the indirect path can stand in for it: this is
        // the one shape whose members could land in the wrong registers, produce a plausible pass, and be
        // invisible until a golden run. Copying the stored attachment into a Shared buffer and reading four
        // bytes is what makes it visible here instead.
        //
        // The blit selector is also the harshest ABI exercise in the file in its own right: two 24-byte
        // composites (MTLOrigin and MTLSize) passed indirectly with scalars on both sides of them, so getting
        // either one wrong shifts every argument after it.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool BlitTargetToBuffer(IntPtr commandBuffer, IntPtr renderTarget, IntPtr readback,
            List<string> notes)
        {
            if (readback == IntPtr.Zero) { notes.Add("the readback buffer could not be created."); return false; }

            IntPtr blit = MsgSend(commandBuffer, Sel("blitCommandEncoder"));
            if (blit == IntPtr.Zero) { notes.Add("blitCommandEncoder came back nil."); return false; }

            MsgSendVoidBlitToBuffer(blit,
                Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:"
                    + "destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                renderTarget, 0, 0,
                new MTLOrigin { X = 0, Y = 0, Z = 0 },
                new MTLSize { Width = TargetSize, Height = TargetSize, Depth = 1 },
                readback, 0, TargetSize * 4, TargetSize * TargetSize * 4);
            MsgSendVoid(blit, Sel("endEncoding"));
            return true;
        }

        // supportsFamily: (M-N3), which replaces the deprecated MTLFeatureSet reads two shipped behaviours hang
        // off today. The families are probed by number for the same reason the enum values above are: the enum
        // is row 4's to declare.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ProbeFamilies(IntPtr device)
        {
            (string Name, nint Value)[] families =
            {
                ("Common1", 3001), ("Common2", 3002), ("Common3", 3003),
                ("Apple6", 1006), ("Apple7", 1007), ("Apple8", 1008), ("Apple9", 1009),
                ("Metal3", 5001),
            };
            var supported = new List<string>();
            IntPtr sel = Sel("supportsFamily:");
            foreach ((string name, nint value) in families)
                if (MsgSendBoolNInt(device, sel, value) != 0) supported.Add(name);
            return supported.Count == 0 ? "(none)" : string.Join(", ", supported);
        }

        // maximumDrawableCount (M-W4), plus the two arm64 scalar caveats, all read off a CAMetalLayer built with
        // no window in sight. A headless layer is not a presentable one and this measures nothing about
        // presentation, which section 16 records as having no CI coverage at all: what it measures is that the
        // property exists, round-trips, and that BOOL and CGFloat cross the boundary at the widths the design
        // assumes.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (bool Created, nuint Drawables, bool BoolIsOneByte, bool CGFloatIsDouble) ProbeMetalLayer(List<string> notes)
        {
            if (!NativeLibrary.TryLoad("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", out _))
            {
                notes.Add("QuartzCore would not load, so CAMetalLayer was not probed.");
                return (false, 0, false, false);
            }

            IntPtr layerClass = Cls("CAMetalLayer");
            if (layerClass == IntPtr.Zero) { notes.Add("CAMetalLayer class not found."); return (false, 0, false, false); }
            IntPtr layer = MsgSend(MsgSend(layerClass, Sel("alloc")), Sel("init"));
            if (layer == IntPtr.Zero) { notes.Add("CAMetalLayer alloc/init returned nil."); return (false, 0, false, false); }

            MsgSendVoidNUInt(layer, Sel("setMaximumDrawableCount:"), 3);
            nuint drawables = MsgSendNUInt(layer, Sel("maximumDrawableCount"));

            // BOOL: set false then true and read both back. A wrong width shows up as a value that is neither.
            MsgSendVoidBool(layer, Sel("setFramebufferOnly:"), 0);
            bool readFalse = MsgSendBool(layer, Sel("framebufferOnly")) == 0;
            MsgSendVoidBool(layer, Sel("setFramebufferOnly:"), 1);
            bool readTrue = MsgSendBool(layer, Sel("framebufferOnly")) == 1;

            // CGFloat: a double on 64-bit, so this rides a SIMD register in both directions.
            MsgSendVoidDouble(layer, Sel("setContentsScale:"), 2.0);
            bool scale = Math.Abs(MsgSendDouble(layer, Sel("contentsScale")) - 2.0) < 1e-12;

            ObjcRelease(layer);
            return (true, drawables, readFalse && readTrue, scale);
        }

        // M-G3: can this process turn the Metal API validation layer on for itself? .NET keeps its own copy of
        // the environment on Unix and never writes through, so Environment.SetEnvironmentVariable cannot
        // possibly reach the layer and only a real setenv could. Even that is a race against first framework
        // use, which is why the answer is recorded with the class name that produced it rather than asserted:
        // MTLCreateSystemDefaultDevice hands back an MTLDebugDevice wrapper when the layer is active, and the
        // driver's own class when it is not.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (bool Reaches, string ClassName, bool AlreadySet) ProbeValidationLayer(List<string> notes)
        {
            string? inherited = Environment.GetEnvironmentVariable("MTL_DEBUG_LAYER");
            bool alreadySet = !string.IsNullOrEmpty(inherited);
            fixed (byte* name = Ascii("MTL_DEBUG_LAYER"))
            fixed (byte* value = Ascii("1"))
            {
                if (SetEnv(name, value, 1) != 0) notes.Add("setenv(MTL_DEBUG_LAYER) failed.");
            }

            IntPtr probe = MTLCreateSystemDefaultDevice();
            string className = probe == IntPtr.Zero ? "" : CString(ObjectGetClassName(probe));
            if (probe != IntPtr.Zero) ObjcRelease(probe);

            // PUT THE ENVIRONMENT BACK THE MOMENT THE PROBE HAS BEEN CLASSIFIED, before anything else in this
            // process creates a device. The reading above depends on MTL_DEBUG_LAYER being consulted once, when
            // Metal first comes up, and that is true of this OS. It is not a guarantee: an OS that read it per
            // device creation would leave this one probe having quietly put the WHOLE test assembly under API
            // validation, which changes timing, allocation and error reporting for every row after it. That is
            // an expensive, hard-to-attribute failure to inherit, so the variable does not outlive its probe.
            //
            // RESTORED rather than blanket-unset, which is the stronger form of the same rule. The native CI leg
            // arms validation at job level, so unsetting an INHERITED value would silently disarm the gate this
            // spike's own fallback depends on. Restoring puts back exactly what the launcher chose, and unsets
            // only the value this process invented.
            if (alreadySet)
            {
                fixed (byte* name = Ascii("MTL_DEBUG_LAYER"))
                fixed (byte* value = Ascii(inherited!))
                {
                    if (SetEnv(name, value, 1) != 0) notes.Add("restoring the inherited MTL_DEBUG_LAYER failed.");
                }
            }
            else
            {
                fixed (byte* name = Ascii("MTL_DEBUG_LAYER"))
                {
                    if (UnsetEnv(name) != 0) notes.Add("unsetenv(MTL_DEBUG_LAYER) failed.");
                }
            }

            if (probe == IntPtr.Zero)
            {
                notes.Add("the validation probe could not create a device.");
                return (false, "", alreadySet);
            }

            // Attribution, not just observation. A debug device when the variable was ALREADY in the environment
            // says the launcher turned the layer on, which is the fallback working rather than the mechanism
            // under test, so the in-process answer is only reported when this process is the only thing that
            // could have set it.
            bool layerActive = className.Contains("Debug", StringComparison.Ordinal);
            if (alreadySet)
            {
                notes.Add("MTL_DEBUG_LAYER was already in the environment, so this run says nothing about "
                    + "whether an in-process mutation would have worked. The layer itself is "
                    + (layerActive ? "active." : "NOT active, which is worth investigating on its own."));
                return (false, className, true);
            }
            if (!layerActive)
            {
                notes.Add("in-process setenv did not reach the validation layer, so M-G3 takes its named "
                    + "fallback: a job-level environment variable in CI plus a documented local prefix. Note "
                    + "that a process which had already used Metal before this ran would answer no for a "
                    + "second reason, so the attributable reading is the one taken with this test alone.");
            }
            return (layerActive, className, false);
        }

        [SupportedOSPlatform("macos")]
        static IntPtr NewTexture(IntPtr device, nuint size, nuint usage)
        {
            IntPtr descriptor = MsgSendPtrTextureDesc(
                Cls("MTLTextureDescriptor"), Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                PixelFormatBgra8Unorm, size, size, 0);
            MsgSendVoidNUInt(descriptor, Sel("setUsage:"), usage);
            MsgSendVoidNUInt(descriptor, Sel("setStorageMode:"), StorageModePrivate);
            return MsgSendPtr(device, Sel("newTextureWithDescriptor:"), descriptor);
        }

        // The block itself: static native memory, never freed, which is correct for a global block rather than
        // sloppy. It has no captures, so Block_copy on it is a no-op and Metal can hold it as long as it likes.
        [SupportedOSPlatform("macos")]
        static IntPtr GlobalCompletionBlock(List<string> notes)
        {
            if (_block != IntPtr.Zero) return _block;

            IntPtr isa = IntPtr.Zero;
            if (NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out IntPtr system))
                NativeLibrary.TryGetExport(system, "_NSConcreteGlobalBlock", out isa);
            if (isa == IntPtr.Zero)
            {
                notes.Add("_NSConcreteGlobalBlock did not resolve, so no completion handler was attached "
                    + "(M-F3's fallback applies).");
                return IntPtr.Zero;
            }

            var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            descriptor->Reserved = 0;
            descriptor->Size = (nuint)sizeof(BlockLiteral);

            var literal = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
            literal->Isa = isa;
            literal->Flags = BlockIsGlobal;
            literal->Reserved = 0;
            literal->Invoke = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&CompletedHandler;
            literal->Descriptor = (IntPtr)descriptor;

            _block = (IntPtr)literal;
            return _block;
        }

        static IntPtr _block;

        // ---- Small helpers ----------------------------------------------------------------------------------

        // ASCII into a heap array rather than a stackalloc, so the fixed statement at the call site owns the
        // lifetime and nothing here depends on inlining decisions. Selector and class names are compile-time
        // constants in this file, so the allocation is bounded and happens once per call on a cold path.
        static byte[] Ascii(string text)
        {
            var bytes = new byte[Encoding.ASCII.GetByteCount(text) + 1];
            Encoding.ASCII.GetBytes(text, bytes);
            return bytes;
        }

        [SupportedOSPlatform("macos")]
        internal static IntPtr Sel(string name)
        {
            fixed (byte* p = Ascii(name)) return SelRegisterName(p);
        }

        [SupportedOSPlatform("macos")]
        internal static IntPtr Cls(string name)
        {
            fixed (byte* p = Ascii(name)) return ObjcGetClass(p);
        }

        static string CString(IntPtr utf8) => utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";

        [SupportedOSPlatform("macos")]
        static string NSStringToManaged(IntPtr nsString) =>
            nsString == IntPtr.Zero ? "" : CString(MsgSend(nsString, Sel("UTF8String")));
    }
}
