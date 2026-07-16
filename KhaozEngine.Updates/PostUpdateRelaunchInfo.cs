using System;
using System.Text.Json.Serialization;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// The <c>update-applied.json</c> marker payload: written by <see cref="UpdateApplier"/> right after a
/// successful apply commit (new binaries verified, manifest installed) and before the relaunch, then read
/// once and deleted by the <see cref="UpdateService"/> constructor on the relaunched game. Its presence
/// tells the game that this boot is a post-update auto-relaunch (e.g. so a consumer can suppress a
/// boot-time "welcome back" prompt). Lives in the game's app-data directory alongside
/// <c>apply-in-progress.json</c>. PascalCase to match the source-generated <see cref="UpdatesJsonContext"/>.
/// </summary>
public sealed class PostUpdateRelaunchInfo
{
    /// <summary>The version string that was just applied (the manifest's target version).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>UTC time the apply completed, stamped just before the updater relaunched the game.</summary>
    public DateTimeOffset AppliedAtUtc { get; set; }
}
