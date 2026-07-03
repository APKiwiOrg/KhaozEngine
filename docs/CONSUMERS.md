# KhaozEngine consumers

Which game uses which packages, at which version. Current state only - for the per-version story see
[`../CHANGELOG.md`](../CHANGELOG.md). Update this whenever a consumer bumps a `<PackageReference>` or the
engine ships a new version.

**Engine current version:** `9.11.0` (the shared `<KhaozEngineVersion>` line, which *is* the engine). The
engine is entirely MonoGame-free on a single version line in `Directory.Build.props` (the doc-version guard
checks it): the custom render/runtime stack + the graduated MonoGame-free foundation + the four umbrella
metapackages, all sharing one version. `Physics.Bepu`, the `WorldStore.Sqlite`/`.SqlServer` backends, and
`Social.Discord` (the Discord Rich Presence backend) are opt-in and referenced explicitly; `Server.Admin`
(the Kestrel HTTPS admin endpoint) is likewise opt-in
and NOT in the `Server` umbrella - add it explicitly when the server needs an admin endpoint, so a sim server
without one never pulls the ASP.NET Core web stack; the author/publish tools (`ke-updater`/`ke-sfxbake`/`ke-propbake`) ship as
dotnet tools in no umbrella, so no consumer references them via `<PackageReference>`. Full package catalog:
the checked-in `CLAUDE.md`. Per-version history: `CHANGELOG.md`.

> **8.0.0 is a breaking release.** The 2D `WorldColliders?`/`WorldSurfaces?` movement overloads are removed;
> the 3D movement path (`CharacterMovement.Step`, `CharacterController3D`, `PlayerMoveSimulator`,
> `WorldServer`/`WorldClient`, `Scene3DChunkSink`, `TerrainStreamer`) now collides against the new
> `IPhysicsWorld` seam (the `Physics` package + opt-in `Physics.Bepu`). `Collision` stays in `Foundation` for
> 2D games and lockstep sims. A 2D `Game2D` consumer is unaffected.

## Metapackages (the one-line entry points)

The four code-free umbrella metapackages (`Game2D` / `Game3D` / `Server` / `Foundation`) and exactly what each
pulls in are the single-source "Umbrella metapackages" table in [`../README.md`](../README.md) - don't duplicate
that table here. The granular packages still exist for fine-grained use (a wire-contract project references just
`Netcode.Abstractions`, etc.; the `.Sqlite`/`.SqlServer` backends, `Physics.Bepu`, `Social.Discord`, and
`Server.Admin` are opt-in and added explicitly). This file tracks only which consumer pins which version (below).

## Consumer matrix

Every consumer is MonoGame-free and references the engine through an umbrella metapackage (plus granular pins
where needed). Each pins its own version (see the Version column).

| Consumer | Project(s) | References | Version |
|---|---|---|---|
| **Hardpoint** (3D) | `Hardpoint.Game` / `Hardpoint.Core` | `KhaozEngine.Game3D` (head) + `KhaozEngine.Foundation` (logic); live auto-updater (`Updates` via Foundation, Gui overlay) against a server-less static-blob feed + OIDC CI publish; `Collision.Segment2D` for swept projectile collision. One root `Directory.Build.props` `<KhaozEngineVersion>` drives every ref (heads, the `HardpointUpdater` shim, the dev `SnapshotTool`); top-level `Hardpoint.slnx`, vendored feed (`Hardpoint/vendor/khaozengine`); `GameAppOptions.WindowIcons` + `WindowIconPath` (multi-size taskbar icon + macOS Dock icon, 9.8.0) | **9.8.0** |
| **Nullwake** (2D) | `Nullwake.Core` | `KhaozEngine.Game2D` + `Diagnostics`/`Persistence`/`Windowing` + `Updates` (shim, dormant) + `Snapshot` (dev tool); uses `AttentionBeacon`, clipboard paste + `Pointer.WindowFocused` gating in name entry, and `GameAppOptions.WindowIcons` + `WindowIconPath` (multi-size taskbar icon + macOS Dock icon, 9.8.0). Vendored feed (`Nullwake/vendor/khaozengine`) | **9.8.0** |
| **SpaceGame** (2D + Render3D) | `SpaceGame.Core` (head) / `SpaceGame.Sim` (lockstep sim) | `Game2D` + `Render3D` + `Netcode.LiteNetLib` + `Primitives` (head); `Ecs`/`Collision`/`Diagnostics`/`Content`/`Serialization`/`App`/`Netcode`/`Determinism` + `Primitives` (sim); `Netcode.Abstractions` (contracts); `Render3D` + `Determinism` for the 2.5D mesh layer; manifest signing via the `ke-updater` tool; `GameAppOptions.WindowIcons` + `WindowIconPath` (multi-size taskbar icon + macOS Dock icon, 9.8.0). Vendored feed (`SpaceGame/vendor/khaozengine` via repo-root `nuget.config`) | **9.8.0** |
| **Ruinborne** (3D MMO) | `Ruinborne.Client` / `Ruinborne.Server` | client: `KhaozEngine.Game3D` + `Foundation` + `NetWorld` + `Netcode.LiteNetLib` + `Netcode.Abstractions` + `Simulation` + `Updates`; server: `KhaozEngine.Server` umbrella + `WorldStore.SqlServer` (Azure SQL via `WorldPersistence`). Single `<KhaozEngineVersion>` pin in `Directory.Build.props`, vendored feed (`vendor/khaozengine`, refreshed via `scripts/refresh-engine.sh`); client uses `GameAppOptions.WindowIcons` + `WindowIconPath` (multi-size taskbar icon + macOS Dock icon 9.8.0, client only) + the 9.1.0 `CollisionShapeOverlay`/`OverlayLegend` (F2 debug proxy view) | **9.8.0** |

## Runtime window icon

The MonoGame-free `AppWindow` (Silk.NET/GLFW + Veldrid) needs an explicit icon API (unlike MonoGame's SDL layer,
which loaded an embedded `Icon.bmp` for free), so a consumer that sets none shows the generic window icon. The
runtime icon API:

- **Option:** `GameAppOptions.WindowIconPath` (a PNG, convenience) or `GameAppOptions.WindowIcons`
  (`IReadOnlyList<ImageRgba>`, explicit multi-res 16/32/48 px so GLFW picks per DPI). `WindowIcons` wins over
  `WindowIconPath`. `GameApp` applies it during construction; a non-`GameApp` host can call `AppWindow.SetIcon(...)`
  directly.
- **Decode-package choice:** `KhaozEngine.Windowing` stays decode-free (no Render2D / StbImageSharp dependency). It
  exposes a `WindowIcon` struct of already-decoded RGBA8 and `AppWindow.SetIcon`; the **Game layer** does the PNG
  decode via `Render2D.ImageRgba` and hands `WindowIcon`s down. This keeps the package graph clean (no image-decode
  dependency pulled into the low-level windowing leaf).
- **macOS Dock icon (9.8.0):** GLFW ignores window icons on Cocoa, so `SetIcon` is a **no-op on macOS**. Since 9.8.0,
  `GameApp` also sets the **Dock / Cmd-Tab** icon at runtime from `WindowIconPath` via
  `AppWindow.SetMacDockIcon` -> `Platform.ApplicationIcon.TrySetMacDockIcon` (`NSApplication.setApplicationIconImage:`),
  so an unbundled `dotnet run` no longer shows the generic document icon. A packaged `.app` bundle's icns still owns
  the Dock icon the normal way; a `WindowIcons`-only config (no PNG path) leaves the Dock icon untouched.
- **Per-consumer follow-up (not the engine release):** each desktop consumer passes its icon PNG via
  `GameAppOptions`, and (independently) re-adds `<ApplicationIcon>...Icon.ico</ApplicationIcon>` to its desktop-head
  csproj for the Windows `.exe` icon shown when the app is not running (that is per-repo, not an engine API).

## Repo locations

| Project   | Path                   | Repo                         |
|-----------|------------------------|------------------------------|
| Hardpoint | `~/Hardpoint`          |                              |
| Nullwake  | `~/Nullwake/Nullwake`  |                              |
| SpaceGame | `~/SpaceGame/SpaceGame` |                             |
| Ruinborne | `~/Ruinborne`          | `APKiwi/Ruinborne` (private) |

## How to refresh this file

```sh
# engine version (source of truth) - the single <KhaozEngineVersion> line is the engine
grep -iE '<KhaozEngineVersion>' ~/KhaozEngine/Directory.Build.props

# what each consumer pins
for d in ~/Hardpoint ~/Nullwake/Nullwake ~/SpaceGame/SpaceGame ~/Ruinborne; do
  find "$d" -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' \
    -exec grep -l KhaozEngine {} \; | while read f; do
      echo "-- $f"; grep -i KhaozEngine "$f"; done
done
```

After editing, run `./scripts/check-doc-versions.sh` (CI runs it too) to confirm the engine-version line
still matches `Directory.Build.props`.

## Bumping a consumer's engine pin (gotchas)

When a consumer raises its `KhaozEngine.*` pin, two things bite if skipped:

- **A passing local build does not prove the bump works.** Once any consumer (or the engine itself) has
  restored a version, that version sits in the machine NuGet cache (`~/.nuget/packages`), so a local restore
  resolves it even if no configured source actually serves it. CI restores cold. Verify the way CI does:
  `dotnet restore <proj> --packages /tmp/cold && dotnet test <proj> --no-restore`. A cold `--packages` dir
  forces resolution from the consumer's real sources only.
- **Vendored-feed consumers must refresh the vendored nupkgs, not just the `<PackageReference>`.** Hardpoint
  (`Hardpoint/vendor/khaozengine`), Nullwake (`Nullwake/vendor/khaozengine`), and Ruinborne (`vendor/khaozengine`)
  each restore in CI from an in-repo feed declared in their `nuget.config`, NOT from `~/KhaozEngine/local-feed`.
  Bumping the pin without copying the new-version nupkgs into that folder fails CI with `NU1102` even though the
  local build is green. Copy the matching `KhaozEngine.*.<ver>.nupkg` set from `local-feed` into the vendored dir
  (keep it to the packages that consumer actually uses) and commit them with the bump. **Watch the side projects:**
  Hardpoint's `tools/` projects live outside the inner tree and reference the snapshot packages directly, so the
  vendored feed must carry those too. Only SpaceGame restores from `local-feed`/GitHub Packages directly (no
  vendored feed), so it alone sets the `local-feed` floor.

_Last verified: 2026-07-03. **Ruinborne** (3D MMO, the active consumer) pins **9.6.2** (the current engine line;
it tracked the 9.x line through 9.6.0/9.6.1 and adopted the 9.6.2 SpriteBatch flicker fix). **SpaceGame**,
**Hardpoint**, and **Nullwake** pin
**9.0.1** (the 9.0.0 package-structure adoption: usings/package-id swaps only, no behaviour change). Stack per
consumer: **Hardpoint** (3D) via `Game3D` + `Foundation`, **Nullwake** (2D) via `Game2D`
(+ `Diagnostics`/`Persistence`/`Windowing`/`Snapshot`/`Updates`), **SpaceGame** (2D + Render3D) via
`Game2D` + `Render3D` head + the split-out `SpaceGame.Sim` foundation pins, and **Ruinborne** (3D MMO) via a
single `<KhaozEngineVersion>` pin across the `Game3D` client and the `Server` + `WorldStore.SqlServer` headless
server. SpaceGame is the only consumer restoring from `local-feed` directly (the other three vendor their own
in-repo feed); with SpaceGame on 9.0.1, `local-feed` may be pruned to a **9.0.1** floor. Everything older lives in
GitHub Packages, the durable store._
