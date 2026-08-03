using System;
using System.Numerics;

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

        // The two arguments that ride a state object rather than being part of it (issue #454). They are VALUES,
        // so they sit beside the object array rather than in it: boxing a blend factor to store it as an object?
        // would allocate on every pipeline bind, which is the cost this whole cache exists to avoid.
        Vector4 _blendFactor = ClearedBlendFactor;
        uint _stencilReference;

        /// <summary>
        /// Build the device's one state object.
        /// </summary>
        /// <param name="binds">The device's one bind flush, or null for the default one (no
        /// <c>!DriverCommandLists</c> workaround and no ring unmap, which is every device-free test and the
        /// deferred driver). It is composed HERE rather than handed to each emitter beside the state, so "the
        /// device owns exactly one" is one object to get right instead of two that could be paired up wrongly, and
        /// so <see cref="Reset"/> drops the schedule and the caches together.</param>
        internal D3D11DeviceState(D3D11BindFlush? binds = null) => Binds = binds ?? new D3D11BindFlush();

        /// <summary>
        /// THE BIND FLUSH OF DECISION R5, one per device, reached through the state every emitter already holds.
        /// The resource-set half of what is bound on the context, exactly as the seven cache slots above are the
        /// pipeline half.
        /// </summary>
        internal D3D11BindFlush Binds { get; }

        /// <summary>
        /// THE INPUT ASSEMBLER'S HALF, one per device, reached through the state every emitter already holds. The
        /// vertex streams carry decision R6's batching rule and the index buffer its redundancy cache, and
        /// <see cref="Reset"/> drops them with everything else because <c>ClearState</c> unbinds them too.
        /// </summary>
        internal D3D11VertexStreams Vertices { get; } = new();

        /// <summary>The framebuffer the context is rendering into, or null when nothing is bound.</summary>
        internal IGpuFramebuffer? BoundFramebuffer => _framebuffer;

        /// <summary>The blend factor <c>OMSetBlendState</c> was last issued with. Part of the blend cache KEY, not
        /// a separate tracked state: see <see cref="ID3D11PipelineState.BlendState"/>.</summary>
        internal Vector4 BoundBlendFactor => _blendFactor;

        /// <summary>The stencil reference <c>OMSetDepthStencilState</c> was last issued with, and the other half
        /// of that decision.</summary>
        internal uint BoundStencilReference => _stencilReference;

        /// <summary>
        /// WHAT A <c>ClearState</c> LEAVES THE BLEND FACTOR AT, which is white rather than zero. Direct3D 11
        /// documents the default blend factor as (1, 1, 1, 1) and the default stencil reference as 0, so
        /// <see cref="Reset"/> restores exactly those: the cache has to describe what the context actually holds,
        /// or the first bind after a replay boundary either issues a call it did not need or skips one it did.
        /// </summary>
        internal static Vector4 ClearedBlendFactor => Vector4.One;

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
        /// <para>
        /// THE BIND FLUSH IS RESET WITH IT, which is the same reason a second time. After a <c>ClearState</c> the
        /// context holds no resource sets either, so a keyed record that still described the last replay would let
        /// a rebind of the same set at the same offset be marked clean and the draw would run against registers
        /// holding nothing. The current pipeline's layouts go too, since <c>ClearState</c> unbinds the shaders and
        /// a retained layout array would number the next replay's first set under the last replay's pipeline.
        /// </para>
        /// <para>
        /// THE INPUT ASSEMBLER GOES WITH THEM, for the third time the same reason, and the two VALUES beside the
        /// object array are restored to what <c>ClearState</c> actually leaves rather than to zero: the blend
        /// factor is white and the stencil reference is 0 (see <see cref="ClearedBlendFactor"/>). Clearing them to
        /// default would make the cache claim a factor the context does not have, and the first pipeline of the
        /// next replay that happens to want white would then skip a call it needs.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_bound);
            _topology = 0u;
            _framebuffer = null;
            _blendFactor = ClearedBlendFactor;
            _stencilReference = 0u;
            Binds.Reset();
            Vertices.Reset();
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
        /// THE WIDENED BLEND KEY (issue #454): true when either the state OBJECT or the blend FACTOR differs from
        /// what <c>OMSetBlendState</c> was last issued with, because the call takes both and the factor rides the
        /// pipeline.
        /// <para>
        /// Both halves are compared and both are stored even when only one changed, which is why this is one
        /// method rather than two calls the caller combines: an early return on the object compare would leave the
        /// factor cache describing a factor the context no longer has the next time the objects happen to match.
        /// </para>
        /// </summary>
        internal bool BindBlendState(object? blendState, Vector4 blendFactor)
        {
            bool changed = Bind(D3D11StateSlot.BlendState, blendState);
            if (_blendFactor != blendFactor)
            {
                _blendFactor = blendFactor;
                changed = true;
            }

            return changed;
        }

        /// <summary>THE WIDENED DEPTH-STENCIL KEY, the same decision on the other pair: <c>OMSetDepthStencilState</c>
        /// takes the state object and a stencil REFERENCE, so two pipelines sharing one object and differing in
        /// reference are two binds.</summary>
        internal bool BindDepthStencilState(object? depthStencilState, uint stencilReference)
        {
            bool changed = Bind(D3D11StateSlot.DepthStencilState, depthStencilState);
            if (_stencilReference != stencilReference)
            {
                _stencilReference = stencilReference;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// One pipeline switch against all seven caches, answering exactly which of them changed. This is the
        /// call 5.3's "a rebind to the same state costs nothing" is made of, and it is deliberately per-object
        /// rather than per-pipeline: switching between two pipelines that share a blend state, a depth-stencil
        /// state and an input layout costs the shaders and nothing else.
        /// <para>
        /// TWO OF THE SEVEN ARE KEYED ON A PAIR (issue #454). <c>OMSetBlendState</c> and
        /// <c>OMSetDepthStencilState</c> take an argument that rides the pipeline alongside the state object, so
        /// the key for those two is (object, factor) and (object, reference). The alternative on the table was to
        /// re-emit both unconditionally on every pipeline bind, and it was rejected because it makes a REDUNDANT
        /// pipeline bind cost two native calls, which contradicts 5.3's "a rebind to the same state costs nothing"
        /// and turns a defensive rebind between two draws back into the #418 shape this cache exists to kill.
        /// Widening the key pays a value compare instead, on a path that already does six reference compares.
        /// </para>
        /// <para>
        /// IT ALSO ADOPTS THE PIPELINE'S VERTEX STRIDES, which is not a cache and is why it is not in the returned
        /// flags. A stride is an argument of <c>IASetVertexBuffers</c>, so a switch to a pipeline with a different
        /// stride array leaves every bound stream owing a re-issue at the next draw. See
        /// <see cref="D3D11VertexStreams.AdoptStrides"/>.
        /// </para>
        /// </summary>
        internal D3D11StateChange BindPipeline(ID3D11PipelineState pipeline)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));

            D3D11StateChange changed = D3D11StateChange.None;
            if (Bind(D3D11StateSlot.VertexShader, pipeline.VertexShader))
                changed |= D3D11StateChange.VertexShader;
            if (Bind(D3D11StateSlot.PixelShader, pipeline.PixelShader))
                changed |= D3D11StateChange.PixelShader;
            if (BindBlendState(pipeline.BlendState, pipeline.BlendFactor))
                changed |= D3D11StateChange.BlendState;
            if (BindDepthStencilState(pipeline.DepthStencilState, pipeline.StencilReference))
                changed |= D3D11StateChange.DepthStencilState;
            if (Bind(D3D11StateSlot.RasterizerState, pipeline.RasterizerState))
                changed |= D3D11StateChange.RasterizerState;
            if (Bind(D3D11StateSlot.InputLayout, pipeline.InputLayout))
                changed |= D3D11StateChange.InputLayout;
            if (BindTopology(pipeline.PrimitiveTopology))
                changed |= D3D11StateChange.PrimitiveTopology;

            Vertices.AdoptStrides(pipeline.VertexStrides);
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
        /// <para>
        /// THE VERTEX SLOTS COME BACK AS A SPAN rather than as one flag per slot, because unbinding them is ONE
        /// <c>IASetVertexBuffers</c> over a contiguous range and a flags enum has no room for 32 of anything. The
        /// span is meaningful only when the answer carries <see cref="D3D11StateChange.VertexBuffers"/>.
        /// </para>
        /// </summary>
        /// <param name="resource">The resource being disposed.</param>
        /// <param name="vertexStartSlot">The first vertex slot that named it, when the answer carries
        /// <see cref="D3D11StateChange.VertexBuffers"/>.</param>
        /// <param name="vertexCount">How many slots the unbind spans from there.</param>
        internal D3D11StateChange Scrub(object resource, out uint vertexStartSlot, out int vertexCount)
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

            // The blend factor and the stencil reference are arguments of the two calls whose objects may have
            // just been scrubbed, and the unbind the caller is about to issue passes the CLEARED values, so the
            // cache follows it. Without this a rebind of another pipeline that happens to want the old factor
            // would compare equal and skip a call the context needs.
            if ((scrubbed & D3D11StateChange.BlendState) != 0) _blendFactor = ClearedBlendFactor;
            if ((scrubbed & D3D11StateChange.DepthStencilState) != 0) _stencilReference = 0u;

            return scrubbed | Vertices.Scrub(resource, out vertexStartSlot, out vertexCount);
        }
    }
}
