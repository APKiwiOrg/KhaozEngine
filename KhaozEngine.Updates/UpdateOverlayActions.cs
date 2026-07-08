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

    /// <summary>
    /// Auto-advance policy for a REQUIRED update: with no player keypress, download and then apply so a
    /// mandatory build installs itself. A no-op unless <see cref="UpdateService.IsRequired"/> is set, so
    /// optional updates stay player-driven via <see cref="Trigger"/>. Idempotent and cheap: each call
    /// acts only at an actionable transition (<see cref="UpdateState.UpdateAvailable"/> -&gt; download,
    /// <see cref="UpdateState.ReadyToApply"/> -&gt; apply) and no-ops otherwise, so it is designed to be
    /// called once per frame from the game loop (which also keeps <see cref="UpdateService.ApplyUpdate"/>
    /// and its forced exit on the caller's thread). It deliberately does NOT auto-retry a
    /// <see cref="UpdateState.Failed"/> update (that would hot-loop); the overlay still offers the player
    /// a keypress retry.
    /// </summary>
    public static void AutoAdvanceRequired(UpdateService service)
    {
        if (!service.IsRequired)
        {
            return;
        }
        switch (ResolveAction(service.State))
        {
            case OverlayAction.Download: _ = service.StartDownloadAsync(); break;
            case OverlayAction.Apply: service.ApplyUpdate(); break;
            // Failed (Retry) is intentionally not auto-driven: see the summary.
        }
    }
}
