using System;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// How an <see cref="AutomationHost"/> is configured. Two required fields, because the endpoint deliberately has
    /// no knobs a shipping build could turn on by accident, plus optional ones for diagnostics and deadlines.
    /// </summary>
    /// <param name="Enabled">
    /// The head's explicit opt-in, gate 2's first half. False means the host is inert whatever the environment
    /// says. A head that wires automation at all should leave this behind its own build condition, so the flag
    /// reads as "this run wants automation" rather than "this binary can do automation".
    /// </param>
    /// <param name="HandshakeDirectory">
    /// The directory the handshake file (<see cref="AutomationHost.HandshakeFileName"/>) is written into. Created
    /// if missing. Pass the app data directory the game already owns, not a shared temp directory, so the token is
    /// no easier to read than the rest of the developer's app state.
    /// </param>
    public sealed record AutomationOptions(bool Enabled, string HandshakeDirectory)
    {
        /// <summary>The inert configuration: never starts, whatever the environment says. Handy as a default.</summary>
        public static AutomationOptions Off { get; } = new(false, "");

        /// <summary>
        /// Where the endpoint reports a loop or a connection ending for a reason other than shutdown: a message
        /// naming which, and the exception when there was one. Null (the default) is silent, which is what a headless
        /// test wants. A head points it at its own log.
        /// <para>
        /// Two arguments rather than one <c>Action&lt;Exception&gt;</c>, because half of what is worth reporting is
        /// not an exception at all: an over-long request line and an expired read deadline are both deliberate
        /// closes, and a hook that only takes an exception forces one to be invented for them. The engine calls it
        /// from a socket thread, so it must not throw and must not block. A throw is swallowed rather than allowed
        /// to take the game down, but a hook that blocks stalls that connection.
        /// </para>
        /// </summary>
        public Action<string, Exception?>? Log { get; init; }

        /// <summary>
        /// How long a fresh connection has to deliver its first complete request line before it is closed. The first
        /// line is the one that must carry the token, so this deadline is what a connection that authenticates
        /// nothing pays. Kept short (5 seconds) on purpose. Zero or less means no deadline.
        /// </summary>
        public TimeSpan FirstLineTimeout { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long an authenticated connection may sit idle between request lines before it is closed. Generous (60
        /// seconds), because an agent thinking between commands is the normal case and a dropped connection costs it
        /// a reconnect. Zero or less means no deadline.
        /// </summary>
        public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a queued command may wait for the window thread to apply or finish it. The default is 5 seconds.
        /// The value must be positive and no larger than the runtime timer limit (about 49.7 days). An expired
        /// queued command receives an error and is retired, so it cannot run when a stalled frame loop resumes. A
        /// synchronous callback already executing at the deadline is allowed to finish and returns its real result.
        /// </summary>
        public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);
    }
}
