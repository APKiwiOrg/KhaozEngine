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
    private readonly ILogger logger;
    private readonly Func<T, T>? sanitizeOnLoad;
    private readonly MigrationChain<T>? migrations;
    private T settings = new();

    /// <summary>The underlying storage.</summary>
    public ISettingsStorage Storage => storage;

    /// <summary>The current settings. Never null.</summary>
    public T Settings => settings;

    /// <summary>How the most recent <see cref="Load"/> resolved: a clean read, a recovery from a backup, a
    /// fresh default, or a rejected-and-defaulted load. See <see cref="SaveLoadOutcome"/>.</summary>
    public SaveLoadOutcome LastLoadOutcome { get; private set; }

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
    /// <param name="migrations">
    /// Optional versioned migration chain run on every load BEFORE <paramref name="sanitizeOnLoad"/>: it
    /// steps the loaded value from its stored schema version up to the chain's current version. Null = no
    /// migration (back-compat). See <see cref="MigrationChain{T}"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null, Func<T, T>? sanitizeOnLoad = null, MigrationChain<T>? migrations = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        // Generic type name would render as "SettingsManager`1"; use a clean fixed category instead.
        this.logger = logger ?? Log.Get("SettingsManager");
        this.sanitizeOnLoad = sanitizeOnLoad;
        this.migrations = migrations;
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
            logger.Error("Failed to save settings.", ex);
        }
    }

    /// <summary>
    /// Loads settings, falling back to defaults on failure, then applies the optional sanitize hook.
    /// Always raises <see cref="SettingsLoaded"/>. Sets <see cref="LastLoadOutcome"/> to how the load
    /// resolved. A fresh or rejected-and-defaulted load is stamped to the migration chain's current
    /// version rather than migrated (a first boot, or a defaulted load, is not a pre-migration save).
    /// </summary>
    public void Load()
    {
        SaveLoadResult<T> result;
        try
        {
            result = storage.LoadSettingsDetailed<T>();
        }
        catch (Exception ex)
        {
            logger.Error("Failed to load settings; using defaults.", ex);
            result = new SaveLoadResult<T> { Value = new T(), Outcome = SaveLoadOutcome.RejectedAndDefaulted, Detail = ex.Message };
        }

        LastLoadOutcome = result.Outcome;
        T loaded = result.Value ?? new T();

        if (migrations is not null)
        {
            loaded = result.Outcome is SaveLoadOutcome.FreshDefault or SaveLoadOutcome.RejectedAndDefaulted
                ? migrations.StampCurrent(loaded)
                : migrations.Migrate(loaded, logger);
        }

        if (sanitizeOnLoad is not null)
        {
            try
            {
                loaded = sanitizeOnLoad(loaded) ?? loaded;
            }
            catch (Exception ex)
            {
                logger.Error("sanitizeOnLoad threw; using unsanitized value.", ex);
            }
        }

        settings = loaded;
        SettingsLoaded?.Invoke(settings);
    }
}
