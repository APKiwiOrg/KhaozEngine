using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE THREE RULES THE NATIVE DIRECT3D 11 EMITTER APPLIES TO EVERY REPLAY, all of them device-free: decision
    /// R6's redundancy caches on the pipeline-level objects, decision R8's precise scrub and unbind when a bound
    /// resource is disposed, and decision W6's framebuffer-change-guarded viewport and scissor.
    /// <para>
    /// Two of these are structural invariants of decision T2's native-call budget, and they are the two this row
    /// owes: exactly one <c>RSSetViewports</c> and one <c>RSSetScissorRects</c> per framebuffer CHANGE, and zero
    /// of each for a redundant re-bind. They are asserted as call TALLIES rather than as a claim about the guard,
    /// because the guard is invisible from the seam and an unconditional emit passes every seam-level check while
    /// silently restoring the full scissor over a live one.
    /// </para>
    /// <para>
    /// Every decision under test is taken inside <see cref="D3D11DeviceState"/>, which the real emitter uses
    /// unchanged, so what these pin is the shipped guard rather than a copy of it living in a harness.
    /// </para>
    /// </summary>
    public sealed class D3D11StateCacheTests
    {
        // ---- Decision W6: the implicit viewport, and the identity guard that makes it correct ----

        /// <summary>
        /// A FRAMEBUFFER CHANGE IS THREE CALLS. There is no <c>SetViewport</c> on the seam at all, so a backend
        /// that does not replicate Veldrid's auto-applied full viewport and full scissor on a framebuffer bind
        /// rasterises nothing. One viewport call and one scissor call per change is the first half of the pair of
        /// tally assertions section 9.4 asks for.
        /// </summary>
        [Fact]
        public void AFramebufferChange_EmitsTheTargetsPlusExactlyOneViewportAndOneScissor()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeFramebuffer target = Framebuffer(640, 480);

            emitter.Begin();
            emitter.SetFramebuffer(target);

            Assert.Equal(1, log.Count(D3D11NativeCall.OMSetRenderTargets));
            Assert.Equal(1, log.Count(D3D11NativeCall.RSSetViewports));
            Assert.Equal(1, log.Count(D3D11NativeCall.RSSetScissorRects));
            Assert.Equal(
                new[]
                {
                    "ClearState()",
                    $"OMSetRenderTargets({log.Id(target)})",
                    "RSSetViewports(1,0,0,640,480,0,1)",
                    "RSSetScissorRects(all:1,0,0,640,480)",
                },
                log.Trace);
        }

        /// <summary>
        /// THE OTHER HALF, and the one that is a correctness rule rather than a saved call: re-binding the SAME
        /// framebuffer issues nothing at all. Veldrid's <c>SetFramebuffer</c> is wrapped in
        /// <c>if (_framebuffer != fb)</c>, so a redundant bind does not reset the viewport and does not reset the
        /// scissor.
        /// </summary>
        [Fact]
        public void ARedundantFramebufferRebind_EmitsNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeFramebuffer target = Framebuffer(640, 480);

            emitter.Begin();
            emitter.SetFramebuffer(target);
            int afterFirstBind = log.TotalCalls;

            emitter.SetFramebuffer(target);
            emitter.SetFramebuffer(target);

            Assert.Equal(afterFirstBind, log.TotalCalls);
            Assert.Equal(1, log.Count(D3D11NativeCall.RSSetViewports));
            Assert.Equal(1, log.Count(D3D11NativeCall.RSSetScissorRects));
        }

        /// <summary>
        /// THE SHIPPED SEQUENCE FROM SECTION 9.4, and the frame an unconditional emit would get wrong:
        /// <c>SetFramebuffer(fb)</c>, <c>SetScissorRect(...)</c>, draw, <c>SetFramebuffer(fb)</c>, draw. The
        /// second bind must not restore the full scissor, or the second draw renders outside the intended
        /// rectangle, which is golden-visible and would have been frozen into the tally as correct.
        /// </summary>
        [Fact]
        public void ARedundantRebind_DoesNotUndoAnExplicitScissor()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeFramebuffer target = Framebuffer(640, 480);

            emitter.Begin();
            emitter.SetFramebuffer(target);
            emitter.SetScissorRect(0, 4, 8, 16, 32);
            emitter.Draw(3, 1, 0, 0);
            emitter.SetFramebuffer(target);
            emitter.Draw(6, 1, 0, 0);

            Assert.Equal(
                new[]
                {
                    "ClearState()",
                    $"OMSetRenderTargets({log.Id(target)})",
                    "RSSetViewports(1,0,0,640,480,0,1)",
                    "RSSetScissorRects(all:1,0,0,640,480)",
                    "RSSetScissorRects(out0:1,4,8,20,40)",
                    "DrawInstanced(3,1,0,0)",
                    "DrawInstanced(6,1,0,0)",
                },
                log.Trace);
        }

        /// <summary>A genuine change still resets the scissor, which is what makes the guard an IDENTITY guard
        /// rather than a first-bind-only one. Binding A, then B, then A again is three changes.</summary>
        [Fact]
        public void AlternatingBetweenTwoFramebuffers_EmitsOncePerChange()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeFramebuffer first = Framebuffer(640, 480);
            FakeFramebuffer second = Framebuffer(256, 256);

            emitter.Begin();
            emitter.SetFramebuffer(first);
            emitter.SetFramebuffer(second);
            emitter.SetFramebuffer(first);

            Assert.Equal(3, log.Count(D3D11NativeCall.OMSetRenderTargets));
            Assert.Equal(3, log.Count(D3D11NativeCall.RSSetViewports));
            Assert.Equal(3, log.Count(D3D11NativeCall.RSSetScissorRects));
            Assert.Contains("RSSetViewports(1,0,0,256,256,0,1)", log.Trace);
        }

        /// <summary>
        /// THE SUBTLE HALF OF THE ONE <c>ClearState</c>: it unbinds the render targets and drops the viewport, so
        /// the cache has to forget the framebuffer too. Keeping it would send the next bind down the redundant
        /// path, and the frame would rasterise into no target with no viewport, which is the failure 9.4
        /// describes for missing the implicit behaviour entirely.
        /// </summary>
        [Fact]
        public void TheClearStateOpeningAReplay_ForgetsTheBoundFramebuffer()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeFramebuffer target = Framebuffer(640, 480);

            emitter.Begin();
            emitter.SetFramebuffer(target);
            emitter.End();

            emitter.Begin();
            emitter.SetFramebuffer(target);

            Assert.Equal(2, log.Count(D3D11NativeCall.OMSetRenderTargets));
            Assert.Equal(2, log.Count(D3D11NativeCall.RSSetViewports));
            Assert.Equal(2, log.Count(D3D11NativeCall.RSSetScissorRects));
        }

        /// <summary>The full scissor IS the bound framebuffer's extent, so asking for it with nothing bound is a
        /// question with no answer rather than a no-op that silently leaves a stale rectangle.</summary>
        [Fact]
        public void SetFullScissorRects_WithNothingBound_IsRefused()
        {
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), new D3D11NativeCallLog());
            emitter.Begin();

            Assert.Throws<InvalidOperationException>(emitter.SetFullScissorRects);
        }

        // ---- Decision R6: the redundancy caches ----

        /// <summary>
        /// THE HEADLINE OF R6: a rebind of the pipeline already bound costs zero native calls. The shadow pass
        /// rebinds the same pipeline thousands of times a frame, and seven native calls each time is the shape
        /// 5.3 exists to remove.
        /// </summary>
        [Fact]
        public void RebindingTheSamePipeline_CostsNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeD3D11Pipeline pipeline = Pipeline();

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            int afterFirstBind = log.TotalCalls;

            emitter.SetPipeline(pipeline);
            emitter.SetPipeline(pipeline);

            Assert.Equal(8, afterFirstBind);   // the ClearState plus all seven state objects
            Assert.Equal(afterFirstBind, log.TotalCalls);
        }

        /// <summary>The first bind of a frame issues all seven, in cache-slot order. Spelled out rather than
        /// counted, because the order is what a reader compares against a capture.</summary>
        [Fact]
        public void TheFirstPipelineBindOfAReplay_IssuesAllSevenInSlotOrder()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeD3D11Pipeline pipeline = Pipeline(topology: 4u);

            emitter.Begin();
            emitter.SetPipeline(pipeline);

            Assert.Equal(
                new[]
                {
                    "ClearState()",
                    $"VSSetShader({log.Id(pipeline.VertexShader)})",
                    $"PSSetShader({log.Id(pipeline.PixelShader)})",
                    $"OMSetBlendState({log.Id(pipeline.BlendState)},1|1|1|1)",
                    $"OMSetDepthStencilState({log.Id(pipeline.DepthStencilState)},0)",
                    $"RSSetState({log.Id(pipeline.RasterizerState)})",
                    $"IASetInputLayout({log.Id(pipeline.InputLayout)})",
                    "IASetPrimitiveTopology(4)",
                },
                log.Trace);
        }

        /// <summary>
        /// WHY THE CACHE IS PER OBJECT AND NOT PER PIPELINE. Two pipelines routinely share a blend state, a
        /// depth-stencil state, a rasterizer state, an input layout and a topology, so the switch between them
        /// costs the two shaders and nothing else. A cache keyed on pipeline identity would issue seven calls for
        /// a change of two, which is the same fan-out defect R6 removes at the resource-set level.
        /// </summary>
        [Fact]
        public void SwitchingBetweenPipelinesThatShareState_RebindsOnlyWhatChanged()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeD3D11Pipeline first = Pipeline();
            var second = new FakeD3D11Pipeline(
                new object(), new object(),
                first.BlendState, first.DepthStencilState, first.RasterizerState, first.InputLayout,
                first.PrimitiveTopology);

            emitter.Begin();
            emitter.SetPipeline(first);
            log.Reset();

            emitter.SetPipeline(second);

            Assert.Equal(
                new[] { $"VSSetShader({log.Id(second.VertexShader)})", $"PSSetShader({log.Id(second.PixelShader)})" },
                log.Trace);
        }

        /// <summary>Topology is cached beside the six object slots, so a pipeline that differs only in topology
        /// costs exactly one call.</summary>
        [Fact]
        public void APipelineDifferingOnlyInTopology_CostsOneCall()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeD3D11Pipeline triangles = Pipeline(topology: 4u);
            var lines = new FakeD3D11Pipeline(
                triangles.VertexShader, triangles.PixelShader, triangles.BlendState,
                triangles.DepthStencilState, triangles.RasterizerState, triangles.InputLayout, 2u);

            emitter.Begin();
            emitter.SetPipeline(triangles);
            log.Reset();

            emitter.SetPipeline(lines);

            Assert.Equal(new[] { "IASetPrimitiveTopology(2)" }, log.Trace);
        }

        /// <summary>The caches are reset by the one <c>ClearState</c> per replay, which is the only thing that
        /// makes them safe: after it the context holds nothing, so a cache that still described the last frame
        /// would skip binds the context needs.</summary>
        [Fact]
        public void TheClearStateOpeningAReplay_ResetsThePipelineCaches()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeD3D11Pipeline pipeline = Pipeline();

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            emitter.End();
            log.Reset();

            emitter.Begin();
            emitter.SetPipeline(pipeline);

            Assert.Equal(8, log.TotalCalls);
            Assert.Equal(1, log.Count(D3D11NativeCall.ClearState));
            Assert.Equal(1, log.Count(D3D11NativeCall.VSSetShader));
        }

        /// <summary>A pipeline that cannot answer what it is made of would rebind everything on every draw, so it
        /// is refused by name rather than accepted as a cache miss forever.</summary>
        [Fact]
        public void APipelineWithoutTheStateContract_IsRefused()
        {
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), new D3D11NativeCallLog());
            emitter.Begin();

            Assert.Throws<ArgumentException>(() => emitter.SetPipeline(new FakePipeline()));
        }

        // ---- Decision R8: precise scrub and unbind on disposal ----

        /// <summary>
        /// DECISION R8, and the reason it is not a <c>ClearState</c>. Disposing a bound object unbinds exactly
        /// the slots that named it and leaves every other binding alone, so the next draw pays for the one object
        /// that went away rather than for a full rebind of the pipeline, the vertex buffers and every shader
        /// resource.
        /// </summary>
        [Fact]
        public void DisposingABoundStateObject_UnbindsExactlyThatSlot_AndNeverClearsState()
        {
            var log = new D3D11NativeCallLog();
            var state = new D3D11DeviceState();
            var emitter = new D3D11NativeTraceEmitter(state, log);
            FakeD3D11Pipeline pipeline = Pipeline();

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            log.Reset();

            emitter.ScrubDisposed(pipeline.RasterizerState!);

            Assert.Equal(new[] { "RSSetState(null)" }, log.Trace);
            Assert.Equal(0, log.Count(D3D11NativeCall.ClearState));
            Assert.Null(state.Bound(D3D11StateSlot.RasterizerState));
            Assert.Same(pipeline.VertexShader, state.Bound(D3D11StateSlot.VertexShader));
        }

        /// <summary>The scrub is what makes the cache honest afterwards: the slot it cleared is rebound by the
        /// next pipeline bind rather than skipped as redundant, which is the whole reason a disposal has to reach
        /// the cache at all.</summary>
        [Fact]
        public void AScrubbedSlot_IsReboundByTheNextPipelineBind()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            FakeD3D11Pipeline pipeline = Pipeline();

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            emitter.ScrubDisposed(pipeline.VertexShader!);
            log.Reset();

            emitter.SetPipeline(pipeline);

            Assert.Equal(new[] { $"VSSetShader({log.Id(pipeline.VertexShader)})" }, log.Trace);
        }

        /// <summary>One object cached in several slots is unbound from all of them, in slot order. Contrived on a
        /// pipeline and not contrived at all on a resource, which is why the scrub answers with every slot rather
        /// than the first.</summary>
        [Fact]
        public void AnObjectBoundInSeveralSlots_IsUnboundFromEachOfThem()
        {
            var log = new D3D11NativeCallLog();
            var shared = new object();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var pipeline = new FakeD3D11Pipeline(shared, new object(), new object(), shared, new object(),
                new object(), 4u);

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            log.Reset();

            emitter.ScrubDisposed(shared);

            Assert.Equal(new[] { "VSSetShader(null)", "OMSetDepthStencilState(null,0)" }, log.Trace);
        }

        /// <summary>The common case, and it must be free: most disposed resources were never bound, or were
        /// replaced long before their owner let them go.</summary>
        [Fact]
        public void DisposingSomethingThatWasNeverBound_IssuesNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);

            emitter.Begin();
            emitter.SetPipeline(Pipeline());
            log.Reset();

            emitter.ScrubDisposed(new object());

            Assert.Empty(log.Trace);
        }

        /// <summary>A disposed framebuffer is unbound too, and the identity guard forgets it, so binding its
        /// replacement is a genuine change rather than a redundant re-bind against a dead object.</summary>
        [Fact]
        public void DisposingTheBoundFramebuffer_UnbindsTheTargets_AndTheNextBindIsAChange()
        {
            var log = new D3D11NativeCallLog();
            var state = new D3D11DeviceState();
            var emitter = new D3D11NativeTraceEmitter(state, log);
            FakeFramebuffer target = Framebuffer(640, 480);

            emitter.Begin();
            emitter.SetFramebuffer(target);
            log.Reset();

            emitter.ScrubDisposed(target);

            Assert.Equal(new[] { "OMSetRenderTargets(null)" }, log.Trace);
            Assert.Null(state.BoundFramebuffer);

            emitter.SetFramebuffer(target);
            Assert.Equal(1, log.Count(D3D11NativeCall.RSSetViewports));
        }

        // ---- The coupling the two enums rest on ----

        /// <summary>
        /// <see cref="D3D11DeviceState.Scrub"/> turns a cache-array index into a flag by shifting, which is only
        /// correct while slot <c>n</c> is bit <c>n</c>. Pinned by NAME, because adding a slot and forgetting its
        /// flag would report the wrong slot as scrubbed and unbind the wrong thing, silently.
        /// </summary>
        [Fact]
        public void EveryStateSlot_HasAChangeFlagOfTheSameName()
        {
            foreach (D3D11StateSlot slot in Enum.GetValues<D3D11StateSlot>())
            {
                D3D11StateChange flag = D3D11DeviceState.FlagOf(slot);

                Assert.True(Enum.IsDefined(flag), $"{slot} shifts to {(int)flag}, which no D3D11StateChange names.");
                Assert.Equal(slot.ToString(), flag.ToString());
            }
        }

        /// <summary>And the flags that are not slots are the four that cannot be: a topology is a value rather
        /// than an object, and a framebuffer, a vertex stream and an index buffer are none of them part of a
        /// pipeline. Pinned as a LIST rather than a count, so a fifth arrives here as a decision to state rather
        /// than as a number nobody reads.</summary>
        [Fact]
        public void TheOnlyChangeFlagsWithoutASlot_AreTopologyTheFramebufferAndTheInputAssembler()
        {
            string[] slots = Enum.GetNames<D3D11StateSlot>();
            string[] extra = Enum.GetNames<D3D11StateChange>()
                .Where(n => n != nameof(D3D11StateChange.None) && !slots.Contains(n))
                .ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(D3D11StateChange.PrimitiveTopology),
                    nameof(D3D11StateChange.Framebuffer),
                    nameof(D3D11StateChange.VertexBuffers),
                    nameof(D3D11StateChange.IndexBuffer),
                },
                extra);
        }

        // ---- Fixtures ----

        static FakeFramebuffer Framebuffer(uint width, uint height) => new(
            new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R8G8B8A8UNorm), width, height);

        /// <summary>A pipeline whose seven state objects are all distinct, which is the shape a first bind of a
        /// frame meets.</summary>
        internal static FakeD3D11Pipeline Pipeline(uint topology = 4u) => new(
            new object(), new object(), new object(), new object(), new object(), new object(), topology);

        /// <summary>
        /// A pipeline that SHARES another one's blend state object and depth-stencil state object and differs
        /// only in the two arguments those calls carry. The shape issue #454's cache key exists for: everything a
        /// cache keyed on the objects alone would call redundant.
        /// </summary>
        internal static FakeD3D11Pipeline SharingStateObjects(FakeD3D11Pipeline other,
            System.Numerics.Vector4 blendFactor, uint stencilReference)
            => new(other.VertexShader, other.PixelShader, other.BlendState, other.DepthStencilState,
                other.RasterizerState, other.InputLayout, other.PrimitiveTopology)
            {
                BlendFactor = blendFactor,
                StencilReference = stencilReference,
                VertexStrides = other.VertexStrides,
            };

        /// <summary>
        /// A graphics pipeline that can answer what it is made of, which is what the redundancy caches compare
        /// against. Its state objects are plain <c>object</c> instances, because a cache asks only whether the
        /// same instance is already bound and the real Direct3D handles are work-breakdown row 7.
        /// <para>
        /// It answers <see cref="ID3D11PipelineLayouts"/> as well, with no layouts by default. A pipeline that
        /// declares none binds no sets, which is every test in this file, and the bind-flush tests hand in the
        /// layout array their sets are numbered against.
        /// </para>
        /// </summary>
        internal sealed class FakeD3D11Pipeline : IGpuPipeline, ID3D11PipelineState, ID3D11PipelineLayouts
        {
            internal FakeD3D11Pipeline(object? vertexShader, object? pixelShader, object? blendState,
                object? depthStencilState, object? rasterizerState, object? inputLayout, uint topology,
                params D3D11ResourceLayout[] layouts)
            {
                VertexShader = vertexShader;
                PixelShader = pixelShader;
                BlendState = blendState;
                DepthStencilState = depthStencilState;
                RasterizerState = rasterizerState;
                InputLayout = inputLayout;
                PrimitiveTopology = topology;
                ResourceLayouts = layouts;
            }

            public object? VertexShader { get; }
            public object? PixelShader { get; }
            public object? BlendState { get; }
            public object? DepthStencilState { get; }
            public object? RasterizerState { get; }
            public object? InputLayout { get; }
            public uint PrimitiveTopology { get; }
            public D3D11ResourceLayout[] ResourceLayouts { get; }

            /// <summary>The blend factor this pipeline is bound with. Defaults to what a cleared context already
            /// holds, so a fixture that does not care about the factor never makes the blend cache report a change
            /// it did not mean to test.</summary>
            public System.Numerics.Vector4 BlendFactor { get; init; } = D3D11DeviceState.ClearedBlendFactor;

            /// <summary>The stencil reference, defaulting to the cleared context's zero for the same reason.
            /// </summary>
            public uint StencilReference { get; init; }

            /// <summary>Per-slot vertex strides. Empty by default, which is a pipeline with no vertex inputs, so
            /// the fixtures in this file bind no streams and pay for none.</summary>
            public uint[] VertexStrides { get; init; } = Array.Empty<uint>();

            public void Dispose()
            {
            }
        }
    }
}
