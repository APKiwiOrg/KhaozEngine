using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DRAW PATH'S TWO OWN DECISIONS (issue #454), both device-free: the pipeline cache key widened to the
    /// arguments that ride a state object, and the input assembler's batched, deferred vertex streams.
    /// <para>
    /// Driven through <see cref="D3D11NativeTraceEmitter"/>, which is not a harness that reproduces the rules: it
    /// applies them through the SAME <see cref="D3D11DeviceState"/> and <see cref="D3D11VertexStreams"/> the real
    /// emitter uses unchanged, and writes down the <c>ID3D11DeviceContext</c> calls it would have made. So what
    /// these pin is the shipped decision. What they cannot reach is which Vortice method the real emitter calls,
    /// which needs a device and arrives with the WARP leg.
    /// </para>
    /// </summary>
    public sealed class D3D11DrawPathTests
    {
        // ---- Issue #454: the blend factor and the stencil reference are part of the cache key ----------------

        /// <summary>
        /// THE HAZARD, MADE EXECUTABLE. Two pipelines share ONE blend state object and differ only in the blend
        /// FACTOR. A cache keyed on the object alone calls the second bind redundant, issues nothing, and the
        /// second pass draws with the first one's factor: golden visible, nothing thrown, nothing logged. The
        /// widened key issues exactly one <c>OMSetBlendState</c>, carrying the new factor.
        /// </summary>
        [Fact]
        public void TwoPipelinesSharingABlendState_AndDifferingInFactor_RebindIt()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            D3D11StateCacheTests.FakeD3D11Pipeline first = D3D11StateCacheTests.Pipeline();
            D3D11StateCacheTests.FakeD3D11Pipeline second = D3D11StateCacheTests.SharingStateObjects(
                first, new Vector4(0.25f, 0.5f, 0.75f, 1f), stencilReference: 0u);

            emitter.Begin();
            emitter.SetPipeline(first);
            log.Reset();
            emitter.SetPipeline(second);

            Assert.Equal(new[] { $"OMSetBlendState({log.Id(first.BlendState)},0.25|0.5|0.75|1)" }, log.Trace);
        }

        /// <summary>The same shape on the other pair: one depth-stencil state object, two stencil references, one
        /// <c>OMSetDepthStencilState</c>. Every shipped pipeline answers 0 today because the GPU seam carries no
        /// stencil at all, which is exactly why this is worth pinning now: the day one arrives, the cache is
        /// already right rather than silently wrong for a release.</summary>
        [Fact]
        public void TwoPipelinesSharingADepthStencilState_AndDifferingInReference_RebindIt()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            D3D11StateCacheTests.FakeD3D11Pipeline first = D3D11StateCacheTests.Pipeline();
            D3D11StateCacheTests.FakeD3D11Pipeline second = D3D11StateCacheTests.SharingStateObjects(
                first, D3D11DeviceState.ClearedBlendFactor, stencilReference: 3u);

            emitter.Begin();
            emitter.SetPipeline(first);
            log.Reset();
            emitter.SetPipeline(second);

            Assert.Equal(new[] { $"OMSetDepthStencilState({log.Id(first.DepthStencilState)},3)" }, log.Trace);
        }

        /// <summary>The other half, and the reason the key was widened rather than the two calls re-emitted
        /// unconditionally: a pipeline that matches in BOTH the objects and the arguments still costs nothing.
        /// Re-emitting would have made every redundant pipeline bind two native calls, which is the #418 shape
        /// arriving through another door.</summary>
        [Fact]
        public void APipelineMatchingInObjectsAndArguments_CostsNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            D3D11StateCacheTests.FakeD3D11Pipeline first = D3D11StateCacheTests.Pipeline();
            D3D11StateCacheTests.FakeD3D11Pipeline twin = D3D11StateCacheTests.SharingStateObjects(
                first, first.BlendFactor, first.StencilReference);

            emitter.Begin();
            emitter.SetPipeline(first);
            log.Reset();
            emitter.SetPipeline(twin);
            emitter.SetPipeline(first);

            Assert.Empty(log.Trace);
        }

        /// <summary>And the factor is tracked rather than merely compared once: going back to the first
        /// pipeline's factor after a switch issues the bind again.</summary>
        [Fact]
        public void SwitchingBackToTheOriginalFactor_IssuesTheBindAgain()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            D3D11StateCacheTests.FakeD3D11Pipeline first = D3D11StateCacheTests.Pipeline();
            D3D11StateCacheTests.FakeD3D11Pipeline second = D3D11StateCacheTests.SharingStateObjects(
                first, new Vector4(1f, 0f, 0f, 1f), stencilReference: 0u);

            emitter.Begin();
            emitter.SetPipeline(first);
            emitter.SetPipeline(second);
            log.Reset();
            emitter.SetPipeline(first);

            Assert.Equal(1, log.Count(D3D11NativeCall.OMSetBlendState));
            Assert.Equal(1, log.TotalCalls);
        }

        /// <summary>
        /// THE CACHE DESCRIBES WHAT <c>ClearState</c> LEAVES, which for these two is white and zero rather than
        /// default. A cache reset to zero would claim a factor the context does not have, and the first pipeline
        /// after a replay boundary that happened to want white would skip a call it needs.
        /// </summary>
        [Fact]
        public void AResetRestoresTheFactorAndReferenceTheContextActuallyHolds()
        {
            var state = new D3D11DeviceState();
            var emitter = new D3D11NativeTraceEmitter(state, new D3D11NativeCallLog());
            D3D11StateCacheTests.FakeD3D11Pipeline pipeline = D3D11StateCacheTests.SharingStateObjects(
                D3D11StateCacheTests.Pipeline(), new Vector4(0.1f, 0.2f, 0.3f, 0.4f), stencilReference: 7u);

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), state.BoundBlendFactor);
            Assert.Equal(7u, state.BoundStencilReference);

            emitter.Begin();

            Assert.Equal(Vector4.One, state.BoundBlendFactor);
            Assert.Equal(0u, state.BoundStencilReference);
        }

        /// <summary>A scrub of the blend state object drops the factor with it, because the unbind the emitter
        /// issues passes the cleared one. Without that a later pipeline wanting the OLD factor would compare equal
        /// against a context that no longer has it.</summary>
        [Fact]
        public void ScrubbingTheBlendState_DropsTheFactorWithIt()
        {
            var state = new D3D11DeviceState();
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(state, log);
            D3D11StateCacheTests.FakeD3D11Pipeline pipeline = D3D11StateCacheTests.SharingStateObjects(
                D3D11StateCacheTests.Pipeline(), new Vector4(0f, 0f, 0f, 1f), stencilReference: 2u);

            emitter.Begin();
            emitter.SetPipeline(pipeline);
            log.Reset();
            emitter.ScrubDisposed(pipeline.BlendState!);

            Assert.Equal(new[] { "OMSetBlendState(null,1|1|1|1)" }, log.Trace);
            Assert.Equal(Vector4.One, state.BoundBlendFactor);
        }

        // ---- 5.3: one IASetVertexBuffers for the streams a draw actually changed --------------------------

        /// <summary>
        /// THE BATCH 5.3 ASKS FOR: two streams bound before one draw are ONE
        /// <c>IASetVertexBuffers(0, 2, ...)</c>, not two calls. The binds themselves issue nothing, which is what
        /// makes the batch possible at all, and the strides come from the pipeline rather than from the bind.
        /// </summary>
        [Fact]
        public void TwoStreamsBeforeOneDraw_AreOneCallAtSlotZero()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var positions = new FakeBuffer(256);
            var instances = new FakeBuffer(64);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u, 16u));
            log.Reset();
            emitter.SetVertexBuffer(0, positions, 0);
            emitter.SetVertexBuffer(1, instances, 48);

            Assert.Equal(0, log.TotalCalls);   // both binds recorded, neither issued

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                new[]
                {
                    $"VertexBufferPending(0,{log.Id(positions)},0)",
                    $"VertexBufferPending(1,{log.Id(instances)},48)",
                    $"IASetVertexBuffers(0,2,{log.Id(positions)}@0/32,{log.Id(instances)}@48/16)",
                    "DrawInstanced(3,1,0,0)",
                },
                log.Trace);
        }

        /// <summary>A rebind of the same buffer at the same offset is redundant and the draw issues no stream
        /// call at all, which is the hot path: a renderer that re-binds its one vertex buffer per draw pays for
        /// it once.</summary>
        [Fact]
        public void ARedundantStreamRebind_CostsNothingAtTheDraw()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var vertices = new FakeBuffer(256);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(0, log.Count(D3D11NativeCall.IASetVertexBuffers));
            Assert.Equal(1, log.TotalCalls);   // the draw and nothing else
        }

        /// <summary>A different OFFSET on the same buffer is a different bind, because the offset is an argument
        /// of the call rather than a property of the buffer.</summary>
        [Fact]
        public void TheSameBufferAtANewOffset_IsARebind()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var vertices = new FakeBuffer(256);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.SetVertexBuffer(0, vertices, 32);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                $"IASetVertexBuffers(0,1,{log.Id(vertices)}@32/32)",
                log.Trace.First(line => line.StartsWith("IASetVertexBuffers", StringComparison.Ordinal)));
        }

        /// <summary>Slots 0 and 2 dirty with 1 clean is ONE call over three slots rather than two calls with a
        /// gap, and the swept-in slot is rebound to exactly what it already holds. The same trade
        /// <see cref="D3D11SetActivation"/> makes for a hole in a register span.</summary>
        [Fact]
        public void AGapBetweenTwoDirtySlots_IsSweptIntoOneCall()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var a = new FakeBuffer(64);
            var b = new FakeBuffer(64);
            var c = new FakeBuffer(64);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(4u, 8u, 12u));
            emitter.SetVertexBuffer(0, a, 0);
            emitter.SetVertexBuffer(1, b, 0);
            emitter.SetVertexBuffer(2, c, 0);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.SetVertexBuffer(0, a, 4);
            emitter.SetVertexBuffer(2, c, 12);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                $"IASetVertexBuffers(0,3,{log.Id(a)}@4/4,{log.Id(b)}@0/8,{log.Id(c)}@12/12)",
                log.Trace.First(line => line.StartsWith("IASetVertexBuffers", StringComparison.Ordinal)));
            Assert.Equal(1, log.Count(D3D11NativeCall.IASetVertexBuffers));
        }

        /// <summary>
        /// A PIPELINE SWITCH THAT CHANGES THE STRIDES RE-ISSUES THE STREAMS, and this is the correctness half of
        /// the batching rather than an optimisation. The stride is an argument of the call, so a switch between
        /// two vertex formats over the same buffer would otherwise draw the second pass at the first pass's
        /// stride, which is geometry noise with nothing thrown.
        /// </summary>
        [Fact]
        public void APipelineWithDifferentStrides_ReissuesTheBoundStreams()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var vertices = new FakeBuffer(256);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.SetPipeline(PipelineWithStrides(20u));
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal($"IASetVertexBuffers(0,1,{log.Id(vertices)}@0/20)",
                log.Trace.First(line => line.StartsWith("IASetVertexBuffers", StringComparison.Ordinal)));
        }

        /// <summary>Identity is taken on the stride ARRAY, so two pipelines that share one invalidate nothing and
        /// a defensive rebind between two draws costs no stream call.</summary>
        [Fact]
        public void TwoPipelinesSharingAStrideArray_ReissueNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var vertices = new FakeBuffer(256);
            uint[] strides = { 32u };

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrideArray(strides));
            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.SetPipeline(PipelineWithStrideArray(strides));
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(0, log.Count(D3D11NativeCall.IASetVertexBuffers));
        }

        /// <summary>A pipeline with NO vertex inputs (the fullscreen passes) declares no strides, so the streams a
        /// previous pass left bound are read by nothing under it and re-issuing them would be a call spent on a
        /// slot no input layout references.</summary>
        [Fact]
        public void APipelineWithNoVertexInputs_IssuesNoStreams()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, new FakeBuffer(64), 0);
            emitter.SetPipeline(D3D11StateCacheTests.Pipeline());   // no strides at all
            log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(new[] { "DrawInstanced(3,1,0,0)" }, log.Trace);
        }

        /// <summary>An index bind is issued at the bind and guarded by the pair (buffer, format): there is no
        /// array form of <c>IASetIndexBuffer</c>, so there is nothing to batch it with and nothing to defer it
        /// for.</summary>
        [Fact]
        public void TheIndexBuffer_IsCachedOnTheBufferAndTheFormat()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var indices = new FakeBuffer(64);

            emitter.Begin();
            emitter.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
            emitter.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
            emitter.SetIndexBuffer(indices, GpuIndexFormat.UInt32);

            Assert.Equal(
                new[] { $"IASetIndexBuffer({log.Id(indices)},UInt16)", $"IASetIndexBuffer({log.Id(indices)},UInt32)" },
                log.Trace.Where(line => line.StartsWith("IASetIndexBuffer", StringComparison.Ordinal)));
        }

        /// <summary>THE ORDER EVERY DRAW PATH TAKES, spelled out because it is invisible from the seam: the
        /// resource-set flush first (decision R5, rule 2), then the batched streams, then the draw.</summary>
        [Fact]
        public void ADraw_FlushesTheSetsThenTheStreamsThenIssues()
        {
            var harness = new D3D11BindFixtures.Harness();
            D3D11NativeTraceEmitter emitter = harness.Emitter;
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout, new FakeBuffer(256));

            emitter.SetPipeline(new D3D11StateCacheTests.FakeD3D11Pipeline(
                new object(), new object(), new object(), new object(), new object(), new object(), 4u, layout)
            {
                VertexStrides = new[] { 32u },
            });
            emitter.SetVertexBuffer(0, new FakeBuffer(64), 0);
            harness.Log.Reset();
            emitter.SetGraphicsResourceSet(0, set);
            emitter.DrawIndexed(6, 1, 0, 0, 0);

            string[] issued = harness.Log.Trace
                .Where(line => !line.StartsWith("ResourceSetPending", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(3, issued.Length);
            Assert.StartsWith("VSSetConstantBuffers1", issued[0], StringComparison.Ordinal);
            Assert.StartsWith("IASetVertexBuffers", issued[1], StringComparison.Ordinal);
            Assert.StartsWith("DrawIndexedInstanced", issued[2], StringComparison.Ordinal);
        }

        /// <summary>The <c>ClearState</c> at the head of the next replay drops the streams and the index buffer
        /// too, because the context holds neither afterwards. A retained record would let the first draw of the
        /// next frame issue nothing and read whatever the input assembler now has, which is nothing.</summary>
        [Fact]
        public void TheClearStateOpeningAReplay_DropsTheInputAssembler()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var vertices = new FakeBuffer(64);
            var indices = new FakeBuffer(64);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
            emitter.Draw(3, 1, 0, 0);

            emitter.Begin();
            log.Reset();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, vertices, 0);
            emitter.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(1, log.Count(D3D11NativeCall.IASetVertexBuffers));
            Assert.Equal(1, log.Count(D3D11NativeCall.IASetIndexBuffer));
        }

        /// <summary>DECISION R8 REACHES THE INPUT ASSEMBLER TOO: a disposed buffer is unbound from the slots that
        /// named it, in one call over the contiguous span, and from the index slot.</summary>
        [Fact]
        public void DisposingABoundStream_UnbindsExactlyTheSlotsThatNamedIt()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var shared = new FakeBuffer(256);
            var other = new FakeBuffer(64);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(4u, 8u, 12u));
            emitter.SetVertexBuffer(0, shared, 0);
            emitter.SetVertexBuffer(1, other, 0);
            emitter.SetVertexBuffer(2, shared, 16);
            emitter.SetIndexBuffer(shared, GpuIndexFormat.UInt16);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.ScrubDisposed(shared);

            Assert.Equal(new[] { "IASetVertexBuffers(0,3,null)", "IASetIndexBuffer(null)" }, log.Trace);
        }

        /// <summary>And a buffer that was never bound scrubs to nothing, which is the common case.</summary>
        [Fact]
        public void DisposingAnUnboundBuffer_IssuesNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);

            emitter.Begin();
            emitter.SetPipeline(PipelineWithStrides(32u));
            emitter.SetVertexBuffer(0, new FakeBuffer(64), 0);
            emitter.Draw(3, 1, 0, 0);
            log.Reset();

            emitter.ScrubDisposed(new FakeBuffer(64));

            Assert.Empty(log.Trace);
        }

        /// <summary>The stream record is keyed by SLOT and does not grow per rebind, which is the same rule 8 the
        /// bind flush's record carries and the same reason: the hot path is thousands of rebinds a frame.</summary>
        [Fact]
        public void TheStreamRecord_FollowsTheHighestSlotAndNotTheRebindCount()
        {
            var streams = new D3D11VertexStreams();
            var buffer = new FakeBuffer(64);

            int initial = streams.RecordedSlotCapacity;
            for (int i = 0; i < 4096; i++) streams.RecordVertexBuffer(0, buffer, (uint)i);

            Assert.Equal(initial, streams.RecordedSlotCapacity);
        }

        /// <summary>A slot past the input assembler's 32 is a mismatch rather than a deep vertex layout, and it is
        /// refused rather than allocating its way toward one.</summary>
        [Fact]
        public void AVertexSlotPastTheInputAssembler_IsRefused()
        {
            var streams = new D3D11VertexStreams();

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => streams.RecordVertexBuffer(64, new FakeBuffer(64), 0));

            Assert.Contains("32 slots", ex.Message, StringComparison.Ordinal);
        }

        // ---- fixtures ---------------------------------------------------------------------------------------

        static D3D11StateCacheTests.FakeD3D11Pipeline PipelineWithStrides(params uint[] strides)
            => PipelineWithStrideArray(strides);

        // A distinct pipeline each time, so a test that switches pipelines really switches. The stride ARRAY is
        // the caller's, which is what lets a test share one deliberately.
        static D3D11StateCacheTests.FakeD3D11Pipeline PipelineWithStrideArray(uint[] strides)
            => new(new object(), new object(), new object(), new object(), new object(), new object(), 4u)
            {
                VertexStrides = strides,
            };
    }
}
