# KhaozEngine.Updates

Game-agnostic delta auto-update pipeline. Only files that changed between the installed build and the
latest published build are downloaded, staged, and applied by an external shim while the game is
stopped. Determinism-neutral: it never touches simulation or RNG, so no hash concern.

Pure .NET (+ `KhaozEngine.Diagnostics`, `KhaozEngine.Platform`, `KhaozEngine.Primitives`), no MonoGame dependency.

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
  control (shim launch, exit) is injectable, so the whole thing is headless-testable. The individual
  `CheckForUpdateAsync` / `StartDownloadAsync` / `ApplyUpdate` steps are for a fire-and-forget overlay;
  for a startup gate see `EnsureUpToDateAsync` below.
- **`EnsureUpToDateAsync`** - the composed startup gate. One awaitable call, run ONCE before connecting,
  that self-heals an out-of-date client: if a newer signed build exists it downloads + verifies + applies +
  relaunches (the process exits into the new version); otherwise it returns an `UpdateGateResult` to branch
  on (`UpToDate` / `Updating` / `FeedUnreachable` / `Failed`). Bounded by a `checkTimeout` (default 10s) so a
  down/slow feed falls through to `FeedUnreachable` and lets the game continue on the current build rather
  than blocking startup. Reports `UpdateGateProgress` (phase + byte/file counts) for a "Downloading
  update..." screen. See "Startup gate" below.
- **`UpdateApplier`** + **`IUpdaterEnvironment`** - the cross-platform staged-apply core: wait behind an
  exit barrier until the game process is gone (see "Windows self-update safety" below), back up each file
  before overwriting, atomically swap in each staged file (`ReplaceFile`: copy-to-temp + rename, retried
  for locked files and permission denials), roll everything back on any failure, install the new manifest,
  wait for the new exe to become launchable, then relaunch with retry (see "Relaunch settle wait + retry"
  below). On a protected install (e.g. Program Files) it relaunches elevated once. A game's updater shim is
  just `UpdateApplier.Run(args, env)`.
- **`IUpdaterUi`** + **`UpdaterUiOptions`** - the optional progress window the shim shows during the
  apply (Install then Finishing). Windows-only native GDI; a no-op elsewhere. See "Progress window".
- **`UpdateVersion`** - numeric `x.y.z` comparison for the update gate (`IsNewer`). A thin wrapper over
  `KhaozEngine.Primitives.VersionComparer`, the rule shared with `KhaozEngine.ServerStatus.VersionOrder`
  so the two packages cannot drift apart.

## Relaunch settle wait + retry

Two things guard the Windows post-apply relaunch against the antivirus / file-lock race. On Windows a
`PublishSingleFile` self-contained bundle's freshly-written exe is scanned by Windows Defender, which
briefly holds an exclusive lock and can keep the image from *executing* even after the file is openable;
relaunching mid-scan crashes the game at startup (`0xc0000142` STATUS_DLL_INIT_FAILED / `0xc0000409`
STATUS_STACK_BUFFER_OVERRUN).

1. **Atomic swap.** Each staged file is written via `IUpdaterEnvironment.ReplaceFile` - the real impl
   copies to a temp file next to the destination then does a same-volume rename (atomic), so the on-disk
   exe is only ever the complete old or complete new image, never a half-written one.
2. **Settle poll (first-line pre-filter).** The applier polls
   `IUpdaterEnvironment.CanOpenExclusively(gameExePath)` (the real impl opens with `FileShare.None`)
   until the exe is no longer locked, up to a 30s ceiling. This is necessary but *not* sufficient:
   openable-for-a-handle does not mean loadable-as-an-image.
3. **Relaunch with retry (the definitive gate).** Instead of a fire-and-forget launch, the applier drives
   `IUpdaterEnvironment.TryRelaunch(exe, dir, watchMs)`, which starts the game and watches it briefly; a
   fast startup failure (a startup NTSTATUS) or a launch error is retried after a capped back-off, up to
   `RelaunchMaxAttempts` (8) tries. Every attempt and its outcome is logged to `updater.log`. If every
   attempt fails the update still stands - only the auto-relaunch is abandoned, so the next start gets the
   new version. On non-Windows `CanOpenExclusively` returns true and `TryRelaunch` reports `Running` on the
   first try, so the wait/retry is a no-op. This is the window's "Finishing" phase, which stays visible
   across the retry wait.

## Windows self-update safety (exit barrier + permissions)

Three guards close the two ways a Windows self-update used to fail: swapping files while the game was
still running, and a Program Files install that denies writes. Both surfaced as an unhandled
`UnauthorizedAccessException` on the file swap.

1. **Exit barrier.** Before it mutates a single install file, `Apply` waits behind a hard barrier: after
   the parent-exit wait it polls `IUpdaterEnvironment.IsProcessAlive(parentPid)` until the game process is
   really gone. A running process locks its own loaded `.exe`/`.dll`, so swapping into a live game is the
   "patched while the window was still up" crash. If the game is still alive after the barrier budget the
   apply aborts UNTOUCHED (`ApplyOutcome.AbortedGameStillRunning`): no files changed, no marker written, no
   relaunch, so the update just defers to the next launch. `IsProcessAlive` returns false off Windows, so
   the barrier is a first-poll no-op and the macOS/Linux in-place apply is unchanged.
2. **Permission-safe retry + commit-aware rollback.** The copy retry catches `UnauthorizedAccessException`
   (a locked image or a denied delete-child) as well as `IOException`, so a transient lock rides out the
   retry budget and a permanent denial rolls back cleanly instead of crashing. The whole mutation phase is
   wrapped in a backstop: a pre-commit failure restores the backups, clears the marker, and relaunches the
   old version (`RolledBack`); a post-commit failure (a hiccup after the new binaries are installed and
   verified) reports `Succeeded` rather than a false rollback and never relaunches twice. The
   `apply-in-progress` marker is written only during the mutation phase, so any earlier abort leaves
   nothing dangling.
3. **Elevate once for a protected install.** Before applying, `Run` checks the install dir with
   `IUpdaterEnvironment.CanWriteToDirectory`. On Windows a per-machine install under a protected root
   (`%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramW6432%`, `%SystemRoot%`) reports not-writable whenever
   the process is not already elevated - a plain create-a-temp-file probe is a false positive there, because a
   new file at the install root can be created even when overwriting the existing binaries or clearing an
   admin-owned `.ke-update-rollback` fails with Access Denied (that false positive is what let a Program Files
   apply skip elevation and roll back). When it is not writable, `Run` relaunches the updater elevated once via
   `TryElevate` (Windows `runas`, one UAC prompt), passing `--relocated --elevated`. The `--elevated` flag
   guards against an elevation loop, and a declined UAC prompt falls through to a clean in-place attempt (which
   rolls back if the write stays denied) rather than crashing. Off a protected root the check falls back to the
   create/delete probe, and the whole check plus elevation are skipped off Windows, so the POSIX apply runs no
   extra filesystem op. The durable fix for the whole permission class is a per-user install location, which
   never needs elevation.

## Progress window

The shim phase used to be invisible: the user saw nothing during install, and on the relaunch crash
they saw a raw Windows error dialog. The shim now shows a tiny native window (Windows only) that walks
through **Install** (files copied / total) then **Finishing** ("Finishing up, checking with your
security software...") during the settle wait above. It is a self-contained Win32 GDI window drawn with
P/Invoke only - no WinForms/WPF, no common-controls dependency, no engine GUI/GPU stack - so it stays
trim/AOT-safe inside a single-file trimmed shim. Off Windows it is a no-op: macOS and Linux apply the
update in place (POSIX replaces the running executable's inode, so there is no self-lock to wait out and
no scan/relaunch race) and need no window - the apply, quarantine clear, macOS bundle re-seal, and
codesign verify still run.

## macOS bundle re-seal

On macOS the game ships as a `.app` bundle whose `_CodeSignature/CodeResources` seal covers every file.
An in-place swap of the staged payload inside `Contents/MacOS/` invalidates that seal, so the fail-closed
`codesign --verify --deep --strict` the applier runs after apply would **always** fail and roll the update
back - macOS in-place self-update could never complete. So after the swap and **before** the verify the
applier calls `IUpdaterEnvironment.ResealCodeSignature(gameExePath)`. The real environment re-signs the
enclosing `.app` **ad-hoc** (`codesign --force --sign -`), signing nested code inner-to-outer (Mach-O
libraries and framework/helper bundles deepest-first, then the top bundle with
`--preserve-metadata=entitlements,flags`; no `--deep`, which Apple deprecates for signing). Ad-hoc is the
only key an end-user Mac has, so the re-seal **drops Developer ID / notarization** - acceptable because
quarantine is already cleared and the app has already launched, so Gatekeeper's first-launch gate is past.
A re-seal failure rolls the update back exactly like a verify failure. The default interface member and the
non-macOS real path are a no-op returning `true` (nothing to re-seal). The companion requirement is
consumer-side: the game's publish CI must Developer ID sign + notarize the **original** bundle so first
launch passes Gatekeeper. See [../docs/UPDATER.md](../docs/UPDATER.md).

The window is themeable and localizable by the game through an optional `UpdaterUiOptions` on
`UpdateServiceOptions` (colors are plain `(R, G, B)` tuples; text is passed already-localized):

```csharp
new UpdateServiceOptions
{
    // ...the usual Source / CurrentVersion / AppDataDir / UpdaterExecutableName / TrustedPublicKeys...
    UpdaterUi = new UpdaterUiOptions
    {
        WindowTitle    = "My Game",
        AccentColor    = (120, 200, 255),
        BackgroundColor= (10, 14, 20),
        TextColor      = (235, 238, 245),
        LogoPath       = "assets/logo.png",                 // relative to the install dir, optional
        InstallingText = Loc("Installing update"),
        FinishingText  = Loc("Finishing up, checking with your security software..."),
    },
};
```

`UpdateService.ApplyUpdate` serializes this into the `apply-update.json` handoff (as the optional `Ui`
block, `ApplyUpdateUiConfig`), which the shim reads to build the window. Omit `UpdaterUi` entirely and
the shim shows a minimal default window (or nothing off Windows) - everything still works. The
consumer's shim stays a one-liner; all of the window is engine code driven by the config.

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

## Startup gate

To make an out-of-date client self-heal *before* it connects (instead of running the steps fire-and-forget
behind an overlay), run the composed gate once at startup and branch on the result:

```csharp
UpdateGateResult gate = await updates.EnsureUpToDateAsync(
    progress: new Progress<UpdateGateProgress>(p => ShowUpdateScreen(p.Phase, p.BytesDownloaded, p.TotalBytes)),
    checkTimeout: TimeSpan.FromSeconds(10));

switch (gate.Outcome)
{
    case UpdateGateOutcome.UpToDate:        break;    // newest build (or nothing offered): proceed
    case UpdateGateOutcome.Updating:        return;   // downloaded + applied: process is exiting into the new build
    case UpdateGateOutcome.FeedUnreachable: break;    // feed down/slow/timed out: proceed on current build
    case UpdateGateOutcome.Failed:          break;    // couldn't download/apply (gate.Error): proceed on current build
}
```

`Updating` means the relaunch was launched and the process is exiting - in production the call does not return;
a test with a non-exiting `ExitProcess` hook is the only place you observe it. `FeedUnreachable` and `Failed` are
non-fatal by design: a startup gate must never block forever on a bad feed, so it falls through and lets the game
continue on the current build. Pair it with the `KhaozEngine.NetWorld` connect-time version handshake as the
backstop for the skew it could not prevent.

## The updater shim

A tiny standalone executable (publish it self-contained / AOT, one per runtime) that the game
launches and then exits. Build it as `<OutputType>WinExe</OutputType>` so applying an update never
flashes a console window over the game. It must not depend on the game runtime - only on this package:

```csharp
// Program.cs of MyGameUpdater - the whole file
using KhaozEngine.Updates;

return UpdaterShim.Main(args);
```

`UpdaterShim.Main` attaches the parent console (via `KhaozEngine.Platform.WindowsConsole`) so its diagnostics
still reach a terminal despite the `WinExe` subsystem, then opens the `updater.log` file next to the apply config
(always written regardless of console), wires the per-OS progress window
(`SystemUpdaterUi.CreateForCurrentOs`), and runs the staged apply. If you want to run the applier
yourself instead of the one-liner, pass the window factory through explicitly so the shim still shows
the window - `UpdateApplier.Run(args, env, SystemUpdaterUi.CreateForCurrentOs)`; the two-arg
`UpdateApplier.Run(args, env)` applies with no window.

`ApplyUpdateConfig` (the `apply-update.json` handoff contract, including the optional `Ui` block)
serializes through a source-generated `UpdatesJsonContext`, so the shim needs no reflection and stays
trim/AOT safe.

## Manifest generation (publish side)

The same `UpdateManifest.GenerateFromDirectory(buildDir, version, platform)` that builds the local
manifest produces a published build's manifest, so an offline `dotnet run` tool can emit
`manifest.json` identical to what the client expects.

## Adopting the updater (last-mile glue)

Everything below ships in this package - adopting the updater means using the engine feature only.

### 1. Keys

Generate an RSA-2048 keypair once with the `ke-updater` dotnet tool:

```
dotnet tool install --global KhaozEngine.Updates.Tool
ke-updater genkey --out ./keys
```

`keys/private.pem` signs your manifests - keep it secret (a CI secret, never committed).
`keys/public.pem` is embedded in the client via `UpdateServiceOptions.TrustedPublicKeys` (ship more
than one to rotate keys).

### 2. In-game overlay

Add `UpdateOverlayScreen` (from `KhaozEngine.Gui`) to your screen stack, pointing it at the
`UpdateService` (which implements `IUpdateStatus`) and wiring its trigger to the default action helper:

```csharp
var overlay = new UpdateOverlayScreen(updateService, font, whiteTexture, viewport);
overlay.OnTrigger += _ => UpdateOverlayActions.Trigger(updateService);   // optional: player-driven
screenStack.Add(overlay);
// ...and once per frame, so a REQUIRED update installs itself with no keypress:
UpdateOverlayActions.AutoAdvanceRequired(updateService);
```

`Trigger` maps the current state to the player action (download / apply / retry).
`AutoAdvanceRequired` is a no-op unless the offered update is required (`IUpdateStatus.IsRequired`, set
from the signed manifest's `required` bit); for a required update it auto-downloads then auto-applies with
no keypress. Call it once per frame from the game loop so `ApplyUpdate` and its forced process exit run on
the caller's thread. See the `KhaozEngine.Gui` README and `docs/UPDATER.md` for the required-update flow.

Retheme via `new UpdateOverlayTheme { ... }` (colours, labels, `TriggerKey`/`TriggerButton`) or
subclass it to override `TitleFor`/`BodyFor` for localized text (a required update draws its titles through
the `TitleFor(UpdateState, IUpdateStatus)` overload, which adds the `*.required` variants). For non-stack
UI, use the lower-level `UpdateOverlayView` directly (`Update(status, input, dt)` +
`Draw(batch, font, white, viewport, status)`).

### 3. The updater shim

Your external updater exe is one line:

```csharp
return KhaozEngine.Updates.UpdaterShim.Main(args);
```

Publish it per-RID with a game-specific name and set `UpdateServiceOptions.UpdaterExecutableName` to match.
This one line also gives you the native progress window on Windows (see "Progress window"); theme it via
`UpdateServiceOptions.UpdaterUi`.

### 4. Publish + feed layout

Two feed shapes are supported. Both lay the build out the same way; they differ only in what the
`latest-<platform>.json` pointer contains and who fills it in. Pick by whether a server sits in front
of the feed:

| | Dynamic-server feed | Server-less static-blob feed |
|---|---|---|
| Template | `templates/publish-update.sh` | `templates/publish-update-static.sh` |
| `latest-<platform>.json` holds | `{"version":"<v>"}` (minimal) | the full `LatestVersionInfo` |
| Who builds the client's response | a dynamic API enriches the minimal blob into the full `LatestVersionInfo` | the blob IS the response; the client reads it directly |
| Client `LatestVersionPath` | `api/updates/latest?platform={platform}` (default) | `<container>/latest-{platform}.json` |
| Use when | you already run a server (SpaceGame) | single-player game, no backend (Hardpoint) |

Copy the matching template (both ship in this package) into your repo, fill in the CONFIG block, and run
it per platform. It builds, generates + signs the manifest with `ke-updater`, uploads, and writes the
latest pointer. Shared build layout under the feed root:

```
<feed-root>/
  latest-<platform>.json            -> see table above
  <version>/<platform>/
    manifest.json
    manifest.json.sig
    <game files...>
```

#### Server-less static-blob feed (no backend)

`publish-update-static.sh` writes the **full** `LatestVersionInfo` so a game with no server deserializes
it straight from the blob (the minimal `{"version"}` form is insufficient here: the client needs
`manifestUrl` to find the manifest, and `LatestVersionInfo` also requires `buildVersion` and `required`):

```json
{ "version": "1.4.0", "buildVersion": "1.4.0", "manifestUrl": "https://acct.blob.core.windows.net/releases/1.4.0/osx-arm64/manifest.json", "required": false }
```

- **`version`** drives the update decision (compared with the client's compiled-in version via
  `UpdateVersion.IsNewer`).
- **`manifestUrl`** is the absolute blob URL of *that build's* `manifest.json`; the client resolves every
  other file as a sibling of it. The template derives it as
  `PUBLIC_BASE_URL/CONTAINER/<version>/<platform>/manifest.json`.
- **`required`** in this pointer is informational only. The client decides mandatoriness from the
  `required` bit in the SIGNED manifest (set with `ke-updater manifest --required`), never from this
  unsigned pointer. A required update auto-downloads and auto-applies via
  `UpdateOverlayActions.AutoAdvanceRequired`. The templates set both from `UPDATE_REQUIRED=true`.
- **`buildVersion`** is an opaque display label the engine does NOT compare against. The template
  defaults it to `version`; pass a 3rd arg for a separate informational/display string.

**Client wiring** points `ServerBaseUrl` at the blob host (same host the manifests live on - the source
enforces https + same-origin) and `LatestVersionPath` at the static pointer:

```csharp
new HttpUpdateSource(new HttpUpdateSourceOptions
{
    ServerBaseUrl = "https://yourgameupdates.blob.core.windows.net/", // your storage account host
    LatestVersionPath = "releases/latest-{platform}.json",             // "<container>/latest-{platform}.json"
});
```

**Public-blob container setup.** The client fetches blobs anonymously, so the container needs public
(anonymous) **blob** read - e.g. `az storage container create --name releases --account-name <acct> --public-access blob`.
`blob` access (not `container`) exposes blobs by exact URL without allowing the container to be listed.
Mandatory signing still gates every install, so a public read-only feed is safe.

For a feed fronted by a dynamic API instead, use `publish-update.sh` (it writes the minimal pointer and
the server fills in `manifestUrl` etc. from the version).
