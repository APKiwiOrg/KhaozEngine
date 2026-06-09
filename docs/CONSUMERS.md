# KhaozEngine consumers

Which game uses which packages, at which version. Update this whenever a consumer
bumps a `<PackageReference>` or the engine ships a new version.

**Engine current version:** `2.3.0` (all packages share one version, set in `Directory.Build.props`).

## Version matrix

`–` = package not referenced directly by that project. `Time` is pulled in transitively by
`Screens` 2.2.0+; consumers vendor `KhaozEngine.Time` even without a direct reference.

| Project   | Project file                         | Input | Screens | UI    | Ecs   | Content | Time |
|-----------|--------------------------------------|-------|---------|-------|-------|---------|------|
| Hardpoint | `Hardpoint/Hardpoint.Core`           | 2.3.0 | 2.3.0   | 2.3.0 | 2.3.0 | 2.3.0   | –    |
| Nullwake  | `Nullwake/Nullwake.Core`             | 2.3.0 | 2.3.0   | 2.3.0 | –     | –       | –    |
| SpaceGame | `SpaceGame/SpaceGame.Core`           | 2.0.0 | 2.0.0   | –     | 2.0.0 | –       | –    |

## Notes

- **Hardpoint** — fully migrated, tracks latest (2.3.0). First and only consumer of
  `KhaozEngine.Content` (JSON schema validation at build).
- **Nullwake** — uses Input/Screens/UI only. Tracks latest (2.3.0). No ECS, no Content.
- **SpaceGame** — uses Input/Screens/Ecs only (no UI). Behind at 2.0.0 (deterministic lockstep;
  has not adopted the 2.2.0+ time features and must not read scaled dt).

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

_Last verified: 2026-06-09 against engine 2.3.0._
