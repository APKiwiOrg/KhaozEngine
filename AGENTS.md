# KhaozEngine

Shared, game-agnostic engine - a custom MonoGame-free 2D/3D render + windowing/input + Gui + ECS + netcode
stack (Hardpoint, Nullwake, SpaceGame, Ruinborne all run on it). See README.md and docs/USING-KHAOZENGINE.md.

This is the canonical, tool-neutral instruction file for every agent (Claude Code, Codex, and any
other) and human contributor. `CLAUDE.md` is a thin `@AGENTS.md` import so Claude Code reads the same
source; Codex reads this file directly.

## Global policy, restated for Codex

Claude loads the full versions from ~/.claude/CLAUDE.md every session. Codex does not, so the rules are restated here in brief:

- Work in a worktree, never loose on main. Finish: fetch, merge main into YOUR branch, build and test there, then merge back and push main right away. Tagging is never automatic.
- Ride the staged version: if the version is ahead of the newest tag, append to its changelog entry, roll the date, no bump, no asking. Nothing in flight: cut exactly ONE fresh version and leave it untagged. Only the user starts a release.
- Ask about releasing only when git worktree list shows you are the last chat standing, once, as the last line of the report.
- A validated result exists as a pushed commit before you move on. A backlog entry is a pointer to a sha, never the only home of a fix.
- Discovered work becomes an issue at discovery and is never actioned mid-task. Resolved means closed on the spot. A decline is closed as not planned with its written reason. Cross-repo handoffs are full URLs, short forms do not backlink private repos.
- No em/en dashes and no prose semicolons in shipped text.

`scripts/check-dashes.sh --tree` is the sweep for that last rule: it checks every tracked `.md`/`.cs`
file as it stands. The pre-commit hook only sees staged additions, and implementers of every tier emit
dashes and then report clean, so run it across the whole branch diff before merging. Since #554, CI
runs it too, alongside `check-prose.sh --tree` and `check-file-size.sh --tree`, in one unconditional
`ci.yml` step on every push and PR. The orchestrator sweep is still the one that catches a violation
before it becomes someone else's red run, and it is the only one that sees a branch that was never
pushed.

Everything below binds those rules to this engine's own mechanics, and wins where it differs.

## Before starting ANY engine work (concurrent-dev rule)
Heavy parallel dev makes the worktree mandatory here (the trivial-change exception below is the only
way out), and finishing a piece of work is merge to `main` + push `main` + pack to `local-feed`.
Before you touch anything:
1. Check for ongoing parallel work first: `git worktree list`, `git branch -a`,
   and `git fetch && git status` to see other branches/trees in flight.
2. If your change fits an existing branch/worktree, work there.
3. If it does not fit any of them, create a NEW worktree (do not start work
   loose on `main` or pile onto an unrelated branch). Isolate the change in its
   own tree so concurrent work does not collide.

That covers every change: code, tests, docs, and version/release work. The one
exception is the trivial-change case below.

- **How to create the tree:** prefer the native `EnterWorktree` tool, not
  `git worktree add`. The native tool is what the parallel-dev workflow expects.
  `EnterWorktree` branches from `origin/<default-branch>` by default. Since this repo
  auto-pushes, `origin/main` is normally current, but a concurrent chat may have just
  landed work - `git fetch` first. If your change builds on local `main` work not yet
  on `origin` (a just-merged commit mid-push), create the tree from local HEAD instead:
  `git worktree add .claude/worktrees/<name> -b worktree-<name> main`, then rename the branch
  immediately (`git branch -m feature/<name>`, or `fix/<name>`) so it obeys the naming rule below,
  then `EnterWorktree` with its `path` to switch in. `EnterWorktree` names its branch
  `worktree-<slug>` too, so it gets the same immediate rename.
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
- **A test that WRITES process-global state enlists in a `DisableParallelization` collection.** xUnit runs
  collections in parallel, so a class that swaps an ambient static and restores it in a `finally` leaves a
  window in which every other class in the assembly reads the other value, which surfaces as a rare failure on
  someone else's branch (#349, `GuiTheme.Default`). The assembly-level collections are the pattern to copy
  (`gui-theme-global`, `ClipboardSerial`, `LoggingSerial`, `AmbientLocalization`, `AllocSensitive`,
  `NativeDeviceLifecycle`), each with the shared state named in its doc comment. The attribute that does the
  work is the `[CollectionDefinition(..., DisableParallelization = true)]`, not the `[Collection]` on the class:
  a `[Collection("name")]` with no definition anywhere serializes that name's classes against each other and
  leaves them running in parallel with everything else, which is how #349 sat open under a collection attribute
  that looked like a fix.
- Hit-test via `InputManager`/`Pointer` bounds helpers (`IsTapIn`, etc.), never raw position + button.
- **The KESIZE file-size ratchet is compile-time, and moving a baseline is the USER's call.** When
  KESIZE001/002 fires, the fix is to put the new code in its own type. Never split a file at an
  arbitrary line to satisfy the check: two god halves are worse than one, and that split is the
  failure the ratchet exists to prevent. When the growth is genuinely legitimate, STOP AND ASK rather
  than editing `.filesize-baseline` yourself. A write-time hook turns any hand-edit of that file into
  a confirmation prompt, so raising a frozen size or granting an exemption cannot land silently
  inside a large diff. Ratcheting DOWN is free and needs no approval: run
  `scripts/check-file-size.sh --update` (it can only lower or drop entries, never raise one) in the
  same branch as the shrink, so the baseline follows the new low-water mark. An `exempt <path>` line
  is for a file whose size is CONTENT rather than STRUCTURE (a generated lookup table, an embedded
  data blob), and never for a test fixture that accreted cases or a screen/frame-loop class, both of
  which should be split instead. **The test is GROWTH, not syntax:** does the file grow only when the
  DATA grows, or also whenever its subsystem gains a feature? Check `git log`, do not reason from what
  the file looks like. "It is all constants" is NOT the test: `ShaderSources.cs` was 2624 lines of
  nothing but `const string` with no logic at all, and was still the wrong candidate, because it grew
  with every renderer feature. It was split into `ShaderSources.<Domain>.cs` partials instead (14.8.1).

## Build / test / release
- `dotnet test` (root, runs `KhaozEngine.slnx` - all 20 test assemblies) - every new behaviour ships with a headless test in its matching per-area project.
- **`ci.yml` runs two paths.** Tag pushes and `workflow_dispatch` run the full sequence (restore, build,
  full test, determinism double-pass, pack, publish). Ordinary pushes and PRs run
  `scripts/ci-selective-test.sh`, which builds and tests only the test projects `dotnet-affected` marks
  affected by the diff, skips entirely on a docs-only diff, and forces full on a
  workflow/scripts/props/slnx/tool-manifest change or a missing base sha. Ahead of that split, both
  paths run the convention step (`check-dashes.sh --tree`, `check-prose.sh --tree`,
  `check-file-size.sh --tree`) and `check-doc-versions.sh` unconditionally, so a docs-only push that
  builds nothing is still gated on content (#554). See
  `docs/design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md` for the full design.
- **Public repo, every leg GitHub-hosted.** The engine went public on 2026-08-06, after its private CI
  became the largest line on the org's GitHub bill, so standard hosted runners are free and no leg
  touches a personal machine any more. `ci.yml` builds, tests, packs and publishes on **x64**
  `ubuntu-latest` (x64 is load-bearing, the test suite needs x64-only natives like `libveldrid-spirv`,
  which ships linux-x64 but not linux-arm64), and the path-gated `cross-platform-gpu.yml` matrix runs
  FIVE blocking golden legs: Metal on hosted `macos-26` (pinned to the number, not to `macos-latest`,
  so an image promotion cannot move the GPU under a golden gate), two Windows/WARP D3D11 legs and two
  Linux/lavapipe Vulkan legs, each pair being the Veldrid incumbent plus the engine's own native
  backend as a GUEST in the incumbent's golden family. That matrix also carries the engine's only
  Vulkan validation gate, in two tiers: `strict` on the native leg's scheduled full suite, and `sync`
  in a separate golden-and-compute job, which is the one instrument in CI that can see a missing
  barrier a software rasterizer orders correctly anyway. `docs/CROSS-PLATFORM.md` is the living doc for
  the matrix, and the games' fleet-wide CI model (org, both runners, secretless OIDC, macOS arm64-only)
  is in `GameTemplate/docs/CI-AND-RUNNERS.md`.
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
- **The shared `<KhaozEngineVersion>` line collides constantly.** Parallel dev is heavy here and
  local `main` is routinely ahead of `origin/main`, so never assume your intended version or tag is
  free. A concurrent chat may have already bumped it and tagged that `vX.Y.Z`, so re-read the current
  version and `git tag` on the up-to-date `main` right before you bump, take the next FREE version
  for your bump + tag, and rebase your `CHANGELOG.md` entry onto it. That collision is auto-resolved,
  no asking.
- Finishing ritual, in order: bump `<KhaozEngineVersion>` in `Directory.Build.props`, unless you are
  riding an in-flight bump (global policy above), in which case skip this step → add the
  `CHANGELOG.md` entry (or append your notes to the in-flight entry when riding) → update the
  engine-version declarations the guard checks (EVERY `README.md` `<PackageReference>` example line,
  one per umbrella) → close any issue this work resolves (`gh issue close`, or `Closes #123` in the
  commit) if it is somehow still open, a backstop only: that should already have happened when the
  work landed, per the Discovered work section → `dotnet pack -c Release -o ./local-feed` (cumulative,
  and happens on every finish whether or not a tag follows) → commit → push `main` right away, don't
  hold or ask. Stop there: a `vX.Y.Z` tag is a separate, deliberate act, never automatic (the user
  starts one, with the single pinned-and-waiting exception below). When a release IS due, cut it with
  `scripts/tag-release.sh` (creates the annotated tag `vX.Y.Z` with the canonical
  `area(<version>): summary`, reading `<KhaozEngineVersion>`, and do NOT hand-type `git tag vX.Y.Z`, a
  lightweight tag is rejected by `pre-push` and is how merge-commit subjects leaked into old tags),
  then push the tag (CI publishes to GitHub Packages on `v*`).
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
  - **Gotcha:** the package id is `KhaozEngine.Sharding`, NOT `KhaozEngine.World` (a `World` leaf would
    shadow the ECS `World` type). Older package history is in `CHANGELOG.md`.
- **Commit subjects:** conventional-commit style `area(scope): summary`, e.g.
  `audio(4.3.1): MacOsMusicBackend loads built .ogg` or `docs(readme): ...`.
  On a release/version-bump commit, use the new version as the scope (`audio(4.3.1):`).
- **One version bump per batch, not per item (avoid version-number churn).** When a worktree promotes several
  related items, commit each item individually but do the single `Directory.Build.props`
  bump + `CHANGELOG.md` entry + `dotnet pack` ONCE at the end of the batch, then do
  per-consumer adopt PRs. Never bump the version per-item within a batch. Same spirit for a small
  standalone fix landing alongside other small work: fold it into that shared bump rather than cutting
  its own `vX.Y.Z`, and lean on the trivial-change exception (no bump at all) for anything that ships no
  package, so the engine version does not creep through a run of one-line releases.
- **The one sanctioned divergence from the global rule: tag immediately, without asking, when a game
  is pinned-and-waiting on this change.** The engine-first rule pauses the dependent game's work until
  the upgrade ships, so holding the tag holds that game. This is the one case the engine tags on its
  own initiative, and the only place in this file where an automatic tag is authorized at all. Riding,
  cutting a fresh version, and the release ask are the global policy at the top of this file, unchanged.
  The default end of a piece of work here is merge, push `main`, pack to `local-feed`, stop.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- **SessionStart injects the discovered-work ledger** into every session: the open backlog count, via
  `scripts/session-context.sh` on top of `scripts/ledger.sh`. Informational, never blocks. There is no
  handoff-reciprocity guard any more (`scripts/check-handoffs.sh` and `HANDOFF_CHECK_OK` are both retired):
  a cross-repo handoff is an issue reference now, and GitHub backlinks it for free, so there is no
  one-sided handoff left to block.
- **Two guards keep the backlog files retired.** A pre-commit check and a `Write`/`Edit` agent hook both
  reject re-creating `docs/TODO.md` or `docs/ROADMAP.md` (migrated to GitHub Issues 2026-07-17, deleted),
  pointing you at `gh issue create` instead. Override a deliberate exception with `BACKLOG_FILE_OK=1`.
- **The ledger still wants a token.** The repo is public now, so the issues are anonymously readable, but
  `gh` rate-limits unauthenticated calls hard enough (60/hour per IP) that a mirror sync will fail part way
  through and look like an outage. Keep `gh auth login` or `GH_TOKEN` exported. Codex
  and CI generally need the env var. When it cannot read, it says `BACKLOG: UNKNOWN` or `STALE MIRROR` and
  names the fix. **It never says `0`**, deliberately, because a count is only ever printed when it was
  actually read (the full case is in the `ledger.sh` header).
- net10.0, MonoGame-free: Silk.NET (windowing + input, GLFW natives bundled per-RID), Veldrid behind
  `KhaozEngine.Gpu` (GPU), Silk.NET.OpenAL (audio), xUnit (tests).

### Agent build workaround: .buildhome (unreadable ~/.gitconfig)

`dotnet build` / `dotnet test` can fail with an error about reading `~/.gitconfig` when run from a
sandboxed or agent context. When, and only when, that specific failure appears, rerun with a
scratch HOME inside the working tree and the real NuGet cache pinned, so restore does not
re-download:

```bash
real_home="$HOME"
mkdir -p "$PWD/.buildhome"
HOME="$PWD/.buildhome" NUGET_PACKAGES="$real_home/.nuget/packages" dotnet build KhaozEngine.slnx
```

Same prefix for `dotnet test`. `.buildhome/` is gitignored fleet-wide (game-template standard):
never commit it, avoid `git add -A` around it, and let it die with the worktree. Where the build
reads `~/.gitconfig` fine, this rule never fires.

## Discovered work (follow-ups and chips)

### The ledger

The backlog is **GitHub Issues** in this repo. There is no `docs/TODO.md` and no `docs/ROADMAP.md`
any more, and no file to keep in sync.

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

**At your checkpoint.** Discovery here usually happens in a bake or a consumer adopt. The checkpoint
is the moment you are about to end your turn and report back, reachable in every session including the
ones that never cut a release. Anything small and self-contained is a subagent job then, so do it and
say you did. Anything needing its own design, its own release, or another repo is handed off and
reported, not started.

**Say how much to trust it.** Every backlog issue carries a `confidence/*` label, and it is required
(`.github/workflows/issue-confidence.yml` flags anything filed without one, CLI included). This is the
thing most often lost: a checked finding and an unverified guess look identical once they are both just
an issue in a list, and acting on a guess as though it were a finding wastes exactly the time the guess
was meant to save. `confidence/verified` = checked against the code. `confidence/lead` = surfaced, not
checked, may well be wrong. `confidence/authored` = written deliberately, with the context. A decline
carries `confidence/refuted` alongside its written reason.

**Handoffs go out labelled `needs/upstream`, written as a full URL**
(`https://github.com/APKiwiOrg/Nullwake/issues/45`). Plain `#123` is still correct WITHIN this repo.
Cross-repo is the common direction here: the engine is upstream of four games, so a consumer-side item
the engine is blocking (or a game-side gap an engine change creates) is a reference, not a pair of
hand-written entries.

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

### Filing

- Use the issue forms (**Backlog item** / **Roadmap item**). Blank issues are off, because a blank issue
  cannot carry a confidence rating.
- **Backlog vs roadmap** is the old TODO-vs-ROADMAP split, now two labels instead of two files.
  `kind/backlog` is the chip pile. `kind/roadmap` is the program list: anything that earns its own design
  spec and its own release. **If it needs a spec, it is a roadmap item. Otherwise it is a TODO.** That is
  the whole test, and it did not change when the files went away. A roadmap item's spec lands in
  `docs/design/` (`<TOPIC>-DESIGN-<YYYY-MM-DD>.md`), never `docs/` root, with the issue pointing at it
  and a row added to `docs/INDEX.md`'s design table.
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
