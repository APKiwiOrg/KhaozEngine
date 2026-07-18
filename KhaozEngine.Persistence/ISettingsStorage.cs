namespace KhaozEngine.Persistence;

/// <summary>
/// Saves and loads strongly-typed application settings.
/// </summary>
public interface ISettingsStorage
{
    /// <summary>The settings file name used by the storage.</summary>
    string SettingsFileName { get; set; }

    /// <summary>Saves <paramref name="settings"/> to storage.</summary>
    /// <typeparam name="T">The settings type (must have a parameterless constructor).</typeparam>
    void SaveSettings<T>(T settings) where T : new();

    /// <summary>Loads settings from storage, or a new <typeparamref name="T"/> if none exist.</summary>
    /// <typeparam name="T">The settings type (must have a parameterless constructor).</typeparam>
    T LoadSettings<T>() where T : new();

    /// <summary>Returns true if a settings file exists in storage.</summary>
    bool SettingsExist();

    /// <summary>Loads settings with an outcome report. The default implementation maps the plain
    /// <see cref="LoadSettings{T}"/> path (Loaded, or FreshDefault when no file exists). Implementations
    /// with recovery (see <see cref="FileSettingsStorage"/>) override it to report what actually happened.</summary>
    SaveLoadResult<T> LoadSettingsDetailed<T>() where T : new()
        => SettingsExist()
            ? new SaveLoadResult<T> { Value = LoadSettings<T>(), Outcome = SaveLoadOutcome.Loaded }
            : new SaveLoadResult<T> { Value = new T(), Outcome = SaveLoadOutcome.FreshDefault };
}
