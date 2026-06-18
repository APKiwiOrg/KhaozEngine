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
        WriteQueue = new PersistenceQueue(options.Logger, options.MaxWriteAttempts, options.RetryDelay);
        Settings = new FileSettingsStorage(Paths, WriteQueue);
        Encoder = options.Encoder;
    }

    /// <summary>Serializes <paramref name="value"/> to indented JSON, optionally encodes it, and queues a write to <paramref name="fileName"/> in the app-data dir.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="encode"/> is true but no encoder was configured.</exception>
    public void Save<T>(string fileName, T value, bool encode = false)
    {
        string json = JsonSerializer.Serialize(value, JsonDefaults.IndentedWrite);
        if (encode)
        {
            if (Encoder is null)
            {
                throw new InvalidOperationException("Encoded save requested but no SaveEncoder was configured (set GameStorageOptions.Encoder).");
            }
            json = Encoder.Encode(json);
        }
        WriteQueue.Enqueue(Paths.GetFilePath(fileName), json);
    }

    /// <summary>
    /// Loads <paramref name="fileName"/> and deserializes to <typeparamref name="T"/>. Returns a new
    /// <typeparamref name="T"/> if the file is absent. If an encoder is configured and the content is
    /// encoded, it is decoded transparently first (lenient: recovers JSON even on HMAC mismatch).
    /// </summary>
    public T Load<T>(string fileName) where T : new()
    {
        string path = Paths.GetFilePath(fileName);
        if (!File.Exists(path))
        {
            return new T();
        }

        string content = File.ReadAllText(path);
        if (Encoder is not null && Encoder.IsEncoded(content))
        {
            content = Encoder.Decode(content) ?? content;
        }

        return JsonSerializer.Deserialize<T>(content) ?? new T();
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
