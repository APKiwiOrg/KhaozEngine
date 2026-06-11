# KhaozEngine.Persistence

Game-agnostic save/persistence helpers.

`SaveEncoder` wraps save JSON in a Base64 + HMAC-SHA256 envelope (`{prefix}:{hmac}:{base64}`) to
deter casual tampering. It is a deterrent, not real security: the HMAC key ships in the game binary.
Decoding is lenient (recovers the JSON even on an HMAC mismatch) and reports outcomes through the
engine logger.

```csharp
using System.Text;
using KhaozEngine.Diagnostics;   // ILogger / Log
using KhaozEngine.Persistence;

var encoder = new SaveEncoder(
    Encoding.UTF8.GetBytes("MyGame-SaveIntegrity-v1"),
    "MGSV1",
    Log.For<SaveEncoder>());

string onDisk = encoder.Encode(json);
string? loaded = encoder.Decode(onDisk);   // null only if not-our-format / malformed / corrupt
```

## AtomicJsonWriter

Static crash-safe writer: content goes to a sibling `.tmp` file which is then moved over the target,
so a crash mid-write never leaves a half-written destination. Synchronous and throws on IO failure
(the caller decides whether to catch).

```csharp
AtomicJsonWriter.WriteText(path, json);
AtomicJsonWriter.Write(path, myValue);                  // serialize (indented) then write
AtomicJsonWriter.Write(appDataPaths, "save.json", myValue);
```

## PersistenceQueue

Coalesced asynchronous writer (`IPersistenceQueue`). Each `Enqueue` records the latest payload per
target path (rapid repeats to one path collapse to the last) and a background worker drains them via
`AtomicJsonWriter`. Writes never throw into the caller: they retry briefly, then log through the
optional `ILogger` and raise `WriteFailed`. `Flush()` blocks until drained and the queue is
`IDisposable` (disposing flushes), so a game can guarantee a clean write on shutdown.

```csharp
using var queue = new PersistenceQueue(Log.For<PersistenceQueue>());   // logger, maxAttempts, retryDelay all optional
queue.WriteFailed += (_, e) => Notify(e.Path, e.Exception);

queue.Enqueue(appDataPaths, "save.json", saveData);    // or Enqueue(path, json) / Enqueue<T>(path, value)
// on shutdown:
queue.Flush();
```

## Settings

`SettingsManager<T>` holds a strongly-typed settings object and persists it through an
`ISettingsStorage`. `FileSettingsStorage` serializes to indented JSON under a
`KhaozEngine.App.AppDataPaths` directory and writes through an `IPersistenceQueue` (atomic,
per-path coalescing); reads are direct. Load/save failures are swallowed and reported via an
optional `KhaozEngine.Diagnostics.ILogger`.

```csharp
using KhaozEngine.App;
using KhaozEngine.Persistence;

var paths = new AppDataPaths("MyGame");
var storage = new FileSettingsStorage(paths, persistenceQueue);   // queue supplied by the host
var settings = new SettingsManager<MySettings>(storage, Log.For<SettingsManager<MySettings>>());

settings.Settings.MasterVolume = 0.8f;
settings.Save();
```
