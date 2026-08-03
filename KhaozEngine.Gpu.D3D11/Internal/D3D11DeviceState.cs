using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHAT IS BOUND ON THE DEVICE CONTEXT, and the one object every emitter value points at. This is where
    /// decision R6's redundancy caches live, where decision W6's framebuffer identity guard lives, and where
    /// decision R8's precise scrub is taken. One instance per device, never one per command list.
    /// <para>
    /// THE DEVICE OWNS IT, AND THAT IS THE WHOLE POINT (issue #476). An emitter is a readonly struct that the
    /// recorder stores BY VALUE, one copy per list, so N lists over one <c>ID3D11DeviceContext</c> hold N copies
    /// of the struct. A cache describes what is bound on the CONTEXT rather than what one list recorded, so a
    /// cache carried inline in the struct would be per-list on the immediate driver and per-device on the
    /// deferred one: list B binds pipeline P, list A's copy still claims A's pipeline is current, and A skips the
    /// rebind and draws with B's state. Nothing throws and nothing logs. The seam's readonly-struct rule is what
    /// forces the state behind a class reference, and this is that class. An emitter must RECEIVE one rather than
    /// construct its own, because a struct that allocates its own state in its constructor satisfies the readonly
    /// rule and reintroduces the same defect.
    /// </para>
    /// <para>
    /// NOTHING HERE ISSUES A NATIVE CALL, and nothing here names a Direct3D type. It answers what changed and the
    /// caller issues the calls, which is what lets the real emitter and the device-free
    /// <see cref="D3D11NativeTraceEmitter"/> share ONE implementation of the guards rather than two that drift.
    /// It also means every rule below is testable under a plain <c>dotnet test</c> on macOS.
    /// </para>
    /// <para>
    /// NOT THREAD-SAFE, and it does not need to be on either driver, for two different reasons. Under the
    /// deferred driver every mutation here happens inside a replay, which decision W4's one submit lock covers
    /// along with present and the resize apply, and recording is lock-free precisely because it never touches
    /// this object: a recorder appends to its own array and meets an emitter only at submit. Under
    /// <c>KE_D3D11_RECORD=immediate</c> that is inverted, since every mutation happens during lock-free RECORD
    /// and the submit lock guards nothing here at all. What makes both safe is decision W5 rather than the
    /// lock: concurrent recording is structurally permitted and unsupported in v1, so one thread records at a
    /// time and nothing else reaches this object.
    /// </para>
    /// </summary>
    internal sealed class D3D11DeviceState
    {
        // The cache array is exactly as long as D3D11StateSlot, read off the enum rather than written out as a
        // literal beside it. A hand-kept 6 stays green through every test here the day a seventh slot is added,
        // because nothing in this file mentions the enum's length, and the first Bind of that slot throws
        // IndexOutOfRangeException from inside a replay. The enum is contiguous from zero, which FlagOf's shift
        // already depends on, so its member count IS the array length.
        static readonly int SlotCount = Enum.GetValues<D3D11StateSlot>().Length;

        readonly object?[] _bound = new object?[SlotCount];
        uint _topology;
        IGpuFramebuffer? _framebuffer;

        /// <summary>The framebuffer the context is rendering into, or null when nothing is bound.</summary>
        internal IGpuFramebuffer? BoundFramebuffer => _framebuffer;

        /// <summary>The object currently cached in <paramref name="slot"/>. Present for tests and for a scrub to
        /// read, never needed to decide a bind: <see cref="Bind"/> answers that.</summary>
        internal object? Bound(D3D11StateSlot slot) => _bound[(int)slot];

        /// <summary>The cached primitive topology, zero meaning <c>UNDEFINED</c>.</summary>
        internal uint BoundTopology => _topology;

        /// <summary>
        /// FORGET EVERYTHING, which is exactly what the ONE <c>ClearState</c> at the head of each replay does to
        /// the context (decision R3). Called by the emitter's <c>Begin</c>, immediately after the call, so the
        /// cache and the context agree at the only moment they are guaranteed to.
        /// <para>
        /// THE FRAMEBUFFER IS CLEARED TOO, and forgetting to would be the subtle half of this method.
        /// <c>ClearState</c> unbinds the render targets and drops the viewport and the scissor, so a cache that
        /// still claimed the framebuffer was bound would let the next <c>SetFramebuffer</c> take W6's redundant
        /// path and issue nothing. The frame would then rasterise into no target with no viewport, which is the
        /// failure mode 9.4 describes for missing the implicit behaviour entirely.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_bound);
            _topology = 0u;
            _framebuffer = null;
        }

        /// <summary>
        /// DECISION W6's IDENTITY GUARD: true when <paramref name="framebuffer"/> is not the one already bound,
        /// which is the only case that earns an <c>OMSetRenderTargets</c> plus the full <c>RSSetViewports</c> and
        /// <c>RSSetScissorRects</c> that come with it.
        /// <para>
        /// A redundant re-bind answers false and MUST issue nothing, viewport and scissor included. The shipped
        /// sequence <c>SetFramebuffer(fb)</c>, <c>SetScissorRect(...)</c>, draw, <c>SetFramebuffer(fb)</c>, draw
        /// is what turns that from a saved call into a correctness rule: an unconditional emit would restore the
        /// full scissor on the second bind and the second draw would render outside the intended rectangle. That
        /// is golden-visible, and the incumbent's <c>SetFramebuffer</c> carries the same guard.
        /// </para>
        /// </summary>
        internal bool BindFramebuffer(IGpuFramebuffer framebuffer)
        {
            if (framebuffer is null) throw new ArgumentNullException(nameof(framebuffer));
            if (ReferenceEquals(_framebuffer, framebuffer)) return false;

            _framebuffer = framebuffer;
            return true;
        }

        /// <summary>
        /// DECISION R6, one slot at a time: true when <paramref name="value"/> is not what is already cached, so
        /// the caller issues the native call, and false when the rebind is redundant and costs nothing.
        /// <para>
        /// A null <paramref name="value"/> over a non-null one is a CHANGE, because unbinding is a native call
        /// like any other. Null over null is redundant, which is what makes a scrub followed by a rebind of
        /// something else behave the way the context does.
        /// </para>
        /// </summary>
        internal bool Bind(D3D11StateSlot slot, object? value)
        {
            if (ReferenceEquals(_bound[(int)slot], value)) return false;

            _bound[(int)slot] = value;
            return true;
        }

        /// <summary>The flag that names <paramref name="slot"/>. The two enums are deliberately parallel, slot
        /// <c>n</c> being bit <c>n</c>, so a walk over the cache array turns into flags without a lookup table.
        /// That parallel is a coupling rather than a coincidence, so a test pins it by NAME: adding a slot and
        /// forgetting its flag would silently report the wrong slot as changed.</summary>
        internal static D3D11StateChange FlagOf(D3D11StateSlot slot) => (D3D11StateChange)(1 << (int)slot);

        /// <summary>The topology's own <see cref="Bind"/>, kept separate so a topology never has to be boxed to
        /// be compared.</summary>
        internal bool BindTopology(uint topology)
        {
            if (_topology == topology) return false;

            _topology = topology;
            return true;
        }

        /// <summary>
        /// One pipeline switch against all seven caches, answering exactly which of them changed. This is the
        /// call 5.3's "a rebind to the same state costs nothing" is made of, and it is deliberately per-object
        /// rather than per-pipeline: switching between two pipelines that share a blend state, a depth-stencil
        /// state and an input layout costs the shaders and nothing else.
        /// </summary>
        internal D3D11StateChange BindPipeline(ID3D11PipelineState pipeline)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));

            D3D11StateChange changed = D3D11StateChange.None;
            if (Bind(D3D11StateSlot.VertexShader, pipeline.VertexShader))
                changed |= D3D11StateChange.VertexShader;
            if (Bind(D3D11StateSlot.PixelShader, pipeline.PixelShader))
                changed |= D3D11StateChange.PixelShader;
            if (Bind(D3D11StateSlot.BlendState, pipeline.BlendState))
                changed |= D3D11StateChange.BlendState;
            if (Bind(D3D11StateSlot.DepthStencilState, pipeline.DepthStencilState))
                changed |= D3D11StateChange.DepthStencilState;
            if (Bind(D3D11StateSlot.RasterizerState, pipeline.RasterizerState))
                changed |= D3D11StateChange.RasterizerState;
            if (Bind(D3D11StateSlot.InputLayout, pipeline.InputLayout))
                changed |= D3D11StateChange.InputLayout;
            if (BindTopology(pipeline.PrimitiveTopology))
                changed |= D3D11StateChange.PrimitiveTopology;

            return changed;
        }

        /// <summary>
        /// DECISION R8: forget <paramref name="resource"/> everywhere it is cached, and report exactly where it
        /// was so the caller unbinds those slots and no others. Called when a resource is disposed, which is the
        /// one moment a cache can be left describing an object that no longer exists.
        /// <para>
        /// A wholesale <c>ClearState</c> would also be correct and is rejected: it unbinds every slot, every
        /// shader resource and the render targets to drop ONE object, so the next draw pays a full rebind for a
        /// disposal that touched one shader. The single <c>ClearState</c> of R3 covers the start of a replay and
        /// says nothing about the middle of one, which is where a disposal lands.
        /// </para>
        /// <para>
        /// Answering <see cref="D3D11StateChange.None"/> is the common case and is not a failure. Most disposed
        /// resources were never bound, or were replaced long before they were disposed.
        /// </para>
        /// </summary>
        internal D3D11StateChange Scrub(object resource)
        {
            if (resource is null) throw new ArgumentNullException(nameof(resource));

            D3D11StateChange scrubbed = D3D11StateChange.None;
            for (int i = 0; i < _bound.Length; i++)
            {
                if (!ReferenceEquals(_bound[i], resource)) continue;

                _bound[i] = null;
                scrubbed |= FlagOf((D3D11StateSlot)i);
            }

            if (ReferenceEquals(_framebuffer, resource))
            {
                _framebuffer = null;
                scrubbed |= D3D11StateChange.Framebuffer;
            }

            return scrubbed;
        }
    }
}
