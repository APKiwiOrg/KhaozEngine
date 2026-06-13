# KhaozEngine docs index

Start here. The docs split into **living docs** (kept current, edit in place) and a **design archive**
(dated, append-only, historical). When two docs state the same fact, the source-of-truth column says
which one wins.

## Living docs

| Doc | What it's for | Source of truth for |
|---|---|---|
| [README.md](../README.md) | Top-level overview: the 16-package table, the one hard rule, quickstart wiring, repo layout. | The package list + what each package owns. |
| [USING-KHAOZENGINE.md](USING-KHAOZENGINE.md) | The consumer contract: hard rules, the data-flow model, per-layer API reference, headless-test patterns. Read before wiring a game in. | How a game must use the engine. |
| [CHANGELOG.md](../CHANGELOG.md) | Newest-first, per-package, every version. | Release history. Nothing else should restate the per-version story. |
| [CONSUMERS.md](CONSUMERS.md) | Current state only: which game pins which package at which version, and notable non-adoptions with reasons. | The version + adoption matrices. |
| [ROADMAP.md](ROADMAP.md) | Larger feature areas not yet scheduled; shipped items marked. | The backlog. |
| `../<Package>/README.md` | One-paragraph purpose + a snippet per package (16 of them). | Per-package quick reference. |

The **engine current version** lives in `../Directory.Build.props` (`<Version>`). Docs that restate it
(CONSUMERS "Engine current version", ROADMAP "Current released version", the README PackageReference
example) are guarded by [`../scripts/check-doc-versions.sh`](../scripts/check-doc-versions.sh), which CI
runs on every push. Consumer *pins* are allowed to lag and are not checked.

## Process docs

- [../CLAUDE.md](../CLAUDE.md) — concurrent-dev rule (worktree per change), the release ritual, build/test commands.
- Release ritual, short form: bump `Directory.Build.props` `<Version>` -> add the `CHANGELOG.md` entry ->
  update the engine-version line in `CONSUMERS.md` -> `dotnet pack -c Release -o ./local-feed` -> commit ->
  `git tag vX.Y.Z` -> push `main` + tag.

## Design archive (`docs/superpowers/`)

Historical, not maintained as living docs. Two files per feature, named `YYYY-MM-DD-<feature>`:

- `specs/` — the design doc agreed before building a feature (the "what + why").
- `plans/` — the implementation plan executed against that spec (the "how").

These capture the reasoning behind a feature at the time it shipped; the living docs above describe the
current state. Don't update archive files to match later changes — supersede them with the CHANGELOG and
the living docs instead. To browse what's there:

```sh
ls docs/superpowers/specs   # designs, by date
ls docs/superpowers/plans   # implementation plans, by date
```

Roughly grouped by subsystem (see filenames for dates): **ECS** (`khaozecs-*`), **Time/clock**
(`time-scale-pause`, `timeskip`), **Graphics/Camera** (`graphics-camera2d`, `graphics-display-manager`,
`pannable-canvas`, `pannable-camera-core`), **Audio** (`khaozengine-audio`), **Effects**
(`khaozengine-effects-particles`), **Persistence** (`persistence-queue`, `persistence-settings-promotion`,
`saveencoder-promotion`), **Content** (`khaozengine-content`), **App/Diagnostics promotions**
(`appdatapaths-promotion`, `buildmetadata-promotion`, `servicelocator-promotion`,
`localizationmanager-promotion`, `logging-service`), **Input** (`input-0.2.0`).
