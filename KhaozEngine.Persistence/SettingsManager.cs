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
    private readonly Func<T, T>? sanitizeOnLoad;
    private T settings = new();

    /// <summary>The underlying storage.</summary>
    public ISettingsStorage Storage => storage;

    /// <summary>The current settings. Never null.</summary>
    public T Settings => settings;

    /// <summary>Raised after settings are loaded (including when defaults are substituted).</summary>
    public event Action<T>? SettingsLoaded;

    /// <summary>
    /// Raised after the current settings are handed to storage without error. Storage may persist
    /// asynchronously (writes go through an <see cref="IPersistenceQueue"/>), so this signals the
    /// save was accepted/queued, not that bytes are durably on disk.
    /// </summary>
    public event Action<T>? SettingsSaved;

    /// <summary>Creates a manager over <paramref name="storage"/> and immediately loads.</summary>
    /// <param name="storage">Backing storage.</param>
    /// <param name="logger">Optional logger for swallowed load/save failures.</param>
    /// <param name="sanitizeOnLoad">
    /// Optional hook applied to the deserialized value after EVERY load, including the initial load
    /// in this constructor (which fires before any caller can subscribe to <see cref="SettingsLoaded"/>).
    /// Clamp fields, migrate a schema-version field, etc.; return the sanitized object, which becomes
    /// <see cref="Settings"/>. A hook that throws is swallowed/logged and the unsanitized value is used.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null, Func<T, T>? sanitizeOnLoad = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.logger = logger;
        this.sanitizeOnLoad = sanitizeOnLoad;
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

    /// <summary>
    /// Loads settings, falling back to defaults on failure, then applies the optional sanitize hook.
    /// Always raises <see cref="SettingsLoaded"/>.
    /// </summary>
    public void Load()
    {
        T loaded;
        try
        {
            loaded = storage.LoadSettings<T>() ?? new T();
        }
        catch (Exception ex)
        {
            logger?.Error("Failed to load settings; using defaults.", ex);
            loaded = new T();
        }

        if (sanitizeOnLoad is not null)
        {
            try
            {
                loaded = sanitizeOnLoad(loaded) ?? loaded;
            }
            catch (Exception ex)
            {
                logger?.Error("sanitizeOnLoad threw; using unsanitized value.", ex);
            }
        }

        settings = loaded;
        SettingsLoaded?.Invoke(settings);
    }
}
