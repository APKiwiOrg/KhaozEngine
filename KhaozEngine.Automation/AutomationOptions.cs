namespace KhaozEngine.Automation
{
    /// <summary>
    /// How an <see cref="AutomationHost"/> is configured. Two fields, because the endpoint deliberately has no
    /// knobs a shipping build could turn on by accident.
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
    }
}
