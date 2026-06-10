# SaveEncoder promotion (Batch 1, item 6)

Status: approved design, pre-implementation
Date: 2026-06-11

## Goal

Promote Nullwake's `SaveEncoder` (Base64 + HMAC-SHA256 tamper-deterrent) into a new shared
`KhaozEngine.Persistence` package, parameterising the HMAC key and the `NWSV1` magic prefix, and
replacing its dependency on Nullwake's `GameLogger` with the engine's logging service (`ILogger`).

Source: `Nullwake.Core/Systems/SaveEncoder.cs` (static class; hardcoded key + prefix; logs via
`Nullwake.Core.Engine.GameLogger`).

## Decisions (from brainstorming)

1. **New package `KhaozEngine.Persistence`** (net10.0). BCL crypto/text + a `ProjectReference` to
   `KhaozEngine.Diagnostics` (for `ILogger`). Future home for AtomicJsonWriter (#8), SettingsManager (#10).
2. **Instance class**, parameterising `hmacKey` (`byte[]`) and `magicPrefix` (`string`).
3. **Logging via injected `ILogger`** (approach A). The original called `GameLogger`; the shared
   version depends only on the stable `KhaozEngine.Diagnostics.ILogger` interface, not the (now
   removed) `FileLogger`. The game passes `Log.For<SaveEncoder>()` (or any `ILogger`); tests pass a
   fake. A Persistence→Diagnostics dependency is acceptable (engine-wide good logging is wanted).

   > Context: the 3.1.0 logging service **removed** `FileLogger`; logging is now `Log` / `LogManager`
   > / `ILogger` / sinks. An earlier draft of this design injected `FileLogger` — invalid now.

## Public API

Namespace `KhaozEngine.Persistence`:

```csharp
public sealed class SaveEncoder
{
    public SaveEncoder(byte[] hmacKey, string magicPrefix, ILogger logger);

    public string Encode(string json);          // "{prefix}:{hmac-hex}:{base64}"
    public string? Decode(string fileContent);  // json, or null if not-our-format / malformed / corrupt
    public bool IsEncoded(string fileContent);  // starts with "{prefix}:"
}
```

## Behaviour contract

Format: `{magicPrefix}:{hmac-hex-lower}:{base64-payload}` (separator `:` hardcoded, as in the original).

- **Ctor:** `ArgumentNullException.ThrowIfNull(logger)`; `hmacKey` null or empty → `ArgumentException`;
  `magicPrefix` null/empty/whitespace → `ArgumentException`. Store a defensive copy of `hmacKey`.
- **`Encode(json)`:** `base64 = Convert.ToBase64String(UTF8(json))`; `hmac = HMACSHA256(hmacKey)` over
  the base64 string, hex-lower via `Convert.ToHexStringLower`; return `{prefix}:{hmac}:{base64}`.
- **`IsEncoded(content)`:** `content.StartsWith(magicPrefix + ":", StringComparison.Ordinal)`.
- **`Decode(content)`:**
  - not encoded (`!IsEncoded`) → return `null`, **no log** (it's just not our format, e.g. legacy plaintext).
  - encoded but the two `:` separators aren't both present → `logger.Error("[SaveEncoder] malformed
    encoded save")`, return `null`.
  - recompute HMAC over the payload; `authentic = string.Equals(hmac, expected, OrdinalIgnoreCase)`.
  - decode the Base64 payload to UTF-8:
    - `FormatException` → `logger.Error("[SaveEncoder] failed to decode Base64 payload")`, return `null`.
    - success + `authentic` → `logger.Info("[SaveEncoder] save decoded (HMAC ok)")`, return json.
    - success + not authentic → `logger.Warn("[SaveEncoder] save decoded but HMAC mismatch - possible
      tampering")`, return json (lenient: recover the data anyway, preserving the original's alpha-friendly behaviour).
  - Exactly one log line per `Decode` call (except the silent not-our-format case).

This is a casual tamper-**deterrent**, not real security: the HMAC key ships in the game binary.
Comparison stays a plain ordinal string compare (constant-time buys nothing when the key is shippable).

## Consumer shape after adopt (out of scope here)

Nullwake constructs `new SaveEncoder(Encoding.UTF8.GetBytes("Nullwake-SaveIntegrity-v1"), "NWSV1",
Log.For<SaveEncoder>())`, deletes its copy, and drops the `GameLogger` coupling. Other games adopt
if/when they add tamper-deterred saves.

## Project / packaging changes

- New `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`: `PackageId`, `Description`, packed
  `README.md`, and `<ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />`.
  No MonoGame.
- Add the project to `KhaozEngine.slnx` and a `ProjectReference` in `KhaozEngine.Tests`.
- Inherits the shared `<Version>` from `Directory.Build.props`.

## Testing (headless, KhaozEngine.Tests)

A hand-rolled `FakeLogger : ILogger` capturing `(LogLevel, string message)` per call (`IsEnabled` →
true, `Category` → "test"; `Info`/`Warn`/`Error`/etc. append; `Log(level,...)` appends). No real
logging infrastructure needed.

- Round-trip: `Encode(json)` then `Decode(...)` returns the original JSON; the fake captured exactly
  one `Info` containing "HMAC ok".
- `IsEncoded`: true for encoded output, false for plain text.
- Not encoded: `Decode("plain text")` → `null`, fake captured **zero** entries.
- Tamper: take a valid encoded string, flip a character in the base64 payload → `Decode` still
  returns the JSON, and the fake captured one `Warn` containing "HMAC mismatch".
- Malformed: a string that starts with `"{prefix}:"` but has no second separator → `null`, one `Error`.
- Corrupt payload: valid `{prefix}:{hmac}:` then an invalid Base64 body → `null`, one `Error`.
- Parameterisation: different `magicPrefix` / `hmacKey` change `Encode` output; decoding with the
  wrong key triggers the mismatch `Warn`.
- Ctor validation: null/empty `hmacKey`, null/empty/whitespace `magicPrefix` → `ArgumentException`;
  null `logger` → `ArgumentNullException`.

## Release handling

Item 6 of Batch 1. No `<Version>` bump / `CHANGELOG.md` / `dotnet pack` here. The single
`3.1.0 → 3.2.0` release happens at the end of the batch (after the consolidation cleanup review).

## Out of scope

- Migrating Nullwake (or any game) to consume it — separate adopt PRs after release.
- Save read/write, atomic file writing (item 8), settings (item 10).
