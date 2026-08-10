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
    }
}
