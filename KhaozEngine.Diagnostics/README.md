# KhaozEngine.Diagnostics

Game-agnostic diagnostics primitives for MonoGame games. Pure `System.IO`, no MonoGame dependency.

## FileLogger

Thread-safe, timestamped file logger for diagnosing silent crashes and startup failures.

```csharp
var log = new FileLogger();
log.Initialize(logFilePath, previousLogFilePath);   // paths are caller-supplied (game-specific)
log.Info("Boot complete");
log.Warn("Config fell back to defaults");
log.Error("Save failed", exception);
log.Shutdown();                                      // or `using`/Dispose
```

- Writes `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] message` lines via an `AutoFlush` `StreamWriter`.
- On `Initialize`, rotates an existing log to `previousLogFilePath` (when supplied) so the most
  recent session is always in the primary file. Pass `null` to skip rotation.
- Every method is guarded by a lock and swallows IO failures: logging never crashes the game.
- `Initialize` is idempotent; repeat calls are ignored until `Shutdown`.

The log file location is the caller's concern. Each game resolves its own app-data path (e.g. an
`AppDataPaths` helper) and hands the resolved paths to `Initialize`.
