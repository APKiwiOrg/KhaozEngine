using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE RECORDING CONTRACT, made executable. Decision R3 says <c>Begin</c> resets and touches no device
    /// state, N lists may record concurrently, each submit's replay opens with exactly one <c>ClearState</c>, and
    /// SUBMIT order is the observable order. Decision R4 keeps that as the NATIVE backend's contract under the
    /// deferred driver, the default, while the portable seam contract stays at one open recording per device.
    /// <para>
    /// Without these, "nested Begin is legal and submit order is observable" is a sentence in a design document
    /// with no executable meaning, which is the reason section 12 homes this test on this row. It is device-free
    /// and runs under a plain <c>dotnet test</c> on macOS and Linux, because the emitter seam is written in
    /// engine-owned handle types and names no Direct3D type.
    /// </para>
    /// <para>
    /// These drive the NATIVE call trace rather than the seam call trace, so <c>ClearState</c> is asserted as
    /// itself. A landed sibling test asserts the same ordering one level up, at the seam, where the scope markers
    /// stand in for it.
    /// </para>
    /// </summary>
    public sealed class D3D11ReplayContractTests
    {
        // ---- Decision R3: N recorders, interleaved, replayed in submit order ----

        /// <summary>
        /// THE CONTRACT TEST OF SECTION 12, verbatim: open N recorders, interleave recorded commands across them,
        /// submit them OUT of record order, and the replayed sequence is exactly per-list order concatenated in
        /// submit order, with exactly one <c>ClearState</c> at the head of each replay.
        /// <para>
        /// Three lists rather than two, because two cannot tell "submit order" apart from "reverse record order".
        /// Every submit goes through the real <see cref="D3D11CommandDrivers.Submit{TEmitter}"/> with the device's
        /// lock, so what is under test is the shipped path and not a replay helper.
        /// </para>
        /// <para>
        /// DEFERRED RECORDERS ONLY, and that is not an omission: under <c>KE_D3D11_RECORD=immediate</c> the
        /// interleaving above IS the emission order, so the contract this pins does not exist on that driver to
        /// be asserted.
        /// </para>
        /// </summary>
        [Fact]
        public void ThreeInterleavedRecordings_ReplayWholeAndInSubmitOrder_EachOpeningWithOneClearState()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            object submitLock = new();

            using IGpuCommandList first = D3D11CommandDrivers.CreateDeferred();
            using IGpuCommandList second = D3D11CommandDrivers.CreateDeferred();
            using IGpuCommandList third = D3D11CommandDrivers.CreateDeferred();

            first.Begin();
            second.Begin();
            first.Draw(1);
            third.Begin();
            second.Draw(10);
            third.Draw(100);
            first.Draw(2);
            second.Draw(20);
            third.Draw(200);
            first.End();
            third.End();
            second.End();

            // Submitted in an order that is neither the record order nor its reverse.
            D3D11CommandDrivers.Submit(submitLock, third, ref emitter);
            D3D11CommandDrivers.Submit(submitLock, first, ref emitter);
            D3D11CommandDrivers.Submit(submitLock, second, ref emitter);

            Assert.Equal(
                new[]
                {
                    "ClearState()", "DrawInstanced(100,1,0,0)", "DrawInstanced(200,1,0,0)",
                    "ClearState()", "DrawInstanced(1,1,0,0)", "DrawInstanced(2,1,0,0)",
                    "ClearState()", "DrawInstanced(10,1,0,0)", "DrawInstanced(20,1,0,0)",
                },
                log.Trace);
            Assert.Equal(3, log.Count(D3D11NativeCall.ClearState));
        }

        /// <summary>
        /// EXACTLY ONE <c>ClearState</c> PER SUBMIT, which is the first of decision T2's four structural
        /// invariants and the one that makes the redundancy caches safe at all: the caches describe the context,
        /// and this is the single moment per replay where the context is known to hold nothing.
        /// <para>
        /// An EMPTY list still costs one, because the invariant is per submit and not per command. Submitting the
        /// same list twice costs two, because a stream is replayable and each replay opens its own scope.
        /// </para>
        /// </summary>
        [Fact]
        public void EverySubmit_OpensWithExactlyOneClearState_EmptyRecordingsIncluded()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            object submitLock = new();

            using IGpuCommandList empty = D3D11CommandDrivers.CreateDeferred();
            empty.Begin();
            empty.End();

            D3D11CommandDrivers.Submit(submitLock, empty, ref emitter);
            Assert.Equal(new[] { "ClearState()" }, log.Trace);

            D3D11CommandDrivers.Submit(submitLock, empty, ref emitter);
            Assert.Equal(new[] { "ClearState()", "ClearState()" }, log.Trace);
        }

        /// <summary>
        /// <c>Begin</c> TOUCHES NO DEVICE STATE, which is what makes a nested or concurrent <c>Begin</c>
        /// structurally harmless rather than harmless by claim: on the deferred driver it truncates an array, and
        /// the device's emitter is not reached until submit. Three lists open at once issue nothing between them.
        /// </summary>
        [Fact]
        public void OpeningThreeRecordingsAtOnce_IssuesNoNativeCall()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);

            using IGpuCommandList first = D3D11CommandDrivers.CreateDeferred();
            using IGpuCommandList second = D3D11CommandDrivers.CreateDeferred();
            using IGpuCommandList third = D3D11CommandDrivers.CreateDeferred();

            first.Begin();
            second.Begin();
            third.Begin();
            first.Draw(1);
            second.Draw(2);
            third.Draw(3);

            Assert.Equal(0, log.TotalCalls);

            first.End();
            second.End();
            third.End();
            D3D11CommandDrivers.Submit(new object(), first, ref emitter);

            Assert.Equal(new[] { "ClearState()", "DrawInstanced(1,1,0,0)" }, log.Trace);
        }

        /// <summary>
        /// AND THEY MAY RECORD ON DIFFERENT THREADS AT ONCE, which is what "N lists may record concurrently"
        /// means when nothing shares state. Structurally permitted and neither exercised nor supported as a
        /// shipped contract (decision W5), so this asserts that the recordings do not corrupt each other rather
        /// than that anything is safe to build on.
        /// </summary>
        [Fact]
        public void RecordingsOnDifferentThreads_DoNotCorruptEachOther()
        {
            const int Lists = 4;
            const int DrawsPerList = 64;
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            object submitLock = new();

            IGpuCommandList[] lists = Enumerable.Range(0, Lists)
                .Select(_ => (IGpuCommandList)D3D11CommandDrivers.CreateDeferred())
                .ToArray();

            Parallel.For(0, Lists, i =>
            {
                uint first = (uint)i * 1000u;
                lists[i].Begin();
                for (uint draw = 0; draw < DrawsPerList; draw++) lists[i].Draw(first + draw + 1u);
                lists[i].End();
            });

            foreach (IGpuCommandList list in lists) D3D11CommandDrivers.Submit(submitLock, list, ref emitter);

            var expected = new List<string>();
            for (uint i = 0; i < Lists; i++)
            {
                expected.Add("ClearState()");
                for (uint draw = 0; draw < DrawsPerList; draw++)
                    expected.Add($"DrawInstanced({(i * 1000u) + draw + 1u},1,0,0)");
            }

            Assert.Equal(expected, log.Trace);
            foreach (IGpuCommandList list in lists) list.Dispose();
        }

        // ---- List reuse, on both drivers ----

        /// <summary>
        /// A LIST IS REUSABLE, AND A SECOND RECORDING REPLACES THE FIRST rather than extending it. The permanent
        /// home for a check that only ever existed as a throwaway during review, and the reason it matters is the
        /// frame loop: one command list is opened, recorded, submitted and reopened every frame forever, so a
        /// recording that accumulated would grow without bound and replay last frame's draws with this frame's
        /// state.
        /// <para>
        /// Asserted on BOTH drivers, because they reach it differently. The deferred one truncates its stream in
        /// <c>Begin</c> and replays at submit, the immediate one has already emitted and its <c>Begin</c> is
        /// where the <c>ClearState</c> happens. Identical traces are what makes the milestone M1 A/B a
        /// measurement of when the calls happen rather than of what was recorded.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AListIsReusable_AndTheSecondRecordingReplacesTheFirst(bool immediate)
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            object submitLock = new();

            using IGpuCommandList list = immediate
                ? D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, emitter)
                : (IGpuCommandList)D3D11CommandDrivers.CreateDeferred();

            list.Begin();
            list.Draw(1);
            list.End();
            D3D11CommandDrivers.Submit(submitLock, list, ref emitter);

            list.Begin();
            list.Draw(2);
            list.End();
            D3D11CommandDrivers.Submit(submitLock, list, ref emitter);

            Assert.Equal(
                new[]
                {
                    "ClearState()", "DrawInstanced(1,1,0,0)",
                    "ClearState()", "DrawInstanced(2,1,0,0)",
                },
                log.Trace);
        }

        /// <summary>The same list reused many times over holds one recording's worth of ops, never a growing
        /// pile. The frame loop's shape, asserted directly on the stream the deferred driver keeps.</summary>
        [Fact]
        public void ReusingAListForManyFrames_KeepsOneFramesWorthOfOps()
        {
            using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();

            for (int frame = 0; frame < 32; frame++)
            {
                list.Begin();
                list.Draw(3);
                list.Draw(6);
                list.End();

                Assert.Equal(2, list.Emitter.Stream.Count);
            }
        }

        // ---- Issue #476: one emitter state per device, not one per list ----

        /// <summary>
        /// AN EMITTER RECEIVES ITS DEVICE STATE AND NEVER ALLOCATES ONE. The seam's readonly-struct rule forces
        /// mutable state behind a class reference, and a reflection test already enforces that shape, but a
        /// readonly struct that news up its own state object in its constructor passes that check and gives every
        /// command list its own redundancy caches, which is the exact defect the shape rule exists to prevent.
        /// <para>
        /// So the rule is stronger than "behind a reference": the DEVICE constructs one
        /// <see cref="D3D11DeviceState"/> and every emitter value it hands out points at that one. Expressed
        /// mechanically as "an emitter that carries device state takes it as a constructor parameter", which is
        /// what makes it impossible to obtain one without a caller deciding which state it belongs to.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryEmitterCarryingDeviceState_ReceivesItRatherThanAllocatingItsOwn()
        {
            Type[] emitters = typeof(ID3D11Emitter).Assembly.GetTypes()
                .Where(t => typeof(ID3D11Emitter).IsAssignableFrom(t) && t != typeof(ID3D11Emitter))
                .ToArray();

            // A scan that finds nothing passes without checking anything, which is how this test would rot the
            // day the emitter carrying the caches is renamed or moved.
            Assert.Contains(typeof(D3D11NativeTraceEmitter), emitters);

            string[] hazards = emitters
                .Where(CarriesDeviceState)
                .SelectMany(e => e.GetConstructors(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic))
                .Where(c => !c.GetParameters().Any(p => p.ParameterType == typeof(D3D11DeviceState)))
                .Select(c => c.DeclaringType!.Name + " has a constructor that takes no D3D11DeviceState, so an "
                    + "emitter value can exist without the device deciding which state it belongs to. The device "
                    + "owns exactly one state object and every emitter value must point at it, or each command "
                    + "list gets redundancy caches of its own and one list skips a rebind another invalidated.")
                .ToArray();

            Assert.True(hazards.Length == 0, string.Join(Environment.NewLine, hazards));
        }

        /// <summary>
        /// THE SAME RULE FROM THE OTHER SIDE, behaviourally. Two lists created from ONE emitter value reach ONE
        /// set of caches, so list A binding a pipeline, list B binding another, and A binding its own again
        /// issues the third bind. With a per-list cache A would skip it and draw with B's state, silently, on the
        /// immediate driver only.
        /// </summary>
        [Fact]
        public void TwoListsFromOneEmitter_ShareOneSetOfRedundancyCaches()
        {
            var log = new D3D11NativeCallLog();
            var device = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            D3D11StateCacheTests.FakeD3D11Pipeline first = D3D11StateCacheTests.Pipeline();
            D3D11StateCacheTests.FakeD3D11Pipeline second = D3D11StateCacheTests.Pipeline();

            using IGpuCommandList a = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, device);
            using IGpuCommandList b = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, device);

            a.Begin();
            b.Begin();
            a.SetPipeline(first);
            b.SetPipeline(second);
            log.Reset();
            a.SetPipeline(first);

            // Six, not seven: the two pipelines share a topology, so only the six object slots changed back.
            Assert.Equal(6, log.TotalCalls);
            Assert.Equal(1, log.Count(D3D11NativeCall.VSSetShader));
        }

        /// <summary>And a rebind through the OTHER list is still redundant, which is the half a per-list cache
        /// gets right by accident and the half that proves the state is genuinely shared.</summary>
        [Fact]
        public void ARebindThroughASecondList_IsStillRedundant()
        {
            var log = new D3D11NativeCallLog();
            var device = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            D3D11StateCacheTests.FakeD3D11Pipeline pipeline = D3D11StateCacheTests.Pipeline();

            using IGpuCommandList a = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, device);
            using IGpuCommandList b = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, device);

            a.Begin();
            b.Begin();
            a.SetPipeline(pipeline);
            log.Reset();

            b.SetPipeline(pipeline);

            Assert.Empty(log.Trace);
        }

        /// <summary>Across a replay boundary the caches are DROPPED rather than shared, and that is the same rule
        /// rather than an exception to it: two submitted lists reach the device's one state object, and the
        /// <c>ClearState</c> opening the second replay is what empties it.</summary>
        [Fact]
        public void TheClearStateOpeningTheSecondSubmit_DropsWhatTheFirstLeftBound()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            object submitLock = new();
            D3D11StateCacheTests.FakeD3D11Pipeline pipeline = D3D11StateCacheTests.Pipeline();

            using IGpuCommandList first = D3D11CommandDrivers.CreateDeferred();
            using IGpuCommandList second = D3D11CommandDrivers.CreateDeferred();

            first.Begin();
            first.SetPipeline(pipeline);
            first.End();
            second.Begin();
            second.SetPipeline(pipeline);
            second.End();

            D3D11CommandDrivers.Submit(submitLock, first, ref emitter);
            log.Reset();
            D3D11CommandDrivers.Submit(submitLock, second, ref emitter);

            // The ClearState that opens the second replay dropped the caches, so all seven are bound again. Not
            // a redundancy failure: the context genuinely holds nothing after a ClearState.
            Assert.Equal(8, log.TotalCalls);
            Assert.Equal(1, log.Count(D3D11NativeCall.ClearState));
        }

        static bool CarriesDeviceState(Type emitter) => emitter
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(f => f.FieldType == typeof(D3D11DeviceState));
    }
}
