using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S FOUR RESOURCE-SET MEMBERS AND THE THREE PRE-COMMAND HOOKS, handing every decision to
    /// <see cref="MetalBindRecords"/> and <see cref="MetalVertexStreamRecords"/>. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para><b>THE DECISIONS ARE NOT HERE ON PURPOSE</b>, which is the split <c>MetalCommandList.Passes.cs</c>
    /// and <c>MetalCommandList.Uploads.cs</c> already make for the same reason. Which slots are dirty, which
    /// stages get a bind, how a run is cut, when the offsets-only call is legal and what the composed offset is
    /// can all be wrong in ways a golden sees late and a device-free test sees immediately, and none of them
    /// needs an <c>MTLDevice</c>. What is left here is the two things that DO: turning an
    /// <see cref="IGpuResourceSet"/> into this backend's own record, and the recording state.</para>
    ///
    /// <para><b>THE FLUSH IS GENERIC OVER THE SINK AND THE MEMBERS ARE NOT, which is M-T2's line drawn through
    /// one type.</b> The seam members are <see cref="IGpuCommandList"/> members and cannot be generic, and they
    /// make no native call, so nothing is lost. The three hooks are consumed through
    /// <c>where TSink : struct, IMetalEncoderSink</c> so the JIT monomorphizes the seam away on the per-draw
    /// path, exactly as the Vulkan sibling's <c>FlushGraphicsBinds</c> is.</para>
    ///
    /// <para><b>THE ORDER INSIDE A DRAW IS FIXED AND ROW 14 OWNS IT</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580):
    /// <see cref="MetalRenderPassSchedule.PrepareDraw"/> first, which opens the pass and emits whatever dynamic
    /// state is owed and returns the encoder, then <see cref="FlushGraphicsBinds"/> into that encoder, then
    /// <see cref="FlushVertexStreams"/>, then the draw. A dispatch is
    /// <see cref="MetalEncoderScope.EnsureComputeEncoder"/>, then <see cref="FlushComputeBinds"/>, then the
    /// dispatch.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        // BOTH ARMS ARE BUILT IN THE CONSTRUCTOR rather than initialised here, because they need the DEVICE's
        // reported buffer-offset alignment and a field initialiser cannot see a constructor parameter. See
        // MetalBindRecords.RequireOffsetAligned for why it is the device's number rather than M-M3's 256.
        readonly MetalBindRecords _graphicsBinds;
        readonly MetalBindRecords _computeBinds;
        readonly MetalVertexStreamRecords _streams = new();

        /// <summary>
        /// THE GRAPHICS BIND RECORDS, and the wiring point row 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577) reaches for: its <c>SetPipeline</c> calls
        /// <see cref="MetalBindRecords.SetIndexTable"/> with the pipeline's own index table, AFTER M-R8's
        /// identity guard has decided the pipeline really is changing. Exposed for the same reason
        /// <see cref="Passes"/> is: later rows drive their commands through it, and the device-free tests drive
        /// it before those members exist.
        /// </summary>
        internal MetalBindRecords GraphicsBinds => _graphicsBinds;

        /// <summary>The compute arm, which <c>SetComputePipeline</c> feeds the same way.</summary>
        internal MetalBindRecords ComputeBinds => _computeBinds;

        /// <summary>
        /// THE VERTEX-STREAM CACHE (section 6.3). Row 14's <c>SetVertexBuffer</c> resolves its buffer and calls
        /// <see cref="MetalVertexStreamRecords.Record"/> with the raw <c>MTLBuffer</c> and the caller's offset,
        /// and row 11 reads <see cref="MetalVertexStreamIndex"/> for the matching
        /// <c>MTLVertexDescriptor</c> layout indices.
        /// </summary>
        internal MetalVertexStreamRecords VertexStreams => _streams;

        /// <inheritdoc/>
        /// <remarks>Clause 1: this RECORDS and emits nothing. The next draw's flush is what reaches the
        /// encoder.</remarks>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => RecordSet(_graphicsBinds, slot, set, 0, "Binding a graphics resource set");

        /// <inheritdoc/>
        /// <remarks>The offset-carrying overload, into the same record. It is the per-draw offset the composition
        /// adds on top of the frame base and the set's own range offset, for the one element the layout declares
        /// dynamic.</remarks>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => RecordSet(_graphicsBinds, slot, set, dynamicOffset, "Binding a graphics resource set");

        /// <inheritdoc/>
        /// <remarks>The compute arm, into its own records: a graphics bind does not feed a dispatch and this does
        /// not feed a draw, which is the seam's own split and Metal's too, since the two reach different
        /// encoders.</remarks>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => RecordSet(_computeBinds, slot, set, 0, "Binding a compute resource set");

        /// <inheritdoc/>
        /// <remarks>The compute arm of the offset-carrying overload.</remarks>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => RecordSet(_computeBinds, slot, set, dynamicOffset, "Binding a compute resource set");

        /// <summary>
        /// THE PRE-DRAW HOOK, GRAPHICS ARM (clause 2). Flushes every dirty slot into
        /// <paramref name="encoder"/> and leaves them clean.
        /// </summary>
        /// <param name="sink">M-T2's seam, by <c>ref</c> so no defensive copy is made per draw.</param>
        /// <param name="encoder">The open render encoder, from
        /// <see cref="MetalRenderPassSchedule.PrepareDraw"/>.</param>
        internal void FlushGraphicsBinds<TSink>(ref TSink sink, IntPtr encoder)
            where TSink : struct, IMetalEncoderSink
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _graphicsBinds.Flush(ref sink, encoder, _encoders.Epoch, _segment);
        }

        /// <summary>The compute arm, which <c>Dispatch</c> calls for the same reason and in the same
        /// place.</summary>
        internal void FlushComputeBinds<TSink>(ref TSink sink, IntPtr encoder)
            where TSink : struct, IMetalEncoderSink
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _computeBinds.Flush(ref sink, encoder, _encoders.Epoch, _segment);
        }

        /// <summary>
        /// THE VERTEX-STREAM HALF OF THE PRE-DRAW HOOK, which row 14 calls AFTER
        /// <see cref="FlushGraphicsBinds"/> and before the draw. The two write into the same vertex-stage buffer
        /// table from opposite ends and cannot collide (M-B2), so the order between them is free and fixed only
        /// so it is one order.
        /// </summary>
        internal void FlushVertexStreams<TSink>(ref TSink sink, IntPtr encoder)
            where TSink : struct, IMetalEncoderSink
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _streams.Flush(ref sink, encoder, _encoders.Epoch);
        }

        // THE RECORD, in ONE place rather than at each of the four overloads, so the two arms and the two offset
        // shapes cannot drift apart by an edit to one of them. Everything it does is a type check and a store:
        // MetalResourceSet.Require refuses another backend's set, another device's, and a disposed one, and
        // AsBound is the plain-data projection the record holds so nothing in the recorder's field graph reaches
        // a layout or a liveness token.
        //
        // A NULL SET IS RECORDED RATHER THAN REFUSED, which is clause 6: the seam has no unbind and a caller
        // clearing a slot is saying this draw does not use it. Require would refuse null, so the null arm is
        // taken in front of it.
        void RecordSet(MetalBindRecords records, uint slot, IGpuResourceSet? set, uint dynamicOffset, string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording(what);

            MetalBoundSet bound = set is null
                ? default
                : MetalResourceSet.Require(set, _liveness, what).AsBound;

            records.Record(slot, bound, dynamicOffset);
        }
    }
}
