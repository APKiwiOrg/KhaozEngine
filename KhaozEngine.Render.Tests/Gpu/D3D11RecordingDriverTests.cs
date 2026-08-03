using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The two recording drivers of the native Direct3D 11 backend, driven through the real
    /// <see cref="IGpuCommandList"/> seam with a counting emitter behind them: the deferred command stream
    /// (decision R1) and the immediate emit that <c>KE_D3D11_RECORD=immediate</c> selects (decision R2).
    /// <para>
    /// EXIT CRITERION FOR THIS WORK, verbatim from section 13: build and unit only, meaning both drivers exist,
    /// compile, and pass their device-free op-encoding and replay-ordering tests. Milestone M1 is NOT measured
    /// here and nothing here claims to. M1 is end-to-end frame time on a real scene taken after the minimal
    /// renderable path lands, and the first version of the spec made it this row's exit criterion, which was
    /// circular: measuring a frame needs resources, pipelines, shaders, a bind flush, draws and a swapchain,
    /// which are exactly the rows M1 was said to block.
    /// </para>
    /// </summary>
    public sealed class D3D11RecordingDriverTests
    {
        // ---- Decision X1: the emitter cannot create anything ----

        /// <summary>
        /// THE COMPILE-TIME INVARIANT OF DECISION X1, asserted so it stays one. Every SRV, RTV, DSV, UAV and
        /// state object is created at resource, set or pipeline creation, and the emitter interface therefore has
        /// no <c>Create</c> member at all, which makes creating a view during replay a compile error rather than
        /// an assertion that fires on somebody's machine. All 25 DEVICE_REMOVED stacks in the incumbent's field
        /// reports surfaced inside a view constructor reached from activation, which is the failure this rules
        /// out.
        /// <para>
        /// A reflection check rather than a comment, because the property is only worth anything while it holds,
        /// and the way it stops holding is somebody adding one convenient member years from now.
        /// </para>
        /// </summary>
        [Fact]
        public void TheEmitterSeam_HasNoCreateMember()
        {
            string[] creators = typeof(ID3D11Emitter).GetMethods()
                .Select(m => m.Name)
                .Where(n => n.StartsWith("Create", StringComparison.Ordinal))
                .ToArray();

            Assert.True(creators.Length == 0,
                "ID3D11Emitter grew a creation member: [" + string.Join(", ", creators) + "]. Decision X1 keeps "
                + "every view and state object created at resource, set or pipeline creation, so draw-time "
                + "creation is a compile error. Create it eagerly and pass the handle instead.");
        }

        // ---- Decision R1: zero native calls during record ----

        /// <summary>
        /// THE HEADLINE PROPERTY OF THE DEFERRED DRIVER: a whole frame records into the stream and every emitter
        /// call happens inside the replay. That is decision R1, and it is what removes the nested-Begin hazard
        /// class structurally rather than by a claim about what does not happen.
        /// <para>
        /// The emptiness during record is STRUCTURAL, and worth saying plainly because a passing assertion here
        /// alone would not have earned it: the deferred recorder's type argument is the stream emitter, so it has
        /// no other emitter to reach until submit hands it one. The assertion pins that shape rather than
        /// discovering it.
        /// </para>
        /// </summary>
        [Fact]
        public void OnTheDeferredDriver_EveryEmitterCallHappensInsideTheReplay()
        {
            var log = new D3D11EmitterCallLog();
            using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();

            RecordOneOfEverything(list, new Fixtures());

            Assert.True(list.Emitter.Stream.Count > 0, "The frame was not recorded at all.");
            Assert.Equal(0, log.TotalCalls);

            var emitter = new D3D11CountingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref emitter);

            Assert.Equal(list.Emitter.Stream.Count + 2, log.TotalCalls);
        }

        /// <summary>
        /// THE SEAM PROPERTY SECTION 16 REQUIRES: the op stream is ONE DRIVER of the emitter and not a mandatory
        /// layer under it. The immediate driver reaches the emitter as the seam is called, with no stream
        /// anywhere, which is both decision R2's M1 fallback and the shape phase 3 needs, since Vulkan and Metal
        /// have real deferred command buffers and would emit at record time straight into them.
        /// </summary>
        [Fact]
        public void OnTheImmediateDriver_TheEmitterIsReachedAtRecordTime_WithNoStream()
        {
            var log = new D3D11EmitterCallLog();
            using IGpuCommandList list = D3D11CommandDrivers.Create(
                D3D11RecordMode.Immediate, new D3D11CountingEmitter(log));

            list.Begin();
            list.Draw(3);

            // Before any submit, and before End: the calls have already been made.
            Assert.Equal(2, log.TotalCalls);
            Assert.Equal(1, log.Count(D3D11OpCode.Begin));
            Assert.Equal(1, log.Count(D3D11OpCode.Draw));

            // And it is not a stream recorder wearing a different hat.
            Assert.IsNotType<D3D11CommandRecorder<D3D11StreamEmitter>>(list);

            list.End();
            var replay = new D3D11CountingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref replay);

            // Submitting adds nothing, because there is nothing recorded to replay.
            Assert.Equal(3, log.TotalCalls);
        }

        // ---- Decision R2: both drivers share every line above the emitter ----

        /// <summary>
        /// THE SHARING, PROVEN RATHER THAN ASSERTED IN A COMMENT: the same seam calls produce the SAME emitter
        /// call sequence on both drivers, argument for argument and in the same order. Section 5.3 requires the
        /// fallback driver to share every line above the emitter, and this is what that means observably.
        /// <para>
        /// The two drivers are the same class with a different type argument, so the sharing is structural, and
        /// this test is what would catch it stopping being so. Milestone M1 A/Bs these two drivers on one build,
        /// and an A/B between two things that record differently would measure the difference in the recording
        /// rather than the difference in when the native calls happen.
        /// </para>
        /// </summary>
        [Fact]
        public void BothDrivers_ProduceTheSameEmitterCallSequence()
        {
            var fixtures = new Fixtures();

            var deferredLog = new D3D11EmitterCallLog();
            using (IGpuCommandList deferred = D3D11CommandDrivers.CreateDeferred())
            {
                RecordOneOfEverything(deferred, fixtures);
                var emitter = new D3D11CountingEmitter(deferredLog);
                D3D11CommandDrivers.Replay(deferred, ref emitter);
            }

            var immediateLog = new D3D11EmitterCallLog();
            using (IGpuCommandList immediate = D3D11CommandDrivers.Create(
                D3D11RecordMode.Immediate, new D3D11CountingEmitter(immediateLog)))
            {
                RecordOneOfEverything(immediate, fixtures);
                var emitter = new D3D11CountingEmitter(immediateLog);
                D3D11CommandDrivers.Replay(immediate, ref emitter);
            }

            Assert.Equal(deferredLog.Trace, immediateLog.Trace);
            Assert.Equal(deferredLog.TotalCalls, immediateLog.TotalCalls);
        }

        // ---- Replay ordering ----

        /// <summary>
        /// REPLAY ORDER IS RECORD ORDER, over one of every command the seam carries. Every command is here on
        /// purpose: a switch arm that decoded the wrong payload word would otherwise sit undetected until the
        /// pass that uses it renders wrong, and the arguments in the trace are what catch a swapped pair.
        /// </summary>
        [Fact]
        public void ReplayIssuesEveryRecordedCommand_InRecordOrder()
        {
            var log = new D3D11EmitterCallLog();
            var fixtures = new Fixtures();
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            RecordOneOfEverything(list, fixtures);

            var emitter = new D3D11CountingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref emitter);

            Assert.Equal(ExpectedTrace(log, fixtures), log.Trace);
        }

        /// <summary>
        /// Every command the seam declares is exercised by <see cref="RecordOneOfEverything"/>. Without this the
        /// coverage above is a claim about a list somebody has to keep in sync by hand, and a seam member added
        /// later would quietly go untested on both drivers at once.
        /// </summary>
        [Fact]
        public void TheRecordedFrame_ExercisesEverySeamCommand()
        {
            var log = new D3D11EmitterCallLog();
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            RecordOneOfEverything(list, new Fixtures());
            var emitter = new D3D11CountingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref emitter);

            string[] untouched = Enum.GetValues<D3D11OpCode>()
                .Where(c => c != D3D11OpCode.None && log.Count(c) == 0)
                .Select(c => c.ToString())
                .ToArray();

            Assert.True(untouched.Length == 0,
                "These commands are never recorded by the device-free frame, so nothing checks how they encode "
                + "or replay: [" + string.Join(", ", untouched) + "]. Add them to RecordOneOfEverything.");
        }

        /// <summary>
        /// DECISION R3: each replay opens exactly ONE scope, which is where a real emitter issues its single
        /// <c>ClearState</c>. Replaying the same recording twice therefore opens two clean scopes rather than
        /// accumulating, which is what makes the scope markers a property of replay instead of a recorded
        /// command.
        /// </summary>
        [Fact]
        public void EachReplay_OpensExactlyOneScope()
        {
            var log = new D3D11EmitterCallLog();
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(3);
            list.End();

            var emitter = new D3D11CountingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref emitter);
            Assert.Equal(1, log.Count(D3D11OpCode.Begin));
            Assert.Equal(1, log.Count(D3D11OpCode.End));

            D3D11CommandDrivers.Replay(list, ref emitter);
            Assert.Equal(2, log.Count(D3D11OpCode.Begin));
            Assert.Equal(2, log.Count(D3D11OpCode.End));
            Assert.Equal(2, log.Count(D3D11OpCode.Draw));
        }

        /// <summary>
        /// SUBMIT ORDER IS THE OBSERVABLE ORDER (decision R3). Two lists recorded interleaved replay whole, one
        /// after the other, in the order they were submitted, which is what Vulkan and Metal naturally provide
        /// and what the seam already documents. The rejected alternative, a nested recording JOINING an open one
        /// so its commands interleave where they were recorded, is a third semantics no phase 3 backend could
        /// reproduce.
        /// </summary>
        [Fact]
        public void TwoInterleavedRecordings_ReplayWholeAndInSubmitOrder()
        {
            var log = new D3D11EmitterCallLog();
            using IGpuCommandList first = D3D11CommandDrivers.CreateDeferred();
            using IGpuCommandList second = D3D11CommandDrivers.CreateDeferred();

            first.Begin();
            second.Begin();
            first.Draw(1);
            second.Draw(2);
            first.Draw(3);
            second.Draw(4);
            first.End();
            second.End();

            var emitter = new D3D11CountingEmitter(log);
            D3D11CommandDrivers.Replay(second, ref emitter);
            D3D11CommandDrivers.Replay(first, ref emitter);

            Assert.Equal(new[]
            {
                "Begin()", "Draw(2,1,0,0)", "Draw(4,1,0,0)", "End()",
                "Begin()", "Draw(1,1,0,0)", "Draw(3,1,0,0)", "End()",
            }, log.Trace);
        }

        /// <summary>
        /// The payload a caller handed over is COPIED, so a caller that reuses its own scratch array between the
        /// record and the submit still gets what it recorded. That is the whole reason the payload arena exists,
        /// and getting it wrong produces a frame of stale or torn vertex data with nothing in the code to point
        /// at.
        /// </summary>
        [Fact]
        public void ARecordedPayload_SurvivesTheCallerReusingItsBuffer()
        {
            var log = new D3D11EmitterCallLog();
            var buffer = new FakeBuffer(64);
            byte[] scratch = { 1, 2, 3, 4 };
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();

            list.Begin();
            list.UpdateBuffer<byte>(buffer, 0, scratch);
            list.End();
            scratch.AsSpan().Fill(0xFF);

            var recorded = new CapturingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref recorded);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, recorded.Bytes.Single());
        }

        /// <summary>A single struct upload reaches the emitter as its bytes, which is what a replayed write has
        /// left after the generic parameter is erased.</summary>
        [Fact]
        public void ASingleStructUpload_ReachesTheEmitterAsItsBytes()
        {
            var log = new D3D11EmitterCallLog();
            var buffer = new FakeBuffer(64);
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();

            list.Begin();
            list.UpdateBuffer(buffer, 16, in Sixteen);
            list.End();

            var recorded = new CapturingEmitter(log);
            D3D11CommandDrivers.Replay(list, ref recorded);

            Assert.Equal(16, recorded.Bytes.Single().Length);
        }

        static readonly Vector4Like Sixteen = new(1f, 2f, 3f, 4f);

        /// <summary>Sixteen bytes of unmanaged struct, so the single-value upload overload has something with a
        /// size worth asserting.</summary>
        internal readonly struct Vector4Like
        {
            public readonly float X, Y, Z, W;
            public Vector4Like(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        }

        // ---- The Begin, End and Submit contract ----

        /// <summary>A second <c>Begin</c> would silently discard everything recorded since the first, because
        /// <c>Begin</c> truncates. It says so instead.</summary>
        [Fact]
        public void ASecondBegin_ThrowsRatherThanSilentlyDiscardingTheRecording()
        {
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();

            Assert.Throws<InvalidOperationException>(list.Begin);
        }

        /// <summary><c>End</c> with no recording open is a caller sequencing error and never anything the
        /// backend can make sense of.</summary>
        [Fact]
        public void EndWithoutBegin_Throws()
        {
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();

            Assert.Throws<InvalidOperationException>(list.End);
        }

        /// <summary>
        /// Submitting a list that was never ended THROWS. Replaying it would replay a half-recorded frame rather
        /// than failing, which reads as a rendering defect somewhere else entirely, and both drivers answer the
        /// same way so the check does not depend on which one is selected.
        /// </summary>
        [Fact]
        public void SubmittingAnUnsealedList_Throws()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);

            using (IGpuCommandList deferred = D3D11CommandDrivers.CreateDeferred())
            {
                deferred.Begin();
                deferred.Draw(3);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    var replay = new D3D11CountingEmitter(log);
                    D3D11CommandDrivers.Replay(deferred, ref replay);
                });
            }

            using IGpuCommandList immediate = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, emitter);
            immediate.Begin();
            immediate.Draw(3);
            Assert.Throws<InvalidOperationException>(() =>
            {
                var replay = new D3D11CountingEmitter(log);
                D3D11CommandDrivers.Replay(immediate, ref replay);
            });
        }

        /// <summary>A list this backend did not create cannot be replayed, and the message says why rather than
        /// producing an invalid cast somewhere further down.</summary>
        [Fact]
        public void SubmittingAForeignList_SaysSo()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            using var foreign = new NullGpuCommandList();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11CommandDrivers.Replay(foreign, ref emitter));

            Assert.Contains("Direct3D 11", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// RECORDING TAKES NO LOCK (section 5.1 and decision W4). The submit lock covers replay, present and the
        /// resize apply, and nothing else, so a list can be recorded on one thread while another is mid-submit.
        /// Asserted by holding the lock and recording a whole frame underneath it, which is the only way to
        /// observe the absence of a lock rather than reason about it.
        /// </summary>
        [Fact]
        public void RecordingDoesNotTakeTheSubmitLock()
        {
            object submitLock = new();
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            using var recorded = new ManualResetEventSlim();
            var recorder = new Thread(() =>
            {
                RecordOneOfEverything(list, new Fixtures());
                recorded.Set();
            }) { IsBackground = true };

            lock (submitLock)
            {
                recorder.Start();

                Assert.True(recorded.Wait(TimeSpan.FromSeconds(10)),
                    "Recording blocked while the submit lock was held. Begin and the record path must touch no "
                    + "lock and no device state, which is what lets N lists record while one is submitting.");
            }
        }

        /// <summary>Submit takes the lock around the replay, which is what serialises two submits against each
        /// other while leaving recording free.</summary>
        [Fact]
        public void SubmitTakesTheLockAroundTheReplay()
        {
            var log = new D3D11EmitterCallLog();
            object submitLock = new();
            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(3);
            list.End();

            using var started = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var submitter = new Thread(() =>
            {
                started.Set();
                var emitter = new D3D11CountingEmitter(log);
                D3D11CommandDrivers.Submit(submitLock, list, ref emitter);
                finished.Set();
            }) { IsBackground = true };

            bool blocked;
            lock (submitLock)
            {
                submitter.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "The submitting thread never started.");
                blocked = !finished.Wait(TimeSpan.FromMilliseconds(250));
                Assert.Equal(0, log.TotalCalls);
            }

            Assert.True(finished.Wait(TimeSpan.FromSeconds(10)), "Submit never completed after the lock released.");
            Assert.True(blocked, "Submit replayed without taking the submit lock the caller passed it.");
            Assert.Equal(3, log.TotalCalls);
        }

        /// <summary>
        /// Disposing a list DROPS the resources its recording held, which is the far end of section 5.1's
        /// lifetime rule. Leaving it to the collector would have worked and would have made "for the recording's
        /// lifetime" mean "until a collection happens to run".
        /// </summary>
        [Fact]
        public void DisposingAList_ReleasesTheResourcesItsRecordingHeld()
        {
            D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
            D3D11CommandStream stream = list.Emitter.Stream;
            RecordOneOfEverything(list, new Fixtures());
            Assert.True(stream.ReferenceCount > 0);

            list.Dispose();

            Assert.Equal(0, stream.ReferenceCount);
            Assert.Equal(0, stream.Count);
            Assert.Equal(0, stream.PayloadLength);
        }

        /// <summary>Disposing twice is harmless, and a disposed list refuses a new recording rather than handing
        /// back a stream nothing will ever replay.</summary>
        [Fact]
        public void ADisposedList_IsIdempotentAndRefusesToRecordAgain()
        {
            IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Dispose();
            list.Dispose();

            Assert.Throws<ObjectDisposedException>(list.Begin);
        }

        // ---- KE_D3D11_RECORD, the M1 kill switch ----

        /// <summary>
        /// The command-stream driver is the DEFAULT, which is decision R1 shipping and R2 waiting. Unset, empty
        /// and whitespace all mean the default, since an unset variable and a variable set to nothing are the
        /// same statement about intent.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("deferred")]
        [InlineData("Deferred")]
        [InlineData("stream")]
        [InlineData(" STREAM ")]
        public void TheCommandStreamDriver_IsTheDefault(string? value)
        {
            Assert.Equal(D3D11RecordMode.Deferred, D3D11RecordModes.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>The kill switch itself. It exists from the moment this row lands, because milestone M1 needs
        /// both drivers A/B-able on one build.</summary>
        [Theory]
        [InlineData("immediate")]
        [InlineData("Immediate")]
        [InlineData("  IMMEDIATE  ")]
        public void TheImmediateDriver_IsSelectedByName(string value)
        {
            Assert.Equal(D3D11RecordMode.Immediate, D3D11RecordModes.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// A value that was set and understood as nothing comes back so the caller can WARN. This variable exists
        /// to attribute a measurement to a driver, so a mistyped switch that silently ran the default is how a
        /// number gets published under the wrong driver and then retracted.
        /// </summary>
        [Theory]
        [InlineData("defered")]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("off")]
        public void AMistypedValue_ComesBackVerbatimAndFallsBackToTheDefault(string value)
        {
            Assert.Equal(D3D11RecordMode.Deferred, D3D11RecordModes.Resolve(value, out string? unrecognized));
            Assert.Equal(value, unrecognized);
            Assert.Contains(value, D3D11RecordModes.UnrecognizedWarning(value), StringComparison.Ordinal);
            Assert.Contains(D3D11RecordModes.EnvVarName, D3D11RecordModes.UnrecognizedWarning(value),
                StringComparison.Ordinal);
        }

        /// <summary>Reading the live environment agrees with the pure parse, which is the only thing the impure
        /// member adds. Nothing here mutates the environment, so it stays safe to run in parallel.</summary>
        [Fact]
        public void ReadingTheEnvironment_AgreesWithThePureParse()
        {
            D3D11RecordMode fromEnvironment = D3D11RecordModes.FromEnvironment(out string? envUnrecognized);
            D3D11RecordMode parsed = D3D11RecordModes.Resolve(
                Environment.GetEnvironmentVariable(D3D11RecordModes.EnvVarName), out string? parseUnrecognized);

            Assert.Equal(parsed, fromEnvironment);
            Assert.Equal(parseUnrecognized, envUnrecognized);
        }

        /// <summary>The active-driver line names the driver a capture ran on, so a session log PROVES which one
        /// produced its numbers rather than resting on the tester believing they set the variable.</summary>
        [Fact]
        public void TheActiveDriverLine_NamesTheDriverThatRan()
        {
            Assert.Contains("IMMEDIATE", D3D11RecordModes.ActiveDescription(D3D11RecordMode.Immediate),
                StringComparison.Ordinal);
            Assert.Contains("COMMAND-STREAM", D3D11RecordModes.ActiveDescription(D3D11RecordMode.Deferred),
                StringComparison.Ordinal);
        }

        /// <summary>The mode picks the instantiation, which is the one place in the backend that branches on the
        /// switch.</summary>
        [Fact]
        public void TheModeSelectsTheDriver()
        {
            var log = new D3D11EmitterCallLog();

            using IGpuCommandList deferred = D3D11CommandDrivers.Create(
                D3D11RecordMode.Deferred, new D3D11CountingEmitter(log));
            using IGpuCommandList immediate = D3D11CommandDrivers.Create(
                D3D11RecordMode.Immediate, new D3D11CountingEmitter(log));

            Assert.IsType<D3D11CommandRecorder<D3D11StreamEmitter>>(deferred);
            Assert.IsType<D3D11CommandRecorder<D3D11CountingEmitter>>(immediate);
        }

        // ---- Fixtures ----

        /// <summary>The handles the device-free frame binds. One instance per frame, shared between the two
        /// drivers so their traces name the same resources.</summary>
        internal sealed class Fixtures
        {
            internal FakeFramebuffer Framebuffer { get; } =
                new(new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R8G8B8A8UNorm), 640, 480);
            internal FakePipeline Pipeline { get; } = new();
            internal FakeComputePipeline ComputePipeline { get; } = new();
            internal FakeResourceSet Set { get; } = new();
            internal FakeBuffer Vertices { get; } = new(4096);
            internal FakeBuffer Indices { get; } = new(2048);
            internal FakeBuffer Uniforms { get; } = new(256);
            internal FakeBuffer Staging { get; } = new(256);
            internal FakeTexture Colour { get; } = new(64, 64, 4, 1, GpuPixelFormat.R8G8B8A8UNorm);
            internal FakeTexture Readback { get; } = new(64, 64, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            internal FakeTexture Multisampled { get; } = new(64, 64, 1, 4, GpuPixelFormat.R8G8B8A8UNorm);
        }

        internal sealed class FakeComputePipeline : IGpuComputePipeline { public void Dispose() { } }

        /// <summary>
        /// One of every command the seam carries, in a fixed order. Shared by the ordering check, the two-driver
        /// comparison and the coverage check, so all three describe the same frame and a new seam member only has
        /// to be added once.
        /// </summary>
        static void RecordOneOfEverything(IGpuCommandList list, Fixtures f)
        {
            list.Begin();
            list.SetFramebuffer(f.Framebuffer);
            list.ClearColorTarget(0, new Color(0.1f, 0.2f, 0.3f, 1f));
            list.ClearDepthStencil(1f);
            list.SetPipeline(f.Pipeline);
            list.SetGraphicsResourceSet(0, f.Set);
            list.SetGraphicsResourceSet(1, f.Set, 512);
            list.SetVertexBuffer(0, f.Vertices);
            list.SetVertexBuffer(1, f.Vertices, 64);
            list.SetIndexBuffer(f.Indices, GpuIndexFormat.UInt32);
            list.SetScissorRect(0, 4, 8, 16, 32);
            list.SetFullScissorRects();
            list.Draw(3);
            list.Draw(6, 2, 1, 3);
            list.DrawIndexed(12, 1, 6, -2, 0);
            list.UpdateBuffer<byte>(f.Uniforms, 32, new byte[] { 7, 7, 7, 7 });
            list.CopyBuffer(f.Uniforms, 0, f.Staging, 16, 64);
            list.CopyTexture(f.Colour, f.Readback);
            list.CopyTextureSubresource(f.Colour, 2, 0, f.Readback, 16, 16);
            list.CopyTextureSubresource(f.Colour, 1, 0, f.Readback, 3, 1, 32, 32);
            list.GenerateMipmaps(f.Colour);
            list.ResolveTexture(f.Multisampled, f.Readback);
            list.SetComputePipeline(f.ComputePipeline);
            list.SetComputeResourceSet(0, f.Set);
            list.SetComputeResourceSet(1, f.Set, 256);
            list.Dispatch(8, 4, 1);
            list.End();
        }

        /// <summary>
        /// What <see cref="RecordOneOfEverything"/> must look like on the far side of a replay, written out by
        /// hand. Spelled out rather than derived from the recorder, because a check derived from the thing it
        /// checks passes whatever that thing does: this is the only place the mapping from a seam call to an
        /// emitter call is stated independently, including the three forwarding rules (no-offset vertex bind is
        /// offset zero, single-instance draw is one instance from zero, short subresource copy targets mip and
        /// layer zero).
        /// </summary>
        static IEnumerable<string> ExpectedTrace(D3D11EmitterCallLog log, Fixtures f) => new[]
        {
            "Begin()",
            $"SetFramebuffer({log.Id(f.Framebuffer)})",
            "ClearColorTarget(0,0.1,0.2,0.3,1)",
            "ClearDepthStencil(1)",
            $"SetPipeline({log.Id(f.Pipeline)})",
            $"SetGraphicsResourceSet(0,{log.Id(f.Set)})",
            $"SetGraphicsResourceSetDynamic(1,{log.Id(f.Set)},512)",
            $"SetVertexBuffer(0,{log.Id(f.Vertices)},0)",
            $"SetVertexBuffer(1,{log.Id(f.Vertices)},64)",
            $"SetIndexBuffer({log.Id(f.Indices)},UInt32)",
            "SetScissorRect(0,4,8,16,32)",
            "SetFullScissorRects()",
            "Draw(3,1,0,0)",
            "Draw(6,2,1,3)",
            "DrawIndexed(12,1,6,-2,0)",
            $"UpdateBuffer({log.Id(f.Uniforms)},32,4b,{Fnv(new byte[] { 7, 7, 7, 7 })})",
            $"CopyBuffer({log.Id(f.Uniforms)},0,{log.Id(f.Staging)},16,64)",
            $"CopyTexture({log.Id(f.Colour)},{log.Id(f.Readback)})",
            $"CopyTextureSubresource({log.Id(f.Colour)},2,0,{log.Id(f.Readback)},0,0,16,16)",
            $"CopyTextureSubresource({log.Id(f.Colour)},1,0,{log.Id(f.Readback)},3,1,32,32)",
            $"GenerateMipmaps({log.Id(f.Colour)})",
            $"ResolveTexture({log.Id(f.Multisampled)},{log.Id(f.Readback)})",
            $"SetComputePipeline({log.Id(f.ComputePipeline)})",
            $"SetComputeResourceSet(0,{log.Id(f.Set)})",
            $"SetComputeResourceSetDynamic(1,{log.Id(f.Set)},256)",
            "Dispatch(8,4,1)",
            "End()",
        };

        static uint Fnv(ReadOnlySpan<byte> data)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < data.Length; i++) { hash ^= data[i]; hash *= 16777619u; }
            return hash;
        }

        /// <summary>A counting emitter that also KEEPS the upload bytes, so a test can assert the payload arena
        /// preserved content rather than only a length and a checksum.</summary>
        internal readonly struct CapturingEmitter : ID3D11Emitter
        {
            readonly D3D11CountingEmitter _inner;
            readonly List<byte[]> _bytes;

            internal CapturingEmitter(D3D11EmitterCallLog log)
            {
                _inner = new D3D11CountingEmitter(log);
                _bytes = new List<byte[]>();
            }

            internal IReadOnlyList<byte[]> Bytes => _bytes;

            public void Begin() => _inner.Begin();
            public void End() => _inner.End();
            public void SetFramebuffer(IGpuFramebuffer framebuffer) => _inner.SetFramebuffer(framebuffer);
            public void ClearColorTarget(uint index, Color rgba) => _inner.ClearColorTarget(index, rgba);
            public void ClearDepthStencil(float depth) => _inner.ClearDepthStencil(depth);
            public void SetPipeline(IGpuPipeline pipeline) => _inner.SetPipeline(pipeline);
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
                => _inner.SetGraphicsResourceSet(slot, set);
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
                => _inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
            public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
                => _inner.SetVertexBuffer(slot, buffer, offsetBytes);
            public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
                => _inner.SetIndexBuffer(buffer, format);
            public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
                => _inner.SetScissorRect(index, x, y, width, height);
            public void SetFullScissorRects() => _inner.SetFullScissorRects();
            public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
                => _inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
            public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
                => _inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);

            public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
            {
                _bytes.Add(data.ToArray());
                _inner.UpdateBuffer(buffer, offsetBytes, data);
            }

            public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
                => _inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
            public void CopyTexture(IGpuTexture src, IGpuTexture dst) => _inner.CopyTexture(src, dst);
            public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
                IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
                => _inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer,
                    width, height);
            public void GenerateMipmaps(IGpuTexture texture) => _inner.GenerateMipmaps(texture);
            public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => _inner.ResolveTexture(src, dst);
            public void SetComputePipeline(IGpuComputePipeline pipeline) => _inner.SetComputePipeline(pipeline);
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
                => _inner.SetComputeResourceSet(slot, set);
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
                => _inner.SetComputeResourceSet(slot, set, dynamicOffset);
            public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
                => _inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        }
    }
}
