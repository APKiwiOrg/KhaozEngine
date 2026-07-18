using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Serialization;

namespace KhaozEngine.Persistence;

/// <summary>
/// File-based <see cref="ISettingsStorage"/> that serializes settings to indented JSON under the
/// app-data directory resolved by <see cref="AppDataPaths"/>. Writes go through an
/// <see cref="IPersistenceQueue"/> (which owns the atomic-write strategy); reads are direct.
/// </summary>
public sealed class FileSettingsStorage : ISettingsStorage
{
    private readonly AppDataPaths appDataPaths;
    private readonly IPersistenceQueue writeQueue;

    /// <summary>Creates a storage rooted at <paramref name="appDataPaths"/>, writing via <paramref name="writeQueue"/>.</summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public FileSettingsStorage(AppDataPaths appDataPaths, IPersistenceQueue writeQueue)
    {
        this.appDataPaths = appDataPaths ?? throw new ArgumentNullException(nameof(appDataPaths));
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
    }

    /// <summary>The settings file name within the app-data directory. Defaults to "settings.json".</summary>
    public string SettingsFileName { get; set; } = "settings.json";

    /// <summary>The number of numbered backup generations probed on a failed primary read, in addition to
    /// the primary itself. Defaults to 2 (checks the primary plus <c>.bak1</c> and <c>.bak2</c>). Set this
    /// to match the write queue's own <c>backupGenerations</c> so a corrupt primary can actually recover.</summary>
    public int BackupGenerations { get; set; } = 2;

    private string SettingsFilePath => appDataPaths.GetFilePath(SettingsFileName);

    /// <summary>Serializes <paramref name="settings"/> to JSON and queues an atomic write.</summary>
    public void SaveSettings<T>(T settings) where T : new()
    {
        string json = JsonSerializer.Serialize(settings, JsonDefaults.IndentedWrite);
        writeQueue.Enqueue(SettingsFilePath, json);
    }

    /// <summary>Loads settings from disk, recovering from a backup generation if needed, or returns a new
    /// <typeparamref name="T"/> if nothing usable exists. Never throws. See <see cref="LoadSettingsDetailed{T}"/>
    /// for the outcome report.</summary>
    public T LoadSettings<T>() where T : new() => LoadSettingsDetailed<T>().Value;

    /// <summary>
    /// Loads settings, probing the primary and each backup generation in order for the first candidate that
    /// reads and deserializes cleanly. A candidate that fails (missing, unreadable, or not valid JSON for
    /// <typeparamref name="T"/>) is skipped and the ladder continues to the next generation, recording the
    /// first failure as <see cref="SaveLoadResult{T}.Detail"/>. Returns <see cref="SaveLoadOutcome.Loaded"/>
    /// for a good primary, <see cref="SaveLoadOutcome.RecoveredFromBackup"/> for a good backup (with
    /// <see cref="SaveLoadResult{T}.RecoveredGeneration"/> set), <see cref="SaveLoadOutcome.RejectedAndDefaulted"/>
    /// when at least one candidate existed but none were valid, or <see cref="SaveLoadOutcome.FreshDefault"/>
    /// when nothing exists on disk. Never throws.
    /// </summary>
    public SaveLoadResult<T> LoadSettingsDetailed<T>() where T : new()
    {
        string primaryPath = SettingsFilePath;
        string? firstFailureDetail = null;
        bool anyCandidateExisted = false;

        for (int gen = 0; gen <= BackupGenerations; gen++)
        {
            string path = SaveBackups.GenerationPath(primaryPath, gen);
            if (!File.Exists(path))
            {
                continue;
            }

            anyCandidateExisted = true;

            try
            {
                string json = File.ReadAllText(path);
                T value = JsonSerializer.Deserialize<T>(json, JsonDefaults.TolerantRead) ?? new T();
                return new SaveLoadResult<T>
                {
                    Value = value,
                    Outcome = gen == 0 ? SaveLoadOutcome.Loaded : SaveLoadOutcome.RecoveredFromBackup,
                    RecoveredGeneration = gen,
                };
            }
            catch (Exception ex)
            {
                firstFailureDetail ??= ex.Message;
            }
        }

        return new SaveLoadResult<T>
        {
            Value = new T(),
            Outcome = anyCandidateExisted ? SaveLoadOutcome.RejectedAndDefaulted : SaveLoadOutcome.FreshDefault,
            Detail = firstFailureDetail,
        };
    }

    /// <summary>True when the primary settings file exists on disk.</summary>
    public bool SettingsExist()
    {
        string path = SettingsFilePath;
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }
}
