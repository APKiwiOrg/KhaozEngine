using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Serialization;

namespace KhaozEngine.Persistence;

/// <summary>
/// One-call facade over the engine's storage stack: publisher-rooted <see cref="AppDataPaths"/>, a
/// shared coalesced <see cref="PersistenceQueue"/>, a <see cref="FileSettingsStorage"/>, and an
/// optional <see cref="SaveEncoder"/>. Exposes generic typed save/load (plaintext or transparently
/// encoded) so games stop hand-assembling paths + queue + storages. Owns the write queue and
/// flushes/disposes it on <see cref="Dispose"/>.
/// </summary>
public sealed class GameStorage : IDisposable
{
    private readonly ILogger logger;
    private readonly TamperPolicy tamperPolicy;
    private readonly bool acceptLegacyPlaintext;
    private readonly int backupGenerations;
    private readonly string? gameVersion;

    /// <summary>Publisher-rooted paths: <c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c>.</summary>
    public AppDataPaths Paths { get; }

    /// <summary>The single shared write queue (atomic, coalesced) all writes go through.</summary>
    public PersistenceQueue WriteQueue { get; }

    /// <summary>Settings storage over <see cref="Paths"/> and <see cref="WriteQueue"/>.</summary>
    public ISettingsStorage Settings { get; }

    /// <summary>The configured save encoder, or null when none was provided.</summary>
    public SaveEncoder? Encoder { get; }

    /// <summary>Creates a storage facade rooted at <c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c> using the real OS environment.</summary>
    public GameStorage(string publisher, string appName, GameStorageOptions? options = null)
        : this(new AppDataPaths(publisher, appName), options)
    {
    }

    /// <summary>
    /// Creates a storage facade over an already-built <paramref name="paths"/>. Use this overload to
    /// supply a custom <see cref="AppDataPaths"/> (it is also the seam tests use, building paths over a
    /// fake environment).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public GameStorage(AppDataPaths paths, GameStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new GameStorageOptions();
        this.logger = options.Logger ?? Log.For<GameStorage>();
        Paths = paths;
        tamperPolicy = options.TamperPolicy;
        acceptLegacyPlaintext = options.AcceptLegacyPlaintext;
        backupGenerations = options.BackupGenerations;
        gameVersion = options.GameVersion;
        WriteQueue = new PersistenceQueue(options.Logger, options.MaxWriteAttempts, options.RetryDelay, backupGenerations);
        Settings = new FileSettingsStorage(Paths, WriteQueue);
        Encoder = options.Encoder;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to indented JSON, encodes it when an encoder is configured, and
    /// queues a write to <paramref name="fileName"/> in the app-data dir. Configuring
    /// <see cref="GameStorageOptions.Encoder"/> makes encoding the default for every call. Opt out per
    /// call (or force it) via the <see cref="SaveWriteOptions"/> overload, for example for a file meant to
    /// stay deliberately hand-editable. The write is queued (asynchronous and coalesced), so a subsequent
    /// <see cref="Load{T}"/> of the same file only reflects it after a <see cref="Flush"/>.
    /// </summary>
    public void Save<T>(string fileName, T value) => Save(fileName, value, (SaveWriteOptions?)null);

    /// <summary>
    /// Serializes <paramref name="value"/> to indented JSON, applies <paramref name="writeOptions"/>, and
    /// queues a write to <paramref name="fileName"/> in the app-data dir. A null
    /// <see cref="SaveWriteOptions.Encode"/> (or a null <paramref name="writeOptions"/>) follows the
    /// default: encode when an encoder is configured, plaintext otherwise. <c>false</c> forces plaintext
    /// for this call, even with an encoder configured, for example for a file meant to stay deliberately
    /// hand-editable. <c>true</c> forces encoding. When encoded, <see cref="SaveWriteOptions.Summary"/> and
    /// the configured <see cref="GameStorageOptions.GameVersion"/> are stamped into the envelope's
    /// <see cref="SaveMetadata"/>. The write is queued (asynchronous and coalesced), so a subsequent
    /// <see cref="Load{T}"/> of the same file only reflects it after a <see cref="Flush"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Encoding is requested (implicitly or explicitly) but no encoder was configured.</exception>
    public void Save<T>(string fileName, T value, SaveWriteOptions? writeOptions)
    {
        bool encode = writeOptions?.Encode ?? Encoder is not null;
        string json = JsonSerializer.Serialize(value, JsonDefaults.IndentedWrite);
        if (encode)
        {
            if (Encoder is null)
            {
                throw new InvalidOperationException("Encoded save requested but no SaveEncoder was configured (set GameStorageOptions.Encoder).");
            }

            SaveMetadata metadata = new() { SavedAtUtc = DateTime.UtcNow, GameVersion = gameVersion, Summary = writeOptions?.Summary };
            json = Encoder.Encode(json, metadata);
        }
        WriteQueue.Enqueue(Paths.GetFilePath(fileName), json);
    }

    /// <summary>Serializes <paramref name="value"/> to indented JSON, optionally encodes it, and queues a write to <paramref name="fileName"/> in the app-data dir.
    /// The write is queued (asynchronous and coalesced), so a subsequent <see cref="Load{T}"/> of the same file only reflects it after a <see cref="Flush"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="encode"/> is true but no encoder was configured.</exception>
    public void Save<T>(string fileName, T value, bool encode) => Save(fileName, value, new SaveWriteOptions { Encode = encode });

    /// <summary>
    /// Builds a <see cref="SettingsManager{T}"/> over <see cref="Settings"/> (which loads on
    /// construction), using the facade's logger. <paramref name="sanitizeOnLoad"/> is applied after
    /// every load (clamp fields, normalize, etc.); <paramref name="migrations"/> is an optional versioned
    /// migration chain run before it.
    /// </summary>
    public SettingsManager<T> CreateSettingsManager<T>(Func<T, T>? sanitizeOnLoad = null, MigrationChain<T>? migrations = null) where T : new()
        => new SettingsManager<T>(Settings, logger, sanitizeOnLoad, migrations);

    /// <summary>
    /// Loads <paramref name="fileName"/> and deserializes to <typeparamref name="T"/>, then runs the optional
    /// <paramref name="migrations"/> chain. Returns a new <typeparamref name="T"/> if the file is absent. If an
    /// encoder is configured and the content is encoded, it is decoded transparently first (lenient: recovers
    /// JSON even on HMAC mismatch). Reads committed on-disk state, so after a <see cref="Save{T}(string, T)"/>
    /// call <see cref="Flush"/> before loading the same file. Parsing tolerates comments and trailing commas (saves
    /// are written as human-editable indented JSON).
    /// </summary>
    public T Load<T>(string fileName, MigrationChain<T>? migrations = null) where T : new()
    {
        string path = Paths.GetFilePath(fileName);
        T value;
        if (!File.Exists(path))
        {
            value = new T();
        }
        else
        {
            string content = File.ReadAllText(path);
            if (Encoder is not null && Encoder.IsEncoded(content))
            {
                content = Encoder.Decode(content) ?? content;
            }

            value = JsonSerializer.Deserialize<T>(content, JsonDefaults.TolerantRead) ?? new T();
        }

        return migrations is null ? value : migrations.Migrate(value, logger);
    }

    /// <summary>True when <paramref name="fileName"/> exists in the app-data directory.</summary>
    public bool Exists(string fileName) => File.Exists(Paths.GetFilePath(fileName));

    /// <summary>Deletes <paramref name="fileName"/> if present. Absent file is a no-op.</summary>
    public void Delete(string fileName)
    {
        string path = Paths.GetFilePath(fileName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"failed to delete '{path}'", ex);
        }
    }

    /// <summary>Drains all pending writes (use on shutdown).</summary>
    public void Flush() => WriteQueue.Flush();

    /// <summary>Flushes pending writes, then disposes the write queue.</summary>
    public void Dispose() => WriteQueue.Dispose();
}
