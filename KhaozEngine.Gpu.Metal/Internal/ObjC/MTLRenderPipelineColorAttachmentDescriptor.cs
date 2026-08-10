using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLBlendFactor</c>, an <c>NSUInteger</c>. The eleven <see cref="GpuBlendFactor"/> can express plus the
    /// two Metal names the seam spells differently, and the dual-source and saturated members are absent for
    /// <see cref="MTLVertexFormat"/>'s reason: the list is a statement about what this backend asks for.
    /// </summary>
    internal enum MTLBlendFactor : ulong
    {
        /// <summary>Zero.</summary>
        Zero = 0,

        /// <summary>One.</summary>
        One = 1,

        /// <summary>Source colour.</summary>
        SourceColor = 2,

        /// <summary>One minus source colour.</summary>
        OneMinusSourceColor = 3,

        /// <summary>Source alpha, which the alpha and additive presets both take.</summary>
        SourceAlpha = 4,

        /// <summary>One minus source alpha, which the alpha preset takes as its destination factor.</summary>
        OneMinusSourceAlpha = 5,

        /// <summary>Destination colour.</summary>
        DestinationColor = 6,

        /// <summary>One minus destination colour.</summary>
        OneMinusDestinationColor = 7,

        /// <summary>Destination alpha.</summary>
        DestinationAlpha = 8,

        /// <summary>One minus destination alpha.</summary>
        OneMinusDestinationAlpha = 9,

        /// <summary>The constant blend colour, which the seam calls <c>BlendFactor</c> and sets with
        /// <c>-setBlendColor:</c> at the encoder.</summary>
        BlendColor = 11,

        /// <summary>One minus the constant blend colour.</summary>
        OneMinusBlendColor = 12,
    }

    /// <summary><c>MTLBlendOperation</c>, an <c>NSUInteger</c>. The full set, and the seam can express all
    /// five.</summary>
    internal enum MTLBlendOperation : ulong
    {
        /// <summary>Source plus destination.</summary>
        Add = 0,

        /// <summary>Source minus destination.</summary>
        Subtract = 1,

        /// <summary>Destination minus source.</summary>
        ReverseSubtract = 2,

        /// <summary>Component-wise minimum.</summary>
        Min = 3,

        /// <summary>Component-wise maximum.</summary>
        Max = 4,
    }

    /// <summary>
    /// <c>MTLColorWriteMask</c>, an <c>NSUInteger</c> of flags, and note that RED IS THE HIGH BIT: Metal orders
    /// this mask alpha, blue, green, red from bit 0 upward, which is the reverse of the reading order the name
    /// suggests.
    /// <para>
    /// ONLY <see cref="All"/> IS EVER SELECTED. The GPU seam carries no colour write mask at all, and
    /// <c>Veldrid.MTL.MTLPipeline</c> reaches the same value through <c>ColorWriteMask.GetOrDefault()</c>, whose
    /// default is every channel. The named members are here so the constant reads as a mask rather than as a
    /// magic 15.
    /// </para>
    /// </summary>
    [Flags]
    internal enum MTLColorWriteMask : ulong
    {
        /// <summary>Write nothing. Never selected.</summary>
        None = 0,

        /// <summary>Alpha, bit 0.</summary>
        Alpha = 1 << 0,

        /// <summary>Blue, bit 1.</summary>
        Blue = 1 << 1,

        /// <summary>Green, bit 2.</summary>
        Green = 1 << 2,

        /// <summary>Red, bit 3.</summary>
        Red = 1 << 3,

        /// <summary>Every channel, which is what every pipeline this backend creates writes.</summary>
        All = Red | Green | Blue | Alpha,
    }

    /// <summary>
    /// ONE ENTRY OF AN <c>MTLRenderPipelineDescriptor</c>'s <c>colorAttachments</c> ARRAY: one colour target's
    /// pixel format and its blend state. Reached only through
    /// <see cref="MTLRenderPipelineDescriptor.ColorAttachmentAt"/> and owned by the descriptor, so there is
    /// nothing here to release.
    ///
    /// <para><b>PER-ATTACHMENT BLENDING IS WHY THIS IS AN ARRAY RATHER THAN ONE STATE.</b> The engine's
    /// multiple-render-target model pass blends one attachment while setting another to preserve its destination
    /// (<see cref="GpuBlendAttachment.PreserveDestination"/>), so collapsing these onto one shared state would
    /// paint the normal and linear-depth targets with the colour pass's blend.</para>
    ///
    /// <para><b>THE WRITE MASK IS WRITTEN EXPLICITLY EVEN THOUGH IT IS THE DEFAULT.</b> Its default IS
    /// <see cref="MTLColorWriteMask.All"/>, so the call changes nothing today. It is made because the incumbent
    /// makes it, and this row's whole licence for reusing the committed <c>metal</c> goldens is that the two
    /// paths write the same descriptor: a value left at a default is a value that moves the day Apple changes the
    /// default, silently, on a path no test would see.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLRenderPipelineColorAttachmentDescriptor(IntPtr Handle)
    {
        /// <summary>
        /// Write the whole attachment, in the incumbent's own order: the format, the enable, the write mask, then
        /// the alpha triple and the colour triple.
        /// </summary>
        /// <param name="format">The attachment's pixel format, which must match the framebuffer's texture.</param>
        /// <param name="blendingEnabled">Whether this attachment blends at all.</param>
        /// <param name="writeMask">Which channels are written.</param>
        /// <param name="alphaOperation">The alpha blend equation.</param>
        /// <param name="sourceAlpha">The alpha source factor.</param>
        /// <param name="destinationAlpha">The alpha destination factor.</param>
        /// <param name="colourOperation">The colour blend equation.</param>
        /// <param name="sourceColour">The colour source factor.</param>
        /// <param name="destinationColour">The colour destination factor.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Configure(MTLPixelFormat format, bool blendingEnabled, MTLColorWriteMask writeMask,
            MTLBlendOperation alphaOperation, MTLBlendFactor sourceAlpha, MTLBlendFactor destinationAlpha,
            MTLBlendOperation colourOperation, MTLBlendFactor sourceColour, MTLBlendFactor destinationColour)
        {
            SetNUInt("setPixelFormat:", (nuint)format);
            ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setBlendingEnabled:"),
                blendingEnabled ? (byte)1 : (byte)0);
            SetNUInt("setWriteMask:", (nuint)writeMask);

            SetNUInt("setAlphaBlendOperation:", (nuint)alphaOperation);
            SetNUInt("setSourceAlphaBlendFactor:", (nuint)sourceAlpha);
            SetNUInt("setDestinationAlphaBlendFactor:", (nuint)destinationAlpha);

            // -setRgbBlendOperation: is spelled with a lower-case 'gb' and the two FACTOR selectors are not,
            // which is Objective-C property naming rather than a typo in either place: a property named
            // rgbBlendOperation capitalises only its first letter for the setter, and sourceRGBBlendFactor
            // already carries the upper-case run.
            SetNUInt("setRgbBlendOperation:", (nuint)colourOperation);
            SetNUInt("setSourceRGBBlendFactor:", (nuint)sourceColour);
            SetNUInt("setDestinationRGBBlendFactor:", (nuint)destinationColour);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void SetNUInt(string selector, nuint value)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel(selector), value);
    }
}
