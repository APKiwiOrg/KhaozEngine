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
}

/// <summary>
/// Source-generated JSON context for <see cref="ApplyUpdateConfig"/>. Lets the updater shim
/// (re)serialize the apply contract without reflection, so it can be published trimmed / AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApplyUpdateConfig))]
public partial class UpdatesJsonContext : JsonSerializerContext;
