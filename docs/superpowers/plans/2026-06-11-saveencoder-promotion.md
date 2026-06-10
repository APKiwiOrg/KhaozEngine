# SaveEncoder Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote Nullwake's `SaveEncoder` (Base64 + HMAC-SHA256 tamper-deterrent) into a new `KhaozEngine.Persistence` package, parameterising the HMAC key + magic prefix and logging via the engine's `ILogger`.

**Architecture:** One `public sealed class SaveEncoder` in `KhaozEngine.Persistence`, ctor-injected `(byte[] hmacKey, string magicPrefix, ILogger logger)`. BCL crypto + a `ProjectReference` to `KhaozEngine.Diagnostics` for `ILogger`. Headless xUnit tests drive it via a hand-rolled `FakeLogger`.

**Tech Stack:** C# / net10.0, `System.Security.Cryptography`, `KhaozEngine.Diagnostics.ILogger`, xUnit.

**Spec:** `docs/superpowers/specs/2026-06-11-saveencoder-promotion-design.md`

---

## File Structure

- `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj` — new package (refs Diagnostics).
- `KhaozEngine.Persistence/README.md` — packed readme.
- `KhaozEngine.Persistence/SaveEncoder.cs` — the class.
- `KhaozEngine.slnx`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — wiring.
- `KhaozEngine.Tests/SaveEncoderTests.cs` — tests + `FakeLogger`.

No version bump / CHANGELOG / pack — deferred to the end-of-batch 3.2.0 release. Commands run from the worktree root `/Users/antonio/KhaozEngine/.claude/worktrees/batch1-promote`.

---

## Task 1: Scaffold the KhaozEngine.Persistence package

**Files:**
- Create: `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`
- Create: `KhaozEngine.Persistence/README.md`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

- [ ] **Step 1: Create the package csproj** (`KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Persistence</PackageId>
    <Description>Game-agnostic save/persistence helpers. SaveEncoder is a Base64 + HMAC-SHA256 tamper-deterrent for save files. Pure .NET (+ KhaozEngine.Diagnostics for logging), no MonoGame dependency.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the README** (`KhaozEngine.Persistence/README.md`):

```markdown
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
```

- [ ] **Step 3: Add to `KhaozEngine.slnx`** — insert between Localization and Screens:

```xml
  <Project Path="KhaozEngine.Localization/KhaozEngine.Localization.csproj" />
  <Project Path="KhaozEngine.Persistence/KhaozEngine.Persistence.csproj" />
  <Project Path="KhaozEngine.Screens/KhaozEngine.Screens.csproj" />
```

- [ ] **Step 4: Add a `ProjectReference` in `KhaozEngine.Tests/KhaozEngine.Tests.csproj`** — between Localization and Screens:

```xml
    <ProjectReference Include="../KhaozEngine.Localization/KhaozEngine.Localization.csproj" />
    <ProjectReference Include="../KhaozEngine.Persistence/KhaozEngine.Persistence.csproj" />
    <ProjectReference Include="../KhaozEngine.Screens/KhaozEngine.Screens.csproj" />
```

- [ ] **Step 5: Build** — `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Persistence/KhaozEngine.Persistence.csproj KhaozEngine.Persistence/README.md KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "Scaffold KhaozEngine.Persistence package"
```

---

## Task 2: SaveEncoder (TDD)

**Files:**
- Create: `KhaozEngine.Tests/SaveEncoderTests.cs`
- Create: `KhaozEngine.Persistence/SaveEncoder.cs`

- [ ] **Step 1: Write the failing tests + FakeLogger** (`KhaozEngine.Tests/SaveEncoderTests.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class SaveEncoderTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-key-v1");
    private const string Prefix = "TSV1";

    private static SaveEncoder NewEncoder(out FakeLogger log)
    {
        log = new FakeLogger();
        return new SaveEncoder(Key, Prefix, log);
    }

    [Fact]
    public void RoundTrip_ReturnsOriginalJson_AndLogsInfo()
    {
        var encoder = NewEncoder(out FakeLogger log);
        string json = "{\"score\":42}";

        string encoded = encoder.Encode(json);
        string? decoded = encoder.Decode(encoded);

        Assert.Equal(json, decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Info, log.Entries[0].Level);
        Assert.Contains("HMAC ok", log.Entries[0].Message);
    }

    [Fact]
    public void IsEncoded_TrueForEncoded_FalseForPlain()
    {
        var encoder = NewEncoder(out _);

        Assert.True(encoder.IsEncoded(encoder.Encode("{}")));
        Assert.False(encoder.IsEncoded("just some plain text"));
    }

    [Fact]
    public void Decode_NotOurFormat_ReturnsNull_NoLog()
    {
        var encoder = NewEncoder(out FakeLogger log);

        Assert.Null(encoder.Decode("plain text, not encoded"));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Decode_Tampered_StillReturnsJson_AndLogsWarn()
    {
        var encoder = NewEncoder(out FakeLogger log);
        string json = "{\"hp\":7}";
        string encoded = encoder.Encode(json);

        // Flip the last character of the base64 payload (after the 2nd separator).
        int lastSep = encoded.LastIndexOf(':');
        char flipped = encoded[^1] == 'A' ? 'B' : 'A';
        string tampered = encoded[..^1] + flipped;

        string? decoded = encoder.Decode(tampered);

        Assert.NotNull(decoded);                 // lenient: data recovered
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warn, log.Entries[0].Level);
        Assert.Contains("HMAC mismatch", log.Entries[0].Message);
    }

    [Fact]
    public void Decode_MalformedMissingSeparator_ReturnsNull_AndLogsError()
    {
        var encoder = NewEncoder(out FakeLogger log);

        // Has the prefix + one separator, but no second separator.
        string? decoded = encoder.Decode(Prefix + ":deadbeef");

        Assert.Null(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, log.Entries[0].Level);
    }

    [Fact]
    public void Decode_CorruptBase64_ReturnsNull_AndLogsError()
    {
        var encoder = NewEncoder(out FakeLogger log);

        // Valid prefix + a (wrong) hmac + an invalid base64 body.
        string? decoded = encoder.Decode(Prefix + ":00:!!!not-base64!!!");

        Assert.Null(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, log.Entries[0].Level);
    }

    [Fact]
    public void Encode_DiffersByPrefixAndKey()
    {
        var a = new SaveEncoder(Key, "AAA1", new FakeLogger());
        var b = new SaveEncoder(Key, "BBB1", new FakeLogger());
        var c = new SaveEncoder(Encoding.UTF8.GetBytes("other-key"), "AAA1", new FakeLogger());

        string json = "{}";
        Assert.StartsWith("AAA1:", a.Encode(json));
        Assert.StartsWith("BBB1:", b.Encode(json));
        Assert.NotEqual(a.Encode(json), c.Encode(json)); // same prefix, different key -> different hmac
    }

    [Fact]
    public void Decode_WithWrongKey_LogsMismatch()
    {
        string encoded = new SaveEncoder(Key, Prefix, new FakeLogger()).Encode("{\"x\":1}");

        var log = new FakeLogger();
        var wrong = new SaveEncoder(Encoding.UTF8.GetBytes("WRONG-key"), Prefix, log);
        string? decoded = wrong.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warn, log.Entries[0].Level);
    }

    [Fact]
    public void Ctor_NullKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SaveEncoder(null!, Prefix, new FakeLogger()));
    }

    [Fact]
    public void Ctor_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SaveEncoder(Array.Empty<byte>(), Prefix, new FakeLogger()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidPrefix_Throws(string? badPrefix)
    {
        Assert.Throws<ArgumentException>(() => new SaveEncoder(Key, badPrefix!, new FakeLogger()));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SaveEncoder(Key, Prefix, null!));
    }
}

/// <summary>Captures log calls for assertions.</summary>
internal sealed class FakeLogger : ILogger
{
    public readonly record struct Entry(LogLevel Level, string Message);
    private readonly List<Entry> entries = new();
    public IReadOnlyList<Entry> Entries => entries;

    public string Category => "test";
    public bool IsEnabled(LogLevel level) => true;
    public void Log(LogLevel level, string message, Exception? exception = null) => entries.Add(new Entry(level, message));
    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);
    public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SaveEncoderTests" -v q`
Expected: FAIL — `SaveEncoder` does not exist in namespace `KhaozEngine.Persistence`.

- [ ] **Step 3: Write the implementation** (`KhaozEngine.Persistence/SaveEncoder.cs`):

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Encodes/decodes save data to deter casual tampering: Base64 for obfuscation plus an
/// HMAC-SHA256 integrity tag. File format: <c>{prefix}:{hmac-hex}:{base64-payload}</c>.
/// This is a deterrent, not real security: the HMAC key ships in the game binary. Decoding is
/// lenient (recovers the JSON even on an HMAC mismatch) and reports outcomes via the injected
/// <see cref="ILogger"/>.
/// </summary>
public sealed class SaveEncoder
{
    private const char Separator = ':';

    private readonly byte[] hmacKey;
    private readonly string magicPrefix;
    private readonly ILogger logger;

    /// <summary>Creates an encoder with the given HMAC key, magic prefix, and logger.</summary>
    /// <exception cref="ArgumentException"><paramref name="hmacKey"/> is null/empty, or <paramref name="magicPrefix"/> is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public SaveEncoder(byte[] hmacKey, string magicPrefix, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (hmacKey is null || hmacKey.Length == 0)
        {
            throw new ArgumentException("An HMAC key must be provided.", nameof(hmacKey));
        }
        if (string.IsNullOrWhiteSpace(magicPrefix))
        {
            throw new ArgumentException("A magic prefix must be provided.", nameof(magicPrefix));
        }

        this.hmacKey = (byte[])hmacKey.Clone();
        this.magicPrefix = magicPrefix;
        this.logger = logger;
    }

    /// <summary>Encodes a JSON string into the obfuscated save format.</summary>
    public string Encode(string json)
    {
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string hmac = ComputeHmac(base64);
        return $"{magicPrefix}{Separator}{hmac}{Separator}{base64}";
    }

    /// <summary>Returns true if <paramref name="fileContent"/> appears to be in the encoded format.</summary>
    public bool IsEncoded(string fileContent)
    {
        return fileContent.StartsWith(magicPrefix + Separator, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decodes an encoded save back to JSON. Returns null if it is not in the encoded format, is
    /// malformed, or has a corrupt payload. On an HMAC mismatch it still returns the JSON (lenient)
    /// and logs a warning. Outcomes are logged via the injected logger.
    /// </summary>
    public string? Decode(string fileContent)
    {
        if (!IsEncoded(fileContent))
        {
            return null; // not our format; quietly ignore (e.g. legacy plaintext save)
        }

        int firstSep = fileContent.IndexOf(Separator);
        int secondSep = fileContent.IndexOf(Separator, firstSep + 1);
        if (secondSep < 0)
        {
            logger.Error("[SaveEncoder] malformed encoded save (missing separator)");
            return null;
        }

        string hmac = fileContent[(firstSep + 1)..secondSep];
        string base64 = fileContent[(secondSep + 1)..];

        bool authentic = string.Equals(hmac, ComputeHmac(base64), StringComparison.OrdinalIgnoreCase);

        string json;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            json = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            logger.Error("[SaveEncoder] failed to decode Base64 payload");
            return null;
        }

        if (authentic)
        {
            logger.Info("[SaveEncoder] save decoded (HMAC ok)");
        }
        else
        {
            logger.Warn("[SaveEncoder] save decoded but HMAC mismatch - possible tampering");
        }

        return json;
    }

    private string ComputeHmac(string data)
    {
        using HMACSHA256 hmac = new(hmacKey);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexStringLower(hash);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SaveEncoderTests" -v q`
Expected: PASS — 14 passed (11 facts + a 3-case `Ctor_InvalidPrefix` theory).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/SaveEncoder.cs KhaozEngine.Tests/SaveEncoderTests.cs
git commit -m "Add KhaozEngine.Persistence.SaveEncoder (ILogger-based)"
```

---

## Task 3: Full suite green + isolated build

- [ ] **Step 1: Full suite** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: PASS — baseline (252) + 14 = 266, 0 failed. (Confirm baseline at start; delta +14.)

- [ ] **Step 2: Isolated package build** — `dotnet build KhaozEngine.Persistence/KhaozEngine.Persistence.csproj -v q`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

---

## Notes for the release / adopt phase (do NOT do here)

- End-of-batch 3.2.0: bump `<Version>`, one `CHANGELOG.md` entry, update `docs/CONSUMERS.md` (add the new `App` / `Localization` / `Persistence` packages), `dotnet pack -c Release -o ./local-feed`.
- Adopt: Nullwake builds `new SaveEncoder(Encoding.UTF8.GetBytes("Nullwake-SaveIntegrity-v1"), "NWSV1", Log.For<SaveEncoder>())`, deletes its copy.
