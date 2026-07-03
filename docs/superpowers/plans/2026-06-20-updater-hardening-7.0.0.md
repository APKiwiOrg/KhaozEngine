# Updater Hardening 7.0.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close all ten security/robustness findings in `KhaozEngine.Updates` (mandatory RSA-signed manifests, feed origin lock, path-traversal + reparse guards, downgrade enforcement, size/disk caps, macOS codesign re-verify) and ship them as engine **7.0.0**.

**Architecture:** The updater stays a check -> resumable staged download -> external-shim apply pipeline. We add a signing layer (RSA-2048 PKCS#1 v1.5 + SHA-256, pure BCL) verified over the raw manifest bytes before parsing; tighten the HTTP transport to https + same-origin; add input validation to the apply core; and route the new macOS codesign check + reparse check through the existing `IUpdaterEnvironment` seam so everything stays headless-testable. All security decisions read only signed fields.

**Tech Stack:** .NET 10, `System.Security.Cryptography` (RSA, SHA-256), `System.Text.Json`, xUnit. No new package dependency (`KhaozEngine.Updates` keeps depending only on `KhaozEngine.Diagnostics`).

**Spec:** `docs/superpowers/specs/2026-06-20-updater-hardening-7.0.0-design.md`

**Conventions:**
- Build/test: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Run a single test class: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ManifestSigningTests"`
- All source files start with `#nullable enable` and `namespace KhaozEngine.Updates;` (file-scoped).
- Tests live under `KhaozEngine.Tests/Updates/`, namespace `KhaozEngine.Tests.Updates`.
- Commit subjects: `area(scope): summary`. Use plain `updates:` scope for per-task commits within the batch; the single version-bump commit at the end uses `updates(7.0.0):`.

---

## File Structure

**New source files (`KhaozEngine.Updates/`):**
- `ManifestSigning.cs` - `ManifestSigner` (sign + keygen) and `ManifestVerifier` (verify against a key list). Pure crypto, no IO.

**Modified source files (`KhaozEngine.Updates/`):**
- `UpdateManifest.cs` - add signed `Required` field.
- `IUpdateSource.cs` - replace `DownloadManifestAsync` with `DownloadBytesAsync`; add `maxBytes` to `DownloadFileAsync`.
- `HttpUpdateSource.cs` - implement `DownloadBytesAsync`, origin lock (https + same host), `maxBytes` streaming guard.
- `UpdateServiceOptions.cs` - add `TrustedPublicKeys` (required), `MaxFileBytes`, `MaxTotalDownloadBytes`.
- `UpdateService.cs` - mandatory signature verify, signed downgrade enforcement, signed `Required`, size + free-disk caps.
- `UpdateApplier.cs` - new `ApplyOutcome.AbortedUnsafePath`, shared path validator, pre-flight rejection, dest-reparse handling, deferred-rollback codesign check.
- `IUpdaterEnvironment.cs` - add `IsReparsePoint` and `VerifyCodeSignature`.
- `SystemUpdaterEnvironment.cs` - real `IsReparsePoint` + `VerifyCodeSignature` (codesign -v on macOS).

**Modified test files (`KhaozEngine.Tests/Updates/`):**
- `FakeUpdateSource.cs` - serve raw manifest + signature bytes; honour `maxBytes`.
- `FakeUpdaterEnvironment.cs` - settable `IsReparsePoint` set + `VerifyCodeSignature` bool.
- `UpdateServiceTests.cs` - sign every fixture manifest; add signing/downgrade/size tests.
- `UpdateApplierTests.cs` - path-traversal, reparse, codesign tests.
- `HttpUpdateSourceTests.cs` - origin-lock tests.
- New `ManifestSigningTests.cs` - sign/verify/rotation/keygen.

**Modified non-code:**
- `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

---

## Task 1: Manifest signing primitives

RSA-2048 PKCS#1 v1.5 over SHA-256, signing/verifying the **raw manifest bytes**. Public, because publish-side tools (and tests) use the signer.

**Files:**
- Create: `KhaozEngine.Updates/ManifestSigning.cs`
- Create: `KhaozEngine.Tests/Updates/ManifestSigningTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Updates/ManifestSigningTests.cs`:

```csharp
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class ManifestSigningTests
{
    private static (string privPem, string pubPem) NewKeyPair()
    {
        using RSA rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void SignedBytes_VerifyAgainstMatchingKey()
    {
        (string priv, string pub) = NewKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("{\"version\":\"2.0.0\"}");

        byte[] sig = ManifestSigner.Sign(data, priv);

        Assert.True(ManifestVerifier.Verify(data, sig, new[] { pub }));
    }

    [Fact]
    public void WrongKey_FailsVerification()
    {
        (string priv, _) = NewKeyPair();
        (_, string otherPub) = NewKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("payload");

        byte[] sig = ManifestSigner.Sign(data, priv);

        Assert.False(ManifestVerifier.Verify(data, sig, new[] { otherPub }));
    }

    [Fact]
    public void TamperedData_FailsVerification()
    {
        (string priv, string pub) = NewKeyPair();
        byte[] sig = ManifestSigner.Sign(Encoding.UTF8.GetBytes("original"), priv);

        Assert.False(ManifestVerifier.Verify(Encoding.UTF8.GetBytes("tampered"), sig, new[] { pub }));
    }

    [Fact]
    public void RotationKeyList_AcceptsAnyTrustedKey()
    {
        (string oldPriv, string oldPub) = NewKeyPair();
        (string newPriv, string newPub) = NewKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("payload");

        byte[] sigByNew = ManifestSigner.Sign(data, newPriv);

        // Client trusts BOTH keys during a rotation window; a sig from either is accepted.
        var trusted = new List<string> { oldPub, newPub };
        Assert.True(ManifestVerifier.Verify(data, sigByNew, trusted));
        Assert.True(ManifestVerifier.Verify(data, ManifestSigner.Sign(data, oldPriv), trusted));
    }

    [Fact]
    public void GenerateKeyPair_RoundTripsThroughSignVerify()
    {
        ManifestKeyPair kp = ManifestSigner.GenerateKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("payload");

        byte[] sig = ManifestSigner.Sign(data, kp.PrivateKeyPem);

        Assert.True(ManifestVerifier.Verify(data, sig, new[] { kp.PublicKeyPem }));
    }

    [Fact]
    public void Verify_NoKeys_ReturnsFalse()
    {
        byte[] data = Encoding.UTF8.GetBytes("payload");
        Assert.False(ManifestVerifier.Verify(data, new byte[] { 1, 2, 3 }, System.Array.Empty<string>()));
    }

    [Fact]
    public void Verify_GarbageSignature_ReturnsFalseNotThrow()
    {
        (_, string pub) = NewKeyPair();
        Assert.False(ManifestVerifier.Verify(Encoding.UTF8.GetBytes("x"), new byte[] { 0xFF }, new[] { pub }));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ManifestSigningTests"`
Expected: FAIL to compile - `ManifestSigner`, `ManifestVerifier`, `ManifestKeyPair` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Updates/ManifestSigning.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>An RSA key pair in PEM form: a PKCS#1 private key and a SubjectPublicKeyInfo public key.</summary>
public sealed record ManifestKeyPair(string PrivateKeyPem, string PublicKeyPem);

/// <summary>
/// Publish-side manifest signing. The private key signs the exact manifest bytes with RSA-2048
/// PKCS#1 v1.5 over SHA-256; the detached signature ships as <c>manifest.json.sig</c> (base64).
/// Pure BCL, so the Updates package keeps its near-zero-dependency footprint.
/// </summary>
public static class ManifestSigner
{
    /// <summary>Generates a fresh RSA-2048 key pair (private PKCS#1 PEM + public SPKI PEM).</summary>
    public static ManifestKeyPair GenerateKeyPair()
    {
        using RSA rsa = RSA.Create(2048);
        return new ManifestKeyPair(rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Signs <paramref name="data"/> with the PKCS#1 PEM private key. Returns the raw signature.</summary>
    public static byte[] Sign(byte[] data, string privateKeyPem)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}

/// <summary>
/// Client-side manifest verification. A signature is accepted if it validates against ANY of the
/// trusted public keys, which is what makes key rotation a config change (ship the new key alongside
/// the old, switch the signer, drop the old key later). Never throws: any malformed key or signature
/// is a verification failure.
/// </summary>
public static class ManifestVerifier
{
    /// <summary>
    /// True when <paramref name="signature"/> is a valid RSA-2048/SHA-256/PKCS#1 signature of
    /// <paramref name="data"/> under at least one key in <paramref name="trustedPublicKeysPem"/>.
    /// </summary>
    public static bool Verify(byte[] data, byte[] signature, IEnumerable<string> trustedPublicKeysPem)
    {
        foreach (string pem in trustedPublicKeysPem)
        {
            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                if (rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                // Malformed key or signature: treat as a non-match, try the next key.
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ManifestSigningTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/ManifestSigning.cs KhaozEngine.Tests/Updates/ManifestSigningTests.cs
git commit -m "updates: RSA-2048 manifest sign/verify primitives"
```

---

## Task 2: Add signed `Required` field to the manifest

`Required` must be a signed field so the "mandatory update" flag can't be forged by a feed that controls the unsigned `/latest` response.

**Files:**
- Modify: `KhaozEngine.Updates/UpdateManifest.cs:17-29`
- Modify: `KhaozEngine.Tests/Updates/UpdateManifestTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `KhaozEngine.Tests/Updates/UpdateManifestTests.cs` (inside the existing test class):

```csharp
    [Fact]
    public void Manifest_RequiredFlag_RoundTripsThroughJson()
    {
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64", Required = true };

        UpdateManifest? parsed = UpdateManifest.Deserialize(manifest.Serialize());

        Assert.NotNull(parsed);
        Assert.True(parsed!.Required);
    }

    [Fact]
    public void Manifest_RequiredFlag_DefaultsFalseWhenAbsent()
    {
        UpdateManifest? parsed = UpdateManifest.Deserialize("{\"version\":\"2.0.0\",\"platform\":\"win-x64\",\"files\":[]}");

        Assert.NotNull(parsed);
        Assert.False(parsed!.Required);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateManifestTests"`
Expected: FAIL to compile - `UpdateManifest` has no `Required` property.

- [ ] **Step 3: Write minimal implementation**

In `KhaozEngine.Updates/UpdateManifest.cs`, add the property after `PublishedAtUtc` (line 26):

```csharp
    [JsonPropertyName("required")]
    public bool Required { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateManifestTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/UpdateManifest.cs KhaozEngine.Tests/Updates/UpdateManifestTests.cs
git commit -m "updates: add signed Required flag to manifest"
```

---

## Task 3: Transport seam changes (`DownloadBytesAsync`, `maxBytes`)

Signature verification needs the **raw manifest bytes** (and the `.sig` bytes), so replace the parse-on-fetch `DownloadManifestAsync` with a byte fetcher. Add a streaming size cap to `DownloadFileAsync`. This task only changes signatures + the fakes so the suite compiles; behavior wiring is Tasks 4-5.

**Files:**
- Modify: `KhaozEngine.Updates/IUpdateSource.cs:26-46`
- Modify: `KhaozEngine.Updates/HttpUpdateSource.cs:81-128`
- Modify: `KhaozEngine.Updates/UpdateService.cs:107,206`
- Modify: `KhaozEngine.Tests/Updates/FakeUpdateSource.cs`

- [ ] **Step 1: Update the interface**

In `KhaozEngine.Updates/IUpdateSource.cs`, replace the `DownloadManifestAsync` member (lines 31-32) with:

```csharp
    /// <summary>
    /// Downloads the raw bytes at <paramref name="url"/> (the manifest or its detached signature).
    /// Returns null on any transport/IO error. Implementations MUST reject a URL that is not https
    /// or not same-origin with their configured base (see <see cref="HttpUpdateSource"/>).
    /// </summary>
    Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken = default);
```

And change `DownloadFileAsync` (line 38) to add the cap:

```csharp
    /// <summary>
    /// Streams a single file to <paramref name="destPath"/>, reporting cumulative bytes, aborting if
    /// more than <paramref name="maxBytes"/> arrive (a hostile/oversized payload guard). Returns false
    /// on any transport/IO error or overrun so the caller can retry.
    /// </summary>
    Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default);
```

Add `using System.Threading;` is already present; no other change.

- [ ] **Step 2: Update FakeUpdateSource so the suite compiles + serves bytes**

In `KhaozEngine.Tests/Updates/FakeUpdateSource.cs`, replace the `DownloadManifestAsync` method (lines 29-30) and add raw-bytes support. Replace:

```csharp
    public Task<UpdateManifest?> DownloadManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(RemoteManifest);
```

with:

```csharp
    /// <summary>Raw bytes keyed by URL: the manifest JSON and its ".sig" live here.</summary>
    public readonly Dictionary<string, byte[]> Bytes = new(StringComparer.Ordinal);

    public Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        => Task.FromResult(Bytes.TryGetValue(url, out byte[]? b) ? b : null);
```

Change `DownloadFileAsync` (line 34) signature to include `long maxBytes`:

```csharp
    public Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
```

(The body is unchanged; the fake does not enforce the cap.)

- [ ] **Step 3: Implement on HttpUpdateSource**

In `KhaozEngine.Updates/HttpUpdateSource.cs`, replace `DownloadManifestAsync` (lines 81-92) with `DownloadBytesAsync`:

```csharp
    public async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedOrigin(url))
        {
            log.Info($"Refusing off-origin or non-https URL: {url}");
            return null;
        }

        try
        {
            return await httpClient.GetByteArrayAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            log.Info($"Download failed ({url}): {ex.Message}");
            return null;
        }
    }
```

Change `DownloadFileAsync` (line 94) to take and enforce `maxBytes`, and origin-check the URL. Replace the method signature and the streaming loop:

```csharp
    public async Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedOrigin(fileUrl))
        {
            log.Info($"Refusing off-origin or non-https file URL: {fileUrl}");
            return false;
        }

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                fileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string? destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            byte[] buffer = new byte[options.DownloadBufferSize];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > maxBytes)
                {
                    log.Info($"File exceeded size cap ({maxBytes} bytes), aborting: {fileUrl}");
                    return false;
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesProgress?.Report(totalBytesRead);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log.Info($"File download failed ({fileUrl}): {ex.Message}");
            return false;
        }
    }
```

Add a private origin check near the bottom of the class (above `Dispose`). The real logic comes in Task 4; for now add a stub that compiles and admits everything so this task's change is signature-only:

```csharp
    // Implemented in Task 4. Placeholder admits all so Task 3 is purely the transport-shape change.
    private bool IsAllowedOrigin(string url) => true;
```

- [ ] **Step 4: Fix the two UpdateService call sites so it compiles**

In `KhaozEngine.Updates/UpdateService.cs`, the manifest fetch (line 107) and the file download (line 206) must change. For now make them compile; the verification/cap wiring is Task 5.

Replace line 107:

```csharp
            UpdateManifest? remoteManifest = await source.DownloadManifestAsync(latest.ManifestUrl, cancellationToken);
```

with:

```csharp
            byte[]? manifestBytes = await source.DownloadBytesAsync(latest.ManifestUrl, cancellationToken);
            UpdateManifest? remoteManifest = manifestBytes is null
                ? null
                : UpdateManifest.Deserialize(System.Text.Encoding.UTF8.GetString(manifestBytes));
```

Replace line 206:

```csharp
                    success = await source.DownloadFileAsync(fileUrl, destPath, progress, cancellationToken);
```

with (the real per-file cap from options arrives in Task 5; `long.MaxValue` keeps current behavior for now):

```csharp
                    success = await source.DownloadFileAsync(fileUrl, destPath, long.MaxValue, progress, cancellationToken);
```

- [ ] **Step 5: Run the full Updates suite to verify it still compiles + passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~KhaozEngine.Tests.Updates"`
Expected: existing tests still PASS (behavior unchanged; only shapes moved). Note: `UpdateServiceTests` will be re-pointed at the new bytes-based manifest path in Task 5; if any fail now because they set `RemoteManifest` but not `Bytes`, that is expected and fixed in Task 5. If so, proceed - do not "fix" by reverting.

Reality check: `UpdateServiceTests` currently relies on `RemoteManifest`. After Step 4, the service reads `DownloadBytesAsync`, so those tests WILL fail here. That is the expected hand-off into Task 5. Commit this task's transport change first.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Updates/IUpdateSource.cs KhaozEngine.Updates/HttpUpdateSource.cs KhaozEngine.Updates/UpdateService.cs KhaozEngine.Tests/Updates/FakeUpdateSource.cs
git commit -m "updates: raw-bytes manifest fetch + download size cap seam"
```

---

## Task 4: HTTP origin lock (https + same host)

Reject any manifest/sig/file URL that is not https or whose host differs from the configured `ServerBaseUrl`. This stops a feed response from redirecting downloads to an attacker host.

**Files:**
- Modify: `KhaozEngine.Updates/HttpUpdateSource.cs` (the `IsAllowedOrigin` stub from Task 3, and store the base host)
- Modify: `KhaozEngine.Tests/Updates/HttpUpdateSourceTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `KhaozEngine.Tests/Updates/HttpUpdateSourceTests.cs` (inside the existing class):

```csharp
    private static HttpUpdateSource Source(string baseUrl)
        => new(new HttpUpdateSourceOptions { ServerBaseUrl = baseUrl });

    [Fact]
    public async Task DownloadBytes_OffOriginHost_ReturnsNull()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        byte[]? result = await src.DownloadBytesAsync("https://attacker.com/manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadBytes_NonHttps_ReturnsNull()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        byte[]? result = await src.DownloadBytesAsync("http://updates.example.com/manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadFile_OffOriginHost_ReturnsFalse()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        bool ok = await src.DownloadFileAsync("https://attacker.com/game.dll", Path.Combine(Path.GetTempPath(), "x.bin"), long.MaxValue);

        Assert.False(ok);
    }
```

Ensure the file has `using System.IO;`, `using System.Threading.Tasks;`, and `using KhaozEngine.Updates;` at the top (add any that are missing).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~HttpUpdateSourceTests"`
Expected: FAIL - the placeholder `IsAllowedOrigin` admits everything, so off-origin URLs are not rejected (the calls will instead attempt a real network fetch and the assertions on `null`/`false` may pass or hang). Treat a non-deterministic pass as failure until the real check exists.

- [ ] **Step 3: Implement the origin check**

In `KhaozEngine.Updates/HttpUpdateSource.cs`, store the base host in the constructor. Add a field near the other fields (after line 45):

```csharp
    private readonly string? baseHost;
```

In the constructor (after line 49, `this.options = options;`), add:

```csharp
        Uri? baseUri = HttpUpdateSource.ParseBase(options.ServerBaseUrl);
        baseHost = baseUri?.Host;
```

Replace the placeholder `IsAllowedOrigin` (from Task 3) with the real implementation, plus a base parser:

```csharp
    /// <summary>True only when <paramref name="url"/> is absolute https on the configured base host.</summary>
    private bool IsAllowedOrigin(string url)
    {
        if (baseHost is null)
        {
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, baseHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the configured base into an absolute https Uri (bare host implies https).</summary>
    private static Uri? ParseBase(string serverBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl))
        {
            return null;
        }
        string normalized = serverBaseUrl.Trim().TrimEnd('/');
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ? uri : null;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~HttpUpdateSourceTests"`
Expected: PASS (existing `BuildLatestVersionUrl` tests plus the 3 new origin tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/HttpUpdateSource.cs KhaozEngine.Tests/Updates/HttpUpdateSourceTests.cs
git commit -m "updates: lock feed transport to https + same origin"
```

---

## Task 5: Mandatory signing + downgrade + size caps in UpdateService

Wire the client: require at least one trusted key, fetch + verify the `.sig` over the raw manifest bytes before parsing, enforce a strict downgrade check against the **signed** version, read the signed `Required`, and apply per-file/total/free-disk caps.

**Files:**
- Modify: `KhaozEngine.Updates/UpdateServiceOptions.cs`
- Modify: `KhaozEngine.Updates/UpdateService.cs`
- Modify: `KhaozEngine.Tests/Updates/UpdateServiceTests.cs`
- Modify: `KhaozEngine.Tests/Updates/FakeUpdateSource.cs` (helper to publish a signed fixture)

- [ ] **Step 1: Add the options**

In `KhaozEngine.Updates/UpdateServiceOptions.cs`, add after `Source` (line 16):

```csharp
    /// <summary>
    /// Trusted RSA public keys (SubjectPublicKeyInfo PEM) for manifest signatures. At least one is
    /// REQUIRED; constructing <see cref="UpdateService"/> with none throws. A list so keys can be
    /// rotated (ship the new key beside the old, switch the signer, drop the old key later).
    /// </summary>
    public required System.Collections.Generic.IReadOnlyList<string> TrustedPublicKeys { get; init; }
```

And add the caps after `MaxDownloadRetries` (line 37):

```csharp
    /// <summary>Per-file download size cap (hostile/oversized payload guard). Default 4 GiB.</summary>
    public long MaxFileBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Total download size cap across all changed files. Default 16 GiB.</summary>
    public long MaxTotalDownloadBytes { get; init; } = 16L * 1024 * 1024 * 1024;
```

- [ ] **Step 2: Write the failing tests**

In `KhaozEngine.Tests/Updates/UpdateServiceTests.cs`, add a signing helper to `FakeUpdateSource` first. In `KhaozEngine.Tests/Updates/FakeUpdateSource.cs`, add:

```csharp
    /// <summary>
    /// Publishes a signed manifest: stores its raw JSON bytes at <paramref name="manifestUrl"/> and a
    /// detached signature at "<paramref name="manifestUrl"/>.sig", and sets <see cref="Latest"/>.
    /// </summary>
    public void PublishSigned(UpdateManifest manifest, string manifestUrl, string privateKeyPem, bool required = false)
    {
        byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest.Serialize());
        Bytes[manifestUrl] = manifestBytes;
        Bytes[manifestUrl + ".sig"] = ManifestSigner.Sign(manifestBytes, privateKeyPem);
        RemoteManifest = manifest;
        Latest = new LatestVersionInfo(manifest.Version, manifest.Version, manifestUrl, required);
    }
```

Then in `UpdateServiceTests.cs`, add a shared key + helper and new tests. Add at the top of the test class:

```csharp
    private static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    private static string PrivPem => SignKey.ExportRSAPrivateKeyPem();
    private static string PubPem => SignKey.ExportSubjectPublicKeyInfoPem();

    private static UpdateServiceOptions SignedOptions(FakeUpdateSource src, string appData, string install, string current, params string[] trustedKeys)
        => new()
        {
            Source = src,
            CurrentVersion = current,
            AppDataDir = appData,
            InstallDir = install,
            Platform = "win-x64",
            UpdaterExecutableName = "TestUpdater",
            TrustedPublicKeys = trustedKeys.Length == 0 ? new[] { PubPem } : trustedKeys,
            LaunchUpdater = (_, _) => true,
            ExitProcess = () => { }
        };
```

Add these tests (match the existing tests' use of temp dirs; reuse whatever setup pattern the file already has - if it uses a per-test temp dir helper, use it):

```csharp
    [Fact]
    public void Ctor_NoTrustedKeys_Throws()
    {
        var src = new FakeUpdateSource();
        Assert.ThrowsAny<System.Exception>(() => new UpdateService(new UpdateServiceOptions
        {
            Source = src,
            CurrentVersion = "1.0.0",
            AppDataDir = NewTempDir(),
            TrustedPublicKeys = System.Array.Empty<string>()
        }));
    }

    [Fact]
    public async Task Check_UnsignedManifest_DoesNotOfferUpdate()
    {
        string appData = NewTempDir(), install = NewTempDir();
        var src = new FakeUpdateSource();
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        manifest.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = src.Add("game.dll", "v2"), Size = 2 });
        // No signature published.
        src.Bytes["https://u.example.com/2.0.0/manifest.json"] = System.Text.Encoding.UTF8.GetBytes(manifest.Serialize());
        src.Latest = new LatestVersionInfo("2.0.0", "2.0.0", "https://u.example.com/2.0.0/manifest.json", false);

        using var svc = new UpdateService(SignedOptions(src, appData, install, "1.0.0"));
        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_WrongKeySignature_DoesNotOfferUpdate()
    {
        string appData = NewTempDir(), install = NewTempDir();
        using System.Security.Cryptography.RSA attacker = System.Security.Cryptography.RSA.Create(2048);
        var src = new FakeUpdateSource();
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        manifest.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = src.Add("game.dll", "v2"), Size = 2 });
        src.PublishSigned(manifest, "https://u.example.com/2.0.0/manifest.json", attacker.ExportRSAPrivateKeyPem());

        using var svc = new UpdateService(SignedOptions(src, appData, install, "1.0.0")); // trusts PubPem, not attacker
        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_ValidSignature_OffersUpdate()
    {
        string appData = NewTempDir(), install = NewTempDir();
        var src = new FakeUpdateSource();
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        manifest.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = src.Add("game.dll", "v2"), Size = 2 });
        src.PublishSigned(manifest, "https://u.example.com/2.0.0/manifest.json", PrivPem);

        using var svc = new UpdateService(SignedOptions(src, appData, install, "1.0.0"));
        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal("2.0.0", svc.RemoteVersion);
    }

    [Fact]
    public async Task Check_SignedRequiredFlag_IsUsed_NotTheLatestResponse()
    {
        string appData = NewTempDir(), install = NewTempDir();
        var src = new FakeUpdateSource();
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64", Required = true };
        manifest.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = src.Add("game.dll", "v2"), Size = 2 });
        // Publish with required=true in the SIGNED manifest, but required=false in the unsigned Latest.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(manifest.Serialize());
        src.Bytes["https://u.example.com/2.0.0/manifest.json"] = bytes;
        src.Bytes["https://u.example.com/2.0.0/manifest.json.sig"] = ManifestSigner.Sign(bytes, PrivPem);
        src.Latest = new LatestVersionInfo("2.0.0", "2.0.0", "https://u.example.com/2.0.0/manifest.json", false);

        using var svc = new UpdateService(SignedOptions(src, appData, install, "1.0.0"));
        await svc.CheckForUpdateAsync();

        Assert.True(svc.IsRequired); // from signed manifest, not the unsigned latest=false
    }

    [Fact]
    public async Task Check_Downgrade_IsRejected_EvenIfSigned()
    {
        string appData = NewTempDir(), install = NewTempDir();
        var src = new FakeUpdateSource();
        var manifest = new UpdateManifest { Version = "1.0.0", Platform = "win-x64" };
        src.PublishSigned(manifest, "https://u.example.com/1.0.0/manifest.json", PrivPem);

        using var svc = new UpdateService(SignedOptions(src, appData, install, "2.0.0")); // current newer than signed
        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_FileOverCap_DoesNotOfferUpdate()
    {
        string appData = NewTempDir(), install = NewTempDir();
        var src = new FakeUpdateSource();
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        manifest.Files.Add(new ManifestFileEntry { Path = "huge.bin", Sha256 = src.Add("huge.bin", "x"), Size = 5_000_000_000 });
        src.PublishSigned(manifest, "https://u.example.com/2.0.0/manifest.json", PrivPem);

        // UpdateServiceOptions is a class with init setters (not a record), so build it inline here
        // to override MaxFileBytes rather than reusing the SignedOptions helper.
        using var svc = new UpdateService(new UpdateServiceOptions
        {
            Source = src, CurrentVersion = "1.0.0", AppDataDir = appData, InstallDir = install,
            Platform = "win-x64", UpdaterExecutableName = "TestUpdater",
            TrustedPublicKeys = new[] { PubPem }, MaxFileBytes = 1_000_000,
            LaunchUpdater = (_, _) => true, ExitProcess = () => { }
        });
        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }
```

Add a `NewTempDir()` helper to the class if the file does not already have one:

```csharp
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-upd-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
```

Also update any EXISTING `UpdateServiceTests` that build `UpdateServiceOptions` without `TrustedPublicKeys` (now required) and that set `src.RemoteManifest` for the check path: switch them to `src.PublishSigned(manifest, manifestUrl, PrivPem)` and add `TrustedPublicKeys = new[] { PubPem }`. The download/apply-focused tests that start from `UpdateState.UpdateAvailable` still work once the check path is signed.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateServiceTests"`
Expected: FAIL - `TrustedPublicKeys` not consumed, no signature check, no cap, no signed-version downgrade/required wiring.

- [ ] **Step 4: Implement in UpdateService**

In `KhaozEngine.Updates/UpdateService.cs`:

(a) Add fields after line 31 (`maxRetries`):

```csharp
    private readonly System.Collections.Generic.IReadOnlyList<string> trustedKeys;
    private readonly long maxFileBytes;
    private readonly long maxTotalDownloadBytes;
```

(b) In the constructor (after line 72, `maxRetries = ...`), add the mandatory-key guard + cap reads:

```csharp
        trustedKeys = options.TrustedPublicKeys;
        if (trustedKeys is null || trustedKeys.Count == 0)
        {
            throw new ArgumentException(
                "UpdateServiceOptions.TrustedPublicKeys must contain at least one RSA public key; " +
                "unsigned updates are not supported.", nameof(options));
        }
        maxFileBytes = options.MaxFileBytes;
        maxTotalDownloadBytes = options.MaxTotalDownloadBytes;
```

(c) Replace the manifest-fetch block (the lines added in Task 3 Step 4, originally line 107) with a verify-before-parse block. After the `latest is null` / `IsNewer(currentVersion, latest.Version)` guards, replace the manifest acquisition:

```csharp
            byte[]? manifestBytes = await source.DownloadBytesAsync(latest.ManifestUrl, cancellationToken);
            byte[]? signature = await source.DownloadBytesAsync(latest.ManifestUrl + ".sig", cancellationToken);
            if (manifestBytes is null || signature is null)
            {
                SetState(UpdateState.Idle);
                return;
            }

            if (!ManifestVerifier.Verify(manifestBytes, signature, trustedKeys))
            {
                log.Warn($"Manifest signature INVALID for {latest.Version}; refusing update.");
                SetState(UpdateState.Idle);
                return;
            }

            UpdateManifest? remoteManifest = UpdateManifest.Deserialize(System.Text.Encoding.UTF8.GetString(manifestBytes));
            if (remoteManifest is null)
            {
                SetState(UpdateState.Idle);
                return;
            }

            // Trust only signed fields for security decisions: re-check the downgrade gate against the
            // signed version (not the unsigned `latest`), and take Required from the signed manifest.
            if (!UpdateVersion.IsNewer(currentVersion, remoteManifest.Version))
            {
                log.Info($"Signed manifest version {remoteManifest.Version} not newer than {currentVersion}; ignoring.");
                SetState(UpdateState.Idle);
                return;
            }

            // Reject a hostile/oversized manifest before doing any work.
            long declaredTotal = 0;
            for (int i = 0; i < remoteManifest.Files.Count; i++)
            {
                long size = remoteManifest.Files[i].Size;
                if (size < 0 || size > maxFileBytes)
                {
                    log.Warn($"Manifest file {remoteManifest.Files[i].Path} size {size} exceeds cap {maxFileBytes}; refusing.");
                    SetState(UpdateState.Idle);
                    return;
                }
                declaredTotal += size;
            }
            if (declaredTotal > maxTotalDownloadBytes)
            {
                log.Warn($"Manifest total {declaredTotal} exceeds cap {maxTotalDownloadBytes}; refusing.");
                SetState(UpdateState.Idle);
                return;
            }
```

(d) Keep the staged-manifest bytes for exact persistence. Add a field after line 36 (`pendingRemoteManifest`):

```csharp
    private byte[]? pendingManifestBytes;
```

After computing `remoteManifest` above, also stash the bytes where `pendingRemoteManifest` is set (around line 142):

```csharp
            pendingManifestBytes = manifestBytes;
```

Then in `StartDownloadAsync`, replace the staged-manifest write (lines 229-240) to persist the exact signed bytes when available:

```csharp
            // Persist the exact signed manifest bytes so the installed local manifest matches what was
            // verified (falls back to re-serialization if bytes are unavailable).
            try
            {
                string stagedManifestPath = Path.Combine(stagingDir, "manifest.json");
                if (pendingManifestBytes is not null)
                {
                    File.WriteAllBytes(stagedManifestPath, pendingManifestBytes);
                }
                else if (pendingRemoteManifest is not null)
                {
                    File.WriteAllText(stagedManifestPath, pendingRemoteManifest.Serialize());
                }
            }
            catch (Exception ex)
            {
                log.Info($"Could not write staged manifest: {ex.Message}");
            }
```

(e) Change the `required` assignment (line 146) from the unsigned latest to the signed manifest:

```csharp
            required = remoteManifest.Required;
```

(f) Add the per-file cap + free-disk check to the download. In `StartDownloadAsync`, right after `Directory.CreateDirectory(stagingDir);` (line 184), add the free-disk guard:

```csharp
            if (!HasEnoughFreeSpace(stagingDir, totalDownloadBytes))
            {
                SetError("Not enough free disk space to download the update.");
                return;
            }
```

Change the download call (line 206, set to `long.MaxValue` in Task 3) to pass the per-file cap:

```csharp
                    success = await source.DownloadFileAsync(fileUrl, destPath, maxFileBytes, progress, cancellationToken);
```

Add the helper near `VerifyFileHash` (bottom of the class):

```csharp
    private bool HasEnoughFreeSpace(string stagingDir, long needed)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(stagingDir));
            if (string.IsNullOrEmpty(root))
            {
                return true; // cannot determine; do not block
            }
            long available = new DriveInfo(root).AvailableFreeSpace;
            return available >= needed + (needed / 10); // 10% headroom
        }
        catch
        {
            return true; // never block an update on a disk-probe failure
        }
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateServiceTests"`
Expected: PASS (updated existing tests + new signing/downgrade/required/cap tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Updates/UpdateServiceOptions.cs KhaozEngine.Updates/UpdateService.cs KhaozEngine.Tests/Updates/UpdateServiceTests.cs KhaozEngine.Tests/Updates/FakeUpdateSource.cs
git commit -m "updates: mandatory signed manifests + downgrade + size/disk caps"
```

---

## Task 6: Path-traversal + reparse guards in the apply core

Reject unsafe relative paths (absolute, drive-letter, `..`, null byte, or anything resolving outside the install dir) in both the copy and delete lists, before touching the install. Replace a destination that is a reparse point rather than writing through it; abort if a staged source is a reparse point.

**Files:**
- Modify: `KhaozEngine.Updates/UpdateApplier.cs`
- Modify: `KhaozEngine.Updates/IUpdaterEnvironment.cs`
- Modify: `KhaozEngine.Tests/Updates/FakeUpdaterEnvironment.cs`
- Modify: `KhaozEngine.Tests/Updates/UpdateApplierTests.cs`

- [ ] **Step 1: Add the env seam for reparse detection**

In `KhaozEngine.Updates/IUpdaterEnvironment.cs`, add after `ClearQuarantine` (line 28):

```csharp
    /// <summary>True when <paramref name="path"/> exists and is a symlink/reparse point.</summary>
    bool IsReparsePoint(string path);
```

In `KhaozEngine.Tests/Updates/FakeUpdaterEnvironment.cs`, add a settable set and the method (after `ClearQuarantine`, line 67):

```csharp
    public readonly HashSet<string> ReparsePoints = new(StringComparer.Ordinal);

    public bool IsReparsePoint(string path) => ReparsePoints.Contains(path);
```

- [ ] **Step 2: Write the failing tests**

Add to `KhaozEngine.Tests/Updates/UpdateApplierTests.cs`:

```csharp
    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("../../escape.dll")]
    [InlineData("sub/../../escape.dll")]
    public void Apply_UnsafeCopyPath_AbortsBeforeTouchingInstall(string badPath)
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll", badPath }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // untouched
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);   // old version relaunched
    }

    [Fact]
    public void Apply_UnsafeDeletePath_AbortsBeforeTouchingInstall()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(
            Config(new() { "game.dll" }, new List<string> { "../../secret" }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]);
    }

    [Fact]
    public void Apply_StagedSourceIsReparsePoint_Aborts()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.ReparsePoints.Add(StagingPath("game.dll"));
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
    }

    [Fact]
    public void Apply_DestIsReparsePoint_RemovesLinkThenCopies()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "link";          // pretend this is a symlink
        env.ReparsePoints.Add(InstallPath("game.dll"));
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]); // real file replaced the link
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateApplierTests"`
Expected: FAIL - `ApplyOutcome.AbortedUnsafePath` does not exist; guards not implemented.

- [ ] **Step 4: Implement the guards**

In `KhaozEngine.Updates/UpdateApplier.cs`:

(a) Add the enum value to `ApplyOutcome` (after `AbortedStagingIncomplete`, line 17):

```csharp
    /// <summary>A manifest path was unsafe (absolute/traversal) or a reparse point; aborted untouched.</summary>
    AbortedUnsafePath,
```

(b) At the very top of `Apply` (after the log line, line 89), add the pre-flight path validation over BOTH lists, before the parent-wait so nothing is touched:

```csharp
        foreach (string relativePath in config.FilesToCopy)
        {
            if (!IsSafeRelativePath(config.InstallDir, relativePath))
            {
                environment.Log($"Unsafe copy path, aborting untouched: {relativePath}");
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }
        foreach (string relativePath in config.FilesToDelete)
        {
            if (!IsSafeRelativePath(config.InstallDir, relativePath))
            {
                environment.Log($"Unsafe delete path, aborting untouched: {relativePath}");
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }
```

(c) In the existing pre-flight staging-existence loop (lines 112-122), add a reparse-source check next to the existence check. Replace the loop body's `if (!environment.FileExists(source))` block with:

```csharp
            string source = Path.Combine(config.StagingDir, ToNative(relativePath));
            if (!environment.FileExists(source))
            {
                environment.Log($"Staged file missing, aborting before any changes: {relativePath}");
                ClearMarker(environment, markerPath);
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedStagingIncomplete, ExitCode = 1 };
            }
            if (environment.IsReparsePoint(source))
            {
                environment.Log($"Staged file is a reparse point, aborting: {relativePath}");
                ClearMarker(environment, markerPath);
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
```

(d) In the copy loop, before backing up / copying, if the destination exists and is a reparse point, delete the link first so the copy writes a real file rather than following the link. Insert right after `dest` is computed (line 131) and before the `destDir` block:

```csharp
            if (environment.FileExists(dest) && environment.IsReparsePoint(dest))
            {
                environment.Log($"Destination is a reparse point, removing link before copy: {relativePath}");
                try { environment.DeleteFile(dest); }
                catch (Exception ex) { environment.Log($"Could not remove link {relativePath}: {ex.Message}"); }
            }
```

(e) Add the validator helper near `ToNative` (bottom of the class):

```csharp
    /// <summary>
    /// True when <paramref name="relativePath"/> is a plain forward-slash relative path that stays
    /// under <paramref name="installDir"/>: not rooted, no drive letter, no <c>..</c> segment, no null
    /// byte, and resolving it against the install dir does not escape it.
    /// </summary>
    private static bool IsSafeRelativePath(string installDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0'))
        {
            return false;
        }
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            return false;
        }
        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (segment == ".." )
            {
                return false;
            }
        }

        string fullInstall = Path.GetFullPath(installDir);
        string combined = Path.GetFullPath(Path.Combine(fullInstall, ToNative(relativePath)));
        string prefix = fullInstall.EndsWith(Path.DirectorySeparatorChar)
            ? fullInstall
            : fullInstall + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.Ordinal);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateApplierTests"`
Expected: PASS (existing apply tests + new traversal/reparse tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Updates/UpdateApplier.cs KhaozEngine.Updates/IUpdaterEnvironment.cs KhaozEngine.Tests/Updates/FakeUpdaterEnvironment.cs KhaozEngine.Tests/Updates/UpdateApplierTests.cs
git commit -m "updates: path-traversal + reparse-point guards in apply core"
```

---

## Task 7: macOS codesign re-verify before relaunch (fail closed)

After the copy/delete/manifest steps, verify the installed game's code signature before relaunching. On failure, roll back from the still-present backups and relaunch the old version. Rollback cleanup is deferred until after the check passes.

**Files:**
- Modify: `KhaozEngine.Updates/IUpdaterEnvironment.cs`
- Modify: `KhaozEngine.Updates/UpdateApplier.cs`
- Modify: `KhaozEngine.Tests/Updates/FakeUpdaterEnvironment.cs`
- Modify: `KhaozEngine.Tests/Updates/UpdateApplierTests.cs`

- [ ] **Step 1: Add the env seam**

In `KhaozEngine.Updates/IUpdaterEnvironment.cs`, add after `IsReparsePoint`:

```csharp
    /// <summary>
    /// Verifies the OS-level code signature of the installed executable/bundle at
    /// <paramref name="executablePath"/>. Returns true on platforms without signature enforcement.
    /// </summary>
    bool VerifyCodeSignature(string executablePath);
```

In `KhaozEngine.Tests/Updates/FakeUpdaterEnvironment.cs`, add a settable result (default true) after the `ReparsePoints` set from Task 6:

```csharp
    public bool CodeSignatureValid = true;

    public bool VerifyCodeSignature(string executablePath) => CodeSignatureValid;
```

- [ ] **Step 2: Write the failing tests**

Add to `KhaozEngine.Tests/Updates/UpdateApplierTests.cs`:

```csharp
    [Fact]
    public void Apply_CodeSignatureInvalid_RollsBackAndRelaunchesOld()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        env.CodeSignatureValid = false;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // restored from backup
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
    }

    [Fact]
    public void Apply_CodeSignatureValid_Succeeds()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        env.CodeSignatureValid = true;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateApplierTests"`
Expected: FAIL - `VerifyCodeSignature` not called; invalid signature still succeeds.

- [ ] **Step 4: Implement the deferred-rollback codesign check**

In `KhaozEngine.Updates/UpdateApplier.cs`, the success tail currently (lines 222-230) deletes staging + rollback, clears quarantine, and relaunches. Restructure so the codesign check happens after quarantine-clear but before the rollback dir is deleted. Replace lines 222-231 (`try { environment.DeleteDirectory(config.StagingDir); ... }` through the `environment.Log(errors > 0 ? ...)` line) with:

```csharp
        // Clear quarantine first so the signature check sees the file as the OS will at launch.
        environment.ClearQuarantine(config.InstallDir);

        // Fail closed: if the installed executable is not validly signed, roll back to the backups
        // (still present - we have not cleaned the rollback dir yet) and relaunch the old version.
        if (!environment.VerifyCodeSignature(config.GameExePath))
        {
            environment.Log("Code signature verification FAILED after apply; rolling back.");
            RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
            try { environment.DeleteDirectory(rollbackDir); }
            catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
            ClearMarker(environment, markerPath);
            environment.Relaunch(config.GameExePath, config.InstallDir);
            return new ApplyResult { Outcome = ApplyOutcome.RolledBack, ExitCode = 1 };
        }

        try { environment.DeleteDirectory(config.StagingDir); }
        catch (Exception ex) { environment.Log($"Cleanup: could not remove staging dir {config.StagingDir}: {ex.Message}"); }
        try { environment.DeleteDirectory(rollbackDir); }
        catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir}: {ex.Message}"); }
        ClearMarker(environment, markerPath);

        environment.Relaunch(config.GameExePath, config.InstallDir);

        environment.Log(errors > 0 ? $"Update completed with {errors} error(s)." : "Update applied successfully!");
```

Note: this removes the old standalone `environment.ClearQuarantine` + `Relaunch` calls at lines 228-229 (now folded into the block above). Make sure there is exactly one `ClearQuarantine` and one success-path `Relaunch` after this edit.

- [ ] **Step 5: Implement the real env methods**

In `KhaozEngine.Updates/SystemUpdaterEnvironment.cs`, add `IsReparsePoint` (for Task 6) and `VerifyCodeSignature`. Add after `ClearQuarantine` (line 96):

```csharp
    public bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
            FileAttributes attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false; // unreadable: treat as not-a-link, the copy/exists checks handle the rest
        }
    }

    public bool VerifyCodeSignature(string executablePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true; // no OS signature enforcement to re-check here
        }

        try
        {
            // Verify the .app bundle that contains the executable, not the inner Mach-O.
            string target = executablePath;
            int appIndex = executablePath.IndexOf(".app/", StringComparison.Ordinal);
            if (appIndex >= 0)
            {
                target = executablePath[..(appIndex + 4)];
            }

            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = "codesign",
                ArgumentList = { "--verify", "--deep", "--strict", target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null)
            {
                Log("codesign could not be started; treating as unverified.");
                return false;
            }
            proc.WaitForExit(15000);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log($"codesign verification error: {ex.Message}");
            return false;
        }
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateApplierTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Updates/IUpdaterEnvironment.cs KhaozEngine.Updates/UpdateApplier.cs KhaozEngine.Updates/SystemUpdaterEnvironment.cs KhaozEngine.Tests/Updates/FakeUpdaterEnvironment.cs KhaozEngine.Tests/Updates/UpdateApplierTests.cs
git commit -m "updates: macOS codesign re-verify before relaunch, fail closed"
```

---

## Task 8: Full suite green + README/security docs

Run the whole engine test suite (including the GPU-gated nets are not needed here) and update the package README for mandatory signing, key management, and the release-build feed-URL lockdown (finding 9 is documentation + the signing guarantee).

**Files:**
- Modify: `KhaozEngine.Updates/README.md`

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all tests PASS (previous count + the new updater tests). If anything outside `Updates/` fails, it is unrelated to this change - investigate before continuing.

- [ ] **Step 2: Update the README**

In `KhaozEngine.Updates/README.md`, replace the example that reads the feed URL from an env var with guidance that release builds hardcode it, and add a "Signing" section. Find the `ServerBaseUrl = Environment.GetEnvironmentVariable(...)` example and change it to:

```csharp
// Release builds: hardcode the feed URL. Do NOT read it from an env var in production - a local
// attacker could repoint the updater. (Mandatory signing already blocks a repointed feed from
// serving a valid manifest, but hardcoding removes the vector entirely.)
ServerBaseUrl = "https://my-server.example.com/",
```

Add this section near the top of the README (after the intro paragraph):

```markdown
## Signing (required)

Manifests are RSA-2048 / SHA-256 / PKCS#1 signed. The client REQUIRES at least one trusted public
key and refuses any manifest without a valid signature - there is no unsigned mode.

1. Generate a key pair once: `ManifestSigner.GenerateKeyPair()` (or your publish tool's `--genkey`).
   Keep the private key secret (a CI secret); commit nothing.
2. At publish time, sign the exact manifest bytes and ship `manifest.json.sig` (base64) next to
   `manifest.json`: `File.WriteAllBytes(path + ".sig", ManifestSigner.Sign(manifestBytes, privPem))`.
3. Embed the public key(s) in the game and pass them to the service:

   ```csharp
   new UpdateServiceOptions
   {
       Source = new HttpUpdateSource(new HttpUpdateSourceOptions { ServerBaseUrl = "https://my-server.example.com/" }),
       CurrentVersion = BuildConfig.Version,
       AppDataDir = appDataDir,
       TrustedPublicKeys = new[] { MyEmbeddedPublicKeyPem },
   };
   ```

Rotate by shipping the new public key alongside the old (both in `TrustedPublicKeys`), switching the
signer to the new private key, then dropping the old key in a later release.
```

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Updates/README.md
git commit -m "docs(updates): document mandatory signing + feed-URL lockdown"
```

---

## Task 9: Release ritual (7.0.0)

One version bump for the whole batch, per the engine release ritual. This is a breaking change (mandatory signatures reject every existing unsigned feed), so the shared line goes to **7.0.0**.

**Files:**
- Modify: `Directory.Build.props:18`
- Modify: `CHANGELOG.md`
- Modify: `CHANGENOTES.md`
- Modify: `docs/CONSUMERS.md:7`
- Modify: `docs/ROADMAP.md:3`
- Modify: `README.md:120-123`

- [ ] **Step 1: Bump the shared version line**

In `Directory.Build.props`, change line 18:

```xml
    <KhaozEngineVersion>7.0.0</KhaozEngineVersion>
```

- [ ] **Step 2: Add the CHANGELOG entry (newest first)**

Add to the top of the entries in `CHANGELOG.md`:

```markdown
## 7.0.0

### KhaozEngine.Updates - security hardening (BREAKING)

- **Mandatory manifest signing.** Manifests are RSA-2048 / SHA-256 / PKCS#1 signed; the client
  verifies a detached `manifest.json.sig` over the raw manifest bytes before parsing and refuses
  anything unsigned or signed by an untrusted key. `UpdateServiceOptions.TrustedPublicKeys` is now
  REQUIRED (at least one key) - constructing `UpdateService` without one throws. New
  `ManifestSigner` / `ManifestVerifier` / `ManifestKeyPair` (pure BCL, no new dependency).
- **Signed fields only for security decisions.** `Required` is now a signed manifest field; the
  downgrade gate runs against the signed version. The unsigned `/latest` response is a hint only.
- **Feed transport locked to https + same origin.** `HttpUpdateSource` refuses any manifest, `.sig`,
  or file URL that is not https or not on the configured `ServerBaseUrl` host. `IUpdateSource` now
  exposes `DownloadBytesAsync` (replacing `DownloadManifestAsync`) and `DownloadFileAsync` takes a
  `maxBytes` cap.
- **Apply-time guards.** Path-traversal rejection on both copy and delete lists (new
  `ApplyOutcome.AbortedUnsafePath`), reparse-point guard on staged sources and destinations, and
  macOS `codesign --verify --deep --strict` before relaunch (fail closed, rolls back on failure).
  `IUpdaterEnvironment` gains `IsReparsePoint` and `VerifyCodeSignature`.
- **Size + disk caps.** Per-file (`MaxFileBytes`, default 4 GiB) and total (`MaxTotalDownloadBytes`,
  default 16 GiB) download caps, streaming overrun abort, and a free-disk pre-check.

Whole engine shares one version line, so all packages bump to 7.0.0. Only `KhaozEngine.Updates`
changed; other packages are a version-number bump. Consumers using the updater must generate keys,
embed the public key, and publish a signed manifest (SpaceGame).
```

- [ ] **Step 3: Add the CHANGENOTES digest (newest first)**

Add to the top of `CHANGENOTES.md`:

```markdown
- **7.0.0** - Updater security hardening (BREAKING): mandatory RSA-signed manifests, https+same-origin feed lock, path-traversal/reparse/codesign apply guards, downgrade enforcement, size+disk caps. `TrustedPublicKeys` now required.
```

- [ ] **Step 4: Update the three guard-checked version declarations**

In `docs/CONSUMERS.md` line 7, change `` `6.4.0` `` to `` `7.0.0` ``.

In `docs/ROADMAP.md` line 3, change `**6.4.0**` to `**7.0.0**`.

In `README.md` lines 120-123, change each `Version="6.4.0"` to `Version="7.0.0"`.

- [ ] **Step 5: Run the doc-version guard + full test suite**

Run: `bash scripts/check-doc-versions.sh`
Expected: PASS (the three declarations now match 7.0.0).

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all PASS.

- [ ] **Step 6: Pack to the local feed**

```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: `KhaozEngine.Updates.7.0.0.nupkg` (and the rest of the line) written to `local-feed/`.

- [ ] **Step 7: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "updates(7.0.0): mandatory signed manifests + updater security hardening"
```

- [ ] **Step 8: Tag (done at branch finish)**

The tag + push to remote (`git tag v7.0.0` then push `main` + the tag, which triggers the GitHub Packages publish) happens when the branch is merged/finished, per the finishing-a-development-branch flow. Do NOT tag mid-plan. Note in the CI caveat from memory: the v*-tag publish may be budget-blocked; the durable artifact this plan produces is the `local-feed` nupkg + the committed bump.

---

## Self-Review

**Spec coverage:**
- Finding 1 (signing): Tasks 1, 5. ✓
- Finding 2 (origin lock): Tasks 3, 4. ✓
- Findings 3+4 (path traversal copy/delete): Task 6. ✓
- Finding 5 (downgrade + signed Required): Tasks 2, 5. ✓
- Finding 6 (symlink/reparse): Tasks 6, 7 (env impl). ✓
- Finding 7 (size/disk caps): Tasks 3, 5. ✓
- Finding 8 (macOS codesign): Task 7. ✓
- Finding 9 (feed-URL lockdown): Task 8 (docs + signing guarantee). ✓
- Finding 10 (post-download size check): subsumed by the streaming `maxBytes` abort + existing SHA-256 verify (Tasks 3, 5). ✓
- Tooling (`GenerateKeyPair` / `Sign` engine helpers): Task 1. CLI `--genkey`/`--sign` wiring is SpaceGame adoption (out of scope, per spec). ✓
- 7.0.0 release ritual: Task 9. ✓

**Type consistency:** `ManifestSigner.Sign` / `ManifestVerifier.Verify` / `ManifestKeyPair`, `UpdateServiceOptions.TrustedPublicKeys` / `MaxFileBytes` / `MaxTotalDownloadBytes`, `IUpdateSource.DownloadBytesAsync` / `DownloadFileAsync(..., long maxBytes, ...)`, `IUpdaterEnvironment.IsReparsePoint` / `VerifyCodeSignature`, `ApplyOutcome.AbortedUnsafePath`, `FakeUpdateSource.Bytes` / `PublishSigned`, `FakeUpdaterEnvironment.ReparsePoints` / `CodeSignatureValid` - names used consistently across tasks.

**Note on `DownloadManifestAsync` removal:** replaced by `DownloadBytesAsync` in `IUpdateSource` (breaking, acceptable in a major). Any out-of-tree `IUpdateSource` implementation must add `DownloadBytesAsync` and the `maxBytes` parameter - only `HttpUpdateSource` (engine) and `FakeUpdateSource` (tests) implement it in this repo; SpaceGame uses `HttpUpdateSource` directly, so no SpaceGame source-code change is needed for the transport shape (only the signing adoption).
