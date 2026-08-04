using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION G4's PUMP: drain the Direct3D debug layer's message queue into the engine logger, rate limited,
    /// with corruption and error severities raised to WARN. Created and driven by the device when
    /// <c>KE_D3D11_DEBUG</c> is on, once per frame at the submit boundary, and never created at all otherwise.
    /// <para>
    /// RAISED TO WARN AND NOT TO ERROR, which is a deliberate ceiling rather than an oversight. The engine's ERROR
    /// level is for something the engine could not do, and a debug-layer corruption message is a diagnostic about
    /// something that already happened, frequently in a driver, frequently benign in the sense that the frame
    /// still rendered. Logging it at ERROR would make a debug session look like a broken engine and would put a
    /// row in any consumer's error-rate telemetry for every diagnostic run. WARN puts it above the INFO chatter,
    /// which is the whole thing the promotion is for: the message that matters must not be lost among the ones
    /// that do not.
    /// </para>
    /// <para>
    /// THE QUEUE IS CLEARED AT THE END OF EVERY PUMP, INCLUDING ONE THE RATE LIMIT SUPPRESSED ENTIRELY. Not
    /// clearing would let the queue grow without bound on exactly the session the limiter exists for, and the
    /// stored messages would then be re-read and re-suppressed every frame, so the limiter's cost would grow with
    /// the run. Clearing is also what makes an index-based read safe: each pump reads a snapshot count and then
    /// empties it.
    /// </para>
    /// <para>
    /// A THROWING SOURCE IS SWALLOWED, once, with the reason. Everything below this is interop against a debug
    /// layer that is by definition present only on a developer's machine, and a diagnostic that takes down the
    /// frame loop is worse than the problem it was added to diagnose. That is the same rule
    /// <see cref="D3D11FeatureProbe"/> and the driver-threading probe already follow.
    /// </para>
    /// </summary>
    internal sealed class D3D11InfoQueuePump : IDisposable
    {
        static readonly ILogger log = Log.For<D3D11InfoQueuePump>();

        readonly ID3D11InfoQueueSource _source;
        readonly D3D11InfoQueueRateLimit _limit;
        readonly ILogger _log;

        bool _faulted;
        bool _disposed;

        /// <summary>
        /// Wrap <paramref name="source"/>, which is taken over: <see cref="Dispose"/> disposes it.
        /// </summary>
        /// <param name="source">The message queue. <see cref="D3D11InfoQueueMessages"/> on Windows, a recording
        /// fake in the tests.</param>
        /// <param name="limit">The rate limit, or null for the defaults.</param>
        /// <param name="logger">The sink, or null for this type's own category logger. Present so a test can
        /// assert what was logged and at which level, which is the half of this type worth asserting.</param>
        internal D3D11InfoQueuePump(ID3D11InfoQueueSource source, D3D11InfoQueueRateLimit? limit = null,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            _source = source;
            _limit = limit ?? new D3D11InfoQueueRateLimit();
            _log = logger ?? log;
        }

        /// <summary>How many messages this pump has refused, cumulative. The number that stops a truncated log
        /// reading as a quiet one.</summary>
        internal int Suppressed => _limit.Suppressed;

        /// <summary>True once the source threw and the pump gave up. A pump in this state does nothing on every
        /// later call, so one broken queue does not cost a message per frame forever.</summary>
        internal bool Faulted => _faulted;

        /// <summary>
        /// Drain the queue once and return how many messages were LOGGED, which is not how many were read: the
        /// rate limit's refusals are in <see cref="Suppressed"/>.
        /// </summary>
        internal int Pump()
        {
            if (_disposed || _faulted) return 0;

            try
            {
                return PumpCore();
            }
            catch (Exception ex)
            {
                _faulted = true;
                _log.Warn("The Direct3D 11 debug-layer message pump failed and is now off for this session. "
                    + $"Rendering is unaffected. It threw {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        int PumpCore()
        {
            // The count is snapshotted BEFORE the walk, so a message the driver raises mid-walk is next frame's
            // rather than an index this read believes it has and the clear below then removes.
            ulong stored = _source.StoredMessageCount;
            if (stored == 0) return 0;

            _limit.BeginPump();

            int logged = 0;
            for (ulong i = 0; i < stored; i++)
            {
                D3D11InfoMessage message = _source.Read(i);
                if (_limit.Admit(message, out string? note))
                {
                    Write(message);
                    logged++;
                }
                else if (note != null)
                {
                    _log.Warn(note);
                    logged++;
                }
            }

            _source.ClearStoredMessages();
            return logged;
        }

        void Write(in D3D11InfoMessage message)
        {
            string line = Describe(message);
            if (PromotesToWarning(message.Severity)) _log.Warn(line);
            else _log.Info(line);
        }

        /// <summary>Whether <paramref name="severity"/> is logged at WARN. Corruption and error are RAISED to it
        /// per decision G4, and the layer's own warning severity is already there, so the three of them read alike
        /// in a log and the informational two do not.</summary>
        internal static bool PromotesToWarning(D3D11InfoSeverity severity)
            => severity is D3D11InfoSeverity.Corruption
                or D3D11InfoSeverity.Error
                or D3D11InfoSeverity.Warning;

        /// <summary>One log line. The severity and the message id are both in it because the id is the stable
        /// identity a reader searches on and the text is not: the runtime rewords messages between Windows
        /// versions.</summary>
        internal static string Describe(in D3D11InfoMessage message)
            => $"D3D11 debug layer [{message.Severity}] id {message.Id} (category {message.Category}): "
                + message.Text;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}
