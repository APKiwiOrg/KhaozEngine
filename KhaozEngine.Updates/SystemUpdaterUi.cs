using System;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// Factory for the real, per-OS <see cref="IUpdaterUi"/> the updater shim uses. Windows gets the native
/// GDI progress window (<see cref="Win32UpdaterUi"/>); every other platform gets <see cref="NullUpdaterUi"/>
/// (the window is Windows-only - macOS and Linux apply the update in place with no scan race to wait out).
/// The shim passes <see cref="CreateForCurrentOs"/> as the factory to <c>UpdateApplier.Run</c>, so no
/// window code is reachable off Windows.
/// </summary>
public static class SystemUpdaterUi
{
    /// <summary>Returns the native progress window on Windows, otherwise the no-op UI.</summary>
    public static IUpdaterUi CreateForCurrentOs()
        => OperatingSystem.IsWindows() ? new Win32UpdaterUi() : NullUpdaterUi.Instance;
}
