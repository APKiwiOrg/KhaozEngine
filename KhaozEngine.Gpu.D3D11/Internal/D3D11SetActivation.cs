using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE ARRAY-BATCHED FAN-OUT (decision R6): turn one resource set into ONE native call per register file per
    /// visible stage, so the scaling law is O(kinds x stages) rather than O(elements x stages).
    /// <para>
    /// THIS IS THE #418 DEFECT'S CURE, and the numbers are worth stating because they are what the budget test
    /// freezes. The model set is seven elements and costs FOUR calls: the UBO to the vertex stage, the UBO to the
    /// pixel stage, four shader resources to the pixel stage in one array, two samplers to the pixel stage in one
    /// array. The worst case in the engine is the WATER set at SIX, not the model set, and both are seven
    /// elements: <c>WaterRenderer</c> declares its bathymetry texture, its ocean map, their two samplers and its
    /// dynamic UBO at <c>Vertex | Fragment</c>, so the vertex stage needs a shader-resource array and a sampler
    /// array of its own. Six is the bound to quote. The incumbent issued one call per resource per stage, which is
    /// 42 for the same set before its own batching and 8 after.
    /// </para>
    /// <para>
    /// A SPAN IS CONTIGUOUS AND MAY HAVE HOLES. Registers are numbered per file across the whole layout in
    /// declaration order (decision S2), not per stage, so the registers one stage sees are not necessarily
    /// adjacent. Rather than splitting a run into two calls at every gap, one call covers the whole span from the
    /// lowest visible register to the highest and writes a null into each hole. That is safe because a file's
    /// registers in <c>[base, base + count)</c> belong to THIS set alone, so a null there unbinds nothing anyone
    /// else is using, and it is what keeps "one call per file per stage" true rather than nearly true. In shipped
    /// layouts the holes do not arise anyway: the cross-compiler numbers each stage densely over only the bindings
    /// that stage declares, so a renderer whose stage-visible resources are not a PREFIX of its layout is already
    /// broken for another reason (see the comment on <c>WaterRenderer._layout</c>).
    /// </para>
    /// <para>
    /// THE OFFSETS-ONLY PATH SKIPS TEXTURES AND SAMPLERS ENTIRELY, which is the reason
    /// <see cref="D3D11SlotDirty"/> has three states rather than two. Everything else about it is the same walk
    /// over a narrower span: the span runs from the lowest to the highest DYNAMIC constant buffer, and every
    /// constant buffer of the set inside that span is written with its own binding, so a non-dynamic buffer caught
    /// between two dynamic ones is re-bound unchanged rather than unbound.
    /// </para>
    /// <para>
    /// NO DEVICE, NO DIRECT3D TYPE, NO ALLOCATION PER FLUSH. The spans come out of scratch arrays this object owns
    /// and grows once, so the hot path is a walk and a memcpy-free fill. One instance per device, held by
    /// <see cref="D3D11BindFlush"/>, which is also what makes the scratch safe: decision W5 has one thread
    /// recording at a time.
    /// </para>
    /// </summary>
    internal sealed class D3D11SetActivation
    {
        // The four register files, in the order a full activation issues them. D3D11 does not care, and a trace a
        // reader compares against a capture does.
        static readonly D3D11RegisterFile[] Files =
        {
            D3D11RegisterFile.ConstantBuffer,
            D3D11RegisterFile.ShaderResource,
            D3D11RegisterFile.Sampler,
            D3D11RegisterFile.UnorderedAccess,
        };

        // Every stage Direct3D 11 has a setter for, in pipeline order. Iterated in full rather than trimmed to the
        // three the engine uses, because the seam's GpuShaderStages carries all six and a layout that declares a
        // geometry binding must not silently bind nothing.
        static readonly GpuShaderStages[] Stages =
        {
            GpuShaderStages.Vertex,
            GpuShaderStages.TessellationControl,
            GpuShaderStages.TessellationEvaluation,
            GpuShaderStages.Geometry,
            GpuShaderStages.Fragment,
            GpuShaderStages.Compute,
        };

        D3D11ConstantBufferBind[] _constants = new D3D11ConstantBufferBind[8];
        IGpuBindableResource?[] _resources = new IGpuBindableResource?[8];

        /// <summary>
        /// DECISION C1'S TRACKER, one per device because this object is. It sits HERE rather than beside the dirty
        /// records because C1 says the auto-unbind belongs where the bind arrays are assembled, and this is that
        /// place: the null it writes goes into an array call of the same shape as the bind it protects, issued
        /// inside the same activation. <see cref="D3D11BindFlush"/> resets it and drains its raises.
        /// </summary>
        internal D3D11ViewConflicts Conflicts { get; } = new();

        /// <summary>
        /// Issue <paramref name="set"/> into <paramref name="sink"/>: one array call per register file per visible
        /// stage, at absolute registers computed from <paramref name="baseCounts"/>.
        /// </summary>
        /// <param name="sink">Where the calls go. A struct, so the whole walk monomorphizes.</param>
        /// <param name="set">The set to activate, with every binding already resolved at set creation.</param>
        /// <param name="baseCounts">The per-file base for this set's slot, from
        /// <see cref="D3D11RegisterScheme.BaseFor"/>.</param>
        /// <param name="dynamicOnly">True for a <see cref="D3D11SlotDirty.DynamicOffsetsOnly"/> flush, which
        /// pushes only the dynamic constant buffers.</param>
        /// <param name="dynamicOffsetBytes">The per-draw byte offset added to every binding the layout marked
        /// dynamic. Ignored by every other binding.</param>
        /// <param name="unsetConstantBuffersBeforeSet">The <c>!DriverCommandLists</c> workaround of decision R7.
        /// </param>
        /// <param name="arm">Which dirty record this activation is draining, for decision C1's raise-to-dirty. An
        /// unbind has to name the arm as well as the slot, because the arm it invalidates is usually the OTHER
        /// one.</param>
        /// <param name="slot">The slot inside that record, likewise.</param>
        internal void Activate<TSink>(ref TSink sink, D3D11ResourceSet set, in D3D11RegisterCounts baseCounts,
            bool dynamicOnly, uint dynamicOffsetBytes, bool unsetConstantBuffersBeforeSet,
            D3D11PipelineArm arm, uint slot)
            where TSink : struct, ID3D11BindSink
        {
            ArgumentNullException.ThrowIfNull(set);

            ReadOnlySpan<D3D11BoundResource> bindings = set.Bindings;
            GpuShaderStages union = VisibleStages(bindings, dynamicOnly);
            if (union == GpuShaderStages.None) return;

            for (int f = 0; f < Files.Length; f++)
            {
                D3D11RegisterFile file = Files[f];

                // The offsets-only path is constant buffers and nothing else, which is the whole of rule 3. It is
                // also why an offsets-only flush can never trip C1: neither file that can conflict is touched.
                if (dynamicOnly && file != D3D11RegisterFile.ConstantBuffer) continue;

                for (int s = 0; s < Stages.Length; s++)
                {
                    GpuShaderStages stage = Stages[s];
                    if ((union & stage) == 0) continue;

                    IssueOne(ref sink, bindings, file, stage, baseCounts, dynamicOnly, dynamicOffsetBytes,
                        unsetConstantBuffersBeforeSet, arm, slot);
                }
            }
        }

        // One (file, stage) pair: find the span, fill it, make the call. Returns without a call when the pair has
        // no visible binding, which is the common case for most of the 24 pairs a walk considers.
        void IssueOne<TSink>(ref TSink sink, ReadOnlySpan<D3D11BoundResource> bindings, D3D11RegisterFile file,
            GpuShaderStages stage, in D3D11RegisterCounts baseCounts, bool dynamicOnly, uint dynamicOffsetBytes,
            bool unsetConstantBuffersBeforeSet, D3D11PipelineArm arm, uint slot)
            where TSink : struct, ID3D11BindSink
        {
            if (!SpanOf(bindings, file, stage, dynamicOnly, out uint lo, out uint hi)) return;

            int count = (int)(hi - lo) + 1;
            uint startSlot = baseCounts.For(file) + lo;

            if (file == D3D11RegisterFile.ConstantBuffer)
            {
                FillConstants(bindings, stage, lo, count, dynamicOffsetBytes);

                // The unset goes IMMEDIATELY before the bind, with the same span, which is the whole shape of the
                // workaround: anything between the two gives the runtime a chance to coalesce them again.
                if (unsetConstantBuffersBeforeSet) sink.UnsetConstantBuffers(stage, startSlot, count);
                sink.SetConstantBuffers(stage, startSlot, _constants.AsSpan(0, count));
                return;
            }

            FillResources(bindings, file, stage, lo, count);
            ReadOnlySpan<IGpuBindableResource?> resources = _resources.AsSpan(0, count);

            // A SAMPLER CANNOT BE HALF OF A HAZARD, so it takes the short path with no tracking at all. The other
            // two files are the 't' and 'u' pair decision C1 is entirely about, and each one asks the tracker to
            // clear the opposite file FIRST, then issues, then records what it just bound.
            switch (file)
            {
                case D3D11RegisterFile.Sampler:
                    sink.SetSamplers(stage, startSlot, resources);
                    return;

                case D3D11RegisterFile.ShaderResource:
                    Conflicts.UnbindConflicts(ref sink, D3D11RegisterFile.ShaderResource, resources);
                    sink.SetShaderResources(stage, startSlot, resources);
                    break;

                default:
                    Conflicts.UnbindConflicts(ref sink, D3D11RegisterFile.UnorderedAccess, resources);
                    sink.SetUnorderedAccessViews(stage, startSlot, resources);
                    break;
            }

            // AFTER the call, never before. A sink can REFUSE a bind (an unordered-access binding outside compute
            // is refused by name), and recording a bind the sink threw on would leave the tracker describing a
            // register the context never got, which is a null written at some later flush against nothing.
            Conflicts.Record(file, stage, startSlot, resources, arm, slot);
        }

        // The stages any binding this flush will touch is visible to. Computed once per activation so the walk
        // below skips whole stages instead of scanning the bindings for each of the 24 (file, stage) pairs.
        static GpuShaderStages VisibleStages(ReadOnlySpan<D3D11BoundResource> bindings, bool dynamicOnly)
        {
            GpuShaderStages union = GpuShaderStages.None;
            for (int i = 0; i < bindings.Length; i++)
            {
                if (dynamicOnly && !IsDynamicConstantBuffer(bindings[i])) continue;
                union |= bindings[i].Stages;
            }

            return union;
        }

        // The contiguous register span one (file, stage) pair covers, or false when it covers nothing. On the
        // offsets-only path the span is bounded by the DYNAMIC bindings alone, so a set whose dynamic UBO is b2
        // binds one register at b2 rather than three from b0.
        static bool SpanOf(ReadOnlySpan<D3D11BoundResource> bindings, D3D11RegisterFile file, GpuShaderStages stage,
            bool dynamicOnly, out uint lo, out uint hi)
        {
            lo = 0;
            hi = 0;
            bool any = false;

            for (int i = 0; i < bindings.Length; i++)
            {
                ref readonly D3D11BoundResource b = ref bindings[i];
                if (b.Slot.File != file || (b.Stages & stage) == 0) continue;
                if (dynamicOnly && !b.Dynamic) continue;

                if (!any)
                {
                    lo = b.Slot.Index;
                    hi = b.Slot.Index;
                    any = true;
                    continue;
                }

                if (b.Slot.Index < lo) lo = b.Slot.Index;
                if (b.Slot.Index > hi) hi = b.Slot.Index;
            }

            return any;
        }

        // Every constant buffer of the set that this stage sees and that falls inside the span, holes left at the
        // default value (null buffer, zero constants), which is what Direct3D 11 requires of a null entry. A
        // non-dynamic buffer inside a dynamic span is written with its own unchanged window rather than nulled:
        // the span is a batching decision and must not unbind something the shader still reads.
        void FillConstants(ReadOnlySpan<D3D11BoundResource> bindings, GpuShaderStages stage, uint lo, int count,
            uint dynamicOffsetBytes)
        {
            if (_constants.Length < count)
                _constants = new D3D11ConstantBufferBind[D3D11BindResolve.RoundedCapacity(count)];
            Array.Clear(_constants, 0, count);

            for (int i = 0; i < bindings.Length; i++)
            {
                ref readonly D3D11BoundResource b = ref bindings[i];
                if (b.Slot.File != D3D11RegisterFile.ConstantBuffer || (b.Stages & stage) == 0) continue;
                if (b.Slot.Index < lo || b.Slot.Index >= lo + (uint)count) continue;

                _constants[b.Slot.Index - lo] = ConstantBind(in b, dynamicOffsetBytes);
            }
        }

        void FillResources(ReadOnlySpan<D3D11BoundResource> bindings, D3D11RegisterFile file, GpuShaderStages stage,
            uint lo, int count)
        {
            if (_resources.Length < count)
                _resources = new IGpuBindableResource?[D3D11BindResolve.RoundedCapacity(count)];
            Array.Clear(_resources, 0, count);

            for (int i = 0; i < bindings.Length; i++)
            {
                ref readonly D3D11BoundResource b = ref bindings[i];
                if (b.Slot.File != file || (b.Stages & stage) == 0) continue;
                if (b.Slot.Index < lo || b.Slot.Index >= lo + (uint)count) continue;

                _resources[b.Slot.Index - lo] = b.Resource;
            }
        }

        /// <summary>
        /// THE BIND-TIME ARITHMETIC, and the one place it happens. The window a set resolved at creation, the
        /// per-draw dynamic offset and the uniform ring's per-frame base are three byte values against the same
        /// buffer, and they simply add (decision U1). The ring's base is read HERE, at bind, rather than baked
        /// into the set, which is what keeps the pinned <see cref="GpuBufferRange"/> of decision U3 valid across
        /// all 68 load-time call sites that build one.
        /// <para>
        /// A FULL-RANGE BIND TAKES THE SAME PATH, deliberately. It is tempting to branch on
        /// <see cref="D3D11BoundResource.IsFullRange"/> and use the plain <c>*SetConstantBuffers</c> there, and it
        /// would be wrong the moment the buffer is ring-backed: the frame base is an addend on every bind, so a
        /// full-range bind of a ring-backed buffer still starts at a non-zero constant. Decision R7 says every
        /// bind goes through the <c>1</c> overload for exactly that reason, and one path means one place for the
        /// arithmetic to be wrong in.
        /// </para>
        /// </summary>
        static D3D11ConstantBufferBind ConstantBind(in D3D11BoundResource binding, uint dynamicOffsetBytes)
        {
            uint frameBase = binding.Buffer is ID3D11RingBacked { Ring: { } ring } ? ring.CurrentFrameBaseBytes : 0u;
            uint dynamic = binding.Dynamic ? dynamicOffsetBytes : 0u;

            return new D3D11ConstantBufferBind(
                binding.Buffer,
                D3D11ConstantRange.FirstConstant(frameBase, binding.OffsetBytes, dynamic),
                D3D11ConstantRange.ConstantCount(binding.SizeBytes));
        }

        static bool IsDynamicConstantBuffer(in D3D11BoundResource binding)
            => binding.Dynamic && binding.Slot.File == D3D11RegisterFile.ConstantBuffer;
    }
}
