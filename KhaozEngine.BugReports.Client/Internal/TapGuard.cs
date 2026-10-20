using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>
/// Turns a capture fault into one warning and a stopped capture. The first body that throws trips the guard: the
/// fault is warned once, the trip action runs once, and every later <see cref="Run"/> returns false without
/// running its body. A throw from the warning or the trip action is swallowed, so <see cref="Run"/> never throws
/// for its body. Used from the frame thread only.
/// </summary>
internal sealed class TapGuard
{
    readonly string _owner;
    readonly Action? _onTrip;
    readonly Action<string, Exception> _warn;

    /// <param name="owner">Names the guarded capture in the warning.</param>
    /// <param name="onTrip">Runs once when the guard trips, such as stopping the recording.</param>
    /// <param name="warn">Receives the one warning. Defaults to <see cref="Log.Warn"/>.</param>
    internal TapGuard(string owner, Action? onTrip = null, Action<string, Exception>? warn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        _owner = owner;
        _onTrip = onTrip;
        _warn = warn ?? Log.Warn;
    }

    internal bool Tripped { get; private set; }

    /// <summary>Runs <paramref name="body"/> and returns true, or false when it threw or the guard has tripped.</summary>
    internal bool Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Tripped) return false;
        try
        {
            body();
            return true;
        }
        catch (Exception ex)
        {
            Tripped = true;
            // Neither callback may take the frame down with it. The guard is already tripped.
            try
            {
                _warn($"{_owner} faulted and stopped capturing: {ex.Message}", ex);
            }
            catch (Exception)
            {
                // The warning is lost. Stopping the capture still matters.
            }

            try
            {
                _onTrip?.Invoke();
            }
            catch (Exception)
            {
                // The trip action failed part way. The guard still refuses every later body.
            }
            return false;
        }
    }
}
