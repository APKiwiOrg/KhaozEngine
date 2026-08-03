using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHICH OF THE TWO DIRTY ARRAYS A SLOT BELONGS TO. The bind flush keeps a separate record per arm (decision
    /// C1's "same schedule with a separate compute dirty array"), so a slot number alone does not name a slot: the
    /// auto-unbind has to say which arm's record it just invalidated, and the whole point of it is that the answer
    /// is usually the OTHER one from the arm that caused it.
    /// </summary>
    internal enum D3D11PipelineArm
    {
        /// <summary>The graphics record, flushed by a draw.</summary>
        Graphics,

        /// <summary>The compute record, flushed by a dispatch.</summary>
        Compute,
    }

    /// <summary>One slot the auto-unbind left owing a full activation: the arm's record it lives in, and the slot
    /// index inside it.</summary>
    internal readonly struct D3D11SlotRaise
    {
        internal D3D11SlotRaise(D3D11PipelineArm arm, uint slot)
        {
            Arm = arm;
            Slot = slot;
        }

        /// <summary>Which record the slot lives in.</summary>
        internal D3D11PipelineArm Arm { get; }

        /// <summary>The slot index inside that record.</summary>
        internal uint Slot { get; }
    }

    /// <summary>
    /// DECISION C1, THE SRV-VERSUS-UAV AUTO-UNBIND, IN BOTH DIRECTIONS. Direct3D 11 will not let one resource be
    /// readable through a <c>t</c> register and writable through a <c>u</c> register at the same time, and the GPU
    /// seam's ordering contract names THIS as the D3D11 mechanism behind rule 1 in as many words: "Direct3D11
    /// unbinds the UAV as the SRV is bound" (<c>GpuInterfaces.cs</c>, the compute ordering contract). So the
    /// backend owes that unbind, and it owes it in both directions, because the ocean's ping-pong binds the same
    /// buffer as a UAV in one stage of a chain and as an SRV in the next.
    /// <para>
    /// IT LIVES WHERE THE BIND ARRAYS ARE ASSEMBLED, which is the half of C1 that is a decision rather than a
    /// mechanism. <see cref="D3D11SetActivation"/> already fills one contiguous array per register file per stage,
    /// so the conflicting register is nulled INTO an array call of the same shape, issued inside the same
    /// activation, immediately before the bind that conflicts with it. Under array batching that costs one call per
    /// (file, stage) no matter how many registers conflict, which is what "costs nothing extra" in the spec means:
    /// the unbind obeys the same O(kinds x stages) law as the bind rather than adding a call per register.
    /// </para>
    /// <para>
    /// AND THAT IS THE ONE PLACE IT DIFFERS FROM THE FORK, deliberately. Veldrid's
    /// <c>D3D11CommandList.UnbindSRVTexture</c> nulls the slot and then marks the OWNING RESOURCE SET fully dirty,
    /// so the next draw re-runs that set's entire fan-out to put one register back. That is correct and it pays a
    /// full activation for a single register. Here the null goes out in the batch that is already being assembled,
    /// and the raise-to-dirty is what covers the OTHER arm, whose flush is not happening now. See
    /// <see cref="D3D11BindFlush.Raise"/> for that half.
    /// </para>
    /// <para>
    /// A HOLE INSIDE AN UNBIND SPAN IS REBOUND TO WHAT IT ALREADY HOLDS, never nulled, which is the same rule the
    /// batched vertex flush follows and for the same reason. The span runs from the lowest conflicting register to
    /// the highest, and a non-conflicting register caught between two of them belongs to something else that is
    /// still being read. Writing a null across it would unbind a live resource behind its owner's back, and the
    /// owner's record would still call that slot clean.
    /// </para>
    /// <para>
    /// IDENTITY IS THE UNDERLYING RESOURCE, not the value the caller bound. <see cref="GpuBufferRange"/> is a
    /// readonly STRUCT that implements <see cref="IGpuBindableResource"/>, so storing one in a resource set boxes
    /// it and two boxes of the same window are two different references. A structured buffer bound as a bare
    /// buffer at a <c>u</c> register and as a range at a <c>t</c> register is one resource and one hazard, so the
    /// range is unwrapped to its buffer before anything is compared.
    /// </para>
    /// <para>
    /// A SET THAT BINDS ONE RESOURCE BOTH WAYS AT ONCE resolves deterministically rather than being refused: the
    /// activation issues the <c>t</c> file before the <c>u</c> file, so the UAV wins and the SRV is nulled. That is
    /// the case <see cref="D3D11BindFlush"/>'s rule 4 already calls "a resource bound two incompatible ways at
    /// once, which Direct3D 11 cannot honour either way", and a deterministic answer is worth more here than a
    /// throw: the same stream would otherwise pass on the other two backends and fail on this one.
    /// </para>
    /// <para>
    /// NOTHING HERE ISSUES A NATIVE CALL AND NOTHING HERE NAMES A DIRECT3D TYPE, exactly like the flush and the
    /// activation it belongs to. The calls go to an <see cref="ID3D11BindSink"/>, so the whole of C1 is a
    /// device-free <c>[Fact]</c> over the trace emitter and the shipped logic is what the test drives.
    /// </para>
    /// <para>
    /// NOT THREAD-SAFE, on the same grounds as <see cref="D3D11BindFlush"/>: one per device, mutated only from
    /// inside a flush, and decision W5 has one thread recording at a time.
    /// </para>
    /// </summary>
    internal sealed class D3D11ViewConflicts
    {
        // Every stage a Direct3D 11 setter exists for, in the order D3D11SetActivation walks them, so a trace
        // reads the same way whichever type produced the call. Iterated in full rather than trimmed to the three
        // the engine uses, for the reason stated there.
        static readonly GpuShaderStages[] Stages =
        {
            GpuShaderStages.Vertex,
            GpuShaderStages.TessellationControl,
            GpuShaderStages.TessellationEvaluation,
            GpuShaderStages.Geometry,
            GpuShaderStages.Fragment,
            GpuShaderStages.Compute,
        };

        // Registers per stage the tracker currently covers. Grown by doubling when a bind names a higher one, so a
        // pipeline that reaches t20 reallocates once rather than per bind. Direct3D 11 caps 't' at 128 and 'u' at
        // 8, so this converges immediately in practice.
        int _stride = 16;

        Entry[] _shaderResources;
        Entry[] _unorderedAccess;

        // The span handed to the sink for one unbind: nulls at the conflicting registers, the tracker's own record
        // everywhere else. Owned here and grown once, so an unbind allocates nothing.
        IGpuBindableResource?[] _scratch = new IGpuBindableResource?[8];

        D3D11SlotRaise[] _raises = new D3D11SlotRaise[4];
        int _raiseCount;

        internal D3D11ViewConflicts()
        {
            _shaderResources = new Entry[Stages.Length * _stride];
            _unorderedAccess = new Entry[Stages.Length * _stride];
        }

        /// <summary>How many slots the auto-unbind is currently asking the flush to re-activate. Present for a
        /// test and for a diagnostic: the flush drains this to zero at every activation.</summary>
        internal int PendingRaiseCount => _raiseCount;

        /// <summary>
        /// FORGET EVERY TRACKED REGISTER, which is what the one <c>ClearState</c> at the head of a replay does to
        /// the context. Called from <see cref="D3D11BindFlush.Reset"/> so the tracker cannot outlive the records it
        /// raises: a tracker that still described the last replay would null a register nothing is bound at, and
        /// raise a slot whose record has just been wiped.
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_shaderResources);
            Array.Clear(_unorderedAccess);
            _raiseCount = 0;
        }

        /// <summary>
        /// C1 ITSELF: null every register of the file OPPOSITE <paramref name="incomingFile"/> that currently holds
        /// any resource in <paramref name="incoming"/>, one array call per stage that has one, and leave the owning
        /// slot owing a full activation.
        /// <para>
        /// EVERY STAGE IS SCANNED, not just the stage the incoming bind names, and that is the correctness of it
        /// rather than thoroughness. The hazard is a property of the RESOURCE and of the whole pipeline: a storage
        /// texture written through <c>u0</c> at the compute stage is just as unbindable when the same texture
        /// arrives at <c>t3</c> of the PIXEL stage, which is exactly the handoff rule 1 describes.
        /// </para>
        /// <para>
        /// Called BEFORE the bind it protects, because Direct3D 11 would otherwise resolve the conflict itself, by
        /// dropping the incoming bind and writing a debug-layer warning nobody reads. Doing it explicitly is what
        /// keeps this backend's own record of what is bound true, which is what every redundancy decision rests on.
        /// </para>
        /// </summary>
        internal void UnbindConflicts<TSink>(ref TSink sink, D3D11RegisterFile incomingFile,
            ReadOnlySpan<IGpuBindableResource?> incoming)
            where TSink : struct, ID3D11BindSink
        {
            D3D11RegisterFile opposite = Opposite(incomingFile);
            Entry[] entries = EntriesFor(opposite);

            for (int s = 0; s < Stages.Length; s++)
            {
                int row = s * _stride;
                if (!ConflictSpan(entries, row, _stride, incoming, out int lo, out int hi)) continue;

                int count = hi - lo + 1;
                if (_scratch.Length < count)
                    _scratch = new IGpuBindableResource?[D3D11BindResolve.RoundedCapacity(count)];

                for (int i = 0; i < count; i++)
                {
                    ref Entry entry = ref entries[row + lo + i];
                    if (!Conflicts(in entry, incoming))
                    {
                        // A live register swept in by the span. Rebound to exactly what it already holds, never
                        // nulled: the span is a batching decision and must not unbind something else's resource.
                        _scratch[i] = entry.Bound;
                        continue;
                    }

                    _scratch[i] = null;
                    Raise(entry.Arm, entry.Slot);
                    entry = default;
                }

                Issue(ref sink, opposite, Stages[s], (uint)lo, _scratch.AsSpan(0, count));
            }
        }

        /// <summary>
        /// Note what an array bind has just put into a contiguous register span, so a later conflicting bind can
        /// find it. <paramref name="arm"/> and <paramref name="slot"/> are the resource-set slot the bind came
        /// from, which is what an unbind raises back to dirty.
        /// <para>
        /// Only the two files that can conflict are tracked. Constant buffers and samplers are recorded by nobody,
        /// because neither can be a write target and therefore neither can be half of a hazard.
        /// </para>
        /// </summary>
        internal void Record(D3D11RegisterFile file, GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<IGpuBindableResource?> resources, D3D11PipelineArm arm, uint slot)
        {
            if (file != D3D11RegisterFile.ShaderResource && file != D3D11RegisterFile.UnorderedAccess) return;
            if (resources.Length == 0) return;

            int stageIndex = IndexOf(stage);
            if (stageIndex < 0) return;

            EnsureStride(startSlot + (uint)resources.Length);

            Entry[] entries = EntriesFor(file);
            int row = stageIndex * _stride + (int)startSlot;
            for (int i = 0; i < resources.Length; i++)
            {
                entries[row + i] = new Entry(resources[i], Identity(resources[i]), arm, slot);
            }
        }

        /// <summary>
        /// The slots the last unbind invalidated, and CLEARING them as it answers. The flush drains this
        /// immediately after every activation, so the list never grows past the handful of registers one bind can
        /// conflict with.
        /// </summary>
        internal ReadOnlySpan<D3D11SlotRaise> TakeRaises()
        {
            ReadOnlySpan<D3D11SlotRaise> raises = _raises.AsSpan(0, _raiseCount);
            _raiseCount = 0;
            return raises;
        }

        /// <summary>The resource identity a bound value carries, with a <see cref="GpuBufferRange"/> unwrapped to
        /// its buffer. See the type remarks for why the unwrap is not optional.</summary>
        internal static object? Identity(IGpuBindableResource? resource) => resource switch
        {
            null => null,
            GpuBufferRange range => range.Buffer,
            _ => resource,
        };

        // The lowest and highest register of this stage's row that any incoming resource conflicts with, or false
        // when none does, which is the overwhelmingly common case and the reason this is a scan rather than a map.
        static bool ConflictSpan(Entry[] entries, int row, int stride,
            ReadOnlySpan<IGpuBindableResource?> incoming, out int lo, out int hi)
        {
            lo = -1;
            hi = -1;

            for (int r = 0; r < stride; r++)
            {
                if (!Conflicts(in entries[row + r], incoming)) continue;

                if (lo < 0) lo = r;
                hi = r;
            }

            return lo >= 0;
        }

        static bool Conflicts(in Entry entry, ReadOnlySpan<IGpuBindableResource?> incoming)
        {
            if (entry.Identity is null) return false;

            for (int i = 0; i < incoming.Length; i++)
            {
                if (ReferenceEquals(Identity(incoming[i]), entry.Identity)) return true;
            }

            return false;
        }

        static void Issue<TSink>(ref TSink sink, D3D11RegisterFile file, GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<IGpuBindableResource?> resources)
            where TSink : struct, ID3D11BindSink
        {
            if (file == D3D11RegisterFile.ShaderResource)
            {
                sink.SetShaderResources(stage, startSlot, resources);
                return;
            }

            sink.SetUnorderedAccessViews(stage, startSlot, resources);
        }

        static D3D11RegisterFile Opposite(D3D11RegisterFile file)
            => file == D3D11RegisterFile.ShaderResource
                ? D3D11RegisterFile.UnorderedAccess
                : D3D11RegisterFile.ShaderResource;

        Entry[] EntriesFor(D3D11RegisterFile file)
            => file == D3D11RegisterFile.ShaderResource ? _shaderResources : _unorderedAccess;

        // A raise is recorded once per (arm, slot). One unbind span can null several registers belonging to the
        // same set, and re-raising the same slot would grow the list without changing what the next flush does.
        void Raise(D3D11PipelineArm arm, uint slot)
        {
            for (int i = 0; i < _raiseCount; i++)
            {
                if (_raises[i].Arm == arm && _raises[i].Slot == slot) return;
            }

            if (_raiseCount == _raises.Length) Array.Resize(ref _raises, _raises.Length * 2);
            _raises[_raiseCount++] = new D3D11SlotRaise(arm, slot);
        }

        // Widen every stage's row to cover a register, preserving what each row already holds. Reached only by a
        // bind naming a register past the current width, which converges after the first frame.
        void EnsureStride(uint registersNeeded)
        {
            if (registersNeeded <= (uint)_stride) return;

            int stride = _stride;
            while ((uint)stride < registersNeeded) stride <<= 1;

            _shaderResources = Widen(_shaderResources, _stride, stride);
            _unorderedAccess = Widen(_unorderedAccess, _stride, stride);
            _stride = stride;
        }

        static Entry[] Widen(Entry[] entries, int oldStride, int newStride)
        {
            var widened = new Entry[Stages.Length * newStride];
            for (int s = 0; s < Stages.Length; s++)
            {
                Array.Copy(entries, s * oldStride, widened, s * newStride, oldStride);
            }

            return widened;
        }

        static int IndexOf(GpuShaderStages stage)
        {
            for (int i = 0; i < Stages.Length; i++)
            {
                if (Stages[i] == stage) return i;
            }

            return -1;
        }

        /// <summary>One tracked register: what is bound there, its unwrapped identity, and the resource-set slot
        /// that put it there.</summary>
        readonly struct Entry
        {
            internal Entry(IGpuBindableResource? bound, object? identity, D3D11PipelineArm arm, uint slot)
            {
                Bound = bound;
                Identity = identity;
                Arm = arm;
                Slot = slot;
            }

            /// <summary>The value as it was handed to the sink, which is what a hole inside an unbind span is
            /// rebound to.</summary>
            internal IGpuBindableResource? Bound { get; }

            /// <summary>The unwrapped resource, which is what a conflict is decided on.</summary>
            internal object? Identity { get; }

            /// <summary>Which record the binding slot lives in.</summary>
            internal D3D11PipelineArm Arm { get; }

            /// <summary>The resource-set slot the binding came from.</summary>
            internal uint Slot { get; }
        }
    }
}
