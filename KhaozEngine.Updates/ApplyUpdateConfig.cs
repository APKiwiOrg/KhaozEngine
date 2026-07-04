using System.Collections.Generic;
using System.Text.Json.Serialization;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// The handoff contract between the running game and the external updater shim. The game serializes
/// this to <c>apply-update.json</c>; the shim reads it to copy staged files over the install, delete
/// removed files, swap the manifest, and relaunch. Property names are PascalCase to match the shim's
/// source-generated deserializer (<see cref="UpdatesJsonContext"/>), which keeps the shim AOT-safe.
/// </summary>
public sealed class ApplyUpdateConfig
{
    /// <summary>Version being applied (used for logging and the staging directory name).</summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>Absolute path of the install directory the shim copies into.</summary>
    public string InstallDir { get; set; } = string.Empty;

    /// <summary>Absolute path of the staging directory holding the downloaded files.</summary>
    public string StagingDir { get; set; } = string.Empty;

    /// <summary>Forward-slash relative paths to copy from staging into the install.</summary>
    public List<string> FilesToCopy { get; set; } = new();

    /// <summary>Forward-slash relative paths to delete from the install.</summary>
    public List<string> FilesToDelete { get; set; } = new();

    /// <summary>Executable the shim relaunches once the copy completes.</summary>
    public string GameExePath { get; set; } = string.Empty;

    /// <summary>PID of the game process the shim waits on before touching files.</summary>
    public int ParentPid { get; set; }

    /// <summary>Where the shim copies the new manifest so the next launch sees the updated state.</summary>
    public string ManifestDestPath { get; set; } = string.Empty;

    /// <summary>
    /// The game's per-user app-data directory. The shim places its relocation scratch dir under here
    /// (<c>updater-relocate/&lt;version&gt;</c>) so the self-relocated updater copy lives inside the app's
    /// own data area, never in a shared/system temp location, and is swept on the next launch. When empty
    /// the shim falls back to the manifest dest directory.
    /// </summary>
    public string AppDataDir { get; set; } = string.Empty;

    /// <summary>
    /// Optional look and localized text for the shim's progress window. Null (the default) means the
    /// shim shows a minimal default window (or nothing, on non-Windows); the apply still works either
    /// way. Populated by <c>UpdateService.ApplyUpdate</c> from <c>UpdateServiceOptions.UpdaterUi</c>.
    /// </summary>
    public ApplyUpdateUiConfig? Ui { get; set; }
}

/// <summary>
/// The serialized look and localized text for the shim's progress window (the <c>Ui</c> block of
/// <see cref="ApplyUpdateConfig"/>). Every field is optional; the shim fills any gap with a default.
/// Colors are nested <see cref="UpdaterUiColor"/> objects; text is passed already-localized by the
/// consumer. PascalCase to match the source-generated <see cref="UpdatesJsonContext"/>.
/// </summary>
public sealed class ApplyUpdateUiConfig
{
    /// <summary>Native window caption / title-bar text (e.g. the game name).</summary>
    public string? WindowTitle { get; set; }

    /// <summary>Large heading drawn inside the window (defaults to the window title when absent).</summary>
    public string? Heading { get; set; }

    /// <summary>Progress-bar / heading accent color.</summary>
    public UpdaterUiColor? Accent { get; set; }

    /// <summary>Window background (panel) color.</summary>
    public UpdaterUiColor? Background { get; set; }

    /// <summary>Text color for the heading and status line.</summary>
    public UpdaterUiColor? Text { get; set; }

    /// <summary>Forward-slash path of a logo image to draw, relative to the install directory. Optional.</summary>
    public string? LogoPath { get; set; }

    /// <summary>Status text for the Install phase (e.g. "Installing update").</summary>
    public string? InstallingText { get; set; }

    /// <summary>Status text for the Finishing (settle) phase (e.g. "Finishing up, checking with your security software...").</summary>
    public string? FinishingText { get; set; }

    /// <summary>Status text for the Download phase. Unused by the shim today (see <see cref="UpdaterPhase.Download"/>).</summary>
    public string? DownloadingText { get; set; }
}

/// <summary>An 8-bit-per-channel RGB color on the apply-config wire (0-255 components).</summary>
public sealed class UpdaterUiColor
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
}

/// <summary>
/// Source-generated JSON context for <see cref="ApplyUpdateConfig"/>. Lets the updater shim
/// (re)serialize the apply contract without reflection, so it can be published trimmed / AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApplyUpdateConfig))]
[JsonSerializable(typeof(ApplyUpdateUiConfig))]
[JsonSerializable(typeof(UpdaterUiColor))]
public partial class UpdatesJsonContext : JsonSerializerContext;
