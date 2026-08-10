using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-R4, WRITTEN BEHAVIOURALLY RATHER THAN AS A STATE ASSERTION. Row 7 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, section 6.2.
    ///
    /// <para><b>THE DEFECT THIS EXISTS TO CATCH IS THE INCUMBENT'S, AND IT IS INVISIBLE TO EVERY GOLDEN.</b>
    /// Metal's argument tables, bound pipeline state, viewport, scissor and VERTEX STREAMS are properties of the
    /// ENCODER, so ending a render encoder discards all of them. The incumbent's
    /// <c>EndCurrentRenderPass</c> sets the pipeline-changed flag, clears the active-set array and re-marks the
    /// viewport and scissor, and does NOT clear <c>_vertexBuffersActive</c>. It gets away with it only because of
    /// a SECOND defect: its <c>PreDrawCommand</c> vertex loop issues <c>setVertexBuffer</c> when the flag is
    /// false and never sets it true, so the cache is permanently cold and every stream is re-bound on every draw.
    /// Porting the redundancy tracking (which this backend does, because the incumbent's per-draw cost is what it
    /// exists to beat) WITHOUT porting the invalidation ships a corruption no golden reaches, because the goldens
    /// do not restart a render pass mid-scene.</para>
    ///
    /// <para><b>SO THE TEST IS THE SHAPE RATHER THAN THE BOOKKEEPING</b>: bind, force an encoder end through a
    /// blit, bind again, and assert the second bind was re-issued. It fails on the corruption rather than on the
    /// implementation of the cache, which is what lets rows 11, 13 and 14 change how their records are stored
    /// without this test needing to know.</para>
    ///
    /// <para><b>THE RECORD HERE IS A REAL <see cref="MetalEncoderMark"/> STANDING IN FOR A VERTEX STREAM.</b> Row
    /// 7 ships the mechanism and row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580) ships the streams,
    /// so the fuller version of this test (a real <c>Draw</c> re-issuing a real <c>setVertexBuffer</c>) lands
    /// there. What it will assert is this same shape against the same mechanism, and the mechanism is what has to
    /// be right BEFORE any record is written against it, which is why the assertion is here rather than waiting
    /// for its first consumer.</para>
    /// </summary>
    public sealed class MetalEncoderScopeInvalidationTests
    {
        /// <summary>
        /// A miniature of row 14's vertex-stream record: the buffer it believes is bound, plus the encoder epoch
        /// that belief is stamped against. Written the way a real record is so the test exercises the real
        /// mechanism rather than a paraphrase of it.
        /// </summary>
        sealed class StreamRecord
        {
            MetalEncoderMark _mark;
            IntPtr _buffer;

            internal int Binds { get; private set; }

            /// <summary>What a per-draw bind does: skip when the record still describes what this encoder holds,
            /// emit and re-stamp otherwise.</summary>
            internal void Bind(IntPtr buffer, ulong epoch)
            {
                if (_buffer == buffer && _mark.IsValidIn(epoch)) return;

                Binds++;
                _buffer = buffer;
                _mark.Mark(epoch);
            }
        }

        static (MetalEncoderScope Scope, FakeMetalEncoderCalls Calls) NewScope()
        {
            FakeMetalEncoderCalls calls = new();
            MetalEncoderScope scope = new(new FakeMetalEncoderSink(calls));
            scope.BeginRecording(new IntPtr(0x100));
            return (scope, calls);
        }

        /// <summary>THE ONE THAT MATTERS. The second draw must re-issue its stream bind, because the blit ended
        /// the render encoder and took the binding with it.</summary>
        [Fact]
        public void AVertexStreamBoundBeforeABlitIsReBoundAfterIt()
        {
            (MetalEncoderScope scope, _) = NewScope();
            StreamRecord stream = new();
            IntPtr vertices = new(0xBEEF);

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            stream.Bind(vertices, scope.Epoch);
            Assert.Equal(1, stream.Binds);

            // The record-time upload 2.1 is about: a blit encoder is opened, and the render encoder ends.
            scope.EnsureBlitEncoder();

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            stream.Bind(vertices, scope.Epoch);

            Assert.Equal(2, stream.Binds);
        }

        /// <summary>THE OTHER HALF, without which the first assertion is satisfied by a cache that never caches:
        /// inside ONE encoder, the same bind is not re-issued. "We re-activated when we did not need to" and "we
        /// failed to re-activate when we did" are both invisible in a green suite unless both are asserted.</summary>
        [Fact]
        public void TheSameStreamInsideOneEncoderIsBoundOnce()
        {
            (MetalEncoderScope scope, _) = NewScope();
            StreamRecord stream = new();
            IntPtr vertices = new(0xBEEF);

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            stream.Bind(vertices, scope.Epoch);
            stream.Bind(vertices, scope.Epoch);
            stream.Bind(vertices, scope.Epoch);

            Assert.Equal(1, stream.Binds);
        }

        /// <summary>Ending an encoder invalidates even when nothing reopens, so a record cannot read as
        /// describing live encoder state in the window between an end and the next begin.</summary>
        [Fact]
        public void EndingAnEncoderInvalidatesWithoutWaitingForTheNextBegin()
        {
            (MetalEncoderScope scope, _) = NewScope();
            StreamRecord stream = new();

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            ulong bound = scope.Epoch;
            stream.Bind(new IntPtr(0xBEEF), bound);

            scope.EnsureNoEncoder();

            Assert.NotEqual(bound, scope.Epoch);
        }

        /// <summary>A record from a recording that was discarded cannot survive into the next one: a fresh
        /// command buffer has no encoder and no encoder state, so every record against the old one describes
        /// state that never existed on this one.</summary>
        [Fact]
        public void ARecordDoesNotSurviveANewRecording()
        {
            (MetalEncoderScope scope, _) = NewScope();
            StreamRecord stream = new();
            IntPtr vertices = new(0xBEEF);

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            stream.Bind(vertices, scope.Epoch);

            scope.BeginRecording(new IntPtr(0x200));
            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            stream.Bind(vertices, scope.Epoch);

            Assert.Equal(2, stream.Binds);
        }

        /// <summary>
        /// The epoch starts at 1 and never reaches 0, which is what makes a default-constructed record read as
        /// invalid in EVERY epoch including the first. A zero-based epoch would make an unbound stream look bound
        /// to whatever the previous encoder happened to hold, on the first draw of the first pass.
        /// </summary>
        [Fact]
        public void AnUnmarkedRecordIsInvalidInEveryEpoch()
        {
            (MetalEncoderScope scope, _) = NewScope();
            MetalEncoderMark mark = default;

            Assert.NotEqual(0UL, scope.Epoch);
            Assert.False(mark.IsValidIn(scope.Epoch));
            Assert.False(mark.IsValidIn(0));
        }

        /// <summary>M-R9's and clause 6's case: a record invalidated by something other than a boundary (an index
        /// table that renumbered, a slot whose set went null) clears its own stamp.</summary>
        [Fact]
        public void AClearedMarkIsInvalidInTheEpochItWasStampedIn()
        {
            (MetalEncoderScope scope, _) = NewScope();
            MetalEncoderMark mark = default;

            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            mark.Mark(scope.Epoch);
            Assert.True(mark.IsValidIn(scope.Epoch));

            mark.Clear();
            Assert.False(mark.IsValidIn(scope.Epoch));
        }
    }
}
