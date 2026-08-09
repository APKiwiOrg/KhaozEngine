using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLGPUFamily</c>, and decision M-N3's whole vocabulary. New capability questions ask
    /// <c>-supportsFamily:</c>, never the <c>-supportsFeatureSet:</c> the incumbent enumerates: that selector has
    /// been deprecated since macOS 10.15, and <c>MTLFeatureSupport.MaxFeatureSet</c> feeds two fragile reads (the
    /// vsync equality test M-W2 removes, and the <c>IsMacOS</c> flag the incumbent derives its uniform-buffer
    /// alignment and its sampler border colour from).
    /// <para>
    /// PARITY SURFACES ARE THE EXCEPTION and reproduce the incumbent's own question (M-C3, section 14), because a
    /// parity surface that asks a DIFFERENT question is a parity failure by construction whatever the new
    /// question's merits.
    /// </para>
    /// <para>
    /// THE UNDERLYING TYPE IS <c>long</c> AND THAT IS EXACT RATHER THAN APPROXIMATE. <c>MTLGPUFamily</c> is an
    /// <c>NSInteger</c>, which is 64 bits on every platform Metal ships on, and C# does not permit <c>nint</c> as
    /// an enum base at all. Getting an enum's width wrong is one of the two mistakes section 2.1 records the
    /// vendored bindings making, so it is written down rather than left to look like a default. The
    /// members are the ones this backend asks about: the Apple generations, <c>Mac2</c>, <c>Common1</c> to
    /// <c>Common3</c> and <c>Metal3</c>. A generation this engine does not ask about is deliberately absent
    /// rather than transcribed, so the list is a statement about what is READ.
    /// </para>
    /// </summary>
    internal enum MTLGPUFamily : long
    {
        /// <summary>Not a family. Zero is not a valid <c>MTLGPUFamily</c>, so it is the safe unset value.</summary>
        None = 0,

        /// <summary><c>MTLGPUFamilyApple1</c>, the lowest Apple generation and the floor's Apple arm.</summary>
        Apple1 = 1001,

        /// <summary><c>MTLGPUFamilyApple9</c>, the highest generation this build knows to ask about. The probe
        /// walks Apple1 upward to here and records the highest yes.</summary>
        Apple9 = 1009,

        /// <summary><c>MTLGPUFamilyMac2</c>, which every Mac GPU on a supported macOS reports and which Apple
        /// silicon reports as well. The floor's other arm.</summary>
        Mac2 = 2002,

        /// <summary><c>MTLGPUFamilyCommon1</c>, the baseline every Metal device shares. Read for the DIAGNOSTIC
        /// rather than for the gate, so a device that answers nothing at all is distinguishable in a log line
        /// from one that simply sits below the floor.</summary>
        Common1 = 3001,

        /// <summary><c>MTLGPUFamilyCommon3</c>, the top of the shared baseline. The spike measured Common1 to
        /// Common3 on an Apple M2 Max.</summary>
        Common3 = 3003,

        /// <summary><c>MTLGPUFamilyMetal3</c>, the feature-set marker Metal 3 devices answer. Not part of the
        /// floor and read for the record, because it is the one family whose absence would matter to a later
        /// row.</summary>
        Metal3 = 5001,
    }

    /// <summary>
    /// <c>MTLPixelFormat</c>, cut down to the ONE member this row asks about. The full map is row 6's
    /// (<c>MetalFormats.Pixel.cs</c>, split by domain the way <c>ShaderSources</c> was), and transcribing it here
    /// would be the second copy the folder rule exists to prevent.
    /// </summary>
    internal enum MTLPixelFormat : ulong
    {
        /// <summary>Not a format.</summary>
        Invalid = 0,

        /// <summary><c>MTLPixelFormatBGRA8Unorm</c>: what the swapchain and every golden readback use, so the
        /// buffer-offset alignment question is asked about the format the engine really binds buffers around
        /// rather than an exotic one.</summary>
        BGRA8Unorm = 80,
    }

    /// <summary>
    /// An <c>MTLDevice</c> handle, and the C entry points that produce one.
    /// <para>
    /// A DISTINCT TYPE FROM A QUEUE, WHICH IS THE POINT OF THE HANDLE FAMILY (M-P2). Everything on the
    /// Objective-C side is <c>id</c>, so a layer built on bare <see cref="IntPtr"/> lets a queue be passed where a
    /// device belongs and the failure is an unrecognised selector at runtime. One readonly struct per protocol
    /// makes that a compile error at no runtime cost.
    /// </para>
    /// <para>
    /// OWNERSHIP IS PART OF THE SIGNATURE HERE, because Objective-C's is a naming convention rather than a type.
    /// <see cref="CreateSystemDefault"/> and <see cref="CopyAllDevices"/> both follow the create/copy rule and
    /// hand back a +1 reference the caller must <see cref="Release"/>. <see cref="NewCommandQueue"/> follows the
    /// new rule and does the same. Everything else here is a plain property read that returns nothing owned.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly partial record struct MTLDevice(IntPtr Handle)
    {
        /// <summary>True when there is no device. What a Mac with no usable Metal device answers, and the floor
        /// the incumbent's own support check stops at.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>MTLCreateSystemDefaultDevice()</c>, which is M-N1's DEFAULT and the incumbent's only path. Returns a
        /// +1 device the caller releases, or nil.
        /// <para>
        /// This is what keeps <c>GpuCapabilities.DeviceName</c> parity satisfiable BY CONSTRUCTION under section
        /// 14's zero-permitted-difference bar: the native backend and the incumbent ask the same function for the
        /// same device unless somebody set <c>KE_METAL_DEVICE</c> on purpose.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.MetalFramework, EntryPoint = "MTLCreateSystemDefaultDevice")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr CreateSystemDefault();

        /// <summary>
        /// <c>MTLCopyAllDevices()</c>, the enumeration <c>KE_METAL_DEVICE</c>'s index and name forms walk. Returns
        /// a +1 <c>NSArray</c> the caller releases.
        /// <para>
        /// ASKED ONLY WHEN THE VARIABLE IS SET. An ordinary run never enumerates, which is not an optimisation:
        /// M-N1 says the default IS <c>MTLCreateSystemDefaultDevice()</c>, and taking element zero of this array
        /// instead would be a different choice on a machine where they differ.
        /// </para>
        /// </summary>
        [LibraryImport(ObjCRuntime.MetalFramework, EntryPoint = "MTLCopyAllDevices")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr CopyAllDevices();

        /// <summary>Release this device. Only ever called on a handle that arrived at +1.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary>Retain this device, for a handle borrowed out of an <see cref="NSArray"/> that is about to be
        /// released.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Retain()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRetain(Handle);
        }

        /// <summary><c>-name</c>, VERBATIM and never trimmed. Section 14 inherits that by name from phase 3: the
        /// incumbent takes it as it comes, so a trim on the native path alone would fail parity on any device
        /// whose reported name carries padding.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal string Name() => new NSString(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("name"))).ToManaged();

        /// <summary>The runtime's class name for this device. The control behind M-G3: an ordinary device is
        /// <c>AGXG14CDevice</c> on Apple silicon and a validated one is <c>MTLDebugDevice</c>, which is how the
        /// spike proved that in-process environment mutation does NOT arm the debug layer.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal string ClassName() => ObjCRuntime.ClassNameOf(Handle);

        /// <summary><c>-newCommandQueue</c>: a +1 queue the caller releases, or nil. ONE per device (M-N2), and
        /// no second queue anywhere in this backend.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLCommandQueue NewCommandQueue()
            => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("newCommandQueue")));

        /// <summary><c>-supportsFamily:</c> (M-N3).</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool SupportsFamily(MTLGPUFamily family)
            => ObjCMsgSend.SendBoolNInt(Handle, ObjCRuntime.Sel("supportsFamily:"), (nint)family) != 0;

        /// <summary><c>-supportsTextureSampleCount:</c>, which is the only sample-count query Metal has and where
        /// M-C3's <c>MaxMsaaSampleCount</c> walk starts.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool SupportsTextureSampleCount(nuint count)
            => ObjCMsgSend.SendBoolNUInt(Handle, ObjCRuntime.Sel("supportsTextureSampleCount:"), count) != 0;

        /// <summary><c>-isLowPower</c>. On a dual-GPU Intel Mac this is the integrated GPU. Metal has no
        /// "discrete" flag at all, so <c>KE_METAL_DEVICE=discrete</c> is defined as the NEGATION of this, which
        /// is the only thing the API can honestly answer.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool IsLowPower() => ObjCMsgSend.SendBool(Handle, ObjCRuntime.Sel("isLowPower")) != 0;

        /// <summary><c>-isRemovable</c>: an external GPU on a Thunderbolt enclosure. Read for the log line rather
        /// than for a decision, because a removable device disappearing mid-session is a device loss the latch
        /// reports and not a selection question.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool IsRemovable() => ObjCMsgSend.SendBool(Handle, ObjCRuntime.Sel("isRemovable")) != 0;

        /// <summary><c>-isHeadless</c>: a device driving no display. Read for the log line, because a headless
        /// device is a perfectly good choice for the golden suite and a surprising one for a windowed run.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool IsHeadless() => ObjCMsgSend.SendBool(Handle, ObjCRuntime.Sel("isHeadless")) != 0;

        /// <summary><c>-registryID</c>, the only STABLE identity a Metal device has. Two identical cards report
        /// the same name, so a substitution log line that only quoted names could not say which one it took.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal ulong RegistryId() => ObjCMsgSend.SendULong(Handle, ObjCRuntime.Sel("registryID"));

        /// <summary><c>-respondsToSelector:</c>, which is how this backend asks about a property this macOS may
        /// not have instead of finding out through an unrecognised-selector crash.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool RespondsTo(string selectorName)
            => ObjCMsgSend.SendBoolPtr(Handle, ObjCRuntime.Sel("respondsToSelector:"),
                ObjCRuntime.Sel(selectorName)) != 0;

        /// <summary>A bare <c>NSUInteger</c> property by name, for the alignment queries whose availability is
        /// asked through <see cref="RespondsTo"/> first.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal nuint UIntProperty(string selectorName)
            => ObjCMsgSend.SendNUInt(Handle, ObjCRuntime.Sel(selectorName));

        /// <summary><c>-minimumLinearTextureAlignmentForPixelFormat:</c>: the device's own reported buffer-offset
        /// alignment, which is the closest question Metal answers to the constant-buffer alignment M-N4 asks
        /// for.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal nuint MinimumLinearTextureAlignment(MTLPixelFormat format)
            => ObjCMsgSend.SendNUIntNUInt(Handle,
                ObjCRuntime.Sel("minimumLinearTextureAlignmentForPixelFormat:"), (nuint)format);
    }
}
