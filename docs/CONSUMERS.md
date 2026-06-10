# KhaozEngine consumers

Which game uses which packages, at which version. Update this whenever a consumer
bumps a `<PackageReference>` or the engine ships a new version.

**Engine current version:** `3.0.0` (all packages share one version, set in `Directory.Build.props`).

## Version matrix

`–` = package not referenced directly by that project. `Time` is pulled in transitively by
`Screens` 2.2.0+; consumers vendor `KhaozEngine.Time` even without a direct reference.

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Diagnostics | Time |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|-------------|------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 2.4.0 | 2.4.0   | 2.4.0 | 2.4.0 | 2.4.0   | –           | –    |
| Nullwake  | `Nullwake/Nullwake.Core`             | 3.0.0 | 3.0.0   | 3.0.0 | –     | –       | 3.0.0       | –    |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 3.0.0 | 3.0.0   | 3.0.0 | 3.0.0 | 3.0.0   | 3.0.0       | –    |

## Adoption matrix

Which packages each consumer pulls in. `✓` = direct `<PackageReference>`, `–` = not used,
`(transitive)` = vendored via `Screens` 2.2.0+ but no direct reference and (for `Time`) no
scaled-dt usage.

| Consumer  | Input | Screens | UI | Ecs | Content | Diagnostics |    Time      |
|-----------|:-----:|:-------:|:--:|:---:|:-------:|:-----------:|:------------:|
| Hardpoint |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      –      | (transitive) |
| Nullwake  |   ✓   |    ✓    | ✓  |  –  |    –    |      ✓      | (transitive) |
| SpaceGame |   ✓   |    ✓    | ✓  |  ✓  |    ✓    |      ✓      | (transitive) |

## Notes

- **Hardpoint** — fully migrated, tracks latest (2.4.0). First consumer of
  `KhaozEngine.Content` (JSON schema validation at build). Has not adopted `Diagnostics` (no file
  logger of its own yet; a candidate to migrate).
- **Nullwake** — uses Input/Screens/UI (3.0.0) + `Diagnostics` (3.0.0). No ECS, no Content on main. Its
  in-house `GameLogger` is now a thin static facade over `FileLogger`; the Nullwake-specific app-data
  path resolution (`LocalApplicationData/Nullwake/game.log`) stays game-side. Mixed pin is fine:
  `Diagnostics` has no dependency on the other engine packages.
- **SpaceGame** — uses Input/Screens/UI/Ecs/Content + `Diagnostics` (3.0.0). UI is `TextInputHandler`
  for its prompt screens. First consumer of `KhaozEngine.Diagnostics`: its `GameLogger` is now a thin
  facade over `FileLogger`. Deterministic lockstep: vendors `KhaozEngine.Time` transitively via Screens
  but reads no scaled dt (no `GameClock`/`TimeScale`/`TimeSkip` usage) and must keep it that way.

## Repo locations

| Project   | Path                  | Repo                         |
|-----------|-----------------------|------------------------------|
| Hardpoint | `~/Hardpoint`         | migrated                     |
| Nullwake  | `~/Nullwake/Nullwake` |                              |
| SpaceGame | `~/SpaceGame/SpaceGame`|                             |

## How to refresh this file

```sh
# engine version
grep -i '<Version>' ~/KhaozEngine/Directory.Build.props

# what each consumer pins
for d in ~/Hardpoint ~/Nullwake ~/SpaceGame; do
  find "$d" -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' \
    -exec grep -l KhaozEngine {} \; | while read f; do
      echo "-- $f"; grep -i KhaozEngine "$f"; done
done
```

> Nullwake's Content/Diagnostics adoption and SpaceGame's Bucket A (TextInputHandler→engine, UI/Content)
> are in progress on feature branches, re-pinned to 3.0.0; not yet merged to their respective mains.

_Last verified: 2026-06-10 against engine 3.0.0._
