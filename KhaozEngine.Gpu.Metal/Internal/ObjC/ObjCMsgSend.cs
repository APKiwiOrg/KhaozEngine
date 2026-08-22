using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// THE TYPED <c>objc_msgSend</c> OVERLOAD SET, one prototype per SIGNATURE SHAPE rather than one per
    /// selector. Every handle type in this folder sends its messages through a member of this class, which is
    /// why it is the one file here that is not an Objective-C class: it is the single dispatch function they all
    /// go through.
    /// <para>
    /// THIS IS THE ARM64 STORY, and it is the thing that kills hand-rolled Objective-C interop rather than a
    /// detail of it. <c>objc_msgSend</c> must be called through a prototype matching the real method signature,
    /// because arguments are placed by the CALLER according to the callee's declared types, so one variadic
    /// declaration reused for every selector is the classic corruption. <c>objc_msgSend_stret</c> does not exist
    /// on arm64 at all, so no stret path is written here rather than one being written and disabled. <c>BOOL</c>
    /// is one byte, which is why every boolean crosses as <see cref="byte"/> and never as <see cref="bool"/> (a
    /// <c>bool</c> would also generate the marshalling stub SYSLIB1054 rejects). <c>CGFloat</c> is a double on
    /// 64-bit.
    /// </para>
    /// <para>
    /// EVERY SHAPE HERE WAS MEASURED BEFORE IT WAS USED. Row 1's interop spike compiled and RAN one representative
    /// of every distinct prototype this design names against a real device on an Apple M2 Max under macOS 26, and
    /// the whole set completed in one command buffer with a nil error (section 3.1). That is what a shape means:
    /// one representative stands for every selector sharing its argument classes, so this row adds selectors
    /// without re-measuring, and a row that needs a NEW shape adds the prototype here and says which spike answer
    /// covers it. The by-value struct shapes are the ones that took measuring, and the rule they produced is
    /// that an arm64 homogeneous floating-point aggregate is at most FOUR members: four doubles ride the
    /// registers, six do not, and a composite of integers never does.
    /// </para>
    /// <para>
    /// THE SPIKE KEEPS ITS OWN DECLARATIONS AND THIS SET DOES NOT ABSORB THEM. <c>MetalInteropSpike.Native.cs</c>
    /// is a MEASUREMENT whose value is that it is self-contained: it names the shapes the design asserted, so a
    /// later reader can re-run exactly what was answered rather than whatever the backend has since grown. The
    /// probe's set was a different thing, a temporary duplicate carrying nine declarations verbatim with a
    /// comment saying to delete it, and row 4 deleted it (the handoff on
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/570). The design rules on neither, so the rule taken is
    /// that a duplicate goes and a measurement stays.
    /// </para>
    /// </summary>
    internal static unsafe partial class ObjCMsgSend
    {
        // ---- Object and void returns -----------------------------------------------------------------------

        /// <summary>A bare object-returning message: <c>-name</c>, <c>-newCommandQueue</c>, <c>-commandBuffer</c>,
        /// <c>-error</c>, <c>-localizedDescription</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr Send(IntPtr receiver, IntPtr sel);

        /// <summary>A bare void message: <c>-commit</c>, <c>-waitUntilCompleted</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoid(IntPtr receiver, IntPtr sel);

        /// <summary>An object-returning message taking one object or selector: <c>-objectAtIndex:</c> uses the
        /// index shape below, this one covers the pointer-argument case.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        /// <summary>An object-returning message taking one <c>NSUInteger</c>: <c>-objectAtIndex:</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtrNUInt(IntPtr receiver, IntPtr sel, nuint a);

        /// <summary>An object-returning message taking one <c>NSInteger</c>:
        /// <c>-computeCommandEncoderWithDispatchType:</c>. The signed sibling of the shape above, and it rides
        /// the same spike answer: an integer argument of pointer width goes in a register, and the sign is a
        /// property of how the callee reads it rather than of where the caller puts it.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtrNInt(IntPtr receiver, IntPtr sel, nint a);

        // ---- BOOL returns, which are ONE BYTE ---------------------------------------------------------------

        /// <summary>A bare <c>BOOL</c> property: <c>-isLowPower</c>, <c>-isRemovable</c>, <c>-isHeadless</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte SendBool(IntPtr receiver, IntPtr sel);

        /// <summary><c>-respondsToSelector:</c>: a <c>SEL</c> argument returning <c>BOOL</c>. The one call that
        /// lets this backend ask about a property an older or newer macOS may not have, instead of finding out
        /// through an unrecognised-selector crash.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte SendBoolPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        /// <summary><c>-supportsFamily:</c>, whose argument is an <c>MTLGPUFamily</c> and therefore an
        /// <c>NSInteger</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte SendBoolNInt(IntPtr receiver, IntPtr sel, nint a);

        /// <summary><c>-supportsTextureSampleCount:</c>, whose argument is an <c>NSUInteger</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte SendBoolNUInt(IntPtr receiver, IntPtr sel, nuint a);

        // ---- Integer returns --------------------------------------------------------------------------------

        /// <summary>A bare <c>NSUInteger</c> property: <c>-count</c>, and the constant-buffer alignment query a
        /// future macOS may add.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nuint SendNUInt(IntPtr receiver, IntPtr sel);

        /// <summary><c>-minimumLinearTextureAlignmentForPixelFormat:</c>: an <c>NSUInteger</c> in and an
        /// <c>NSUInteger</c> out.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nuint SendNUIntNUInt(IntPtr receiver, IntPtr sel, nuint a);

        /// <summary>A bare <c>NSInteger</c> property: <c>-status</c> on a command buffer and <c>-code</c> on an
        /// <c>NSError</c>, which are the two reads M-G4's latch is built on.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nint SendNInt(IntPtr receiver, IntPtr sel);

        /// <summary>A bare <c>uint64_t</c> property: <c>-registryID</c>, which is the only stable identity a
        /// Metal device has (its name is not unique on a machine with two of the same card).</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial ulong SendULong(IntPtr receiver, IntPtr sel);
        /// <summary>A void message with one object argument, e.g. <c>-[MTLCommandBuffer addCompletedHandler:]</c>
        /// with a block pointer. Absorbed from row 5's timeline shim at the rows' merge.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        /// <summary>A void message with one object argument and one <c>uint64_t</c>, e.g.
        /// <c>-[MTLCommandBuffer encodeSignalEvent:value:]</c>. Absorbed from row 5's timeline shim.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidPtrULong(IntPtr receiver, IntPtr sel, IntPtr a, ulong b);

        /// <summary>A BOOL message with two <c>uint64_t</c> arguments, which is
        /// <c>-[MTLSharedEvent waitUntilSignaledValue:timeoutMS:]</c>: both widths are 64 bits per the SDK, the
        /// width row 5's review pinned. Absorbed from row 5's timeline shim.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte SendBoolULongULong(IntPtr receiver, IntPtr sel, ulong a, ulong b);

        // ---- The resource row's shapes ----------------------------------------------------------------------
        //
        // Every one of these is an existing argument CLASS in a new arrangement rather than a new class, except
        // the last, which is the only prototype in this file whose ARGUMENT PLACEMENT is not covered by row 1's
        // spike. Each says which spike answer stands behind it.

        /// <summary>A void message taking one <c>NSUInteger</c>: every descriptor setter this backend calls, and
        /// every Metal enum with it, because <c>MTLPixelFormat</c>, <c>MTLTextureType</c>, <c>MTLTextureUsage</c>,
        /// <c>MTLStorageMode</c> and the four sampler enums are all <c>NSUInteger</c>. The integer-argument class
        /// row 1 measured, with one of them.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidNUInt(IntPtr receiver, IntPtr sel, nuint a);

        /// <summary>
        /// A void message taking one <c>float</c>, which is <c>-setLodMinClamp:</c> and <c>-setLodMaxClamp:</c>.
        /// <para>
        /// A SINGLE-PRECISION ARGUMENT RIDES THE VECTOR REGISTERS, which is the same register file
        /// <c>MTLClearColor</c>'s four doubles crossed in on row 1's spike, and that one had its VALUE checked
        /// rather than only its acceptance (the readback of <c>(191, 128, 64, 255)</c> in section 3.1). Metal's
        /// LOD clamps are <c>float</c> and not <c>CGFloat</c>, so this is genuinely 32 bits and not the double a
        /// <c>CGFloat</c> would be.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidFloat(IntPtr receiver, IntPtr sel, float a);

        /// <summary>An object-returning message taking two <c>NSUInteger</c>s, which is
        /// <c>-[MTLDevice newBufferWithLength:options:]</c>.</summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtrNUIntNUInt(IntPtr receiver, IntPtr sel, nuint a, nuint b);

        /// <summary>
        /// <c>-[MTLBlitCommandEncoder copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:]</c>,
        /// and <b>the one prototype here whose ARGUMENT PLACEMENT row 1's spike does not cover</b>.
        /// <para>
        /// Eleven arguments counting the receiver and the selector, against eight general-purpose argument
        /// registers, so the last three cross ON THE STACK. Every argument CLASS is measured: an object pointer
        /// and an <c>NSUInteger</c> are the integer class the spike used throughout, and a 24-byte integer
        /// composite is passed indirectly, which is the arm <c>MTLScissorRect</c> measured. What is new is the
        /// spill, and the runtime performs that lowering itself from a correct managed signature. See
        /// <see cref="MTLBlitCommandEncoder"/> for the whole argument and for the test that answers it on a
        /// device.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidBufferToTextureCopy(IntPtr receiver, IntPtr sel, IntPtr sourceBuffer,
            nuint sourceOffset, nuint sourceBytesPerRow, nuint sourceBytesPerImage, MTLSize sourceSize,
            IntPtr destinationTexture, nuint destinationSlice, nuint destinationLevel,
            MTLOrigin destinationOrigin);

        // ---- The shader row's shapes -------------------------------------------------------------------------
        //
        // Both are existing argument CLASSES in a new arrangement. An object pointer and a BOOL byte are what row
        // 1's spike measured throughout, and an out-parameter is an object pointer's address, which is the same
        // integer class in a register.

        /// <summary>
        /// A void message taking one <c>BOOL</c>: <c>-[MTLCompileOptions setFastMathEnabled:]</c> and
        /// <c>-setPreserveInvariance:</c>.
        /// <para>
        /// THE ARGUMENT IS A <see cref="byte"/> FOR THE REASON THIS FILE'S HEADER GIVES, and it matters more on
        /// the way IN than on the way out. <c>BOOL</c> is a signed char on arm64, so a caller that declared it as
        /// a four-byte value would leave the upper bytes of the register undefined, and a callee reading one byte
        /// would be right by luck. Declaring the real width is what makes it right by construction.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidBool(IntPtr receiver, IntPtr sel, byte a);

        /// <summary>
        /// An object-returning message taking two objects and an <c>NSError**</c>, which is
        /// <c>-[MTLDevice newLibraryWithSource:options:error:]</c>.
        /// <para>
        /// THE ERROR IS AN <c>out</c> OBJECT POINTER, and it is the reason this shape is here rather than
        /// <see cref="SendPtr"/> being reused with a hand-pinned address. Metal reports a shader compile failure
        /// by returning nil AND writing an autoreleased <c>NSError</c> through this parameter, and the message
        /// inside it is the entire diagnostic value of a failed compile: without it a broken shader is "nil" with
        /// no line number. The pointer is only valid until the enclosing autorelease pool drains, which is why
        /// every caller reads it into a managed string inside the pool.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtrPtrPtrOutPtr(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b,
            out IntPtr error);

        /// <summary>
        /// An object-returning message taking one C string, which is
        /// <c>+[NSString stringWithUTF8String:]</c> and the only <c>byte*</c> argument this backend sends.
        /// <para>
        /// A <c>const char*</c> IS AN ORDINARY POINTER ARGUMENT and rides the same register class as an object
        /// pointer, so this needs no spike answer of its own beyond the one <see cref="SendPtr"/> already has.
        /// It is spelled <c>byte*</c> rather than <c>string</c> deliberately: a <c>string</c> parameter would
        /// generate the marshalling stub SYSLIB1054 rejects, and it would also hide WHERE the UTF-8 conversion
        /// happens, which is the one thing a caller has to get right.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtrBytes(IntPtr receiver, IntPtr sel, byte* utf8);

        // ---- The uniform-ring row's one shape ----------------------------------------------------------------

        /// <summary>
        /// <c>-[MTLBlitCommandEncoder copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:]</c>, which is
        /// the record-time bulk upload's copy out of the staging arena (M-M8).
        /// <para>
        /// NOTHING NEW ABOUT IT, and that is worth saying beside the prototype above, which IS new. Seven
        /// arguments counting the receiver and the selector, all of them the integer class row 1's spike measured
        /// throughout, so every one rides a general-purpose argument register and nothing spills. It is the same
        /// receiver and the same selector family as the buffer-to-texture copy, with the argument list that does
        /// not reach the interesting part of the ABI.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidBufferToBufferCopy(IntPtr receiver, IntPtr sel, IntPtr sourceBuffer,
            nuint sourceOffset, IntPtr destinationBuffer, nuint destinationOffset, nuint size);

        // ---- The pipeline row's one shape ---------------------------------------------------------------------

        /// <summary>
        /// An object-returning message taking ONE object and an <c>NSError**</c>, which is
        /// <c>-[MTLDevice newRenderPipelineStateWithDescriptor:error:]</c> and
        /// <c>-[MTLDevice newComputePipelineStateWithFunction:error:]</c>.
        /// <para>
        /// THE ONE-OBJECT SIBLING OF <see cref="SendPtrPtrPtrOutPtr"/>, and it is here rather than that one being
        /// reused with a nil filler because the argument COUNT is part of the prototype: a caller that passed an
        /// extra register would be placing this callee's <c>NSError**</c> one slot along from where it reads it,
        /// which is the classic corruption this file's header names. Every argument class is the integer class
        /// row 1's spike measured, and an out-parameter is an object pointer's address, so nothing here needs a
        /// new spike answer.
        /// </para>
        /// <para>
        /// AND THE ERROR IS THE WHOLE DIAGNOSTIC VALUE OF A REJECTED PIPELINE, exactly as it is for a rejected
        /// shader. Metal answers nil and writes an autoreleased <c>NSError</c> naming the incompatibility (a
        /// vertex attribute the function does not declare, an attachment format the function does not write), and
        /// without it a mismatched pipeline is an unexplained nil. The pointer is valid only until the enclosing
        /// pool drains, so every caller reads it into a managed string inside the pool.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SendPtrPtrOutPtr(IntPtr receiver, IntPtr sel, IntPtr a, out IntPtr error);

        // ---- The render-pass row's shapes -------------------------------------------------------------------
        //
        // One of the four is genuinely new (the four-double HFA), and it is the ONE shape in this whole file
        // whose VALUE row 1's spike checked rather than only its acceptance. The other three are existing
        // argument classes in new arrangements.

        /// <summary>
        /// A void message taking one <c>MTLClearColor</c>, which is
        /// <c>-[MTLRenderPassColorAttachmentDescriptor setClearColor:]</c> and the one call M-A2's whole
        /// per-attachment fix is expressed through.
        /// <para>
        /// FOUR DOUBLES IS EXACTLY THE arm64 HFA LIMIT, so this rides <c>d0</c> to <c>d3</c> and is the only
        /// by-value composite in this backend that does. Row 1's spike measured it by clearing a target to
        /// <c>(0.25, 0.5, 0.75, 1.0)</c> and reading <c>(191, 128, 64, 255)</c> back through a blit, which is the
        /// only answer in the spike that can separate a correctly passed struct from one whose members landed in
        /// the wrong registers and did not happen to fault (section 3.1).
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidClearColor(IntPtr receiver, IntPtr sel, MTLClearColor color);

        /// <summary>
        /// A void message taking one <c>double</c>, which is
        /// <c>-[MTLRenderPassDepthAttachmentDescriptor setClearDepth:]</c>. Metal's depth clear is a
        /// <c>double</c> where the seam's is a <c>float</c>, so the widening happens at the call site and is
        /// exact.
        /// <para>
        /// THE SAME REGISTER FILE <see cref="SendVoidFloat"/> AND <see cref="SendVoidClearColor"/> USE, with one
        /// member instead of one or four, so it needs no spike answer of its own beyond the value-checked one
        /// those two already have.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidDouble(IntPtr receiver, IntPtr sel, double a);

        /// <summary>
        /// A void message taking one <c>uint32_t</c>, which is
        /// <c>-[MTLRenderPassStencilAttachmentDescriptor setClearStencil:]</c> and the only 32-bit integer
        /// argument this backend sends.
        /// <para>
        /// DECLARED AT ITS REAL WIDTH FOR <see cref="SendVoidBool"/>'s REASON, not because 32 bits is awkward: a
        /// caller that widened it would leave the upper half of the register undefined and a callee reading four
        /// bytes would be right by luck. <c>clearStencil</c> is documented <c>uint32_t</c> and not
        /// <c>NSUInteger</c>, so it is the one place in this file where those two differ.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidUInt(IntPtr receiver, IntPtr sel, uint a);

        /// <summary>
        /// A void message taking a POINTER and an <c>NSUInteger</c>, which is
        /// <c>-[MTLRenderCommandEncoder setViewports:count:]</c> and
        /// <c>-[MTLRenderCommandEncoder setScissorRects:count:]</c> (M-A7). One prototype for both, because this
        /// file is one prototype per SIGNATURE SHAPE and those two share theirs exactly.
        /// <para>
        /// <b>IT REMOVES AN ABI QUESTION RATHER THAN ADDING ONE, which is worth saying because the plural form
        /// looks like the more exotic of the two.</b> The singular <c>setViewport:</c> passes six doubles BY
        /// VALUE, which is one member past the HFA limit and therefore an indirect composite, and
        /// <c>setScissorRect:</c> passes four <c>NSUInteger</c>s indirectly for the other reason. Through this
        /// prototype both structs cross as an ARRAY ADDRESS: two plain register arguments, the class row 1's
        /// spike used throughout, with the layout still exact because the driver reads through the pointer.
        /// </para>
        /// <para>
        /// THE COUNT IS ALWAYS 1 (M-A7). The seam has no multi-viewport concept, so the plural form is taken to
        /// retire the incumbent's <c>macOS_GPUFamily1_v3</c> feature-set read on the hot path rather than to
        /// enable anything, and one code path is the whole of the decision.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidPtrNUInt(IntPtr receiver, IntPtr sel, void* values, nuint count);

        /// <summary>
        /// <c>-[MTLBlitCommandEncoder copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:]</c>,
        /// the READBACK mirror of <see cref="SendVoidBufferToTextureCopy"/> and what turns row 12's clear claims
        /// into a pixel read rather than a completed command buffer.
        /// <para>
        /// THIS IS THE ONE PROTOTYPE ROW 1's SPIKE RAN VERBATIM. It is the exact declaration
        /// <c>MetalInteropSpike.Native.cs</c> named <c>MsgSendVoidBlitToBuffer</c> and used to close the by-value
        /// struct round trip: two 24-byte integer composites interleaved with scalars on both sides, eleven
        /// arguments against eight registers so the last three spill, and the value that came back was the
        /// <c>(191, 128, 64, 255)</c> readback section 3.1 records. So its argument placement is measured to a
        /// stronger standard than any other shape in this file.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidTextureToBufferCopy(IntPtr receiver, IntPtr sel, IntPtr sourceTexture,
            nuint sourceSlice, nuint sourceLevel, MTLOrigin sourceOrigin, MTLSize sourceSize,
            IntPtr destinationBuffer, nuint destinationOffset, nuint destinationBytesPerRow,
            nuint destinationBytesPerImage);

        // ---- The bind flush's three shapes -------------------------------------------------------------------
        //
        // Two of the three carry an NSRange BY VALUE, which is a third arm64 argument class this file did not
        // have: sixteen bytes of integers is exactly the boundary, so it rides TWO general-purpose registers
        // rather than going indirectly the way MTLSize and MTLScissorRect do. Row 1's spike sent all three
        // against a real device rather than reasoning about them, which is the standard this file holds a new
        // class to. The third is two plain NSUIntegers and needs no answer of its own.

        /// <summary>
        /// The ARRAY BUFFER SETTER shape, which is <c>-setVertexBuffers:offsets:withRange:</c>,
        /// <c>-setFragmentBuffers:offsets:withRange:</c> and the compute encoder's <c>-setBuffers:offsets:withRange:</c>.
        /// ONE prototype for all three, because this file is one prototype per SIGNATURE SHAPE and the stage
        /// lives in the selector rather than in the argument list.
        /// <para>
        /// M-R6's WHOLE POINT IS THIS PROTOTYPE. One call writes a contiguous run of the stage's buffer table,
        /// where the incumbent emits one call per element per stage: that fan-out is the #418 defect arriving on
        /// a second API, and the vendored fork does not declare a single array setter, so this is the shape the
        /// design says had to be written by hand rather than copied.
        /// </para>
        /// <para>
        /// BOTH ARRAYS ARE CALLER-OWNED AND ARE NOT READ AFTER THE CALL RETURNS. Metal copies
        /// <paramref name="range"/><c>.Length</c> entries out of each during the call and the encoder holds the
        /// bindings as its own state afterwards, which is what makes a <c>stackalloc</c> legal here for the same
        /// reason it is on <see cref="SendVoidPtrNUInt"/>'s viewport array.
        /// </para>
        /// <para>
        /// A NIL ENTRY IN <paramref name="objects"/> IS LEGAL AND UNBINDS THAT INDEX, which is what a disposed
        /// resource degrades to rather than a dereference of a released pointer.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidBuffersRange(IntPtr receiver, IntPtr sel, IntPtr* objects,
            nuint* offsets, NSRange range);

        /// <summary>
        /// The ARRAY OBJECT SETTER shape with NO offsets array, which is
        /// <c>-setVertexTextures:withRange:</c>, <c>-setFragmentTextures:withRange:</c>,
        /// <c>-setVertexSamplerStates:withRange:</c>, <c>-setFragmentSamplerStates:withRange:</c> and the two
        /// compute siblings. Textures and samplers carry no offset because neither index space has one: only the
        /// buffer table binds a window into an allocation.
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidObjectsRange(IntPtr receiver, IntPtr sel, IntPtr* objects,
            NSRange range);

        /// <summary>
        /// THE OFFSETS-ONLY REBIND (M-R7): a void message taking two <c>NSUInteger</c>s, which is
        /// <c>-setVertexBufferOffset:atIndex:</c>, <c>-setFragmentBufferOffset:atIndex:</c> and the compute
        /// encoder's <c>-setBufferOffset:atIndex:</c>.
        /// <para>
        /// NOTHING NEW ABOUT THE ABI and everything new about the cost. Two integer arguments are the class row
        /// 1's spike used throughout, so this needs no answer of its own. What earns it a prototype rather than
        /// a reuse of the array setter is that it writes an INTEGER into the encoder's command stream where
        /// <c>setBuffers:</c> writes a whole argument-table entry, and the engine's hot path is the shadow pass
        /// doing thousands of offsets-only rebinds of one slot per frame.
        /// </para>
        /// <para>
        /// IT REQUIRES A BUFFER ALREADY BOUND AT THAT INDEX, which is a precondition rather than a hint: the
        /// call adjusts an existing binding and has no buffer to adjust otherwise. The flush only reaches it for
        /// a slot whose set is the one it already wrote into the table in this encoder epoch, which is that
        /// precondition expressed as the arm's own guard.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidNUIntNUInt(IntPtr receiver, IntPtr sel, nuint a, nuint b);

        // ---- The draw row's shapes ---------------------------------------------------------------------------
        //
        // ONE of the seven is a genuinely new ARGUMENT PLACEMENT and the header's standard applies to it: an
        // argument that spills onto the STACK in a slot the callee does not read is not a fault, it is a wrong
        // number the driver acts on. See MTLRenderCommandEncoder.DrawIndexedPrimitives for the device probe that
        // answers it by VALUE rather than by acceptance.
        //
        // FOUR of them are TWO PAIRS, a long form and a short form of the same draw, and which one a call takes
        // is decided in MTLRenderCommandEncoder rather than here. The short forms carry no argument the long
        // forms do not, so they add no ABI question at all: what they add is a second code path, and the reason
        // that path exists is https://github.com/APKiwiOrg/KhaozEngine/issues/598.

        /// <summary>
        /// A void message taking four <c>float</c>s, which is
        /// <c>-[MTLRenderCommandEncoder setBlendColorRed:green:blue:alpha:]</c>.
        /// <para>
        /// FOUR SEPARATE SINGLE-PRECISION ARGUMENTS, NOT A COMPOSITE, which is why this needs no new spike answer:
        /// each rides its own vector register (<c>s0</c> to <c>s3</c>), the same register file
        /// <see cref="SendVoidFloat"/>'s single one does and the same file
        /// <see cref="SendVoidClearColor"/>'s value-checked four-double HFA rode. The HFA rule is about a STRUCT
        /// crossing by value and there is no struct here.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidFloat4(IntPtr receiver, IntPtr sel, float a, float b, float c,
            float d);

        /// <summary>
        /// <c>-[MTLRenderCommandEncoder drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:]</c>.
        /// <para>
        /// SEVEN ARGUMENTS COUNTING THE RECEIVER AND THE SELECTOR, all of them the integer class row 1's spike
        /// measured throughout, so every one rides a general-purpose argument register and NOTHING SPILLS. It is
        /// the form taken when the base instance is NON-ZERO, which the incumbent's own rule also is.
        /// <see cref="SendVoidDrawPrimitivesShort"/> is the other half and
        /// <see cref="MTLRenderCommandEncoder.DrawPrimitives"/> carries why the pair exists.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidDrawPrimitives(IntPtr receiver, IntPtr sel, nuint primitiveType,
            nuint vertexStart, nuint vertexCount, nuint instanceCount, nuint baseInstance);

        /// <summary>
        /// <c>-[MTLRenderCommandEncoder drawPrimitives:vertexStart:vertexCount:instanceCount:]</c>, the SHORT
        /// form, taken whenever the base instance is zero.
        /// <para>
        /// SIX ARGUMENTS COUNTING THE RECEIVER AND THE SELECTOR, which is the long form's list with the last
        /// register dropped, so there is no argument class here row 1's spike did not measure and no new
        /// placement question. The whole content of this prototype is the SELECTOR it is sent with, and
        /// <see cref="MTLRenderCommandEncoder.DrawPrimitives"/> is where that choice and its evidence live.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidDrawPrimitivesShort(IntPtr receiver, IntPtr sel,
            nuint primitiveType, nuint vertexStart, nuint vertexCount, nuint instanceCount);

        /// <summary>
        /// <c>-[MTLRenderCommandEncoder drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:]</c>,
        /// and <b>the one prototype this row adds whose ARGUMENT PLACEMENT row 1's spike does not cover</b>.
        /// <para>
        /// TEN ARGUMENTS COUNTING THE RECEIVER AND THE SELECTOR, against eight general-purpose argument registers,
        /// so <c>baseVertex</c> and <c>baseInstance</c> CROSS ON THE STACK. Every argument CLASS is measured (an
        /// object pointer and an <c>NSUInteger</c> are what the spike used throughout, and <c>baseVertex</c> is
        /// their signed sibling, whose sign is a property of how the callee reads the register rather than of
        /// where the caller puts it). What is new is the spill, and it is new in a WORSE shape than row 6's
        /// eleven-argument copy: there the three spilled slots are a copy's extents, so a wrong placement is a
        /// region the driver refuses or a texture that faults. Here they are a vertex base and an instance base,
        /// so a wrong placement is a draw that reads the wrong vertices and completes with a nil error, which is
        /// the wrong-pixel-no-diagnostic class the whole golden gate exists to catch late.
        /// </para>
        /// <para>
        /// SO IT IS ANSWERED BY VALUE RATHER THAN BY ACCEPTANCE.
        /// <c>MetalDrawPathGpuTests.TheSpilledBaseVertexAndIndexOffsetLandWhereTheDriverReadsThem</c> issues one
        /// draw whose <c>indexBufferOffset</c> and <c>baseVertex</c> each select a DIFFERENT triangle out of the
        /// same pair of buffers, and reads the colour back: every wrong placement of either produces a different
        /// texel or no triangle at all. That is the standard row 1 held <see cref="SendVoidClearColor"/> to and
        /// the one this file's header states.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidDrawIndexedPrimitives(IntPtr receiver, IntPtr sel,
            nuint primitiveType, nuint indexCount, nuint indexType, IntPtr indexBuffer, nuint indexBufferOffset,
            nuint instanceCount, nint baseVertex, nuint baseInstance);

        /// <summary>
        /// <c>-[MTLRenderCommandEncoder drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:]</c>,
        /// the SHORT form, taken when the base vertex and the base instance are BOTH zero.
        /// <para>
        /// EIGHT ARGUMENTS COUNTING THE RECEIVER AND THE SELECTOR, against arm64's eight general-purpose argument
        /// registers, so nothing spills and the long form's stack question does not arise here at all. It is the
        /// quieter half of the pair and it is carried for symmetry: the two draws answer the "which selector"
        /// question one way rather than two, which is what keeps a later reader from concluding that the
        /// non-indexed fork was a typo.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidDrawIndexedPrimitivesShort(IntPtr receiver, IntPtr sel,
            nuint primitiveType, nuint indexCount, nuint indexType, IntPtr indexBuffer, nuint indexBufferOffset,
            nuint instanceCount);

        /// <summary>
        /// <c>-[MTLComputeCommandEncoder dispatchThreadgroups:threadsPerThreadgroup:]</c>.
        /// <para>
        /// TWO 24-BYTE INTEGER COMPOSITES BY VALUE, which is <see cref="MTLSize"/>'s indirect path twice, and it
        /// is the arm row 1's spike measured through <c>MTLScissorRect</c> and row 6 sent twice in one call
        /// through the eleven-argument copy. Four arguments counting the receiver and the selector, so the two
        /// pointers the caller supplies ride registers and nothing spills.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidDispatchThreadgroups(IntPtr receiver, IntPtr sel,
            MTLSize threadgroupsPerGrid, MTLSize threadsPerThreadgroup);

        /// <summary>
        /// <c>-[MTLBlitCommandEncoder copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:]</c>,
        /// the texture-to-texture arm of the copy family whose two buffer-side siblings are already here.
        /// <para>
        /// ELEVEN ARGUMENTS WITH THREE ON THE STACK, and three 24-byte integer composites rather than two, which
        /// is the same placement question <see cref="SendVoidTextureToBufferCopy"/> answers and the same one row
        /// 1's spike ran verbatim. The third composite is the destination origin, which is the LAST argument and
        /// therefore one of the spilled ones: it is always <c>(0, 0, 0)</c> on the calls this backend records, so
        /// the device probe for this shape is the mip readback, where a misplaced source origin or size produces
        /// the wrong texels rather than a refusal.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidTextureToTextureCopy(IntPtr receiver, IntPtr sel,
            IntPtr sourceTexture, nuint sourceSlice, nuint sourceLevel, MTLOrigin sourceOrigin, MTLSize sourceSize,
            IntPtr destinationTexture, nuint destinationSlice, nuint destinationLevel,
            MTLOrigin destinationOrigin);

        // ---- The swapchain row's three shapes -----------------------------------------------------------------
        //
        // All three carry a homogeneous floating-point aggregate of at most FOUR doubles, which is the one arm64
        // class row 1's spike checked by VALUE rather than by acceptance (SendVoidClearColor's four-double HFA and
        // the -setContentsScale: round trip). Two of them RETURN one, which is the direction this file did not
        // have: an HFA return lands in d0 to d3 exactly as an HFA argument does, so there is no
        // objc_msgSend_stret to reach for and none exists on arm64 to reach for anyway.

        /// <summary>
        /// A void message taking one <c>CGSize</c>, which is <c>-[CAMetalLayer setDrawableSize:]</c> (M-W1) and
        /// the one call a resize apply is expressed through (M-W7).
        /// <para>
        /// TWO DOUBLES, SO <c>d0</c> AND <c>d1</c>, inside the four-member HFA limit
        /// <see cref="SendVoidClearColor"/> sits exactly on. A wrong placement here writes a drawable size out of
        /// two registers the layer does not read, which is a window that presents at the wrong resolution rather
        /// than a fault, so it is the shape this row's device probe reads BACK rather than only sends.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void SendVoidCGSize(IntPtr receiver, IntPtr sel, CGSize size);

        /// <summary>
        /// A <c>CGSize</c>-returning bare message, which is <c>-[CAMetalLayer drawableSize]</c>. The read half of
        /// the shape above, and what makes the write checkable by value on a real layer.
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial CGSize SendCGSize(IntPtr receiver, IntPtr sel);

        /// <summary>
        /// A <c>CGRect</c>-returning bare message, which is <c>-[NSView frame]</c> and one of the two places this
        /// backend asks a Cocoa object anything at all.
        /// <para>
        /// FOUR DOUBLES COMING BACK, so <c>d0</c> to <c>d3</c>. See <see cref="CGRect"/> for why the incumbent's
        /// architecture branch around <c>objc_msgSend_stret</c> is not reproduced.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial CGRect SendCGRect(IntPtr receiver, IntPtr sel);

        /// <summary>
        /// A bare <c>CGFloat</c>-returning message, which is <c>-[NSWindow backingScaleFactor]</c> and the OTHER
        /// Cocoa read: the number that turns the view frame's POINTS into the drawable's PIXELS
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/605">#605</see>).
        /// <para>
        /// ONE DOUBLE COMING BACK IN <c>d0</c>, the degenerate case of the HFA return
        /// <see cref="SendCGRect"/> already uses, so there is nothing new about the placement and no
        /// <c>objc_msgSend_stret</c> to reach for on arm64 in any case. <c>CGFloat</c> is a double on 64-bit, per
        /// this file's header.
        /// </para>
        /// <para>
        /// AND ROW 1's SPIKE RAN THIS EXACT PROTOTYPE, which is the standard this file holds a shape to. It
        /// declared <c>MsgSendDouble(IntPtr, IntPtr)</c>, set <c>-setContentsScale:</c> on a real
        /// <c>CAMetalLayer</c> to 2.0 and read the property back through it, and the answer was 2.0 (section 3.1).
        /// So this direction is measured BY VALUE rather than by acceptance, on the very quantity it is now used
        /// to read.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial double SendDouble(IntPtr receiver, IntPtr sel);
    }
}
