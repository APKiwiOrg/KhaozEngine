# Centralize the auto-updater last-mile glue into KhaozEngine (7.2.0)

Status: approved (design). Target release: 7.2.0 (additive minor).

## Problem

`KhaozEngine.Updates` (7.1.0) is a complete, security-hardened delta auto-updater: state
machine (`UpdateService`), cross-platform staged apply (`UpdateApplier` +
`IUpdaterEnvironment`/`SystemUpdaterEnvironment`), HTTPS same-host source
(`HttpUpdateSource`), and mandatory RSA-2048 signed manifests
(`UpdateManifest`/`ManifestSigner`/`ManifestVerifier`).

What is NOT in the engine is the reusable **last-mile glue** every game otherwise rewrites.
Only SpaceGame has it today, bespoke:

- `SpaceGame.Core/Screens/Overlays/UpdateNotificationOverlayScreen.cs` — the in-game
  "update available / press U / downloading / ready / applying / failed" overlay UI.
- `tools/SpaceGameUpdater/Program.cs` — the external updater shim exe (thin wrapper over
  `UpdateApplier.Run`).
- `tools/ManifestGenerator/Program.cs` — a thin CLI over `UpdateManifest.GenerateFromDirectory`
  (no signing wired; `--genkey`/`--sign` were deferred in 7.0.0).
- `scripts/publish-update.sh` — builds, generates the manifest, uploads to Azure Blob, updates
  `latest-{platform}.json`.

## Goal

Extract that glue into reusable engine pieces so Hardpoint, Nullwake, and SpaceGame each adopt
the updater with only thin per-game config (feed URL, embedded public key, a one-line shim entry,
a game-themed overlay). After this ships, "adopt the updater" means: use the engine feature and
only the engine feature.

## Decisions (locked)

1. Overlay = a low-level **widget** (`UpdateOverlayView`) plus a thin **`UpdateOverlayScreen :
   Gui.Screen`** wrapper. Widget is the reusable core; screen is the drop-in for `ScreenStack`.
2. Overlay **raises events**; the game wires the action. A one-line convenience
   (`UpdateOverlayActions.Trigger`) keeps the default wiring trivial.
3. Signing/manifest CLI ships as a **`dotnet tool`** (`PackAsTool`), command name **`ke-updater`**.
4. Publish script ships as a **parameterized template + adoption docs**.
5. Widget input is a read-only **`IUpdateStatus`** interface (testability), implemented by
   `UpdateService`.
6. `KhaozEngine.Gui` takes a NuGet/project dependency on `KhaozEngine.Updates` (pure .NET, only
   pulls Diagnostics — acyclic). Confirmed acceptable; no separate bridge package.

## Architecture / package impact

- `<KhaozEngine5xVersion>` → `7.2.0`. All packable projects already inherit `<Version>`.
- New packable project **`KhaozEngine.Updates.Tool`** (`PackAsTool=true`,
  `ToolCommandName=ke-updater`, `PackageId=KhaozEngine.Updates.Tool`,
  `<Version>$(KhaozEngine5xVersion)</Version>`, references the Updates project). It is a
  publish-side tool, so it is **not** added to any of the 4 umbrella metapackages. The
  `docs/CONSUMERS.md` package list/count is updated to mention it.
- `KhaozEngine.Gui.csproj` adds `ProjectReference` to `../KhaozEngine.Updates`.
- No change to the security model: signing stays mandatory; HTTPS + same-host; size/disk caps;
  fail-closed apply. This work is purely additive reusable surface.

## Components

### A. `KhaozEngine.Updates` additions (foundation, pure .NET)

**`IUpdateStatus`** — read-only view consumed by the Gui widget:

```csharp
public interface IUpdateStatus
{
    UpdateState State { get; }
    string? RemoteVersion { get; }
    int FilesDownloaded { get; }
    int TotalFilesToDownload { get; }
    long BytesDownloaded { get; }
    long TotalDownloadBytes { get; }
    string? ErrorMessage { get; }
    bool IsRequired { get; }
}
```

`UpdateService` implements it (every member already exists as a public property; this is an
interface declaration + `: IUpdateStatus` on the class, no behaviour change).

**`UpdaterShim`** — the reusable shim entry (deliverable 2):

```csharp
public static class UpdaterShim
{
    // Derives the log path from args, opens an autoflush StreamWriter log sink,
    // and returns UpdateApplier.Run(args, new SystemUpdaterEnvironment(sink)).
    public static int Main(string[] args);

    // Testable: log path lives next to the apply-config file (args[1]) or ".".
    public static string ResolveLogPath(string[] args);
}
```

Game shim `Program.cs` collapses to:

```csharp
return KhaozEngine.Updates.UpdaterShim.Main(args);
```

**`UpdateOverlayActions`** — one-line default wiring (deliverable 1 convenience):

```csharp
public static class UpdateOverlayActions
{
    // Maps the service's current state to the right call:
    //   UpdateAvailable -> StartDownloadAsync (fire-and-forget)
    //   ReadyToApply    -> ApplyUpdate
    //   Failed          -> CheckForUpdateAsync (retry, fire-and-forget)
    //   else            -> no-op
    public static void Trigger(UpdateService service);
}
```

### B. `KhaozEngine.Gui` additions (deliverable 1)

**`UpdateOverlayTheme`** — struct, all visuals injected, `Default` mirrors SpaceGame:

- Per-state accent colours: `Available`, `Downloading`, `Ready`, `Applying`, `Failed`
  (title + border tint per state).
- Panel: `PanelFill`, `DimFill`, `BodyText`, `ProgressBackground`, `ProgressFill`.
- Layout: `PanelWidth`, `PanelPadding`, `TitleScale`, `BodyScale`, `ProgressBarHeight`.
- Labels: injectable templates — `TitleFor(UpdateState, version)` and
  `BodyFor(UpdateState, status, keyName)` via format strings (game can feed localized text).
- Binding: `Key TriggerKey` (default `U`) + `GamepadButton? TriggerButton`.

**`UpdateOverlayView`** — the widget:

```csharp
public sealed class UpdateOverlayView
{
    public UpdateOverlayTheme Theme { get; set; }
    public event Action<UpdateState>? OnTrigger;   // carries current state
    public event Action? Triggered;                // paramless convenience

    // Advances fade alpha; detects TriggerKey / TriggerButton press while a visible
    // state is shown; raises OnTrigger/Triggered. Returns true if it consumed input
    // (i.e. a visible, modal state). Hidden for Idle/Checking.
    public bool Update(IUpdateStatus status, InputState input, float dt);

    // Draws dim + panel + title + body + progress bar via GuiDraw, centred in viewport.
    // No-op while hidden.
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport);
}
```

Visible states (panel shown, input consumed): `UpdateAvailable`, `Downloading`,
`ReadyToApply`, `Applying`, `Failed`. Hidden: `Idle`, `Checking`. Progress bar drawn only in
`Downloading` (fraction = `BytesDownloaded / TotalDownloadBytes`, guarded against zero).

**`UpdateOverlayScreen : Gui.Screen`** — thin drop-in wrapper:

- Constructed with an `IUpdateStatus` source + optional `UpdateOverlayTheme`.
- Owns an `UpdateOverlayView`; forwards the Screen lifecycle
  (`Update(float dt, bool receivesInput)` → `view.Update(...)` using `Manager.Input`;
  `Draw(SpriteBatch)` → `view.Draw(...)` using `Manager` font/white/viewport).
- Re-exposes `OnTrigger` / `Triggered`.
- `IsPopup`-style: transitions on/off, always-on-top semantics consistent with existing Gui
  screens.

### C. `KhaozEngine.Updates.Tool` (deliverable 3, `dotnet tool`)

New project, command `ke-updater`. Command logic factored into a testable class with injectable
IO; `Program.cs` is the entry/dispatch. Subcommands:

- `manifest --dir <d> --platform <p> --version <v> [--output <f>]`
  → `UpdateManifest.GenerateFromDirectory(d, v, p).Serialize()` to file or stdout
  (ports SpaceGame's `ManifestGenerator`).
- `genkey --out <dir>` → `ManifestSigner.GenerateKeyPair()`; writes `private.pem` + `public.pem`.
- `sign --manifest <m.json> --key <private.pem>` → reads raw manifest bytes,
  `ManifestSigner.Sign(bytes, pem)`, writes `<m.json>.sig` beside it.
- `verify --manifest <m.json> --sig <m.json.sig> --key <public.pem>` → `ManifestVerifier.Verify`;
  exit 0 if valid, non-zero otherwise (CI convenience).

### D. Publish template + docs (deliverable 4)

- `KhaozEngine.Updates/templates/publish-update.sh` — generalized from SpaceGame's:
  variables for storage account / container / platform list; manifest-source resolution incl.
  the macOS `.app/Contents/MacOS` case; a **sign step** invoking `ke-updater sign`; the
  `latest-{platform}.json` pointer write. Azure Blob default, with a "fill these in" header.
  Shipped in-repo and bundled into the package (`templates/` package path) so a consumer gets a
  local copy.
- `KhaozEngine.Updates/README.md` gains an **"Adopting the updater"** section: generate keys
  (`ke-updater genkey`), where the private key lives (CI secret) vs the public key (embed in
  `TrustedPublicKeys`), signing in the publish flow, the overlay (view + screen) wiring, the shim
  one-liner, and the feed layout
  (`latest-{platform}.json` → `{version}/{platform}/manifest.json[.sig]` + files).

## Testing (headless, `KhaozEngine.Tests`)

- **Overlay state→view mapping:** for each `UpdateState`, the view reports
  hidden vs visible correctly; title/body/accent resolve from the theme; progress fraction
  computed only in `Downloading` and guarded against zero totals.
- **Overlay trigger:** feeding an `InputState` with `TriggerKey` pressed (and separately the
  gamepad `TriggerButton`) while in a visible state raises `OnTrigger` with the current state and
  `Triggered`; no event in hidden states; `Update` returns consumed=true only for visible states.
  Use a fake `IUpdateStatus` + synthetic `InputState` (existing Gui test pattern).
- **Shim:** `UpdaterShim.ResolveLogPath` returns the path beside the apply-config arg (and the
  `.` fallback).
- **Tool:** `genkey → sign → verify` roundtrip succeeds; verify fails on a tampered manifest /
  wrong key. Drive the tool's command class directly (not `Program.Main`).

## Release ritual (per CLAUDE.md)

1. `<KhaozEngine5xVersion>` → `7.2.0` in `Directory.Build.props`.
2. `CHANGELOG.md` newest-first entry (new reusable surface; list the public APIs).
3. `CHANGENOTES.md` one-line digest.
4. Update the 3 doc-version declarations: `docs/CONSUMERS.md` "Engine current version" (+ note the
   new `KhaozEngine.Updates.Tool` package), `docs/ROADMAP.md` "Current released version",
   `README.md` `<PackageReference>` examples. (`scripts/check-doc-versions.sh` enforces these.)
5. `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` green.
6. `dotnet pack -c Release -o ./local-feed`.
7. Commit, `git tag v7.2.0`, push `main` + tag (CI publishes to GitHub Packages on `v*`).

## Out of scope

- Per-game adoption (SpaceGame / Hardpoint / Nullwake) — those are the 3 follow-up chats that
  depend on 7.2.0 being in the feed.
- Any change to the updater security model or the existing `UpdateService`/`UpdateApplier`
  behaviour.
```
