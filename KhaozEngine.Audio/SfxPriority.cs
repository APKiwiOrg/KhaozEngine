namespace KhaozEngine.Audio;

/// <summary>
/// How much a one-shot matters when the voice pool is full. The SFX pool is small and fixed (16 voices in the
/// OpenAL backend), so once every voice is busy a new sound can only be heard by stealing one. Without a
/// priority the steal is pure rotation, which can cut a boss cue or a dialogue line mid-play purely because it
/// happened to be that voice's turn (issue #114). With one, the backend steals the LEAST important voice
/// instead.
/// <para>Deliberately coarse: three tiers are enough to protect the cue a player would notice losing, and a
/// finer scale is a tuning surface nobody can hold in their head while writing gameplay code. The default
/// everywhere is <see cref="Normal"/>, so a game that never passes a priority behaves exactly as before.</para>
/// </summary>
public enum SfxPriority
{
    /// <summary>Background texture: footsteps, impact taps, UI blips, ambient one-shots. The first thing to lose
    /// a voice, and the tier a barrage belongs in so it cannot drown out the rest of the mix.</summary>
    Low = 0,

    /// <summary>The default for every play that does not say otherwise, and the behaviour of every call written
    /// before priorities existed.</summary>
    Normal = 1,

    /// <summary>The cue whose loss the player notices: a boss telegraph, a dialogue line, a critical alert. Only
    /// stolen when nothing less important is playing.</summary>
    High = 2,
}
