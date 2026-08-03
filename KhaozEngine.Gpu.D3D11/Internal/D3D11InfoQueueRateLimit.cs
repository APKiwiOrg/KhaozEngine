using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE RATE LIMIT DECISION G4 ASKS FOR, as pure bookkeeping with no logger and no device in it. The debug
    /// layer is a per-draw firehose, so a pump that logged everything would turn a diagnostic into a session log
    /// nobody can read and a frame time nobody can measure. Three independent caps, because the three ways the
    /// volume arrives are genuinely different problems:
    /// <list type="bullet">
    ///   <item><description><b>Per pump.</b> One frame's worth. Bounds the cost of a single burst, so a bad frame
    ///   costs a bounded number of log lines rather than however many the driver felt like raising.</description></item>
    ///   <item><description><b>Per message identity.</b> The same message id with the same text, repeated. This is
    ///   the cap that does the real work: the debug layer's characteristic failure is ONE mistake reported once
    ///   per draw call, so without it a session is thousands of copies of one line and the second distinct
    ///   message is invisible.</description></item>
    ///   <item><description><b>Per session.</b> The backstop for a long soak, where a slow trickle of DISTINCT
    ///   messages would otherwise pass both caps above forever.</description></item>
    /// </list>
    /// <para>
    /// A CAP THAT SUPPRESSES SAYS SO EXACTLY ONCE, through the note <see cref="Admit"/> hands back. A limiter that
    /// silently drops is worse than no limiter at all in a crash investigation, because the reader cannot tell a
    /// quiet run from a truncated one, and "the log stops at message 512" read as "the problem stopped" is
    /// precisely the wrong conclusion.
    /// </para>
    /// <para>
    /// NOT THREAD-SAFE, and it does not need to be: the pump runs on the frame thread, at the frame boundary,
    /// which is the same contract the fence subsystem's counters already have.
    /// </para>
    /// </summary>
    internal sealed class D3D11InfoQueueRateLimit
    {
        /// <summary>How many messages one pump may log. A frame's worth, not a session's.</summary>
        internal const int DefaultMessagesPerPump = 32;

        /// <summary>How many times one distinct message may be logged before it is suppressed for the rest of the
        /// session. Enough that a repeated message reads as repeated rather than as a one-off.</summary>
        internal const int DefaultRepeatsPerMessage = 8;

        /// <summary>How many messages the whole session may log. The soak backstop.</summary>
        internal const int DefaultMessagesPerSession = 512;

        readonly int _perPump;
        readonly int _perMessage;
        readonly int _perSession;

        // Bounded by construction: a key is only ever added on an admitted message, and admissions are capped by
        // _perSession, so this cannot grow past that however long the session runs or however chatty the layer is.
        readonly Dictionary<(D3D11InfoSeverity Severity, int Id, string Text), int> _seen = new();

        int _thisPump;
        int _thisSession;
        bool _sessionCapAnnounced;
        bool _pumpCapAnnounced;

        internal D3D11InfoQueueRateLimit(
            int messagesPerPump = DefaultMessagesPerPump,
            int repeatsPerMessage = DefaultRepeatsPerMessage,
            int messagesPerSession = DefaultMessagesPerSession)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(messagesPerPump);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeatsPerMessage);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(messagesPerSession);

            _perPump = messagesPerPump;
            _perMessage = repeatsPerMessage;
            _perSession = messagesPerSession;
        }

        /// <summary>How many messages this limiter has admitted since it was created.</summary>
        internal int Admitted => _thisSession;

        /// <summary>How many messages this limiter has refused since it was created. Reported so a session can
        /// say how much it did not show, which is the number that stops a truncated log reading as a quiet
        /// one.</summary>
        internal int Suppressed { get; private set; }

        /// <summary>Start a pump. Resets the per-pump budget and nothing else, since the other two caps are
        /// deliberately cumulative.</summary>
        internal void BeginPump()
        {
            _thisPump = 0;
            _pumpCapAnnounced = false;
        }

        /// <summary>
        /// Whether <paramref name="message"/> should be logged. <paramref name="note"/> is non-null exactly once
        /// per cap per pump (per session for the two cumulative caps), and is the line explaining why logging
        /// stopped. A note is produced even when the answer is false, so the caller logs the note and skips the
        /// message.
        /// <para>
        /// THE ORDER OF THE THREE CHECKS IS THE MOST INFORMATIVE ONE. Session first, because once it is hit
        /// nothing else can matter and saying so once is the whole remaining budget. Per-message second, because
        /// that is the cap a reader most needs explained and it names the message it is about. Per-pump last,
        /// since it is the least interesting of the three and only bounds one frame.
        /// </para>
        /// </summary>
        internal bool Admit(in D3D11InfoMessage message, out string? note)
        {
            note = null;

            if (_thisSession >= _perSession)
            {
                Suppressed++;
                if (!_sessionCapAnnounced)
                {
                    _sessionCapAnnounced = true;
                    note = $"The Direct3D 11 debug layer has produced {_perSession} logged messages this session, "
                        + "which is the cap. Nothing further from it will be logged. Restart with a narrower "
                        + "repro if you need more.";
                }
                return false;
            }

            var key = (message.Severity, message.Id, message.Text);
            _seen.TryGetValue(key, out int seen);
            if (seen >= _perMessage)
            {
                Suppressed++;
                if (seen == _perMessage)
                {
                    // Recorded as one past the cap so this branch runs exactly once for this key, for the whole
                    // session, however many more copies arrive.
                    _seen[key] = seen + 1;
                    note = $"The Direct3D 11 debug layer has repeated message {message.Id} {_perMessage} times. "
                        + "Further copies of it are suppressed for the rest of this session. Other messages are "
                        + "unaffected.";
                }
                return false;
            }

            if (_thisPump >= _perPump)
            {
                Suppressed++;
                if (!_pumpCapAnnounced)
                {
                    _pumpCapAnnounced = true;
                    note = $"The Direct3D 11 debug layer produced more than {_perPump} messages in one frame. The "
                        + "rest of this frame's are suppressed. The next frame starts a fresh budget.";
                }
                return false;
            }

            _seen[key] = seen + 1;
            _thisPump++;
            _thisSession++;
            return true;
        }
    }
}
