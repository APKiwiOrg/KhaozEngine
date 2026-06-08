namespace KhaozEngine.Input;

/// <summary>
/// The seam that makes input testable. Production reads hardware
/// (<see cref="MonoGameRawInput"/>); tests inject synthetic <see cref="RawInputState"/> snapshots.
/// </summary>
public interface IRawInput
{
    /// <summary>Snapshots the current raw hardware state for one frame.</summary>
    RawInputState Read();
}
