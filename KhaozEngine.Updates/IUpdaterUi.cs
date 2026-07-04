#nullable enable

namespace KhaozEngine.Updates;

/// <summary>The stage the updater shim is in, shown by <see cref="IUpdaterUi"/>.</summary>
public enum UpdaterPhase
{
    /// <summary>
    /// Fetching files. The shim never downloads in the current design (the in-game overlay does the
    /// download before the shim runs), so this is modelled for completeness but not shown by the shim
    /// window today. See the "Download phase" note in the updater docs.
    /// </summary>
    Download,

    /// <summary>Copying staged files over the install. Determinate progress (files copied / total).</summary>
    Install,

    /// <summary>
    /// Post-copy settle: waiting for the OS security scan to release the freshly-written executable
    /// before relaunch (the Part A settle wait). Indeterminate / marquee, no file count.
    /// </summary>
    Finishing
}

/// <summary>
/// A minimal progress surface the updater shim drives during an apply. The default is
/// <see cref="NullUpdaterUi"/> (no window); on Windows the shim supplies a lightweight native GDI window
/// (<c>Win32UpdaterUi</c>) so the user sees Install then Finishing instead of a frozen gap or a raw OS
/// crash dialog while a security scan holds the new exe. Implementations must be resilient: a UI failure
/// must never fail the apply, so every method is best-effort and a broken window degrades to a no-op.
/// All methods are safe to call from the apply thread; a real windowed implementation marshals them to
/// its own UI thread.
/// </summary>
public interface IUpdaterUi
{
    /// <summary>Creates and shows the window with the given theme. Called once, before any phase update.</summary>
    void Show(UpdaterUiTheme theme);

    /// <summary>Sets the current phase (drives the progress-bar mode: determinate vs marquee).</summary>
    void SetPhase(UpdaterPhase phase);

    /// <summary>Reports determinate progress within the current phase (e.g. files copied / total).</summary>
    void SetProgress(int done, int total);

    /// <summary>Sets the status line text (already-localized by the consumer via the theme).</summary>
    void SetStatus(string status);

    /// <summary>Closes and destroys the window. Idempotent; safe to call more than once.</summary>
    void Close();
}

/// <summary>
/// The no-op <see cref="IUpdaterUi"/>: the default when no window is configured, and the implementation
/// used on every non-Windows platform (in-place self-update is Windows-only today). All methods do
/// nothing, so the apply core can report phases unconditionally.
/// </summary>
public sealed class NullUpdaterUi : IUpdaterUi
{
    /// <summary>Shared instance (the type is stateless).</summary>
    public static readonly NullUpdaterUi Instance = new();

    private NullUpdaterUi() { }

    public void Show(UpdaterUiTheme theme) { }
    public void SetPhase(UpdaterPhase phase) { }
    public void SetProgress(int done, int total) { }
    public void SetStatus(string status) { }
    public void Close() { }
}
