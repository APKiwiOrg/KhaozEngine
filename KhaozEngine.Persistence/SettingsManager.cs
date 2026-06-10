using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Holds the current settings of type <typeparamref name="T"/> and persists them through an
/// <see cref="ISettingsStorage"/>. Load/save failures are swallowed (and logged via the optional
/// <see cref="ILogger"/>) so a corrupt settings file never crashes the game.
/// </summary>
/// <typeparam name="T">The settings type (must have a parameterless constructor).</typeparam>
public sealed class SettingsManager<T> where T : new()
{
    private readonly ISettingsStorage storage;
    private readonly ILogger? logger;
    private T settings = new();

    /// <summary>The underlying storage.</summary>
    public ISettingsStorage Storage => storage;

    /// <summary>The current settings. Never null.</summary>
    public T Settings => settings;

    /// <summary>Raised after settings are loaded (including when defaults are substituted).</summary>
    public event Action<T>? SettingsLoaded;

    /// <summary>Raised after settings are successfully saved.</summary>
    public event Action<T>? SettingsSaved;

    /// <summary>Creates a manager over <paramref name="storage"/> and immediately loads.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.logger = logger;
        Load();
    }

    /// <summary>Saves the current settings. Failures are swallowed and logged.</summary>
    public void Save()
    {
        try
        {
            storage.SaveSettings(settings);
            SettingsSaved?.Invoke(settings);
        }
        catch (Exception ex)
        {
            logger?.Error("Failed to save settings.", ex);
        }
    }

    /// <summary>Loads settings, falling back to defaults on failure. Always raises <see cref="SettingsLoaded"/>.</summary>
    public void Load()
    {
        try
        {
            settings = storage.LoadSettings<T>() ?? new T();
        }
        catch (Exception ex)
        {
            logger?.Error("Failed to load settings; using defaults.", ex);
            settings = new T();
        }

        SettingsLoaded?.Invoke(settings);
    }
}
