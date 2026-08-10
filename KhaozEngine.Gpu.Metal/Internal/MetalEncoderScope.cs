using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE ONE-ENCODER-AT-A-TIME STATE MACHINE (M-R1, M-R4): the three <c>Ensure</c> helpers, their
    /// <c>EnsureNo</c> counterpart, and the EPOCH every encoder-scoped record is stamped against.
    ///
    /// <para><b>EXACTLY ONE ENCODER IS OPEN AT A TIME, WHICH IS METAL'S RULE RATHER THAN A POLICY THIS DESIGN
    /// INVENTS.</b> A command buffer hands out one encoder and refuses a second until the first has been sent
    /// <c>-endEncoding</c>. Every command in the backend routes through one of the helpers here, so the
    /// transition is written ONCE: <see cref="EnsureRenderEncoder"/>, <see cref="EnsureBlitEncoder"/>,
    /// <see cref="EnsureComputeEncoder"/> and <see cref="EnsureNoEncoder"/>. A command that opened an encoder
    /// itself would be a second copy of the rule, and the rule is the thing that must not drift.</para>
    ///
    /// <para><b>THE EPOCH IS THE INVALIDATION, AND IT COUNTS BOUNDARIES RATHER THAN ENCODERS.</b> It starts at 1,
    /// never reaches 0, and increments on EVERY transition: each begin and each end. A record stamped through
    /// <see cref="MetalEncoderMark"/> is valid only while <see cref="Epoch"/> still reads what it was stamped
    /// with, so an end-then-begin pair invalidates twice and a record cannot survive either half. Counting ENDS
    /// as well as BEGINS costs one increment and closes the window where an encoder has been ended, nothing has
    /// reopened, and a record still reads as describing live encoder state.</para>
    ///
    /// <para><b>IT HOLDS THE SINK AS AN INTERFACE FIELD, which is the one place this backend does.</b> See
    /// <see cref="IMetalEncoderSink"/>: the per-draw classes are consumed through a struct constraint so the JIT
    /// monomorphizes them, and the boundary is reached from interface members that cannot be generic. One virtual
    /// call per encoder transition is a handful per frame, not one per draw.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED, and there is nothing to synchronise against.</b> A scope belongs to
    /// one command list, a list is recorded by one thread at a time, and N lists record concurrently and
    /// genuinely on this backend because each owns its own command buffer and its own encoders and this design has
    /// no shared record-time state at all (M-R3): no layout tracker, no barrier batch, no device state cache.
    /// Driving ONE list from two threads is a data race here and would be one inside the driver too.</para>
    /// </summary>
    internal sealed class MetalEncoderScope
    {
        readonly IMetalEncoderSink _sink;

        IntPtr _commandBuffer;
        IntPtr _encoder;
        MetalEncoderKind _kind;
        ulong _epoch = 1;

        /// <param name="sink">The budget seam every boundary emits through. See
        /// <see cref="IMetalEncoderSink"/> for why the boundary is on that seam rather than on
        /// <see cref="IMetalRenderApi"/>, which is a correction to section 6.4 with M-T2 as its reason.</param>
        internal MetalEncoderScope(IMetalEncoderSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);

            _sink = sink;
        }

        /// <summary>Which kind is open, or <see cref="MetalEncoderKind.None"/>.</summary>
        internal MetalEncoderKind Open => _kind;

        /// <summary>The open encoder, or <see cref="IntPtr.Zero"/> when none is.</summary>
        internal IntPtr Current => _encoder;

        /// <summary>
        /// THE INVALIDATION STAMP (M-R4). Every encoder-scoped record embeds a <see cref="MetalEncoderMark"/> and
        /// compares against this. Starts at 1 so a default-constructed record reads as invalid, and only ever
        /// increases.
        /// </summary>
        internal ulong Epoch => _epoch;

        /// <summary>
        /// Adopt <paramref name="commandBuffer"/> as the buffer every encoder is opened on, and forget everything
        /// about the previous recording. Called by <c>MetalCommandList.Begin</c> and by nothing else.
        /// <para>
        /// IT ENDS A STALE ENCODER FIRST rather than dropping it, which is the ownership rule
        /// <see cref="IMetalEncoderSink"/> states: one release per acquisition, at every exit including this one.
        /// </para>
        /// <para>
        /// IT BUMPS THE EPOCH, so a record made during a recording that was discarded cannot read as valid in the
        /// one that follows. A fresh <c>MTLCommandBuffer</c> has no encoder and no encoder state, so every record
        /// against the old one describes state that never existed on this one.
        /// </para>
        /// </summary>
        internal void BeginRecording(IntPtr commandBuffer)
        {
            // THROUGH THE ONE TRANSITION HELPER, so the release the sink's retain is paired with happens here too.
            // Dropping the encoder instead would leak its +1, and an encoder holds a reference to its own command
            // buffer, so the buffer the command list has just released would stay alive and stay counted against
            // the queue's maximum number of uncommitted buffers for the life of the process. That maximum is 64
            // and -commandBuffer BLOCKS at it, so the leak presents as a frame loop that hangs rather than as a
            // number anything reports. One native call on a buffer nobody will commit is what it costs to buy the
            // slot back, and it leaves the driver a clean state and this type one code path.
            EnsureNoEncoder();

            _commandBuffer = commandBuffer;
            _encoder = IntPtr.Zero;
            _kind = MetalEncoderKind.None;
            _epoch++;
        }

        /// <summary>
        /// Open a render encoder from <paramref name="descriptor"/>, ending whatever else is open first. Returns
        /// the encoder already open when it is already a render encoder, WITHOUT re-emitting anything: the
        /// deferred begin (M-A1) means the descriptor is built once per pass, and a second Ensure inside one pass
        /// is a second draw rather than a second pass.
        /// <para>
        /// IT CAN RETURN <see cref="IntPtr.Zero"/>, which is M-W5's orphan-target case rather than an error to
        /// throw on: a nil drawable makes the framebuffer unrenderable for one frame, and the seam's answer is
        /// that the frame's draws go nowhere and the present is skipped while the frame still COUNTS. Row 12
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/578) is where the caller side of that lands, and it
        /// is a genuine framebuffer failure rather than a state error, which is why this returns rather than
        /// throwing.
        /// </para>
        /// </summary>
        /// <param name="descriptor">The <c>MTLRenderPassDescriptor</c> row 12 builds.</param>
        internal IntPtr EnsureRenderEncoder(IntPtr descriptor)
        {
            if (_kind == MetalEncoderKind.Render) return _encoder;

            EnsureNoEncoder();

            IntPtr encoder = _sink.BeginRenderEncoder(_commandBuffer, descriptor);
            if (encoder == IntPtr.Zero) return IntPtr.Zero;

            Adopt(MetalEncoderKind.Render, encoder);
            return encoder;
        }

        /// <summary>
        /// Open a blit encoder, ending whatever else is open first.
        /// <para>
        /// THIS IS THE EXPENSIVE ONE AND IT IS EXPENSIVE OUT OF PROPORTION TO WHAT IT COPIES (2.1). Ending a
        /// render encoder to open this discards the bound pipeline, every argument-table entry, the viewport, the
        /// scissor and every vertex stream, so the next draw pays a full re-activation for a copy of a few bytes.
        /// That is the whole motivation for row 8's uniform ring
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/574), whose record-time write is a memcpy into
        /// mapped memory and opens nothing at all.
        /// </para>
        /// </summary>
        internal IntPtr EnsureBlitEncoder()
        {
            if (_kind == MetalEncoderKind.Blit) return _encoder;

            EnsureNoEncoder();

            // A nil encoder is NOT adopted, here or in either sibling. Metal hands one back only when the buffer
            // is in a state it will not encode into, and adopting it would leave this scope believing a kind is
            // open while every command against it went nowhere, which is the shape that reads as a silently
            // empty frame rather than as a failure.
            IntPtr encoder = _sink.BeginBlitEncoder(_commandBuffer);
            if (encoder == IntPtr.Zero) return IntPtr.Zero;

            Adopt(MetalEncoderKind.Blit, encoder);
            return encoder;
        }

        /// <summary>Open a compute encoder with the SERIAL dispatch type (M-H4), ending whatever else is open
        /// first. Serial is what makes dependent dispatches inside one encoder ordered without any hazard
        /// machinery, which is why this backend has none.</summary>
        internal IntPtr EnsureComputeEncoder()
        {
            if (_kind == MetalEncoderKind.Compute) return _encoder;

            EnsureNoEncoder();

            IntPtr encoder = _sink.BeginComputeEncoder(_commandBuffer);
            if (encoder == IntPtr.Zero) return IntPtr.Zero;

            Adopt(MetalEncoderKind.Compute, encoder);
            return encoder;
        }

        /// <summary>
        /// End whatever is open, and do nothing when nothing is. THE ONE helper every command illegal inside the
        /// current encoder calls (M-A5), so a caller never has to ask first.
        /// </summary>
        /// <returns>The kind that was ended, or <see cref="MetalEncoderKind.None"/> when nothing was open. Row 12
        /// reads it for the clear-only flush (M-A3): a pass that collected clears and saw no draw still has to
        /// clear, and knowing whether an encoder actually ended is how that decision is made without a second
        /// flag.</returns>
        internal MetalEncoderKind EnsureNoEncoder()
        {
            if (_kind == MetalEncoderKind.None) return MetalEncoderKind.None;

            MetalEncoderKind ended = _kind;

            // Cleared BEFORE the native call, not after, so a throw inside the driver cannot leave this scope
            // believing it still holds an encoder that has been ended. The next Ensure would then return a dead
            // handle, which is a use-after-end rather than a second endEncoding.
            IntPtr encoder = _encoder;
            _encoder = IntPtr.Zero;
            _kind = MetalEncoderKind.None;
            _epoch++;

            _sink.EndEncoding(ended, encoder);
            return ended;
        }

        // The epoch bump belongs to the BEGIN as much as to the end: a record stamped against the encoder that
        // was open before this one describes an argument table that no longer exists, and a bump only at the end
        // would leave a window where nothing had been ended (the very first encoder of a recording) and a record
        // from the previous recording still compared equal.
        void Adopt(MetalEncoderKind kind, IntPtr encoder)
        {
            _encoder = encoder;
            _kind = kind;
            _epoch++;
        }
    }
}
