using System;
using System.Collections.Generic;

namespace KhaozEngine.Audio;

/// <summary>
/// The SFX bus mixer half of <see cref="AudioSystem"/>: the per-bus volume multipliers a game groups sounds
/// under (UI, ambience, combat, ...) and the lookup a play runs to compose its gain. Track rotation, the
/// master / music / SFX volumes and playback itself stay in the main file.
/// </summary>
public sealed partial class AudioSystem
{
    // Per-bus SFX volume multipliers. The default bus is implicit (id "" / not in the map) and always sits at
    // 1.0, so a Play with no bus (or an unknown bus) composes exactly master*sfx*volume as before. A defined
    // bus scales that by its current volume. Unknown-bus plays fall back to the default bus with a warn-once.
    private readonly Dictionary<string, float> _busVolumes = new();
    private readonly HashSet<string> _warnedUnknownBus = new();  // warn-once per unknown bus id seen on Play

    /// <summary>
    /// Registers an SFX bus so a game can group sounds (UI, ambience, combat, ...) under one volume without
    /// tracking individual voices. <paramref name="id"/> is an opaque identifier, not player-facing text (same
    /// localization boundary as everywhere else: bus ids are identifiers). A newly defined bus starts at volume
    /// <c>1.0</c> (audibly identical to the default bus until <see cref="SetBusVolume"/> lowers it). Re-defining
    /// an existing bus is a no-op that preserves its current volume. A <c>null</c> or empty id is ignored (that
    /// space is the implicit default bus, which is always 1.0 and cannot be redefined). Bus volumes are game
    /// settings: the game persists them like <see cref="MasterVolume"/> / <see cref="SfxVolume"/> (no built-in
    /// serialization).
    /// </summary>
    /// <exception cref="InvalidOperationException">The caller is not on the thread that constructed this
    /// <see cref="AudioSystem"/> (see the class remarks: main-thread-only).</exception>
    public void DefineBus(string id)
    {
        EnsureOwningThread();
        if (string.IsNullOrEmpty(id)) return;
        if (!_busVolumes.ContainsKey(id)) _busVolumes[id] = 1f;
    }

    /// <summary>
    /// Sets the volume multiplier (0.0 - 1.0, clamped) for the bus <paramref name="id"/>, defining it if it was
    /// not already defined. Applies to sounds played on that bus AFTER this call. Sounds already playing on the
    /// bus keep the gain they were started with: the SFX backend seam is fire-and-forget (<c>ISfxBackend.Play</c>
    /// returns no voice handle and exposes no per-voice gain setter), so a live per-voice re-gain is not possible
    /// without a breaking seam change. SFX one-shots are short, so this is a mild limitation; see the Audio docs.
    /// A <c>null</c> or empty id (the implicit default bus) is ignored: the default bus is always 1.0.
    /// </summary>
    /// <exception cref="InvalidOperationException">The caller is not on the thread that constructed this
    /// <see cref="AudioSystem"/> (see the class remarks: main-thread-only).</exception>
    public void SetBusVolume(string id, float volume)
    {
        EnsureOwningThread();
        if (string.IsNullOrEmpty(id)) return;
        _busVolumes[id] = Math.Clamp(volume, 0f, 1f);
    }

    /// <summary>
    /// Returns the current volume multiplier for the bus <paramref name="id"/>, or <c>1.0</c> for an unknown /
    /// default bus (a <c>null</c> or empty id, or an id never defined). Never throws.
    /// </summary>
    public float GetBusVolume(string id)
        => !string.IsNullOrEmpty(id) && _busVolumes.TryGetValue(id, out float v) ? v : 1f;

    // The bus multiplier for a play. Null/empty = the implicit default bus (1.0). A defined bus returns its
    // current volume. An unknown (never-defined) bus falls back to the default bus at 1.0 with a warn-once note,
    // never a throw, so a typo or missing DefineBus degrades to audible-at-full rather than silence or a crash.
    private float ResolveBusVolume(string? bus)
    {
        if (string.IsNullOrEmpty(bus)) return 1f;
        if (_busVolumes.TryGetValue(bus, out float v)) return v;
        if (_warnedUnknownBus.Add(bus)) _logger.Debug($"PlaySfx unknown bus '{bus}'; using default bus (1.0). Call DefineBus first.");
        return 1f;
    }
}
