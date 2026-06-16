using System;
using System.Numerics;

namespace KhaozEngine.Audio;

/// <summary>
/// A platform SFX backend: loads short one-shot sounds (whole-file, not streamed) and plays them on a small
/// pool of voices, optionally positioned in 3D relative to a listener. Mirrors <see cref="IMusicBackend"/>.
/// Implemented by the bundled OpenAL backend; games or tests may supply their own. No per-frame Update is
/// needed: OpenAL one-shots are fire-and-forget and voices are reclaimed by querying source state on the
/// next <see cref="Play"/>.
/// </summary>
public interface ISfxBackend : IDisposable
{
    /// <summary>Human-readable backend name (used in logs).</summary>
    string Name { get; }

    /// <summary>Fully decode a file into one buffer; returns a handle (&gt;=0), or -1 on failure.</summary>
    int Load(string path);

    /// <summary>
    /// Play a one-shot on a pooled voice. <paramref name="positional"/> = false attaches the sound to the
    /// listener (heard at full <paramref name="gain"/> regardless of <paramref name="position"/>); true places
    /// it at <paramref name="position"/> in world space and attenuates relative to the listener.
    /// </summary>
    void Play(int handle, float gain, float pitch, bool positional, Vector3 position);

    /// <summary>Set the 3D listener pose (positional sounds attenuate / pan relative to this).</summary>
    void SetListener(Vector3 position, Vector3 forward, Vector3 up);

    /// <summary>Stop all currently-playing voices.</summary>
    void StopAll();
}
