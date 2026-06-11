namespace KhaozEngine.Audio;

/// <summary>How <see cref="AudioSystem"/> chooses the next track when the current one ends.</summary>
public enum PlayMode
{
    /// <summary>Pick a random track, never the same one twice in a row (the default).</summary>
    RandomRotation,

    /// <summary>Replay the current track when it ends (set a specific track via <see cref="AudioSystem.PlayTrack(string)"/>).</summary>
    RepeatOne,
}
