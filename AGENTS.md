# KhaozEngine

KhaozEngine is the shared MonoGame-free 2D and 3D engine used by the APKiwiOrg games. The root
[`README.md`](README.md) is the authoritative package and umbrella catalog.
[`docs/USING-KHAOZENGINE.md`](docs/USING-KHAOZENGINE.md) is the consumer contract.

This is the canonical tool-neutral instruction entry point. `CLAUDE.md` imports it and Codex reads it
directly. Keep this root short enough to load with global guidance. Detailed repository rules live in
[`docs/CONTRIBUTOR-RULES.md`](docs/CONTRIBUTOR-RULES.md).

## Route by task

| Task | Read first |
|---|---|
| Engine code or tests | [Contributor rules: Engine code and test contracts](docs/CONTRIBUTOR-RULES.md#engine-code-and-test-contracts) |
| Package or dependency changes | [Contributor rules: Dependencies and package structure](docs/CONTRIBUTOR-RULES.md#dependencies-and-package-structure), [DEPENDENCY-SEAMS.md](docs/DEPENDENCY-SEAMS.md) |
| Build or CI work | [Contributor rules: Build and CI](docs/CONTRIBUTOR-RULES.md#build-and-ci), [CI selective design](docs/design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md) |
| Version, pack or release work | [Contributor rules: Version, package and release rules](docs/CONTRIBUTOR-RULES.md#version-package-and-release-rules) |
| Documentation | [Contributor rules: Documentation governance](docs/CONTRIBUTOR-RULES.md#documentation-governance), [docs index](docs/INDEX.md) |
| Discovered work or handoffs | [Contributor rules: Issues and handoffs](docs/CONTRIBUTOR-RULES.md#issues-and-handoffs) |
| Rendering verification | [CROSS-PLATFORM.md](docs/CROSS-PLATFORM.md), relevant pipeline and design docs |
| Security-sensitive work | [SECURITY-BASELINE.md](docs/SECURITY-BASELINE.md) |

## Before changing anything

- Work in an isolated worktree. Inspect active worktrees, branches and fetched state first. Join a
  matching worktree or branch from current `origin/main`. The narrow clean-main exception is limited
  to a self-contained documentation typo, comment, governance edit or one-line non-API fix with no
  conflicting work.
- Use `feature/<name>` or `fix/<name>`. Promotion batches may use `<batchN>-promote`.
- This repository is not a game-template adopter. Do not bulk-copy game manifests, version rules,
  feeds or hooks into it. Adopt a shared script only when its contract applies, and preserve the
  engine-specific release, local-feed and doc-version guards.
- In shared worktrees, stage and commit explicit paths. Never use the shared stash for coordination.
- Existing unrelated changes belong to their owner. Do not revert or absorb them.

## Binding engine contracts

- `AppWindow` is the only class that touches raw Silk.NET or GLFW input. All other code reads
  `InputState` through `InputManager` and `Pointer`, including hit tests.
- New behavior gets a headless test in the matching area test project. Test projects reference only
  the engine projects they use and keep namespaces under `KhaozEngine.Tests.*`.
- Tests that mutate process-global state use a collection definition with
  `DisableParallelization = true`.
- Player-facing text resolves through the localization catalog with `StringId`. Prefer
  `LocalizedText` at GUI sinks. Raw text is limited to developer output and non-localizable tokens.
- Third-party libraries sit behind dependency-free engine seams with opt-in backends. Read
  `docs/DEPENDENCY-SEAMS.md` before changing an edge.
- KESIZE is a structure ratchet. Put new behavior in a new type. Do not split at an arbitrary line.
  Baseline growth and exemptions require owner approval. Ratchet down freely with
  `scripts/check-file-size.sh --update`.
- Warnings are errors. Fix them at the source. Do not add blanket suppressions or disable the rule.
- CI tests Release. Validate any Debug-only behavior in Release with a configuration-independent
  observable.
- No em-dash or en-dash glyphs in shipped prose or comments. No prose semicolons in Markdown. Run the
  whole-tree guards before completion.

## Build and test

Create the gitignored local source before restore:

```sh
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
```

Also run the checks appropriate to the files changed. Repository governance changes run at least:

```sh
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Plain `dotnet test` skips GPU facts. GPU work follows `docs/CROSS-PLATFORM.md` and uses the relevant
backend CI bake. Do not launch consumer clients for engine tooling or documentation work.

## Version and release boundary

`Directory.Build.props` contains the single `<KhaozEngineVersion>` used by every package. Tooling,
documentation and repository governance do not bump it.

Package-bearing work follows the full ritual in
[`docs/CONTRIBUTOR-RULES.md`](docs/CONTRIBUTOR-RULES.md#version-package-and-release-rules). The durable
minimum is:

1. Re-read current `main`, the version and tags before selecting a version.
2. Put the `CHANGELOG.md` entry in the same commit as a version bump.
3. Update every declaration guarded by `scripts/check-doc-versions.sh`.
4. Build and test Release. Use an explicit private `KHAOZENGINE_FEED` for any pre-merge pack proof.
5. Reconcile through the owning orchestrator, push the validated result, then run
   `scripts/pack-local-feed.sh` from `main` after `origin/main` contains that commit.

Never run a bare pack into `local-feed`. Never hand-create a release tag. `scripts/tag-release.sh`
creates the canonical annotated tag only when a release is explicitly due. The sole automatic tag
exception is an explicitly pinned and waiting consumer.

## Documentation and issues

Each fact has one source:

- Packages and umbrella membership: root `README.md`.
- Public API use: `docs/USING-KHAOZENGINE.md` and each package README.
- Dependency edges: `docs/DEPENDENCY-SEAMS.md`.
- Release history: `CHANGELOG.md`.
- Design rationale: `docs/design/`, indexed in `docs/INDEX.md`.
- Open work: GitHub Issues.

Every feature, fix and API change gets a full Markdown sweep for the changed names and old behavior.
Guarded version declarations are only part of that sweep.

`docs/TODO.md` and `docs/ROADMAP.md` are retired. Search with
`scripts/ledger.sh search <identifier>` before filing. Use `kind/backlog` for actionable work and
`kind/roadmap` when a design spec is required. Every issue carries one `confidence/*` label.
Cross-repository handoffs use `needs/upstream` and full GitHub URLs. Consumer fit failures are linked
issue pairs with code evidence and the engine `parity` label.

## Hook layout

- `.claude/settings.json` is Claude's project hook registration.
- `.codex/hooks.json` is Codex's native project hook registration. It mirrors the Claude registration.
- `.githooks/` is the tool-independent backstop through `core.hooksPath`.
- `scripts/hooks/file-change-guard.pl` normalizes Claude Write/Edit payloads and Codex apply patches.

The agent hooks preserve engine-specific behavior: local-feed and ledger SessionStart setup, dash and
retired-file guards, the KESIZE approval and preflight checks, tag collision, released-pack refusal,
the engine version to changelog reminder and the Stop doc-version check. Do not replace this set with
the smaller game hook set.
