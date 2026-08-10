using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE-ENCODER-AT-A-TIME INVARIANT AND EVERY TRANSITION (M-R1), device-free. Row 7 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    /// <para>
    /// WHAT A RED RUN MEANS. Metal refuses a second encoder on a command buffer until the first has been sent
    /// <c>-endEncoding</c>, so a scope that opens one without ending the other is a validation failure on a real
    /// device and an undefined recording without the debug layer. Every command in the backend routes through one
    /// of these helpers, so the rule is written once and asserted here rather than at each of its callers.
    /// </para>
    /// </summary>
    public sealed class MetalEncoderScopeTests
    {
        static (MetalEncoderScope Scope, FakeMetalEncoderCalls Calls) NewScope()
        {
            FakeMetalEncoderCalls calls = new();
            MetalEncoderScope scope = new(new FakeMetalEncoderSink(calls));
            scope.BeginRecording(new IntPtr(0x100));
            return (scope, calls);
        }

        [Fact]
        public void AFreshRecordingHasNoEncoderOpen()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            Assert.Equal(MetalEncoderKind.None, scope.Open);
            Assert.Equal(IntPtr.Zero, scope.Current);
            Assert.Equal(0, calls.EncoderBoundaries);
        }

        [Fact]
        public void EachKindOpensItsOwnEncoder()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            Assert.NotEqual(IntPtr.Zero, scope.EnsureRenderEncoder(new IntPtr(0xD5)));
            Assert.Equal(MetalEncoderKind.Render, scope.Open);

            scope.EnsureBlitEncoder();
            Assert.Equal(MetalEncoderKind.Blit, scope.Open);

            scope.EnsureComputeEncoder();
            Assert.Equal(MetalEncoderKind.Compute, scope.Open);

            // Three begins and two ends: each switch closed the previous kind before it opened the next, which is
            // the invariant. A missing end shows up here as four boundaries rather than five.
            Assert.Equal(5, calls.EncoderBoundaries);
            Assert.Equal(3, calls.EncoderBegins);
        }

        [Fact]
        public void SwitchingKindsEndsTheOutgoingEncoderBeforeOpeningTheIncomingOne()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            IntPtr render = scope.EnsureRenderEncoder(new IntPtr(0xD5));
            scope.EnsureBlitEncoder();

            // The ORDER is the invariant, not the count: an end emitted after the begin would be a second encoder
            // opened on a buffer that already had one.
            Assert.Equal(3, calls.Log.Count);
            Assert.StartsWith("begin Render", calls.Log[0], StringComparison.Ordinal);
            Assert.Equal($"end Render {render}", calls.Log[1]);
            Assert.StartsWith("begin Blit", calls.Log[2], StringComparison.Ordinal);
        }

        /// <summary>
        /// A SECOND ENSURE OF THE KIND ALREADY OPEN EMITS NOTHING, which is what makes the deferred begin (M-A1)
        /// one descriptor per pass rather than one per draw: every draw calls EnsureRenderEncoder and only the
        /// first of a pass may open anything.
        /// </summary>
        [Fact]
        public void EnsuringTheKindAlreadyOpenIsFree()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            IntPtr first = scope.EnsureRenderEncoder(new IntPtr(0xD5));
            ulong epoch = scope.Epoch;
            IntPtr second = scope.EnsureRenderEncoder(new IntPtr(0xD6));

            Assert.Equal(first, second);
            Assert.Equal(1, calls.EncoderBoundaries);

            // And it does not bump the epoch, so a redundant Ensure cannot invalidate the binds a draw just made.
            Assert.Equal(epoch, scope.Epoch);
        }

        [Fact]
        public void EnsureNoEncoderClosesWhateverIsOpenAndReportsWhichKindItWas()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            scope.EnsureComputeEncoder();

            Assert.Equal(MetalEncoderKind.Compute, scope.EnsureNoEncoder());
            Assert.Equal(MetalEncoderKind.None, scope.Open);
            Assert.Equal(IntPtr.Zero, scope.Current);
            Assert.Equal(2, calls.EncoderBoundaries);
        }

        /// <summary>
        /// SAFE TO CALL WHEN NOTHING IS OPEN, so a command illegal inside the current encoder never has to ask
        /// first. The reported kind is what row 12 reads for the clear-only flush (M-A3), so "nothing was open"
        /// has to be a distinguishable answer rather than a silent no-op.
        /// </summary>
        [Fact]
        public void EnsureNoEncoderOnAnEmptyScopeEmitsNothing()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            Assert.Equal(MetalEncoderKind.None, scope.EnsureNoEncoder());
            Assert.Equal(MetalEncoderKind.None, scope.EnsureNoEncoder());
            Assert.Equal(0, calls.EncoderBoundaries);
        }

        /// <summary>
        /// M-W5's ORPHAN-TARGET CASE. A nil render encoder is a framebuffer that cannot be rendered to for one
        /// frame, which is a genuine failure rather than a state error, so the scope reports it and adopts
        /// nothing: adopting a nil handle would leave the scope believing a render pass was open while every
        /// command against it went nowhere, which reads as a silently empty frame instead of as a failure.
        /// </summary>
        [Fact]
        public void ANilEncoderIsNotAdopted()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();
            calls.NilForKind = MetalEncoderKind.Render;

            Assert.Equal(IntPtr.Zero, scope.EnsureRenderEncoder(new IntPtr(0xD5)));
            Assert.Equal(MetalEncoderKind.None, scope.Open);

            // And nothing is left to end, so the next EnsureNo emits no endEncoding against a nil handle.
            Assert.Equal(MetalEncoderKind.None, scope.EnsureNoEncoder());
        }

        [Fact]
        public void ANilBlitEncoderIsNotAdoptedEither()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();
            calls.NilForKind = MetalEncoderKind.Blit;

            Assert.Equal(IntPtr.Zero, scope.EnsureBlitEncoder());
            Assert.Equal(MetalEncoderKind.None, scope.Open);
        }

        [Fact]
        public void EveryEncoderIsOpenedOnTheBufferTheRecordingAdopted()
        {
            FakeMetalEncoderCalls calls = new();
            MetalEncoderScope scope = new(new FakeMetalEncoderSink(calls));

            scope.BeginRecording(new IntPtr(0x111));
            scope.EnsureBlitEncoder();
            scope.BeginRecording(new IntPtr(0x222));
            scope.EnsureBlitEncoder();

            Assert.Contains(calls.Log, line => line.Contains("on 273", StringComparison.Ordinal));
            Assert.Contains(calls.Log, line => line.Contains("on 546", StringComparison.Ordinal));
        }

        /// <summary>
        /// A BEGIN THAT DISCARDS A RECORDING STILL ENDS ITS ENCODER, which is the ownership rule rather than a
        /// courtesy to the driver. The sink retains every encoder it opens and the end is the only release, so
        /// dropping one here would leak that +1 and, through the reference an encoder holds on its command
        /// buffer, keep a buffer the command list has already released counted against the queue's uncommitted
        /// maximum. The queue BLOCKS in <c>-commandBuffer</c> at that maximum, so the leak is a hang rather than
        /// a number.
        /// </summary>
        [Fact]
        public void ARecordingDiscardedByANewBeginEndsItsOpenEncoder()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            IntPtr abandoned = scope.EnsureRenderEncoder(new IntPtr(0xD5));
            scope.BeginRecording(new IntPtr(0x200));

            Assert.Equal(MetalEncoderKind.None, scope.Open);

            // A begin and its end, in that order, and the retain balanced with them.
            Assert.Equal(2, calls.EncoderBoundaries);
            Assert.Equal($"end Render {abandoned}", calls.Log[1]);
            Assert.Equal(0, calls.OutstandingEncoders);
            Assert.Equal(0, calls.UnbalancedEncoderReleases);
        }

        /// <summary>THE ENCODER OWNERSHIP RULE, end to end at the scope: exactly one release per acquisition,
        /// across the ordinary end, a kind switch, and a recording abandoned by the next begin. A leak at any of
        /// them holds a command buffer alive against a queue maximum that blocks rather than fails.</summary>
        [Fact]
        public void EveryEncoderIsEndedExactlyOnceAcrossEveryScopeExit()
        {
            (MetalEncoderScope scope, FakeMetalEncoderCalls calls) = NewScope();

            // The kind switch, which ends the outgoing encoder.
            scope.EnsureRenderEncoder(new IntPtr(0xD5));
            scope.EnsureBlitEncoder();

            // The ordinary end.
            scope.EnsureNoEncoder();

            // And the abandon, with one open.
            scope.EnsureComputeEncoder();
            scope.BeginRecording(new IntPtr(0x300));

            Assert.Equal(3, calls.RetainedEncoders.Count);
            Assert.Equal(3, calls.ReleasedEncoders.Count);
            Assert.Equal(0, calls.OutstandingEncoders);
            Assert.Equal(0, calls.UnbalancedEncoderReleases);
        }

        [Fact]
        public void ANullSinkIsRefusedAtConstruction()
            => Assert.Throws<ArgumentNullException>(() => new MetalEncoderScope(null!));
    }
}
