using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLLoadAction</c>, an <c>NSUInteger</c> and therefore <c>ulong</c>: what an attachment does with its
    /// EXISTING contents when a render encoder opens. Under M-A1's deferred begin this is the whole of how a
    /// clear is expressed, which is why there is no clear COMMAND anywhere in this backend.
    /// </summary>
    internal enum MTLLoadAction : ulong
    {
        /// <summary><c>MTLLoadActionDontCare</c>: the contents become undefined. NEVER SELECTED HERE, and the
        /// reason is the store side's (M-A4): undefined contents are not stable across runs and the goldens
        /// require stability on the same device. It is declared because the value is 0 and therefore what a
        /// descriptor field holds before anything writes it, so a reader checking the default needs the name.
        /// </summary>
        DontCare = 0,

        /// <summary><c>MTLLoadActionLoad</c>: the attachment keeps what it already held. What every attachment
        /// with no pending clear gets, including one whose pass was split by a record-time blit.</summary>
        Load = 1,

        /// <summary><c>MTLLoadActionClear</c>: the attachment is filled with the clear value beside it. What a
        /// clear recorded before the first draw of a pass folds into, on the attachment the caller NAMED
        /// (M-A2).</summary>
        Clear = 2,
    }

    /// <summary>
    /// <c>MTLStoreAction</c>, an <c>NSUInteger</c>: what happens to an attachment's contents when the encoder
    /// ends.
    /// <para>
    /// <b>ONLY <see cref="Store"/> IS EVER SELECTED, AND IT IS SET EXPLICITLY (M-A4).</b> Leaving it to the
    /// descriptor default would leave it at <see cref="DontCare"/>, which discards the attachment. The two
    /// members this backend does not take are declared because 2.5 records both as measured follow-ups with
    /// named consumers rather than as omissions: depth <see cref="DontCare"/> is a real tiler win the
    /// determinism rule blocks, and <see cref="StoreAndMultisampleResolve"/> removes a whole encoder but changes
    /// what a producing pass writes out, which would make MM2's golden A/B unreadable in the same phase as
    /// M-A2's rendering change.
    /// </para>
    /// </summary>
    internal enum MTLStoreAction : ulong
    {
        /// <summary><c>MTLStoreActionDontCare</c>: the contents are discarded. The descriptor DEFAULT, which is
        /// why M-A4 sets the store action explicitly rather than relying on one.</summary>
        DontCare = 0,

        /// <summary><c>MTLStoreActionStore</c>: the contents are written out. The only value this backend
        /// selects, for colour and for depth alike.</summary>
        Store = 1,

        /// <summary><c>MTLStoreActionMultisampleResolve</c>: resolve into the resolve attachment and discard the
        /// multisampled one. Not taken: the incumbent resolves through a standalone encoder and
        /// <c>scene3d_hdr_msaa</c> is a committed golden in the family this phase is measured against (2.5).
        /// </summary>
        MultisampleResolve = 2,

        /// <summary><c>MTLStoreActionStoreAndMultisampleResolve</c>: both. The folded resolve 2.5 files with a
        /// named consumer and a golden argument, and does not take here.</summary>
        StoreAndMultisampleResolve = 3,
    }

    /// <summary>
    /// ONE ATTACHMENT SLOT ON AN <see cref="MTLRenderPassDescriptor"/>, which is
    /// <c>MTLRenderPassColorAttachmentDescriptor</c>, <c>MTLRenderPassDepthAttachmentDescriptor</c> or
    /// <c>MTLRenderPassStencilAttachmentDescriptor</c> behind one handle.
    ///
    /// <para><b>THREE OBJECTIVE-C CLASSES, ONE MANAGED TYPE, AND THAT IS THE SELECTOR SET RATHER THAN A
    /// SHORTCUT.</b> All three inherit <c>MTLRenderPassAttachmentDescriptor</c>, which is where
    /// <c>texture</c>, <c>loadAction</c> and <c>storeAction</c> live, so the three shared setters are genuinely
    /// one class's. What differs is the clear VALUE (<see cref="SetClearColor"/>, <see cref="SetClearDepth"/>
    /// and <see cref="SetClearStencil"/>), and sending the wrong one to the wrong slot is an unrecognised
    /// selector rather than a silent misread. The alternative, three handle types over the same pointer, would
    /// buy that one compile-time check at the cost of three files whose shared half is copied three times.</para>
    ///
    /// <para><b>IT IS BORROWED, NEVER OWNED.</b> An attachment descriptor is a property of the pass descriptor
    /// and lives as long as it does, so nothing here retains or releases: the ONE ownership pair in this family
    /// is <see cref="MTLRenderPassDescriptor.Create"/>'s retain and its
    /// <see cref="MTLRenderPassDescriptor.Release"/>.</para>
    ///
    /// <para><b>NO MIP OR SLICE SETTER, and its absence is M-M10 reaching this file.</b>
    /// <c>IGpuDevice.Factory.CreateFramebuffer</c> takes bare textures with no mip and no layer parameter, so
    /// every attachment is mip 0 slice 0, which is what <c>level</c> and <c>slice</c> already default to. A
    /// setter here would be the first way to express a narrowed attachment in a package that deliberately has
    /// none.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for a slot the descriptor did
    /// not hand out.</param>
    internal readonly record struct MTLRenderPassAttachmentDescriptor(IntPtr Handle)
    {
        /// <summary>True when the descriptor answered nil for this slot, which is a descriptor already in
        /// trouble rather than an empty attachment: Metal vends a fresh descriptor object per slot on
        /// demand.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary><c>-setTexture:</c>, the attachment this slot renders into. Borrowed from the framebuffer's
        /// own texture, which outlives the pass.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetTexture(MTLTexture texture)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setTexture:"), texture.Handle);

        /// <summary><c>-setLoadAction:</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetLoadAction(MTLLoadAction action)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setLoadAction:"), (nuint)(ulong)action);

        /// <summary><c>-setStoreAction:</c>. Always sent, never left to the default (M-A4).</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetStoreAction(MTLStoreAction action)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setStoreAction:"), (nuint)(ulong)action);

        /// <summary><c>-setClearColor:</c>, on a COLOUR slot. The four-double HFA row 1's spike value-checked.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetClearColor(MTLClearColor color)
            => ObjCMsgSend.SendVoidClearColor(Handle, ObjCRuntime.Sel("setClearColor:"), color);

        /// <summary><c>-setClearDepth:</c>, on the DEPTH slot. Metal's is a <c>double</c> where the seam's
        /// <c>ClearDepthStencil</c> carries a <c>float</c>, so the widening is exact and happens here.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetClearDepth(double depth)
            => ObjCMsgSend.SendVoidDouble(Handle, ObjCRuntime.Sel("setClearDepth:"), depth);

        /// <summary>
        /// <c>-setClearStencil:</c>, on the STENCIL slot.
        /// <para>
        /// THE SEAM CARRIES NO STENCIL VALUE, so the only one this is ever sent is 0, which is what the
        /// incumbent clears the stencil plane to alongside the depth and what the Vulkan sibling reproduces.
        /// Leaving the plane out would leave it holding whatever the last pass wrote, which is the same
        /// undefined-is-not-stable case M-A4 rules on for the store action.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetClearStencil(uint stencil)
            => ObjCMsgSend.SendVoidUInt(Handle, ObjCRuntime.Sel("setClearStencil:"), stencil);
    }

    /// <summary>
    /// AN <c>MTLRenderPassDescriptor</c>: the whole of a render pass's shape, which on this API is a plain object
    /// graph rather than a created-and-cached driver object.
    ///
    /// <para><b>THIS IS WHAT PHASE 3 HAD TO REACH DYNAMIC RENDERING TO GET (section 7.1).</b> There is no
    /// <c>VkRenderPass</c> analogue to cache and no <c>VkFramebuffer</c> analogue to rebuild on a resize, because
    /// a descriptor is made per pass, read at
    /// <c>-[MTLCommandBuffer renderCommandEncoderWithDescriptor:]</c>, and finished with. Its per-attachment
    /// <c>texture</c>, <c>loadAction</c>, <c>clearColor</c> and <c>storeAction</c> map onto
    /// <c>VkRenderingAttachmentInfo</c>'s members almost one for one, which is #531's prediction about Metal and
    /// Vulkan holding up.</para>
    ///
    /// <para><b>IT ARRIVES AUTORELEASED AND THIS TYPE RETAINS IT, which is the encoder's ownership rule applied
    /// one level up and for the same reason.</b> <c>+renderPassDescriptor</c> is a convenience constructor, so
    /// the object it returns dies with whatever pool was in scope when it was made. The pass is built in one
    /// managed call and the encoder is opened in another, through <c>IMetalEncoderSink</c>, so there is no scope
    /// this backend controls that spans both. <see cref="Create"/> takes a retain and
    /// <see cref="Release"/> gives it back, and the schedule that owns the pair releases in a
    /// <c>finally</c> so a nil encoder (M-W5's orphan target) does not leak one.</para>
    ///
    /// <para><b>THE ATTACHMENT ARRAYS ARE SUBSCRIPTED, NOT INDEXED.</b> <c>colorAttachments</c> answers an
    /// <c>MTLRenderPassColorAttachmentDescriptorArray</c>, which is not an <c>NSArray</c> and does NOT respond to
    /// <c>-objectAtIndex:</c>: the selector is <c>-objectAtIndexedSubscript:</c>, and it vends a slot on demand
    /// rather than returning nil for one nothing has written. Row 1's spike recorded a real pass through exactly
    /// that selector, so this is measured rather than read off a header.</para>
    ///
    /// <para><b>WHAT IS NOT HERE.</b> No <c>renderTargetWidth</c> or <c>renderTargetHeight</c>: Metal derives the
    /// render area from the attachments themselves and setting them is for a pass rendering into a SUBSET of an
    /// attachment, which this seam cannot express. No <c>resolveTexture</c>: M-A4 keeps the standalone resolve
    /// encoder (2.5). No <c>visibilityResultBuffer</c>, no sample-buffer attachments, no tile settings: nothing
    /// on the seam reaches any of them.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLRenderPassDescriptor(IntPtr Handle)
    {
        /// <summary>True when there is no descriptor.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// A fresh descriptor, RETAINED. See the type remarks: the caller owns exactly one
        /// <see cref="Release"/> for this, at every exit including the one where the encoder came back nil.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLRenderPassDescriptor Create()
        {
            IntPtr descriptor = ObjCMsgSend.Send(
                ObjCRuntime.ClassNamed("MTLRenderPassDescriptor"), ObjCRuntime.Sel("renderPassDescriptor"));

            return new MTLRenderPassDescriptor(
                descriptor == IntPtr.Zero ? IntPtr.Zero : ObjCRuntime.ObjcRetain(descriptor));
        }

        /// <summary>Give back the retain <see cref="Create"/> took. Idempotent against a nil handle, which is
        /// what a descriptor Metal would not make leaves behind.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary>
        /// <c>colorAttachments[<paramref name="index"/>]</c>, through
        /// <c>-objectAtIndexedSubscript:</c>. THE INDEX IS THE WHOLE OF M-A2: the incumbent writes every clear
        /// into slot 0, so <c>ModelFB</c>'s normal and linear-depth attachments are never cleared at all, and
        /// passing the caller's own index here is the one-line fix that ends.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLRenderPassAttachmentDescriptor ColourAttachment(uint index)
        {
            IntPtr array = ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("colorAttachments"));
            return new MTLRenderPassAttachmentDescriptor(array == IntPtr.Zero
                ? IntPtr.Zero
                : ObjCMsgSend.SendPtrNUInt(array, ObjCRuntime.Sel("objectAtIndexedSubscript:"), index));
        }

        /// <summary><c>-depthAttachment</c>, a single slot rather than an array.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLRenderPassAttachmentDescriptor DepthAttachment()
            => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("depthAttachment")));

        /// <summary>
        /// <c>-stencilAttachment</c>. Populated ONLY when the depth format carries a stencil plane, which is the
        /// incumbent's <c>FormatHelpers.IsStencilFormat</c> guard: naming a stencil attachment over a
        /// depth-only texture is a validation error under the debug layer M-T7 arms.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLRenderPassAttachmentDescriptor StencilAttachment()
            => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("stencilAttachment")));
    }
}
