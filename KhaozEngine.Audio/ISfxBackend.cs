using System;
using System.Numerics;

namespace KhaozEngine.Audio;

/// <summary>
/// A platform SFX backend: loads short one-shot sounds (whole-file, not streamed) and plays them on a small
/// pool of voices, optionally positioned in 3D relative to a listener. Mirrors <see cref="IMusicBackend"/>.
/// Implemented by the bundled OpenAL backend; games or tests may supply their own. No per-frame Update is
/// needed: OpenAL one-shots are fire-and-forget and voices are reclaimed by querying source state on the
/// next <see cref="Play(int, float, float, bool, Vector3)"/>.
/// </summary>
/// <remarks>
/// <b>Threading.</b> An implementation is not required to be thread-safe and none of the bundled ones is.
/// <see cref="AudioSystem"/> drives this seam from its owning (main) thread only and enforces that on its own
/// side, so every call an implementation sees arrives on one thread.
/// </remarks>
public interface ISfxBackend : IDisposable
{
    /// <summary>Human-readable backend name (used in logs).</summary>
    string Name { get; }

    /// <summary>Fully decode a file into one buffer; returns a handle (&gt;=0), or -1 on failure.</summary>
    int Load(string path);

    /// <summary>
    /// Release the buffer behind <paramref name="handle"/> (a value <see cref="Load"/> returned), stopping any
    /// voice still playing it. The handle is dead afterwards: a <c>Play</c> on it is a no-op, and the slot
    /// is free for a later <see cref="Load"/> to take. Out-of-range and already-released handles are ignored.
    /// <para>For a zone-scoped or level-scoped SFX set, which would otherwise accumulate buffers for the whole
    /// session. A backend holding nothing releasable needs no implementation: the default is a no-op, so an
    /// existing backend keeps compiling untouched.</para>
    /// </summary>
    void Unload(int handle) { }

    /// <summary>
    /// Play a one-shot on a pooled voice. <paramref name="positional"/> = false attaches the sound to the
    /// listener (heard at full <paramref name="gain"/> regardless of <paramref name="position"/>); true places
    /// it at <paramref name="position"/> in world space and attenuates relative to the listener.
    /// </summary>
    void Play(int handle, float gain, float pitch, bool positional, Vector3 position);

    /// <summary>
    /// Play a one-shot at a stated <paramref name="priority"/>, which decides whose voice is taken when the pool
    /// is full: the backend steals the LEAST important voice still playing instead of whatever the rotation
    /// landed on, so a barrage of <see cref="SfxPriority.Low"/> one-shots cannot cut a
    /// <see cref="SfxPriority.High"/> cue mid-play (issue #114). Everything else matches
    /// <see cref="Play(int, float, float, bool, Vector3)"/>.
    /// <para>A default member, like <see cref="Unload"/>: a backend with no voice-stealing policy of its own
    /// (the null backend, a test fake, a game's own) inherits this forward to the priority-free overload and
    /// keeps compiling untouched. A backend that pools voices should override it.</para>
    /// </summary>
    void Play(int handle, float gain, float pitch, bool positional, Vector3 position, SfxPriority priority)
        => Play(handle, gain, pitch, positional, position);

    /// <summary>
    /// Play a positional one-shot with an explicit inverse-distance <paramref name="attenuation"/> curve.
    /// AudioSystem calls this only for positional sounds. The default forwards to the priority overload, so an
    /// existing backend keeps its prior behavior until it chooses to consume the curve.
    /// </summary>
    void Play(
        int handle,
        float gain,
        float pitch,
        bool positional,
        Vector3 position,
        SfxPriority priority,
        SfxAttenuation attenuation)
        => Play(handle, gain, pitch, positional, position, priority);

    /// <summary>Set the 3D listener pose (positional sounds attenuate / pan relative to this).</summary>
    void SetListener(Vector3 position, Vector3 forward, Vector3 up);

    /// <summary>Stop all currently-playing voices.</summary>
    void StopAll();
}
