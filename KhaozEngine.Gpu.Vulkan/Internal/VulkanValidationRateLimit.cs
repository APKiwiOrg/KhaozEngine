using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE RATE LIMIT DECISION V-G3 ASKS FOR, as pure bookkeeping with no logger and no Vulkan handle in it. The
    /// validation layer is a per-draw firehose, so a messenger that logged everything would turn a diagnostic into
    /// a session log nobody can read and a frame time nobody can measure.
    /// <para>
    /// TWO CAPS RATHER THAN THE DIRECT3D 11 PUMP'S THREE, and the missing one is missing for a structural reason
    /// rather than by simplification. That pump DRAINS a queue at the frame boundary, so it has a per-pump cap
    /// bounding one frame's burst. A <c>VK_EXT_debug_utils</c> messenger is PUSHED: the driver calls into the
    /// callback the instant it has something to say, from whatever thread made the call, and there is no boundary
    /// to reset a per-frame budget at. Inventing one would mean the frame loop telling the limiter where a frame
    /// ended, which is a coupling this row has no frame loop to build and which the two remaining caps do not
    /// need.
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Per message identity.</b> The same severity, id and text, repeated. This is the cap
    ///   that does the real work: validation's characteristic failure is ONE mistake reported once per draw call,
    ///   so without it a session is thousands of copies of one line and the second distinct message is
    ///   invisible.</description></item>
    ///   <item><description><b>Per session.</b> The backstop for a long soak, where a slow trickle of DISTINCT
    ///   messages would otherwise pass the cap above forever.</description></item>
    /// </list>
    /// <para>
    /// A CAP THAT SUPPRESSES SAYS SO EXACTLY ONCE, through the note <see cref="Admit"/> hands back. A limiter that
    /// silently drops is worse than no limiter at all in an investigation, because the reader cannot tell a quiet
    /// run from a truncated one, and "the log stops at message 512" read as "the problem stopped" is precisely
    /// the wrong conclusion.
    /// </para>
    /// <para>
    /// THREAD-SAFE, unlike the Direct3D 11 one, and for the same structural reason the per-pump cap is absent: the
    /// callback arrives on whatever thread made the offending call, so two threads can be inside
    /// <see cref="Admit"/> at once. The lock is uncontended on the ordinary path and this whole type only exists
    /// on a session that asked for validation.
    /// </para>
    /// </summary>
    internal sealed class VulkanValidationRateLimit
    {
        /// <summary>How many times one distinct message may be logged before it is suppressed for the rest of the
        /// session. Enough that a repeated message reads as repeated rather than as a one-off.</summary>
        internal const int DefaultRepeatsPerMessage = 8;

        /// <summary>How many messages the whole session may log. The soak backstop.</summary>
        internal const int DefaultMessagesPerSession = 512;

        readonly object _gate = new();
        readonly int _perMessage;
        readonly int _perSession;

        // Bounded by construction: a key is only ever added on an admitted message, and admissions are capped by
        // _perSession, so this cannot grow past that however long the session runs or however chatty the layer is.
        readonly Dictionary<(VulkanValidationSeverity Severity, int Id, string Text), int> _seen = new();

        int _thisSession;
        int _suppressed;
        bool _sessionCapAnnounced;

        internal VulkanValidationRateLimit(
            int repeatsPerMessage = DefaultRepeatsPerMessage,
            int messagesPerSession = DefaultMessagesPerSession)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeatsPerMessage);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(messagesPerSession);

            _perMessage = repeatsPerMessage;
            _perSession = messagesPerSession;
        }

        /// <summary>How many messages this limiter has admitted since it was created.</summary>
        internal int Admitted { get { lock (_gate) return _thisSession; } }

        /// <summary>How many messages this limiter has refused since it was created. Reported so a session can say
        /// how much it did not show, which is the number that stops a truncated log reading as a quiet one.</summary>
        internal int Suppressed { get { lock (_gate) return _suppressed; } }

        /// <summary>
        /// Whether <paramref name="message"/> should be logged. <paramref name="note"/> is non-null exactly once
        /// per cap, and is the line explaining why logging stopped. A note is produced even when the answer is
        /// false, so the caller logs the note and skips the message.
        /// <para>
        /// SESSION FIRST, because once it is hit nothing else can matter and saying so once is the whole
        /// remaining budget. Per-message second, because it names the message it is about, which is what a reader
        /// needs told.
        /// </para>
        /// </summary>
        internal bool Admit(in VulkanValidationMessage message, out string? note)
        {
            note = null;

            lock (_gate)
            {
                if (_thisSession >= _perSession)
                {
                    _suppressed++;
                    if (!_sessionCapAnnounced)
                    {
                        _sessionCapAnnounced = true;
                        note = $"Vulkan validation has produced {_perSession} logged messages this session, which "
                            + "is the cap. Nothing further from it will be logged. Restart with a narrower repro "
                            + "if you need more.";
                    }
                    return false;
                }

                var key = (message.Severity, message.Id, message.Text);
                _seen.TryGetValue(key, out int seen);
                if (seen >= _perMessage)
                {
                    _suppressed++;
                    if (seen == _perMessage)
                    {
                        // Recorded as one past the cap so this branch runs exactly once for this key, for the
                        // whole session, however many more copies arrive.
                        _seen[key] = seen + 1;
                        note = $"Vulkan validation has repeated message '{message.IdName}' {_perMessage} times. "
                            + "Further copies of it are suppressed for the rest of this session. Other messages "
                            + "are unaffected.";
                    }
                    return false;
                }

                _seen[key] = seen + 1;
                _thisSession++;
                return true;
            }
        }
    }
}
