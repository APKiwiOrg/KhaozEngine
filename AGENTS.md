# KhaozEngine

Shared, game-agnostic engine - a custom MonoGame-free 2D/3D render + windowing/input + Gui + ECS + netcode
stack (Hardpoint, Nullwake, SpaceGame, Ruinborne all run on it). See README.md and docs/USING-KHAOZENGINE.md.

This is the canonical, tool-neutral instruction file for every agent (Claude Code, Codex, and any
other) and human contributor. `CLAUDE.md` is a thin `@AGENTS.md` import so Claude Code reads the same
source; Codex reads this file directly.

## Before starting ANY engine work (concurrent-dev rule)
This section is the engine's instance of the global "Branching, worktrees, and finishing work"
default (worktree per change; finish by merge to `main` + commit + push). It wins where it differs:
heavy parallel dev makes the worktree mandatory (with the trivial-change exception below), and a
finished release is a full publish (merge + push `main` + push the `vX.Y.Z` tag + pack to
`local-feed`). **A finished feature auto-publishes: merge to `main`, tag `vX.Y.Z`, and push
`main` + the tag right away** - do NOT hold or batch the push, and don't ask (CI publishing every
package to GitHub Packages on each `v*` tag is the accepted cost). One release per finished feature
leaves the repo clean after each worktree. **There is always parallel development on this engine, so
NEVER assume your intended version or tag is free:** re-read the current version + tags on the
up-to-date `main` right before you bump/tag, and if yours is taken, take the next FREE version/tag
and auto-resolve (see the release-collision rule below). Bump `<KhaozEngineVersion>` in
`Directory.Build.props` on every change - a finished feature always ships a version bump.
Before you touch anything:
1. Check for ongoing parallel work first: `git worktree list`, `git branch -a`,
   and `git fetch && git status` to see other branches/trees in flight.
2. If your change fits an existing branch/worktree, work there.
3. If it does not fit any of them, create a NEW worktree (do not start work
   loose on `main` or pile onto an unrelated branch). Isolate the change in its
   own tree so concurrent work does not collide.

This applies to every change with one exception below: code, tests, docs, and
version/release work.

- **How to create the tree:** prefer the native `EnterWorktree` tool, not
  `git worktree add`. The native tool is what the parallel-dev workflow expects.
  `EnterWorktree` branches from `origin/<default-branch>` by default. Since this repo
  auto-pushes, `origin/main` is normally current, but a concurrent chat may have just
  landed work - `git fetch` first. If your change builds on local `main` work not yet
  on `origin` (a just-merged commit mid-push), create the tree from local HEAD instead:
  `git worktree add .claude/worktrees/<name> -b worktree-<name> main`, then
  `EnterWorktree` with its `path` to switch in.
- **Branch / tree naming:** `feature/<short-name>` for new features, `fix/<short-name>`
  for bug fixes, `<batchN>-promote` for game-code-into-engine promotion batches
  (e.g. `batch1-promote`). Keep the worktree directory name matching the branch.
- **Trivial-change exception:** a self-contained edit that ships no package
  (a doc typo, a comment, an AGENTS.md/governance tweak, a one-line non-API fix
  with no version bump) may be made directly on a clean `main` without a worktree,
  as long as the parallel-work check in step 1 comes back clean. Anything that
  touches public API, tests, or triggers the release ritual still needs a tree.

## Rules
- `AppWindow` (KhaozEngine.Windowing) is the ONLY class that touches the Silk.NET/GLFW input
  statics. Everything else reads the immutable `InputState` snapshot (handed in via `Frame.Input`)
  through `InputManager`/`Pointer` - keeps input headless-testable. (There is no `MonoGameRawInput`
  or `IRawInput` any more; the engine is MonoGame-free.)
- New behaviour ships with a headless test in the matching per-area test project
  (`KhaozEngine.<Area>.Tests`), or the rump `KhaozEngine.Tests` when it is genuinely cross-cutting
  (construct an `InputState` frame-by-frame and feed `InputManager.Update(input, viewport?)` -
  `dt` is a plain `float` in seconds, no `GameTime`). A test project references ONLY the engine
  projects its tests use - push CI selects test projects by the reference graph, so an over-broad
  reference silently degrades selection. Declared test namespaces stay `KhaozEngine.Tests.*`
  (`RootNamespace` is pinned in every split project), and a new test project needs an explicit
  `<IsPackable>false</IsPackable>`.
- Hit-test via `InputManager`/`Pointer` bounds helpers (`IsTapIn`, etc.), never raw position + button.

## Build / test / release
- `dotnet test` (root, runs `KhaozEngine.slnx` - all 16 test assemblies) - every new behaviour ships with a headless test in its matching per-area project.
- **`ci.yml` runs two paths.** Tag pushes and `workflow_dispatch` run the full sequence (restore, build,
  full test, determinism double-pass, pack, publish). Ordinary pushes and PRs run
  `scripts/ci-selective-test.sh`, which builds and tests only the test projects `dotnet-affected` marks
  affected by the diff, skips entirely on a docs-only diff, and forces full on a
  workflow/scripts/props/slnx/tool-manifest change or a missing base sha. See
  `docs/design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md` for the full design.
- **Private repo, one self-hosted leg.** `KhaozEngine` is a private repo under the `APKiwiOrg` org, so
  GitHub-hosted minutes bill (Linux 1x, Windows 2x, macOS 10x). The only expensive work is the macOS
  Metal golden leg at 10x, so that ONE leg is self-hosted on the native `mac-native-arm64` runner (real
  Metal, where the metal golden is baked); it kills the 10x. Everything else stays GitHub-hosted, where
  it's cheap and native-complete: `ci.yml` build/test/pack/publish on x64 `ubuntu-latest` (1x, ~2 min,
  within the free tier - and it MUST be x64: the engine test suite needs x64-only natives like
  `libveldrid-spirv`, which ships linux-x64 but not linux-arm64, so it can't run on the arm64
  self-hosted container), plus the `cross-platform-gpu.yml` D3D11 (Windows/WARP, 2x) and Vulkan
  (Linux/lavapipe, 1x) golden legs (no local host, software rasterizers, path-gated so trivial spend).
  The games' fleet-wide CI model (org, both runners, secretless OIDC, macOS arm64-only) is in
  `game-template/docs/CI-AND-RUNNERS.md`.
- **Warnings are errors.** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`
  (every config), so any compiler/analyzer warning fails the build, the tests, and CI. Keep the engine at zero
  warnings and fix them at the source, not with `<NoWarn>` / `#pragma warning disable` / `TreatWarningsAsErrors=false`
  (the only standing suppression is `1591`, missing XML doc on public members). This is the fleet-wide build rule;
  the engine still keeps its own SemVer and developer-facing `CHANGELOG.md`, and is exempt from the games'
  player-changelog style and version-segment scheme.
- **Always add a `CHANGELOG.md` entry on every version bump.** Newest-first, detailed (public API / behaviour
  change), with a tight one-line summary as the entry's first sentence so the file doubles as the high-level
  "history over time" view. It goes in the SAME commit as the `Directory.Build.props` version bump. Never bump
  the version (or tag a release) without it. (There is no separate `CHANGENOTES.md` - it was folded into
  `CHANGELOG.md`; one file is the single source of truth.)
- **Before merging back / releasing, re-check for concurrent work and integrate it FIRST.** Parallel dev is
  heavy here and local `main` is routinely ahead of `origin/main`, so always assume `main` moved under you. `git
  fetch`; if `main` advanced past your tree's base, merge `main` INTO your tree first, resolve every conflict and
  re-run the build + tests on the merged result THERE, so the merge back is clean (never resolve a pile of
  conflicts on `main`). The shared `<KhaozEngineVersion>` line collides constantly: a concurrent chat may have
  already bumped it and tagged that `vX.Y.Z`, so re-read the current version on the up-to-date `main` and take the
  next FREE version for your bump + tag (and rebase your `CHANGELOG.md` entry onto it).
- Release ritual, in order: bump `<KhaozEngineVersion>` in `Directory.Build.props` → add the
  `CHANGELOG.md` entry → update the engine-version declarations the
  guard checks (EVERY `README.md` `<PackageReference>` example line, one per umbrella) →
  close any issue this release resolved (`gh issue close`, or `Closes #123` in the commit) if it is
  somehow still open, a backstop only: that should already have happened when the work landed, per the
  Discovered work section → `dotnet pack -c Release -o ./local-feed`
  (cumulative within a release) → commit → `scripts/tag-release.sh` (creates the annotated tag `vX.Y.Z` with the canonical
  `area(<version>): summary`, reading `<KhaozEngineVersion>`, and do NOT hand-type `git tag vX.Y.Z`, a
  lightweight tag is rejected by `pre-push` and is how merge-commit subjects leaked into old tags) →
  push `main` + the tag right away, don't hold or batch (CI publishes to GitHub Packages on `v*`).
  `local-feed/` is a gitignored dev convenience; GitHub Packages (every published `v*`) is the durable store, so
  `local-feed` may be pruned up to the lowest version any consumer still pins (each consumer's `Directory.Build.props`
  `<KhaozEngineVersion>`; do not prune below it) without losing anything recoverable.
- **Full doc sweep on EVERY feature / bug / change - not just the guard-checked declarations.**
  `check-doc-versions.sh` verifies the engine-version declarations, the newest `CHANGELOG.md` heading, AND the
  package inventory (every packable package has a README catalog row and ships its own `<Package>/README.md`).
  What it does NOT check is whether
  any of that prose is CORRECT: a stale catalog row, or a package README describing removed API, sails straight
  through. The sweep below is for content accuracy, which no guard can do for you.
  Each artifact has ONE canonical source - edit that one, the rest point at it:
  - **Package + umbrella catalog -> `README.md`** (the package table + "Umbrella metapackages" table, plus the
    repo-layout block). When a package is ADDED/REMOVED or its summary/deps change, edit the README table.
    This `AGENTS.md` only POINTS at the README catalog - do not re-enumerate packages here. (7.34.0 shipped with
    the README package table missing two new packages - one source means one place to forget.)
  - **Per-package API -> that package's own `<Package>/README.md`.** When public API is ADDED/CHANGED WITHIN an
    existing package, update the package's own README - the `PackageReadmeFile` that ships *inside the nupkg* and is
    read standalone on NuGet.org, so it rots independently of the master catalog (the `NetWorld` README still
    described pre-8.0.0 `WorldColliders`/`WorldSurfaces` ctor params two releases later; 8.2.0's telemetry types
    were missing from the `Diagnostics`/`Gui`/`Netcode` READMEs). These stay self-contained; keep them correct.
  - **Usage -> `docs/USING-KHAOZENGINE.md`** (a section for new public API); **seams/edges ->
    `docs/DEPENDENCY-SEAMS.md`** whenever a dependency edge or a seam member changed.
  - **`CHANGELOG.md`** + the version bump as before; for a behaviour/bug change also fix any doc, README, or code
    comment that described the OLD behaviour.
  - **Discovered follow-ups -> a GitHub issue.** Anything the change knowingly leaves undone, defers, or
    works around is filed as you find it, with a `confidence/*` label, and any issue the change resolves
    is closed as it lands.
  - **Design rationale -> `docs/design/`, and NOTHING else lives there.** A design doc holds the why: the
    alternatives weighed, the decisions taken, and the reasoning that a corrected-in-flight implementation
    proved out. It is not a reference surface, so shipped API and usage move to `CHANGELOG.md` /
    `docs/USING-KHAOZENGINE.md` / the package README as they land. An IN-FLIGHT program's doc may carry its own
    round-scoped deferrals as working notes (the map editor does, and its `kind/roadmap` issue delegates to it),
    but that licence ends with the program: anything still open when the program completes MUST become an issue,
    because a design doc nobody is actively working is not a ledger anyone reads. The dungeon generator stranded
    three follow-ups exactly there, invisible for 20+ releases after it finished (now filed as #74). Add a row +
    a status line to `docs/INDEX.md`'s design table when you add a doc, or it is orphaned on arrival (three nav
    docs were, for releases). A complete design doc is KEPT as history rather than deleted,
    because the reasoning behind a shipped decision is the expensive thing to reconstruct. `docs/` root is for
    the living docs only.
  Mechanical check before committing: grep the new (or removed) type / package / flag name across **ALL `*.md`
  recursively** (root, `docs/`, `docs/design/`, AND every per-package `<Package>/README.md`) + `AGENTS.md`, and confirm every place
  that should mention it does (and no stale doc still describes what you removed).
- `scripts/check-doc-versions.sh` enforces three things. First, that the engine-version declarations
  (EVERY `<PackageReference>` example line in `README.md` AND `docs/USING-KHAOZENGINE.md`, not just one) match the
  **engine version line** (`<KhaozEngineVersion>`). `docs/ROADMAP.md` "Current released version" was checked here
  too and is not any more: the roadmap is issues now, the file is gone, and no prose copy of the version is left
  to drift. Second, that the newest `CHANGELOG.md` heading is `## <KhaozEngineVersion>`, so a version bump cannot
  ship without a changelog entry that actually names the new version (the pre-commit and PostToolUse hooks only
  check the file was touched). Third, that every packable package has a README catalog row and ships its own
  `<Package>/README.md` via `<PackageReadmeFile>`, which is what catches a new package landing undocumented. CI
  runs it on every push, so a forgotten bump, a stale doc version, a missing changelog entry, or an undocumented
  package fails the build. Consumer pins are exempt and may lag.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **One shared version line - the engine is entirely MonoGame-free.** `Directory.Build.props` carries a single
  `<KhaozEngineVersion>` governing the WHOLE engine; every packable project sets
  `<Version>$(KhaozEngineVersion)</Version>` in its csproj, so one bump releases all packages together (repack to
  `local-feed`, single tag `vX.Y.Z`). `check-doc-versions.sh` enforces this line. **Per-version history lives in
  `CHANGELOG.md`, and the package + umbrella catalog (every package, what it gives you, and its deps) is the
  table in `README.md` - not here.** When a package is added/removed or a summary/dep changes, edit the README
  table (the single source) plus that package's own `<Package>/README.md`; this file only points at the README
  catalog. The only package facts kept here are the engine-dev orientation below:
  - **Dependency layering** - the SHAPE only. Which packages exist, which umbrella carries each one, and the
    full per-package deps are the README catalog and its "Depends on" column. Do not re-enumerate any of that
    here: the duplicate is what rots, and the README's copy is guard-checked while a copy here is not.
    `Primitives` sits at the bottom of the render/runtime stack, which layers `Gpu` -> `Windowing` ->
    `Render2D`/`Render3D` -> `Gui`/`Game`/`Game.Render3D`. The GPU-free `Foundation` packages sit beside it
    (9.0.0 folded Pooling into Primitives, Localization into App, and Effects into Particles). `Gui` also
    references `App` (for the `LocalizedText` localization sink type, acyclic - `App` never references `Gui`).
    `Simulation` sits at the bottom of the server/netcode stack, which layers `Simulation` ->
    `Netcode`/`Replication`/`Sharding`/`WorldStore` -> `NetWorld`. `Ecs` depends on `Simulation` (acyclic).
    Not every package is in an umbrella: the opt-in ones (`Physics.Bepu` and the `*.Sqlite`/`*.SqlServer`
    backends are the usual suspects) must be referenced explicitly. Each umbrella's own csproj
    `ProjectReference` set is the authority on what it carries - read it, do not trust a prose list.
    A `netstandard2.0` Roslyn analyzer `KhaozEngine.Localization.Analyzers` (KELOC001/002/003) flows to consumers via the `Game2D`/`Game3D` umbrellas.
    The four umbrellas (`Foundation`, `Game2D`, `Game3D`, `Server`) are code-free dependency groups.
  - **Gotchas / history:** the package id is `KhaozEngine.Sharding`, NOT `KhaozEngine.World` (a `World` leaf would
    shadow the ECS `World` type). The legacy 4.x MonoGame line + its six packages
    (`UI`/`Graphics`/`Screens`/`Sprites`/`Input`/`Time`) were DELETED - there is no 4.x line; all consumers are on
    the 7.x/8.x line (each consumer pins its own version in its `Directory.Build.props`). The line dropped `-experimental` at `5.31.0`; the foundation
    graduated onto it at `5.46.0`. See `CHANGELOG.md` (top) for the MonoGame-free pivot history.
- **Commit subjects:** conventional-commit style `area(scope): summary`, e.g.
  `audio(4.3.1): MacOsMusicBackend loads built .ogg` or `docs(readme): ...`.
  On a release/version-bump commit, use the new version as the scope (`audio(4.3.1):`).
- **One version bump per batch, not per item (avoid version-number churn).** When a worktree promotes several
  related items, commit each item individually but do the single `Directory.Build.props`
  bump + `CHANGELOG.md` entry + `dotnet pack` ONCE at the end of the batch, then do
  per-consumer adopt PRs. Never bump the version per-item within a batch. Same spirit for a small
  standalone fix landing alongside other small work: fold it into that shared bump rather than cutting
  its own `vX.Y.Z`, and lean on the trivial-change exception (no bump at all) for anything that ships no
  package, so the engine version does not creep through a run of one-line releases. A FINISHED feature
  still auto-publishes right away (merge + tag + push, don't hold) - batching is about not fragmenting one
  unit of work across several tiny releases, never about holding a finished feature.
- **The batch gate: this one specific call is the user's, so ASK.** Check, at the point of bumping,
  whether a bump is already in flight but unreleased (the current `<KhaozEngineVersion>` is ahead of the
  newest `vX.Y.Z` tag: staged, not yet tagged). If it is, and your change is small enough that it could
  ride that version instead of minting a new one, **stop and ask the user which**. Do not decide it
  yourself.

  Riding an in-flight version means appending your notes to its existing `CHANGELOG.md` entry rather than
  bumping again. Whether that is right turns on what the user intends to release and when, which is not
  visible from the repo: you can see that `<KhaozEngineVersion>` is ahead of the newest tag, but not
  whether that staged version is minutes from tagging or parked pending a bake, nor whether your change
  belongs in the same entry as what is already sitting there. Guessing silently either creeps the version
  through a swarm of tiny releases or smuggles an unrelated change into someone's staged release.

  Ask ONLY at that moment. Not on every bump. When `<KhaozEngineVersion>` is already tagged (nothing in
  flight), cut a fresh version and carry on without asking, exactly as before. A change substantial enough
  to stand alone also just takes its own version, no question needed. The gate is about which version a
  small change rides, never about whether to publish: a finished feature still auto-publishes without
  asking, per the concurrent-dev rule above.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- **SessionStart injects the discovered-work ledger** into every session: the open backlog count, via
  `scripts/session-context.sh` on top of `scripts/ledger.sh`. Informational, never blocks. There is no
  handoff-reciprocity guard any more (`scripts/check-handoffs.sh` and `HANDOFF_CHECK_OK` are both retired):
  a cross-repo handoff is an issue reference now, and GitHub backlinks it for free, so there is no
  one-sided handoff left to block.
- **Two guards keep the backlog files retired.** A pre-commit check and a `Write`/`Edit` agent hook both
  reject re-creating `docs/TODO.md` or `docs/ROADMAP.md` (migrated to GitHub Issues 2026-07-17, deleted),
  pointing you at `gh issue create` instead. Override a deliberate exception with `BACKLOG_FILE_OK=1`.
- **The ledger needs a token, and it will tell you so.** The backlog is GitHub Issues in a private repo,
  so there is no anonymous read: `scripts/ledger.sh` needs `gh auth login` or `GH_TOKEN` exported. Codex
  and CI generally need the env var. When it cannot read, it says `BACKLOG: UNKNOWN` or `STALE MIRROR` and
  names the fix. **It never says `0`.** That is deliberate and is the one invariant worth protecting here:
  `gh issue list` exits non-zero with empty stdout on dead auth, and the obvious `gh issue list | jq length`
  renders that as `0 open`, which is not a degraded answer but an inverted one. "0 open" reads as "the
  sweep is clean" at the precise moment the tool has no idea what is open. So a count is only ever printed
  when it was actually read, and everything else is loud.
- net10.0, MonoGame-free: Silk.NET (windowing + input, GLFW natives bundled per-RID), Veldrid behind
  `KhaozEngine.Gpu` (GPU), Silk.NET.OpenAL (audio), xUnit (tests).

## Discovered work (follow-ups and chips)

Two rules. The first one matters more.

### Durable work product first

A validated result must exist as a pushed commit before you move on. Not only in a container, a scratch
clone, a sandbox, a subagent's context, or a chat transcript. If you built and validated a fix, a repro,
a bake, or a measurement, push it to a branch and then carry on. A backlog entry about that work is a
POINTER to the sha, never a prose description of a fix that exists nowhere.

On 2026-07-17 a validated GPU crash fix evaporated with the throwaway container it was built in, while
its sibling fix survived as a commit on a pushed branch. The note later written about the lost one
preserved the knowledge that a fix had existed and none of the fix. A note hardens the memory of work.
Only a push hardens the work.

### The ledger

Work you notice but do not do becomes a **GitHub issue** before you carry on. Not your head, not only a
chat chip. A chip is a notification, its id does not survive a restart, and the chat that finds a
problem is usually not the chat that fixes it. The issue is the only durable record. There is no
`docs/TODO.md` and no `docs/ROADMAP.md` any more, and no file to keep in sync.

```
gh issue create --label kind/backlog --label confidence/lead --title "..." --body "..."
scripts/ledger.sh search <term>    # prior art, INCLUDING closed issues. Use before filing.
scripts/ledger.sh status           # what the open pile looks like right now
```

**Search before you file.** `ledger.sh search` greps a local mirror of every issue, open and closed.
Use it, not GitHub's search box: GitHub tokenizes, so it will not reliably find `WorldColliders` or
`KELOC001`, and those identifiers are exactly how you look things up. A hit on a *closed* issue is the
most valuable result it can give you, because it usually means "this was investigated and declined,
here is why".

**Raise it at discovery.** The moment you notice it (something you would spawn a chip for, a TODO you
would otherwise leave in code, a gap a bake or a consumer adopt exposes, a workaround you accept to keep
moving), file it. Before you continue the current task, not at the end. The end of a task is where
context runs out and the item evaporates. Spawning a chip does not discharge this. The chip is the
notification, the issue is the record, so do both.

**Never action it mid-task.** A discovered item must NEVER redefine the scope of the work in flight.
That is how a chat sent to fix X quietly ships Y instead. Action open items at your next checkpoint,
which is the moment you are about to end your turn and report back. That moment is reachable in every
session, including the debug and playtest sessions that never cut a release. "The current sub-task feels
finished" is NOT a checkpoint: that boundary is drawn by the same agent that wants to go do the
interesting thing it just found.

At the checkpoint, anything small and self-contained is a subagent job, so do it then and say you did.
Anything needing its own design, its own release, or another repo is handed off and reported, not
started.

**Say how much to trust it.** Every backlog issue carries a `confidence/*` label, and it is required
(`.github/workflows/issue-confidence.yml` flags anything filed without one, CLI included). This is the
thing most often lost: a checked finding and an unverified guess look identical once they are both just
an issue in a list, and acting on a guess as though it were a finding wastes exactly the time the guess
was meant to save. `confidence/verified` = checked against the code. `confidence/lead` = surfaced, not
checked, may well be wrong. `confidence/authored` = written deliberately, with the context.

**A handoff is a cross-repo issue reference, and you write it as a full URL**
(`https://github.com/APKiwiOrg/Nullwake/issues/45`), never the short `APKiwiOrg/Nullwake#45` form. The
short form renders as a link and creates NO backlink between these private repos, so the handoff reads
as filed while being invisible from the other end. That is not hypothetical: SpaceGame#69 sat on
`needs/upstream` pointing at nothing for exactly this reason, and `scripts/check-handoffs.sh` was
retired on the belief that the short form linked both sides. Plain `#123` is still correct WITHIN one
repo. Written as a URL it does backlink both sides, so there is nothing left to keep reciprocal by
hand. Label yours `needs/upstream`. This is the common
direction here: the engine is upstream of four games, so a consumer-side item the engine is blocking
(or a game-side gap an engine change creates) is a reference, not a pair of hand-written entries. For
something that cannot answer back (a branch, a chat, a person), just say so in the body. A branch is
not a party, and a scoping prompt pasted into another chat is not a handoff either, because that chat
can drift and nothing records that it was ever asked.

**Consumer fit-failure pairs are the decline ledger's inflow, and the engine treats them as a primary
API-gap signal.** A game that cannot adopt an engine type files a pair: a `needs/upstream` record on
its side and, here, a `kind/backlog` (or `kind/roadmap` if it needs a spec) issue carrying the
code-cited fit evidence with `confidence/verified` and the fleet `parity` label, cross-linked by full
URL both ways and added by the filer to the org board
(https://github.com/orgs/APKiwiOrg/projects/1, which has no auto-add). These are not noise. They are
the main way the engine learns what blocked adoption, especially for `Gui` and utility API gaps where
a missing knob (a text scale, a predicate hook) silently forces a bespoke fork downstream. The
precedent pair is https://github.com/APKiwiOrg/KhaozEngine/issues/237 (here) and
https://github.com/APKiwiOrg/SpaceGame/issues/82 (SpaceGame). The consumer-side process that generates
these lives in `GameTemplate/docs/ENGINE-INTEGRATION.md` and the games' own
`docs/ENGINE-INTEGRATION.md`, and is not restated here.

**Resolved means CLOSED, on the spot. Close it, do not delete it.** The moment an item is done, close
the issue, in the same sitting, ideally from the commit that resolved it (`Closes #123`). Closing is
what a tick could never be: it takes the item out of the open pile AND keeps the whole record
searchable forever. That is the one real gain of issues over a file, so do not throw it away by
deleting anything.

**A decline is CLOSED AS NOT PLANNED, with the reason in the issue and `confidence/refuted` on it.**
Never silently. "Forgotten" is not a disposition. Write what you ruled out and why, in the issue, then
close it as not planned. It stays greppable via `ledger.sh search`, which is the entire point: an item
declined once and then unfindable gets re-raised by the next agent that reads the same suggestive
comment, and then again by the one after that. Four separate agents re-raised the same `SwingCooldowns`
non-bug because the record of the first decline had been deleted. Closed-with-a-reason is what stops
that. If you are not willing to write the reason, the item is not declined and stays open.

### Filing

- Use the issue forms (**Backlog item** / **Roadmap item**). Blank issues are off, because a blank issue
  cannot carry a confidence rating.
- **Backlog vs roadmap** is the old TODO-vs-ROADMAP split, now two labels instead of two files.
  `kind/backlog` is the chip pile. `kind/roadmap` is the program list: anything that earns its own design
  spec and its own release. **If it needs a spec, it is a roadmap item. Otherwise it is a TODO.** That is
  the whole test, and it did not change when the files went away. A roadmap item's spec still lands as a
  `docs/*-DESIGN.md` in this repo, with the issue pointing at it.
- Title it the way you would say it out loud. Then, in the body, write enough context and file links to
  action it without the chat that found it: paths, symbols, line numbers, what you already ruled out.
  Do not compress what you know into a task title. The next reader has none of your context, and
  re-deriving it costs far more than writing it down did.
- Priority is a `priority/*` label (`critical` > `high` > `medium` > `low`), synced fleet-wide by
  `scripts/sync-labels.sh` and mirrored on the org board's Priority field
  (https://github.com/orgs/APKiwiOrg/projects/1), not the board's order. The board still tracks status
  (Todo / In Progress / Done); the ordered `docs/ROADMAP.md` list it replaced is in git history.

## Localization

**Localization is a founding principle.** All player-facing text (UI, menus, settings, dialogue,
tooltips, notifications, player-visible errors and status) must resolve through the localization
catalog via a `StringId` (`LocalizationManager` / `IStringCatalog`), never a hardcoded display literal.
Add the string to the catalog first, then reference it. The engine is moving player-facing Gui sinks to
a `LocalizedText` value type so bare strings become a compile error. Prefer that API as it lands.
Exemptions: developer/debug-only UI and non-localizable tokens (proper names, numbers) via the explicit
raw escape hatch, kept greppable.
