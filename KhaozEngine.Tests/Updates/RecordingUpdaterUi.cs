using System.Collections.Generic;
using KhaozEngine.Updates;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// An <see cref="IUpdaterUi"/> that records every call so a headless test can assert the phase order,
/// progress ticks, and lifecycle the applier drives, without any real window.
/// </summary>
internal sealed class RecordingUpdaterUi : IUpdaterUi
{
    public readonly List<UpdaterPhase> Phases = new();
    public readonly List<(int Done, int Total)> Progress = new();
    public readonly List<string> Statuses = new();
    public UpdaterUiTheme? ShownTheme;
    public int ShowCalls;
    public int CloseCalls;

    // Fired on the first Close so a test can snapshot other state (e.g. how many relaunch attempts had
    // run) at the moment the window closed, proving the window stayed up across the relaunch-retry wait.
    public System.Action? OnClose;

    public void Show(UpdaterUiTheme theme)
    {
        ShowCalls++;
        ShownTheme = theme;
    }

    public void SetPhase(UpdaterPhase phase) => Phases.Add(phase);

    public void SetProgress(int done, int total) => Progress.Add((done, total));

    public void SetStatus(string status) => Statuses.Add(status);

    public void Close()
    {
        if (CloseCalls == 0)
        {
            OnClose?.Invoke();
        }
        CloseCalls++;
    }
}
