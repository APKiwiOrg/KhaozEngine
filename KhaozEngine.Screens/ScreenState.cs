namespace KhaozEngine.Screens;

/// <summary>Lifecycle state of a screen within the <see cref="ScreenManager"/>.</summary>
public enum ScreenState
{
    /// <summary>Animating in; alpha ramping 0 to 1.</summary>
    TransitionOn,
    /// <summary>Fully visible and interactive.</summary>
    Active,
    /// <summary>Animating out; removed when complete.</summary>
    TransitionOff,
    /// <summary>Not updated, drawn, or routed input.</summary>
    Hidden
}
