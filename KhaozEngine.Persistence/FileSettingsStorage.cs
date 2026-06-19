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

    private string SettingsFilePath => appDataPaths.GetFilePath(SettingsFileName);

    /// <summary>Serializes <paramref name="settings"/> to JSON and queues an atomic write.</summary>
    public void SaveSettings<T>(T settings) where T : new()
    {
        string json = JsonSerializer.Serialize(settings, JsonDefaults.IndentedWrite);
        writeQueue.Enqueue(SettingsFilePath, json);
    }

    /// <summary>Loads settings from disk, or returns a new <typeparamref name="T"/> if none exist.</summary>
    public T LoadSettings<T>() where T : new()
    {
        if (!SettingsExist())
        {
            return new T();
        }

        string json = File.ReadAllText(SettingsFilePath);
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.TolerantRead) ?? new T();
    }

    /// <summary>True when the settings file exists on disk.</summary>
    public bool SettingsExist()
    {
        string path = SettingsFilePath;
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }
}
