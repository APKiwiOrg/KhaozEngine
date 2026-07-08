# Consuming the KhaozEngine updater (`KhaozEngine.Updates`)

The engine ships a signed, delta-based auto-updater in `KhaozEngine.Updates`. This is the guide for a
consuming game that wants a shipped desktop build to update itself on launch. The engine owns all of the
hard parts (check, diff, download, signature verification, staged apply with rollback, relaunch, and the
optional overlay UI); the game wires up a thin layer of glue plus a publish channel.

Audience: a game author standing up the updater for the first time, and a reviewer checking that a game
wired it correctly. For the threat model and where this sits in the wider security posture, see
[SECURITY-BASELINE.md](SECURITY-BASELINE.md). For hard consumer rules on how the engine is used, see
[USING-KHAOZENGINE.md](USING-KHAOZENGINE.md).

Each shipping game keeps its own per-game specifics (feed URL, storage account, Key Vault name, the exact
CI job) in its own `docs/UPDATER.md`. This doc is the generic mechanics; that doc is the concrete wiring.
The live examples are Ruinborne, Nullwake, and SpaceGame.

---

## What the game owns vs what the engine owns

The engine (`KhaozEngine.Updates`) owns: `UpdateService`, `HttpUpdateSource`, `UpdateManifest`,
`ManifestSigning`, `UpdateApplier`, `UpdaterShim`, `UpdateState`, and the headless `UpdateOverlayView`.
All check / diff / download / verify / staged-apply / rollback / relaunch logic lives there. The publish
tooling ships too, as the `ke-updater` dotnet tool (`KhaozEngine.Updates.Tool`, verbs `genkey`,
`manifest`, `sign`, `verify`).

The game owns, per head:

- The feed base URL and the RID-aware layout it points at.
- The **embedded trusted public key** (committed `.pem`, read at runtime, wired as
  `UpdateServiceOptions.TrustedPublicKeys`).
- A one-line shim executable that forwards `args` to `KhaozEngine.Updates.UpdaterShim.Main(args)`,
  published next to the game.
- The check-on-launch wiring (constructing `UpdateService`, firing the startup check).
- The publish channel: an Azure Blob container and a CI job that builds, signs, and uploads a release.

A game typically factors its glue into an `Updates/` folder (config record + `trusted-public-key.pem`) and
a separate shim project. Nothing about the download, verify, stage, swap, or relaunch is reimplemented in
the game.

---

## Why Azure Blob storage, not GitHub Releases

The client must fetch update payloads **anonymously**: a shipped game cannot carry a credential to
authenticate a download, and you do not want to embed one.

The games are **private** repos. GitHub Releases assets on a private repo require an authenticated token
to download, so they cannot serve an unauthenticated client. That rules Releases out as the feed.

The house pattern is therefore an **Azure Blob container with anonymous blob read** (a storage account per
game, a `releases` container set to public blob access). The client fetches the version pointer, the signed
manifest, and the changed files straight from blob storage over plain HTTPS with no auth. The **signing
key never touches the client or the container**: it lives in Key Vault and is used only at publish time
(see below). Anonymous read is safe here precisely because every manifest is signed and the client is
fail-closed: a public feed cannot be used to push an unsigned or attacker-signed payload.

Container hardening: HTTPS-only, TLS 1.2 minimum, public access scoped to `blob` (blobs are readable,
container listing is not).

---

## Keys: generate once, embed the public half

Signing is **mandatory**. A client rejects any unsigned feed or a manifest whose signature does not verify
against the embedded public key. There is no unsigned code path to fall back to.

Generate the keypair once with the engine tool:

```sh
ke-updater genkey --out secrets/
```

That emits `private.pem` and `public.pem` (RSA-2048, SubjectPublicKeyInfo PEM for the public half).

- **`private.pem` never enters the repo.** `.gitignore` guards `secrets/`, `*private*.pem`, and the like.
  It becomes a **Key Vault secret** (the game's own vault). CI downloads it at publish time and wipes it
  after. It is never a GitHub secret and never committed.
- **`public.pem` is committed** into the game (e.g. `<Game>/Updates/trusted-public-key.pem`), embedded as
  a resource (`LogicalName` / `EmbeddedResource`), and read at runtime by the game's updater config, which
  wires it into `UpdateServiceOptions.TrustedPublicKeys`.

The client verifies `manifest.json.sig` (a detached RSA-2048 / SHA-256 / PKCS#1 v1.5 signature over the
exact manifest bytes) against that embedded public key before it trusts any remote manifest. A missing or
mismatched signature aborts the check. Losing the private key means minting a new keypair and shipping a
build with the new embedded public key, so treat the Key Vault secret as the root of trust.

---

## Feed layout and the signed manifest

The client reads a small **per-RID pointer** to find the current version, then the **signed manifest** for
that version, then only the files that differ from what it has locally.

```
releases/
  latest-<rid>.json                         <- {"version","buildVersion","manifestUrl","required"}
  <version>/<rid>/manifest.json
  <version>/<rid>/manifest.json.sig
  <version>/<rid>/<game files...>           (game + shim + content)
```

`latest-<rid>.json` is a tiny JSON pointer per RID (at minimum `{"version": "...", "required": false}`).
Desktop RIDs are `win-x64`, `osx-arm64`, `osx-x64`, and `linux-x64` (a game ships the subset it targets).

The manifest lists every file in the build with its install-relative path, SHA-256, and byte size, sorted
by path, plus a publish timestamp:

```json
{
  "version": "1.2.3",
  "platform": "win-x64",
  "publishedAtUtc": "2026-03-27T12:00:00Z",
  "files": [
    { "path": "Game.dll", "sha256": "a1b2c3d4...", "size": 524288 }
  ]
}
```

`ke-updater manifest` (publish side) and `UpdateManifest.GenerateFromDirectory` (client side) produce the
identical structure from the same hashing logic, so the client can diff the remote manifest against its own
install and download only new-or-changed files. Path is forward-slash relative to the install root; sha256
is lowercase hex; files are ordinal-sorted.

**How the client resolves the feed.** A game can either point the client straight at the blob host (the
client reads `latest-<rid>.json` and the manifest URL itself) or front it with a tiny server endpoint that
returns the resolved manifest URL from the pointer. Either way the signed `manifest.json` and its `.sig`
are fetched by the client directly from blob storage; the server, if any, only resolves the pointer and is
never a trusted intermediary for the payload.

---

## Publish flow (CI)

Publishing turns a tagged release into a signed feed entry. It runs on a `vX.Y.Z` tag (matching the game's
`<Version>`), typically in `.github/workflows/publish-clients.yml`, across a per-RID matrix.

Per platform, the job:

1. **Auth into Azure via OIDC.** `azure/login` with a **federated credential**, not a stored secret. The
   game's GitHub app / service principal has a federated credential scoped to the repo and the release
   environment (`repo:<owner>/<repo>:environment:release`), plus `Storage Blob Data Contributor` on the
   storage account and `Key Vault Secrets User` on the vault. No long-lived Azure secret in GitHub.
2. **Install `ke-updater`** from the vendored engine feed (usually with no version pin so it tracks the
   vendored engine version): `dotnet tool install --global KhaozEngine.Updates.Tool --add-source <feed>`.
3. **Download the signing key from Key Vault** to a temp file (`az keyvault secret download ...`), not from
   a GitHub secret. An `always()` step wipes the key file afterward.
4. **Build, manifest, sign, upload:**
   ```sh
   dotnet publish -c Release -r <rid> --self-contained     # the game + shim
   ke-updater manifest --dir <build-dir> --platform <rid> --version <version> --output manifest.json
   ke-updater sign --manifest manifest.json --key <private-key-pem>
   az storage blob upload-batch ... -d releases/<version>/<rid>/     # files + manifest + .sig
   ```
5. **Flip the pointer** by uploading `latest-<rid>.json` last, so the new version only becomes visible once
   its payload is fully uploaded.

A game usually wraps steps 4-5 in a `scripts/publish-update.sh <rid> <version>` so the same script runs in
CI and locally (after an `az login` / `aztoken`). The build directory to hash is the flat payload dir on
Windows/Linux, and on macOS it is `Contents/MacOS` inside the `.app` (see the caveat below).

The key facts a reviewer checks: the private key comes from Key Vault (never a GitHub secret, never
committed), Azure auth is OIDC federated (no stored cloud secret), and the pointer flip is last.

---

## Client flow: check, apply, rollback

The game constructs `UpdateService` (feed URL, `TrustedPublicKeys`, current version, app-data dir, the
shim executable name, and an optional `OnBeforeForcedExit` hook to flush persistence before exit) and fires
a check on launch. The check is **non-fatal**: a network failure, a 404 before the first publish populates
`latest-<rid>.json`, or an offline machine all return quietly to idle and the game keeps running on its
current build.

On a check the engine:

1. Reads the RID pointer and compares versions numerically.
2. If newer, downloads `manifest.json` + `manifest.json.sig` and **verifies the signature against the
   embedded public key**. A failed verify aborts the check (no download, no apply).
3. Diffs the remote manifest against the local install, downloads only changed files to a staging dir
   (SHA-256 verified, resume-aware), then stages the manifest.
4. Hands off to the shim to apply.

**The shim and rollback.** A running process cannot overwrite its own binaries, so a standalone shim does
the swap while the game is stopped. The game's shim project is one line forwarding to
`KhaozEngine.Updates.UpdaterShim.Main(args)`; the staged-apply core (`UpdateApplier`) lives in the engine.
On apply the shim waits for the game to exit, writes an `apply-in-progress` marker, pre-flights that every
staged file exists (aborting before touching the install if staging is incomplete), then **atomically
swaps** each staged file over the install (copy-to-temp + same-volume rename, so the on-disk image is
never half-written) **after backing up the existing file into a rollback area**. Any copy or backup
failure restores every backed-up file, leaves the old version intact, and relaunches it: the install is
never left half-new. On success it applies deletions, installs the new manifest, cleans up staging and the
rollback area, and relaunches. The relaunch is **resilient on Windows**: the freshly-written exe can still
be blocked from executing by an in-flight antivirus scan even after it is openable, so instead of a
fire-and-forget launch the shim starts the game, watches for a fast startup failure (`0xc0000142` and
friends), and retries with back-off until it boots (logging each attempt to `updater.log`); if it never
boots the update still stands and the next launch picks it up. For the uncatchable case (power loss
mid-copy) the marker survives and the next launch detects the interrupted apply, so the player can
re-download. This is the fail-closed, crash-safe contract the game inherits for free.

---

## The in-game overlay and the shim window

Two separate surfaces report update status, and the engine keeps them in one palette with localized text.

- The **in-game overlay** (`KhaozEngine.Gui.UpdateOverlayView` / `UpdateOverlayScreen`, themed by
  `UpdateOverlayTheme`) is the popup drawn inside the running game: it announces an available update, shows
  download progress, and prompts the restart-and-apply. It is a pure presenter over the service's
  `IUpdateStatus`.
- The **shim progress window** (`UpdaterUiOptions`, in `KhaozEngine.Updates`) is the small native window the
  standalone shim shows during the file swap, after the game has exited. Windows-only, a no-op elsewhere.

### Localized overlay text (no subclass needed)

The default `UpdateOverlayTheme` resolves every line through the ambient `LocalizationContext.Catalog`
(`KhaozEngine.App`) against engine-owned `StringId` keys, and falls back to built-in English
(`UpdateOverlayStrings.EnglishDefaults`) when no catalog is wired or a key is absent. A game therefore localizes
the overlay just by adding these keys to its catalog and wiring `LocalizationContext.Catalog` at startup, with no
`UpdateOverlayTheme` subclass. A game that wires a catalog WITHOUT these keys, or wires none at all, still sees
the historical English, never a raw key. Overriding `TitleFor` / `BodyFor` on a theme subclass still fully
replaces this resolution (the override never routes through the catalog).

The standard keys (`UpdateOverlayStrings`), one title and one body per `UpdateState`, with their format
arguments and the English default:

| State | Key | Format args | English default |
|---|---|---|---|
| UpdateAvailable (title) | `update.overlay.available.title` | `{0}` = remote version | `Update Available - v{0}` |
| UpdateAvailable (body) | `update.overlay.available.body` | `{0}` = trigger-key label | `Press [{0}] to download` |
| Downloading (title) | `update.overlay.downloading.title` | none | `Downloading Update...` |
| Downloading (body) | `update.overlay.downloading.body` | `{0}` files, `{1}` total files, `{2}` MB, `{3}` total MB | `Downloading {0}/{1} files ({2:0.0}/{3:0.0} MB)` |
| ReadyToApply (title) | `update.overlay.ready.title` | `{0}` = remote version | `Update v{0} Ready` |
| ReadyToApply (body) | `update.overlay.ready.body` | `{0}` = trigger-key label | `Press [{0}] to restart and apply` |
| Applying (title) | `update.overlay.applying.title` | none | `Applying Update...` |
| Applying (body) | `update.overlay.applying.body` | none | `Game will restart shortly` |
| Failed (title) | `update.overlay.failed.title` | none | `Update Failed` |
| Failed (body) | `update.overlay.failed.body` | `{0}` = trigger-key label | `Press [{0}] to retry` |

The overlay resolves through the wired catalog with culture-aware formatting. The English fallback formats with
the invariant culture, so an unlocalized build renders exactly as it did before this feature. The shim window's
own status lines (`InstallingText` / `FinishingText` on `UpdaterUiOptions`) are NOT in this set: they are
separate, game-supplied, already-localized strings.

### One palette for both surfaces

`UpdateOverlayTheme.ToUpdaterUiOptions(...)` (a `KhaozEngine.Gui` extension) derives the shim window palette
from the overlay theme, so a game configures colours once instead of hand-syncing two colour sets. It maps
accent from `ProgressFill`, background from `PanelFill`, and text from `BodyText` (alpha dropped, the native
window is opaque), and takes the window title, heading, logo, and localized status lines as optional arguments:

```csharp
UpdaterUi = theme.ToUpdaterUiOptions(
    windowTitle: BuildConfig.DisplayName,
    installingText: strings.Get("update.installing"),
    finishingText: strings.Get("update.finishing"));
```

The helper lives on the Gui side because `KhaozEngine.Updates` (which owns `UpdaterUiOptions`) has no Gui
dependency: Gui references Updates, so the edge points the right way and `Updates` stays renderer-free.
`UpdaterUiThemeExtensions.ToRgb(Vector4)` exposes the RGBA-float to `(R, G, B)`-byte conversion on its own for
any other hand-mapping a game still needs.

---

## macOS: the `.app` re-sign caveat

On macOS the game ships as an `<Game>.app` bundle (for the Finder / Dock icon; see [ICONS.md](ICONS.md) in
the game). The updater still patches the **flat payload inside `Contents/MacOS/`** (that directory is
`AppContext.BaseDirectory`, so the engine's default install dir), and the feed stays flat: only the
hand-distributed zip is a bundle.

The engine is **fail-closed** and re-runs `codesign --verify --deep --strict` after applying. A
`--deep --strict` verify checks the whole bundle seal, and an in-place file swap **breaks that seal**. So
before verifying, the applier **re-seals the bundle** (`IUpdaterEnvironment.ResealCodeSignature`, run after
the swap and before the verify): the real environment re-signs the enclosing `.app` **ad-hoc**
(`codesign --force --sign -`, inner-to-outer, no `--deep`), which rebuilds `CodeResources` so the verify
passes again. The re-seal is ad-hoc because an end-user Mac has no Developer ID private key (only CI does),
so it **drops Developer ID / notarization** from the updated bundle. That is acceptable: the updater has
already cleared quarantine and the app has already launched once, so Gatekeeper's quarantined-first-launch
gate is past. If the re-seal (or the verify) fails, the update rolls back exactly as before.

That closes the engine half. The remaining requirement is **consumer-side**: the game's publish CI must
**Developer ID sign + notarize the original `.app`** so its very first launch passes Gatekeeper (a
freshly downloaded, unsigned bundle would be quarantined-blocked on first launch, before the updater ever
runs). Once the original is signed + notarized, in-place self-update completes on macOS the same as
`win-x64` and `linux-x64`. A consuming game whose macOS head is not yet Developer ID signed should document
that first-launch limitation rather than treat a macOS rollback as a bug.

---

## Wiring checklist for a new game

1. Stand up the feed: a storage account + `releases` container (anonymous blob read, HTTPS-only, TLS 1.2),
   and a Key Vault for the signing key.
2. `ke-updater genkey`; upload `private.pem` to Key Vault; commit + embed `public.pem`; wire it into
   `UpdateServiceOptions.TrustedPublicKeys`.
3. Add the one-line shim project forwarding to `UpdaterShim.Main`, published next to the game.
4. Construct `UpdateService` and fire the on-launch check (non-fatal on failure).
5. Add the OIDC federated credential + role assignments and the `publish-clients.yml` CI job (or
   `scripts/publish-update.sh`) that builds, manifests, signs from Key Vault, uploads, and flips the
   pointer last.
6. Tag `vX.Y.Z`; the first publish populates `latest-<rid>.json`. Verify end-to-end on desktop: a running
   older build detects the new version, downloads, applies via the shim, and relaunches, and confirm an
   unsigned or wrong-key manifest is rejected (fail-closed).
7. Record the per-game specifics (feed URL, account, vault, exact CI job) in the game's own
   `docs/UPDATER.md`.
