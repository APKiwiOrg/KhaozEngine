using System;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION G4's PUMP AND ITS RATE LIMIT, driven off a fake queue on macOS and Linux. Everything that decides
    /// what gets logged, at which level, and when to stop is engine logic, so all of it is here. The Windows
    /// reader that produces the messages lands with the device row (see <see cref="FakeD3D11InfoQueue"/>).
    /// <para>
    /// THE RATE LIMIT IS NOT A NICETY. The debug layer's characteristic failure is ONE mistake reported once per
    /// draw call, so without a per-identity cap a session is thousands of copies of one line and the second
    /// distinct message is invisible. The three caps are per pump (bounds one bad frame), per message identity
    /// (the one that does the real work) and per session (the soak backstop).
    /// </para>
    /// </summary>
    public sealed class D3D11InfoQueuePumpTests
    {
        /// <summary>Decision G4: corruption and error are RAISED to WARN, and the layer's own warning severity is
        /// already there, so the three read alike in a log and the two informational ones do not. One fact rather
        /// than a theory, because the severity type is internal to the backend package and an <c>[InlineData]</c>
        /// of it would have to be public.</summary>
        [Fact]
        public void PromotesToWarning_RaisesCorruptionAndError()
        {
            Assert.True(D3D11InfoQueuePump.PromotesToWarning(D3D11InfoSeverity.Corruption));
            Assert.True(D3D11InfoQueuePump.PromotesToWarning(D3D11InfoSeverity.Error));
            Assert.True(D3D11InfoQueuePump.PromotesToWarning(D3D11InfoSeverity.Warning));
            Assert.False(D3D11InfoQueuePump.PromotesToWarning(D3D11InfoSeverity.Info));
            Assert.False(D3D11InfoQueuePump.PromotesToWarning(D3D11InfoSeverity.Message));
        }

        /// <summary>The ordinals match the Direct3D header's <c>D3D11_MESSAGE_SEVERITY</c>, so a Windows reader
        /// can cast rather than switch and a mismatch shows up as a wrong name in a log rather than as a silent
        /// misclassification of a corruption message as chatter.</summary>
        [Fact]
        public void TheSeverityOrdinalsMatchTheDirect3DHeader()
        {
            Assert.Equal(0, (int)D3D11InfoSeverity.Corruption);
            Assert.Equal(1, (int)D3D11InfoSeverity.Error);
            Assert.Equal(2, (int)D3D11InfoSeverity.Warning);
            Assert.Equal(3, (int)D3D11InfoSeverity.Info);
            Assert.Equal(4, (int)D3D11InfoSeverity.Message);
        }

        [Fact]
        public void Pump_WritesEachSeverityAtItsMappedLevel()
        {
            var queue = new FakeD3D11InfoQueue();
            queue.Add(D3D11InfoSeverity.Corruption, 11, "a corrupted resource");
            queue.Add(D3D11InfoSeverity.Error, 22, "an illegal bind");
            queue.Add(D3D11InfoSeverity.Info, 33, "chatter");
            var log = new RecordingLogger();

            using var pump = new D3D11InfoQueuePump(queue, logger: log);
            Assert.Equal(3, pump.Pump());

            Assert.Equal(2, log.Warns.Count);
            Assert.Single(log.Infos);
            Assert.Contains("a corrupted resource", log.Warns[0], StringComparison.Ordinal);
            Assert.Contains("id 22", log.Warns[1], StringComparison.Ordinal);
        }

        /// <summary>The message id is in every line, because it is the stable identity a reader searches on and
        /// the text is not: the runtime rewords messages between Windows versions.</summary>
        [Fact]
        public void Describe_CarriesTheSeverityAndTheMessageId()
        {
            string line = D3D11InfoQueuePump.Describe(
                new D3D11InfoMessage(D3D11InfoSeverity.Error, category: 3, id: 404, text: "gone"));

            Assert.Contains("Error", line, StringComparison.Ordinal);
            Assert.Contains("404", line, StringComparison.Ordinal);
            Assert.Contains("gone", line, StringComparison.Ordinal);
        }

        /// <summary>The queue is emptied at the end of every pump, including one that logged nothing, or it grows
        /// without bound on exactly the session the limiter exists for and every stored message is re-read and
        /// re-suppressed every frame.</summary>
        [Fact]
        public void Pump_EmptiesTheQueueEvenWhenTheLimitSuppressedEverything()
        {
            var queue = new FakeD3D11InfoQueue();
            queue.AddRepeated(D3D11InfoSeverity.Error, 7, "same", 40);
            var log = new RecordingLogger();
            var limit = new D3D11InfoQueueRateLimit(messagesPerPump: 4, repeatsPerMessage: 2, messagesPerSession: 100);

            using var pump = new D3D11InfoQueuePump(queue, limit, log);
            pump.Pump();

            // The second frame's batch is suppressed ENTIRELY by the per-identity cap the first frame already
            // hit, which is the case worth pinning: a pump that only cleared when it logged something would let
            // the queue grow without bound on exactly the session the limiter exists for.
            queue.AddRepeated(D3D11InfoSeverity.Error, 7, "same", 40);
            Assert.Equal(0, pump.Pump());

            Assert.Equal(2, queue.ClearCount);
        }

        /// <summary>An empty queue costs one read and nothing else, which is what makes leaving the pump on every
        /// frame affordable.</summary>
        [Fact]
        public void Pump_DoesNothingAndClearsNothingOnAnEmptyQueue()
        {
            var queue = new FakeD3D11InfoQueue();
            using var pump = new D3D11InfoQueuePump(queue, logger: new RecordingLogger());

            Assert.Equal(0, pump.Pump());
            Assert.Equal(0, queue.ClearCount);
        }

        /// <summary>A diagnostic that takes down the frame loop is worse than the problem it was added to
        /// diagnose, so a throwing source is swallowed ONCE, with the reason, and the pump then does nothing
        /// forever rather than costing a message per frame.</summary>
        [Fact]
        public void Pump_GivesUpOnceWhenTheSourceThrows()
        {
            var queue = new FakeD3D11InfoQueue { ThrowOnRead = true };
            var log = new RecordingLogger();

            using var pump = new D3D11InfoQueuePump(queue, logger: log);
            pump.Pump();
            pump.Pump();
            pump.Pump();

            Assert.True(pump.Faulted);
            Assert.Single(log.Warns);
            Assert.Contains("InvalidOperationException", log.Warns[0], StringComparison.Ordinal);
        }

        /// <summary>The pump takes the source over, so a device tearing down its pump releases the queue with it.</summary>
        [Fact]
        public void Dispose_ReleasesTheSource()
        {
            var queue = new FakeD3D11InfoQueue();
            var pump = new D3D11InfoQueuePump(queue, logger: new RecordingLogger());

            pump.Dispose();
            Assert.True(queue.IsDisposed);
        }

        // -------------------------------------------------------------------------------------------------
        // The rate limit itself.
        // -------------------------------------------------------------------------------------------------

        /// <summary>THE CAP THAT DOES THE REAL WORK: one mistake reported once per draw becomes a bounded number
        /// of lines, and the note that says so is emitted exactly once for that message however many more copies
        /// arrive, for the whole session rather than for the pump.</summary>
        [Fact]
        public void RateLimit_SuppressesOneRepeatedMessageAndSaysSoExactlyOnce()
        {
            var limit = new D3D11InfoQueueRateLimit(messagesPerPump: 100, repeatsPerMessage: 3, messagesPerSession: 100);
            var message = new D3D11InfoMessage(D3D11InfoSeverity.Error, 1, 42, "same");

            int admitted = 0;
            int notes = 0;
            for (int pump = 0; pump < 3; pump++)
            {
                limit.BeginPump();
                for (int i = 0; i < 10; i++)
                {
                    if (limit.Admit(message, out string? note)) admitted++;
                    if (note != null) notes++;
                }
            }

            Assert.Equal(3, admitted);
            Assert.Equal(1, notes);
            Assert.Equal(27, limit.Suppressed);
        }

        /// <summary>A DISTINCT message is unaffected by another message's cap, which is the whole reason the key
        /// includes the id and the text rather than being a bare counter.</summary>
        [Fact]
        public void RateLimit_CapsPerIdentityRatherThanGlobally()
        {
            var limit = new D3D11InfoQueueRateLimit(messagesPerPump: 100, repeatsPerMessage: 1, messagesPerSession: 100);
            limit.BeginPump();

            Assert.True(limit.Admit(new D3D11InfoMessage(D3D11InfoSeverity.Error, 1, 42, "a"), out _));
            Assert.False(limit.Admit(new D3D11InfoMessage(D3D11InfoSeverity.Error, 1, 42, "a"), out _));
            Assert.True(limit.Admit(new D3D11InfoMessage(D3D11InfoSeverity.Error, 1, 43, "b"), out _));
            Assert.True(limit.Admit(new D3D11InfoMessage(D3D11InfoSeverity.Warning, 1, 42, "a"), out _));
        }

        /// <summary>The per-pump cap bounds ONE frame and the next frame starts a fresh budget, which is what
        /// keeps a single bad frame from silencing the rest of the session.</summary>
        [Fact]
        public void RateLimit_ResetsThePerPumpBudgetEveryPump()
        {
            var limit = new D3D11InfoQueueRateLimit(messagesPerPump: 2, repeatsPerMessage: 100, messagesPerSession: 100);

            Assert.Equal(2, AdmitDistinct(limit, count: 5, seed: 0));
            Assert.Equal(2, AdmitDistinct(limit, count: 5, seed: 100));
        }

        /// <summary>The soak backstop: a slow trickle of DISTINCT messages passes the other two caps forever, so
        /// the session cap is the only thing that stops it, and it announces itself once.</summary>
        [Fact]
        public void RateLimit_StopsAtTheSessionCapAndAnnouncesItOnce()
        {
            var limit = new D3D11InfoQueueRateLimit(messagesPerPump: 100, repeatsPerMessage: 100, messagesPerSession: 5);

            int admitted = 0;
            int notes = 0;
            for (int pump = 0; pump < 4; pump++)
            {
                limit.BeginPump();
                for (int i = 0; i < 5; i++)
                {
                    if (limit.Admit(new D3D11InfoMessage(D3D11InfoSeverity.Info, 1, pump * 100 + i, "x"), out string? note))
                        admitted++;
                    if (note != null) notes++;
                }
            }

            Assert.Equal(5, admitted);
            Assert.Equal(1, notes);
            Assert.Equal(15, limit.Suppressed);
            Assert.Equal(5, limit.Admitted);
        }

        /// <summary>A limiter that silently dropped would be worse than none at all in a crash investigation: a
        /// reader cannot tell a quiet run from a truncated one, and "the log stops here" read as "the problem
        /// stopped" is exactly the wrong conclusion.</summary>
        [Fact]
        public void RateLimit_CountsWhatItRefused()
        {
            var limit = new D3D11InfoQueueRateLimit(messagesPerPump: 1, repeatsPerMessage: 100, messagesPerSession: 100);
            limit.BeginPump();

            AdmitDistinct(limit, count: 4, seed: 0);
            Assert.Equal(3, limit.Suppressed);
        }

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(1, 0, 1)]
        [InlineData(1, 1, 0)]
        [InlineData(-1, 1, 1)]
        public void RateLimit_RefusesANonsenseBudget(int perPump, int perMessage, int perSession)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new D3D11InfoQueueRateLimit(perPump, perMessage, perSession));
        }

        static int AdmitDistinct(D3D11InfoQueueRateLimit limit, int count, int seed)
        {
            limit.BeginPump();
            int admitted = 0;
            for (int i = 0; i < count; i++)
            {
                if (limit.Admit(new D3D11InfoMessage(D3D11InfoSeverity.Info, 1, seed + i, "m" + (seed + i)), out _))
                    admitted++;
            }
            return admitted;
        }
    }
}
