using System;

namespace KhaozEngine.Persistence;

/// <summary>
/// The save posture a <see cref="GameStorage"/> is built with, a required constructor argument so the choice
/// is visible at every construction site. Fleet policy: game saves (progress, unlocks, campaign state) are
/// tamper-encoded, so a game passes <see cref="Encoded"/> with its own <see cref="SaveEncoder"/>.
/// <see cref="Plaintext"/> is the deliberate, greppable opt-out, for a storage that holds no game saves (an
/// editor's preferences, a tool) or a game that has chosen to ship hand-editable saves.
/// <para>The posture governs <see cref="GameStorage.Save{T}(string, T)"/> and <see cref="GameStorage.Load{T}"/>
/// only. Settings (<see cref="GameStorage.Settings"/> and <see cref="GameStorage.CreateSettingsManager{T}"/>)
/// stay plaintext under either posture on purpose: they are the player's to hand-edit.</para>
/// </summary>
public sealed class SaveEncoding
{
    private SaveEncoding(SaveEncoder? encoder)
    {
        Encoder = encoder;
    }

    /// <summary>
    /// Saves are written and read as plain JSON. A per-call
    /// <see cref="SaveWriteOptions.Encode"/> of <c>true</c> throws, since there is no encoder to use.
    /// </summary>
    public static SaveEncoding Plaintext { get; } = new(null);

    /// <summary>
    /// Saves are encoded with <paramref name="encoder"/> by default and transparently decoded on load. A per-call
    /// <see cref="SaveWriteOptions.Encode"/> of <c>false</c> still writes one file plaintext.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="encoder"/> is null.</exception>
    public static SaveEncoding Encoded(SaveEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return new SaveEncoding(encoder);
    }

    /// <summary>The encoder for <see cref="Encoded"/>, or null for <see cref="Plaintext"/>.</summary>
    public SaveEncoder? Encoder { get; }

    /// <summary>True for <see cref="Encoded"/>, false for <see cref="Plaintext"/>.</summary>
    public bool IsEncoded => Encoder is not null;
}
