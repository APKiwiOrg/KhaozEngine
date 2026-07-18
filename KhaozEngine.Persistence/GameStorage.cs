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
    /// encoder is configured and the content is encoded, it is decoded transparently first. A corrupt, tampered,
    /// or otherwise unloadable primary never throws: it falls back through the backup generations and, failing
    /// that, to a fresh default. This is the value of <see cref="LoadWithOutcome{T}"/>. Call that overload when
    /// you need to know which recovery path was taken. Reads committed on-disk state, so after a
    /// <see cref="Save{T}(string, T)"/> call <see cref="Flush"/> before loading the same file. Parsing tolerates
    /// comments and trailing commas (saves are written as human-editable indented JSON).
    /// </summary>
    public T Load<T>(string fileName, MigrationChain<T>? migrations = null) where T : new()
        => LoadWithOutcome(fileName, migrations).Value;

    /// <summary>
    /// Loads <paramref name="fileName"/> and reports how the load resolved. Probes the primary and each backup
    /// generation in order, returning the first valid one: the primary as <see cref="SaveLoadOutcome.Loaded"/>
    /// (or <see cref="SaveLoadOutcome.LoadedLegacyPlaintext"/> for a plaintext save read under a configured
    /// encoder), a backup as <see cref="SaveLoadOutcome.RecoveredFromBackup"/>. A tampered save is rejected under
    /// <see cref="TamperPolicy.Strict"/> and recovered under <see cref="TamperPolicy.Lenient"/>. When nothing
    /// loads, a fresh default is returned and stamped current via <paramref name="migrations"/>, as either
    /// <see cref="SaveLoadOutcome.FreshDefault"/> (nothing on disk) or <see cref="SaveLoadOutcome.RejectedAndDefaulted"/>
    /// (something on disk, all of it invalid). Never throws on a bad save. Reads committed on-disk state, so
    /// <see cref="Flush"/> after a save before loading the same file.
    /// </summary>
    public SaveLoadResult<T> LoadWithOutcome<T>(string fileName, MigrationChain<T>? migrations = null) where T : new()
    {
        string fullPath = Paths.GetFilePath(fileName);
        string? firstFailureDetail = null;
        bool anyCandidateExisted = false;

        for (int gen = 0; gen <= backupGenerations; gen++)
        {
            string path = SaveBackups.GenerationPath(fullPath, gen);
            SaveCandidate candidate = ProbeCandidate(path);

            if (candidate.Validity == SaveGenerationValidity.Missing)
            {
                continue;
            }

            anyCandidateExisted = true;

            if (candidate.Validity != SaveGenerationValidity.Valid)
            {
                firstFailureDetail ??= candidate.Detail;
                continue;
            }

            T value;
            try
            {
                value = JsonSerializer.Deserialize<T>(candidate.Json!, JsonDefaults.TolerantRead) ?? new T();
            }
            catch (JsonException ex)
            {
                // A candidate that decoded and structurally parsed can still fail to bind to T. Treat it as
                // invalid and keep looking, recording the reason as the first failure.
                firstFailureDetail ??= ex.Message;
                continue;
            }

            if (migrations is not null)
            {
                value = migrations.Migrate(value, logger);
            }

            SaveLoadOutcome outcome;
            string? detail = null;
            if (gen == 0)
            {
                if (candidate.LegacyPlaintext)
                {
                    outcome = SaveLoadOutcome.LoadedLegacyPlaintext;
                }
                else
                {
                    outcome = SaveLoadOutcome.Loaded;
                    if (candidate.TamperAccepted)
                    {
                        detail = candidate.Detail;
                    }
                }
            }
            else
            {
                outcome = SaveLoadOutcome.RecoveredFromBackup;
            }

            return new SaveLoadResult<T>
            {
                Value = value,
                Outcome = outcome,
                Detail = detail,
                RecoveredGeneration = gen,
                Metadata = candidate.Metadata,
            };
        }

        T fresh = new();
        if (migrations is not null)
        {
            fresh = migrations.StampCurrent(fresh);
        }

        return new SaveLoadResult<T>
        {
            Value = fresh,
            Outcome = anyCandidateExisted ? SaveLoadOutcome.RejectedAndDefaulted : SaveLoadOutcome.FreshDefault,
            Detail = firstFailureDetail,
        };
    }

    // A single probed save generation: its validity plus whatever decoded content and flags the ladder needs
    // to decide the outcome. Reused by the generation-listing surface. Init-only so it stays a value snapshot.
    private readonly struct SaveCandidate
    {
        public SaveGenerationValidity Validity { get; init; }
        public string? Json { get; init; }
        public SaveMetadata? Metadata { get; init; }
        public string? Detail { get; init; }
        public bool LegacyPlaintext { get; init; }
        public bool TamperAccepted { get; init; }
    }

    // Probes one file path and classifies it, without deserializing to T. The six rules mirror the load
    // contract: missing, unreadable, encoded (decode + tamper policy), plaintext-under-encoder (legacy policy),
    // no-encoder, and a final JSON-parse guard that downgrades a structurally broken payload to Corrupt.
    private SaveCandidate ProbeCandidate(string path)
    {
        if (!File.Exists(path))
        {
            return new SaveCandidate { Validity = SaveGenerationValidity.Missing };
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return new SaveCandidate { Validity = SaveGenerationValidity.Corrupt, Detail = ex.Message };
        }

        string json;
        SaveMetadata? metadata = null;
        string? detail = null;
        bool legacyPlaintext = false;
        bool tamperAccepted = false;

        if (Encoder is not null && Encoder.IsEncoded(text))
        {
            SaveDecodeResult decoded = Encoder.TryDecode(text);
            if (decoded.Verdict == SaveDecodeVerdict.Ok)
            {
                json = decoded.Json!;
                metadata = decoded.Metadata;
            }
            else if (decoded.Verdict == SaveDecodeVerdict.TamperMismatch && tamperPolicy == TamperPolicy.Lenient)
            {
                json = decoded.Json!;
                metadata = decoded.Metadata;
                detail = decoded.Detail;
                tamperAccepted = true;
            }
            else if (decoded.Verdict == SaveDecodeVerdict.TamperMismatch)
            {
                return new SaveCandidate { Validity = SaveGenerationValidity.Tampered, Detail = decoded.Detail };
            }
            else
            {
                // Malformed (an unexpected NotEncoded cannot reach here since IsEncoded was true): structural damage.
                return new SaveCandidate { Validity = SaveGenerationValidity.Corrupt, Detail = decoded.Detail };
            }
        }
        else if (Encoder is not null)
        {
            if (!acceptLegacyPlaintext)
            {
                return new SaveCandidate { Validity = SaveGenerationValidity.Tampered, Detail = "plaintext save rejected (AcceptLegacyPlaintext = false)" };
            }
            json = text;
            legacyPlaintext = true;
        }
        else
        {
            json = text;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new SaveCandidate { Validity = SaveGenerationValidity.Corrupt, Detail = ex.Message };
        }

        return new SaveCandidate
        {
            Validity = SaveGenerationValidity.Valid,
            Json = json,
            Metadata = metadata,
            Detail = detail,
            LegacyPlaintext = legacyPlaintext,
            TamperAccepted = tamperAccepted,
        };
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
