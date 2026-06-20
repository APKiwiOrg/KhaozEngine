namespace KhaozEngine.Updates;

/// <summary>The action the overlay's trigger should perform for the current state.</summary>
public enum OverlayAction { None, Download, Apply, Retry }

/// <summary>
/// Default wiring from the Gui overlay's trigger to the <see cref="UpdateService"/>. Lets a game wire the
/// overlay in one line: <c>overlay.OnTrigger += _ =&gt; UpdateOverlayActions.Trigger(service);</c>. The
/// state-action policy is the pure <see cref="ResolveAction"/> (unit-tested); <see cref="Trigger"/> applies it.
/// </summary>
public static class UpdateOverlayActions
{
    /// <summary>Maps a state to the action its trigger should perform (None for non-actionable states).</summary>
    public static OverlayAction ResolveAction(UpdateState state) => state switch
    {
        UpdateState.UpdateAvailable => OverlayAction.Download,
        UpdateState.ReadyToApply => OverlayAction.Apply,
        UpdateState.Failed => OverlayAction.Retry,
        _ => OverlayAction.None,
    };

    /// <summary>Performs the resolved action against <paramref name="service"/> for its current state.</summary>
    public static void Trigger(UpdateService service)
    {
        switch (ResolveAction(service.State))
        {
            case OverlayAction.Download: _ = service.StartDownloadAsync(); break;
            case OverlayAction.Apply: service.ApplyUpdate(); break;
            case OverlayAction.Retry: _ = service.CheckForUpdateAsync(); break;
        }
    }
}
