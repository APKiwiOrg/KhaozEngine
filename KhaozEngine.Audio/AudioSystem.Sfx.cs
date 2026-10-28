using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace KhaozEngine.Audio;

/// <summary>
/// The SFX registry half of <see cref="AudioSystem"/>: which sounds exist by content name, the
/// name -&gt; backend handle map behind them, and the priority-stating play overloads. Priority-free playback
/// and volume stay in the main file, the bus mixer in <c>AudioSystem.Buses.cs</c>.
/// </summary>
public sealed partial class AudioSystem
{
    /// <summary>
    /// Registers an SFX for one-shots or loops by content name (no extension). If content is already loaded, it is
    /// eager-loaded now (mirrors <see cref="RegisterTrack"/>); otherwise it loads in <see cref="LoadContent"/>.
    /// Idempotent.
    /// </summary>
    public void RegisterSfx(string name)
    {
        EnsureOwningThread();
        if (_sfxNames.Contains(name)) return;
        _sfxNames.Add(name);
        if (_loaded && _contentDirectory is not null) LoadSfx(name);
    }

    /// <summary>Registers several SFX via <see cref="RegisterSfx"/> (idempotent, pre- or post-load).</summary>
    public void RegisterSfxes(IEnumerable<string> names)
    {
        foreach (string name in names) RegisterSfx(name);
    }

    /// <summary>
    /// Releases a loaded buffer and removes <paramref name="name"/> from the registry. A loop-capable
    /// backend must confirm release through <see cref="ISfxLoopBackend.TryUnloadSfx"/>. Failure retains the
    /// registration for an explicit retry, even when some loops were already stopped. Legacy backends
    /// retain their void <see cref="ISfxBackend.Unload"/> path. Returns false for an unloaded name.
    /// A successfully removed name can be registered again later, which reloads it.
    /// </summary>
    public bool UnregisterSfx(string name)
    {
        EnsureOwningThread();
        if (!_sfx.TryGetValue(name, out int handle))
        {
            _sfxNames.Remove(name);
            return false;
        }

        var loopBackend = _sfxBackend as ISfxLoopBackend;
        if (loopBackend is not null)
        {
            try
            {
                if (!loopBackend.TryUnloadSfx(handle)) return false;
            }
            catch (Exception exception)
            {
                try { _logger.Warn($"unloading SFX '{name}' failed.", exception); }
                catch { /* Diagnostics cannot turn retained ownership into a thrown cleanup result. */ }
                return false;
            }
            finally
            {
                // Cleanup can end some identities while retaining the buffer and its remaining loops.
                ReconcileLoopMix();
            }
        }

        _sfxNames.Remove(name);
        _sfx.Remove(name);
        if (loopBackend is null) _sfxBackend.Unload(handle);
        // Forget the warn-once record too, so a later re-register that fails still says so once.
        _warnedSfx.Remove(name);
        return true;
    }

    /// <summary>Unregisters several SFX via <see cref="UnregisterSfx"/>. Returns how many were released.</summary>
    public int UnregisterSfxes(IEnumerable<string> names)
    {
        int released = 0;
        foreach (string name in names) if (UnregisterSfx(name)) released++;
        return released;
    }

    /// <summary>
    /// Plays a registered SFX as a non-positional one-shot at a stated <paramref name="priority"/>. Identical to
    /// <see cref="PlaySfx(string, float, float, string)"/> in gain, warn-once and guard behaviour: the priority
    /// only decides whose voice is taken when the backend's pool is full, where the least important voice still
    /// playing is stolen instead of whatever the rotation landed on (issue #114).
    /// <para>A separate overload rather than an optional parameter on the existing one, so a compiled consumer
    /// keeps binding to the signature it was built against. A play that states nothing is
    /// <see cref="SfxPriority.Normal"/>, exactly as before.</para>
    /// </summary>
    public void PlaySfx(string name, SfxPriority priority, float volume = 1f, float pitch = 1f, string? bus = null)
        => PlaySfxInternal(name, volume, pitch, positional: false, default, bus, priority);

    /// <summary>
    /// Plays a registered SFX as a positional one-shot at <paramref name="position"/> and a stated
    /// <paramref name="priority"/>. Same positional behaviour as
    /// <see cref="PlaySfx3D(string, Vector3, float, float, string)"/>, same voice-stealing rule as
    /// <see cref="PlaySfx(string, SfxPriority, float, float, string)"/>.
    /// </summary>
    public void PlaySfx3D(string name, Vector3 position, SfxPriority priority, float volume = 1f, float pitch = 1f, string? bus = null)
        => PlaySfxInternal(name, volume, pitch, positional: true, position, bus, priority);

    /// <summary>
    /// Plays the first loaded SFX in <paramref name="candidateKeys"/> as a non-positional one-shot at a stated
    /// <paramref name="priority"/>, returning <c>true</c> when one was played. Same first-available and
    /// warn-once semantics as <see cref="PlaySfx(IReadOnlyList{string}, float, float, string)"/>.
    /// </summary>
    public bool PlaySfx(IReadOnlyList<string> candidateKeys, SfxPriority priority, float volume = 1f, float pitch = 1f, string? bus = null)
        => PlayFirstAvailable(candidateKeys, volume, pitch, positional: false, default, bus, priority);

    /// <summary>
    /// Plays the first loaded SFX in <paramref name="candidateKeys"/> as a positional one-shot at
    /// <paramref name="position"/> and a stated <paramref name="priority"/>, returning <c>true</c> when one was
    /// played. Same first-available and warn-once semantics as
    /// <see cref="PlaySfx3D(IReadOnlyList{string}, Vector3, float, float, string)"/>.
    /// </summary>
    public bool PlaySfx3D(IReadOnlyList<string> candidateKeys, Vector3 position, SfxPriority priority, float volume = 1f, float pitch = 1f, string? bus = null)
        => PlayFirstAvailable(candidateKeys, volume, pitch, positional: true, position, bus, priority);

    // Looks for name + .wav/.ogg/.mp3 in the content dir and maps name -> backend handle. Skips + warns on
    // a missing file or a backend load failure (-1).
    private void LoadSfx(string name)
    {
        if (_contentDirectory is null || _sfx.ContainsKey(name)) return;

        foreach (string ext in SfxExtensions)
        {
            string path = Path.Combine(_contentDirectory, name + ext);
            if (!File.Exists(path)) continue;

            int handle = _sfxBackend.Load(path);
            if (handle >= 0) { _sfx[name] = handle; }
            else _logger.Warn($"SFX '{name}' failed to load from '{path}'.");
            return;
        }
        _logger.Warn($"SFX: no WAV/OGG/MP3 file found for '{name}' in '{_contentDirectory}'.");
    }
}
