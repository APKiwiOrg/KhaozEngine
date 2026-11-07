namespace KhaozEngine.Audio;

/// <summary>Rules that refuse a call into an <see cref="AudioSystem"/> because of the thread making it.</summary>
public enum AudioThreadAffinityRefusal
{
    /// <summary>Refuses a call from a thread other than the one that constructed the <see cref="AudioSystem"/>.</summary>
    ThreadMismatch,
}
