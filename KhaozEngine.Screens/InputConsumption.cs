namespace KhaozEngine.Screens;

/// <summary>
/// Declares a screen's intended input-consumption behaviour. This is intent you implement via the
/// bool returned from <see cref="GameScreen.Update"/> — the manager acts on that return value.
/// </summary>
public enum InputConsumption
{
    /// <summary>
    /// The top visible interactive screen occupies input whether or not it acted on it
    /// (Hardpoint/Nullwake style). Implement by returning <c>receivesInput</c>.
    /// </summary>
    ConsumeWhenVisible,

    /// <summary>
    /// The screen blocks lower screens only when it actually handled input this frame
    /// (SpaceGame style). Implement by returning the real handled result.
    /// </summary>
    ConsumeWhenHandled
}
