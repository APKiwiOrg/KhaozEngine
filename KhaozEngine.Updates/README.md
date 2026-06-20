# KhaozEngine.Updates

Game-agnostic delta auto-update pipeline. Only files that changed between the installed build and the
latest published build are downloaded, staged, and applied by an external shim while the game is
stopped. Determinism-neutral: it never touches simulation or RNG, so no hash concern.

Pure .NET (+ `KhaozEngine.Diagnostics`), no MonoGame dependency.

## Signing (required)

Manifests are RSA-2048 / SHA-256 / PKCS#1 signed. The client REQUIRES at least one trusted public
key and refuses any manifest without a valid signature. There is no unsigned mode.

1. Generate a key pair once: `ManifestSigner.GenerateKeyPair()`. Keep the private key secret (a CI
   secret); commit nothing.
2. At publish time, sign the exact manifest bytes and ship `manifest.json.sig` (the raw signature)
   next to `manifest.json`:
   `File.WriteAllBytes(manifestPath + ".sig", ManifestSigner.Sign(manifestBytes, privateKeyPem))`.
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

The HTTP source enforces https and a same-host origin (the manifest and every file must sit on the
configured base host), and caps both the manifest and each downloaded file at a size limit.

## Pieces

- **`UpdateManifest`** - SHA256 file manifest (`path` / `sha256` / `size`, sorted). `GenerateFromDirectory`
  builds one from an install dir; `ComputeDiff(local, remote)` returns the files to download and delete.
- **`IUpdateSource`** - host-agnostic transport. `HttpUpdateSource` is the default (HTTP against a
  configurable endpoint, files laid out as siblings of the manifest - SpaceGame's Azure Blob layout,
  but the base URL and endpoint path are configuration). Implement the interface for any other backend.
- **`UpdateService`** - the check -> download -> apply state machine with resumable staging. Process
  control (shim launch, exit) is injectable, so the whole thing is headless-testable.
- **`UpdateApplier`** + **`IUpdaterEnvironment`** - the cross-platform staged-apply core: back up each
  file before overwriting, copy with retries for locked files, roll everything back on any failure,
  install the new manifest, relaunch. A game's updater shim is just `UpdateApplier.Run(args, env)`.

## In-game wiring

```csharp
using KhaozEngine.Updates;

var source = new HttpUpdateSource(new HttpUpdateSourceOptions
{
    // Release builds: hardcode the feed URL. Do NOT read it from an env var in production, a local
    // attacker could repoint the updater. (Mandatory signing already blocks a repointed feed from
    // serving a valid manifest, but hardcoding removes the vector entirely.)
    ServerBaseUrl = "https://my-server.example.com/",
    // LatestVersionPath defaults to "api/updates/latest?platform={platform}"
});

using var updates = new UpdateService(new UpdateServiceOptions
{
    Source = source,
    CurrentVersion = BuildInfo.Version,          // your compiled-in version
    AppDataDir = appDataPaths.BaseDirectory,     // writable per-user dir for manifest + staging
    UpdaterExecutableName = "MyGameUpdater",     // shim that sits next to the game (".exe" added on Windows)
    TrustedPublicKeys = new[] { MyEmbeddedPublicKeyPem }, // required: see "Signing (required)" above
    OnBeforeForcedExit = () => SaveEverything(),
});

updates.StateChanged += () => RefreshOverlay(updates.State);
await updates.CheckForUpdateAsync();             // offline-safe: failures fall back to Idle
// user accepts -> await updates.StartDownloadAsync();
// ready -> updates.ApplyUpdate();   // launches the shim and exits
```

`InstallDir` defaults to `AppContext.BaseDirectory` and `Platform` to the current OS runtime id
(`win-x64` / `osx-arm64` / `osx-x64` / `linux-x64`); override either if you publish differently.

## The updater shim

A tiny standalone executable (publish it self-contained / AOT, one per runtime) that the game
launches and then exits. It must not depend on the game runtime - only on this package:

```csharp
// Program.cs of MyGameUpdater
using KhaozEngine.Updates;

string logPath = Path.Combine(Path.GetDirectoryName(args.Length > 1 ? args[1] : ".")!, "updater.log");
using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
return UpdateApplier.Run(args, new SystemUpdaterEnvironment(msg =>
{
    Console.WriteLine(msg);
    log.WriteLine(msg);
}));
```

`ApplyUpdateConfig` (the `apply-update.json` handoff contract) serializes through a source-generated
`UpdatesJsonContext`, so the shim needs no reflection and stays trim/AOT safe.

## Manifest generation (publish side)

The same `UpdateManifest.GenerateFromDirectory(buildDir, version, platform)` that builds the local
manifest produces a published build's manifest, so an offline `dotnet run` tool can emit
`manifest.json` identical to what the client expects.
