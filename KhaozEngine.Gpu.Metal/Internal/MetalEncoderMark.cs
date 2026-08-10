namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-R4's MECHANISM, as ONE value a record embeds: an encoder EPOCH stamp, valid only inside the
    /// epoch it was stamped in.
    ///
    /// <para><b>THE FACT EVERYTHING FOLLOWS FROM.</b> Metal's argument tables, bound pipeline state, viewport,
    /// scissor and vertex-stream bindings are properties of the ENCODER rather than of the command buffer, so
    /// ending a render encoder discards all of it. That is the API's rule and not a choice either implementation
    /// makes. What M-R4 adds is that EVERYTHING is invalidated at a boundary rather than the subset the incumbent
    /// remembers: pipeline state, cull mode, front face, fill mode, blend colour, depth-stencil state, depth clip
    /// mode, stencil reference, every argument-table entry, the viewport, the scissor, every vertex stream and
    /// the index buffer.
    ///
    /// <b>The incumbent's version of it is INCOMPLETE, which is 2.1's finding and the reason this type exists
    /// rather than a comment.</b> <c>MTLCommandList.EndCurrentRenderPass</c> sets the pipeline-changed flag,
    /// clears the active-set array and re-marks the viewport and scissor, and does NOT clear
    /// <c>_vertexBuffersActive</c>. It is saved only by a SECOND defect: <c>PreDrawCommand</c>'s vertex-buffer
    /// loop issues <c>setVertexBuffer</c> when the flag is false and never sets it true, so the cache is
    /// permanently cold and every stream is re-bound on every draw. Porting the redundancy tracking without
    /// porting the invalidation ships a corruption no golden would catch, because the goldens do not restart a
    /// render pass mid-scene.</para>
    ///
    /// <para><b>AN EPOCH STAMP RATHER THAN A RESET LIST, and the difference is which mistake it makes
    /// impossible.</b> A callback list (every record registers itself and is reset at a boundary) is forgettable
    /// at REGISTRATION: a later row adds a record, forgets to register it, and the suite stays green because
    /// nothing tests a record nobody wrote a test for. A stamp is forgettable at READ, and a read that forgets to
    /// compare epochs is a record that is USED, so the behavioural test in
    /// <c>MetalEncoderScopeInvalidationTests</c> catches it on the one shape that matters. The stamp also costs
    /// one comparison against a field the scope already holds, where a reset list costs an allocation and an
    /// indirection per record per boundary.</para>
    ///
    /// <para><b>WHO EMBEDS ONE.</b> Row 11's bound-pipeline record
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577), row 13's per-slot bind records
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579), and row 14's vertex-stream and index-buffer records
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580). Row 7 ships the mechanism and the test rather than
    /// the records, because the records are those rows' content and the invalidation is the thing that has to be
    /// true before any of them is written.</para>
    ///
    /// <para><b>A MUTABLE STRUCT, deliberately</b>, so a record embeds one by value and no allocation happens per
    /// slot per list. It is always accessed through a field on a class (a bind record, a stream record) rather
    /// than copied, which is what keeps the mutation visible.</para>
    /// </summary>
    internal struct MetalEncoderMark
    {
        // 0 is NEVER MARKED, which is why MetalEncoderScope's epoch starts at 1 and only ever increases: a
        // default-constructed record must read as invalid in every epoch, including the first.
        ulong _epoch;

        /// <summary>Stamp this record as valid in <paramref name="epoch"/>, which is
        /// <see cref="MetalEncoderScope.Epoch"/> read at the moment the native bind was emitted.</summary>
        internal void Mark(ulong epoch) => _epoch = epoch;

        /// <summary>Whether this record still describes what the encoder holds. False for a record that was
        /// never marked, and false for one marked before any encoder boundary since.</summary>
        internal readonly bool IsValidIn(ulong epoch) => _epoch != 0 && _epoch == epoch;

        /// <summary>Forget the stamp, for the case where a record is invalidated by something OTHER than a
        /// boundary: M-R9's index-table comparison on a pipeline switch, and a slot whose recorded set has gone
        /// null.</summary>
        internal void Clear() => _epoch = 0;
    }
}
