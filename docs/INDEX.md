# KhaozEngine docs index

Start here. Two kinds of doc live here and they have different lifecycles:

- **Living docs** (this directory's root) are kept current and edited in place. When two of them state the
  same fact, the source-of-truth column says which one wins.
- **Design docs** ([`design/`](design/)) are the rationale behind a program: the why, the alternatives
  weighed, the decisions taken. They are NOT a reference surface. Shipped API and usage move out to
  CHANGELOG / USING / the package READMEs as they land, and what stays behind is the reasoning. A design doc
  is written once, corrected in place while the program is in flight, and then kept as history.

## Living docs

| Doc | What it's for | Source of truth for |
|---|---|---|
| [README.md](../README.md) | Top-level overview: the package table (granular packages + umbrella metapackages), the one hard rule, quickstart wiring, repo layout. | The package list + what each package owns. Guard-enforced complete by `check-doc-versions.sh`, so never trust a prose package list anywhere else. |
| [USING-KHAOZENGINE.md](USING-KHAOZENGINE.md) | The consumer contract: hard rules, the data-flow model, per-layer API reference, headless-test patterns. Read before wiring a game in. | How a game must use the engine. |
| [CHANGELOG.md](../CHANGELOG.md) | Newest-first, every version; each entry leads with a one-line summary, so the file doubles as the scannable "story over time". | Release history (full detail + the high-level digest). The single source for the per-version story; nothing else restates it. |
| [CROSS-PLATFORM.md](CROSS-PLATFORM.md) | Desktop GPU story: platform → Veldrid backend mapping (Metal/D3D11/Vulkan), the golden-snapshot net, the CI matrix. | How rendering is verified per OS. |
| [SECURITY-BASELINE.md](SECURITY-BASELINE.md) | The engine-wide security posture: threat model (where untrusted bytes enter), layered defenses (managed memory-safety, input validation, patched deps, signed updates, the CETCompat tradeoff), what's out of scope, and per-game vs engine responsibilities. | The security posture every game inherits. |
| [UPDATER.md](UPDATER.md) | How a consuming game wires up the signed auto-updater (`KhaozEngine.Updates`): why Azure Blob over GitHub Releases, key generation + the embedded public key, the signed manifest + feed layout, the OIDC/Key-Vault publish CI flow, the apply/rollback shim, and the macOS `.app` re-sign caveat. | How a game consumes the updater. Per-game specifics live in each game's own `docs/UPDATER.md`. |
| [RENDER-PIPELINE.md](RENDER-PIPELINE.md) | High-level (container-level) Mermaid map of how a C# draw call becomes rendered triangles: Render2D/Render3D -> the Gpu seam -> Veldrid -> backend -> GPU, plus the geometry-upload and GLSL->SPIR-V side-flows. | Understanding the render path. |
| [PHYSICS-PIPELINE.md](PHYSICS-PIPELINE.md) | High-level (container-level) Mermaid map of how a character move becomes a collision-resolved position: Locomotion -> the `IPhysicsWorld` seam -> Physics.Bepu -> BepuPhysics, plus the shape-bake and authoritative-vs-predicted side-flows. | Understanding the physics path. |
| [DEPENDENCY-SEAMS.md](DEPENDENCY-SEAMS.md) | The convention RENDER-PIPELINE and PHYSICS-PIPELINE are instances of: how every third-party library (GPU, physics, netcode, persistence, audio, windowing) is wrapped behind a dependency-free seam with an opt-in backend, and how to add a new backend. | The seam + swappable-backend pattern. |
| `../<Package>/README.md` | One-paragraph purpose + a snippet per package. Ships inside the nupkg via `PackageReadmeFile`, so it is what a NuGet consumer reads. | Per-package quick reference. |

The **engine current version** lives in `../Directory.Build.props` (`<KhaozEngineVersion>`). Docs that restate it
(the README and USING-KHAOZENGINE PackageReference examples) are guarded by
[`../scripts/check-doc-versions.sh`](../scripts/check-doc-versions.sh), which CI runs on every push. That script
also checks the newest `CHANGELOG.md` heading is for the current version, and enforces the package inventory:
every packable package must have a README catalog row and ship its own `<Package>/README.md`. Consumer *pins*
are allowed to lag and are not checked.

The **backlog and roadmap** are GitHub Issues now (`kind/backlog` / `kind/roadmap`), searchable via
[`../scripts/ledger.sh`](../scripts/ledger.sh). The old `docs/ROADMAP.md` / `docs/TODO.md` files were migrated
there on 2026-07-17 and deleted.

## Design docs ([`design/`](design/))

Rationale, not reference. Status is against the engine version line, and a "complete" doc is kept as history,
not deleted: the reasoning behind a shipped decision is the thing that is expensive to reconstruct later.

| Doc | The program | Status |
|---|---|---|
| [MAP-EDITOR-DESIGN.md](design/MAP-EDITOR-DESIGN.md) | The MapDoc zone-document format, the MapEditor GUI runtime + per-game editor heads, the `ke-mapedit` MCP tool, phasing, and the Ruinborne adoption path. | **In flight.** Phases A-D shipped 10.44.0-10.67.0 and eight further rounds through 11.4.0. Open work is tracked in GitHub Issues (`kind/backlog` / `kind/roadmap`), not here. |
| [DUNGEON-GENERATOR-DESIGN.md](design/DUNGEON-GENERATOR-DESIGN.md) | `KhaozEngine.Dungeon`: deterministic grow-and-embed layout, completability by construction + an always-on solver, MapDoc bake + runtime stamp sinks, the greybox kit contract, the `ke-dungeon` CLI / MCP verbs. | **Complete.** Shipped 10.56.0-10.58.0, polish to ~10.109.0, `DungeonNav.Bake` at 10.123.0. Deferred follow-ups are tracked in GitHub Issues (`kind/backlog`). |
| [NPC-NAVIGATION-DESIGN.md](design/NPC-NAVIGATION-DESIGN.md) | `KhaozEngine.Navigation`: the founding grid-vs-navmesh trade-off, the clearance grid, and the statics-only call. | **Complete.** Shipped 10.123.0. |
| [NAV-STEP-SURFACES-DESIGN.md](design/NAV-STEP-SURFACES-DESIGN.md) | The follow-on to 10.123.0: walkable step surfaces, and the `INavSurfaceProvider` dependency-inversion decision. | **Complete.** Shipped 11.2.0. |
| [NAV-HOP-LINKS-DESIGN.md](design/NAV-HOP-LINKS-DESIGN.md) | The follow-on to 11.2.0: vertical hop links, the generation rule, and the admissibility derivation. | **Complete.** Shipped 11.8.0. |
| [PARTICLES-VFX-DESIGN-2026-07-16.md](design/PARTICLES-VFX-DESIGN-2026-07-16.md) | The particles/VFX modernization: the three-package split (`KhaozEngine.Particles` sim, Render3D's modern particle pass, the `Particles.Render3D` adapter + presets), the additive `EmitterConfig` evolution, procedural SDF shapes, the soft-depth instanced pass. | **Complete.** Shipped 10.126.0. |
| [AAA-VFX-TIER1-DESIGN-2026-07-16.md](design/AAA-VFX-TIER1-DESIGN-2026-07-16.md) | AAA VFX Tier 1: the HDR float16 chain + ACES filmic tonemap + pre-tonemap bloom, flipbook particles with motion-vector blending, and the screen-space distortion pass. | **Complete.** Shipped 10.128.0 / 10.129.0 / 10.130.0, plus ChromaPreservation at 11.7.0. Tiers 2/3 are tracked in GitHub Issues (`kind/roadmap`) with no design doc yet. |
| [BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md](design/BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md) | Moving the procedural starfield out of the final blit into a real background pass (plus the `BackgroundMode` enum folding `Post.Starfield` / `Sky.Enabled`), then the opt-in `GroundDecal.VoidFallback` virtual-plane projection it unblocks. | **Complete.** Shipped 11.9.0 (Release 1) and 12.1.0 (Release 2). Both halves of the original rule were corrected in place during implementation; the doc records the corrected reasoning. |
| [CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md](design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md) | Splitting the `KhaozEngine.Tests` monolith into per-area test projects aligned to the package layering, and the `dotnet-affected` selective path in ci.yml (pushes/PRs build+test only affected projects, tags and the weekly sweep stay full). | **Complete.** Shipped 13.0.3. Selection-precision follow-ups resolved: [#211](https://github.com/APKiwiOrg/KhaozEngine/issues/211) (over-selection, Showcase tests split to their own project) and [#212](https://github.com/APKiwiOrg/KhaozEngine/issues/212) (under-selection, csproj change adds the architecture-test rump). |
| [RECONNECT-SCREEN-DESIGN-2026-07-18.md](design/RECONNECT-SCREEN-DESIGN-2026-07-18.md) | `KhaozEngine.Gui`'s connection-outage primitive: `ConnectionStatusController`'s banner-then-screen escalation policy (with the anti-flicker floor), why both halves live in `Gui` rather than `Game`, and the `ReconnectScreenTheme`/`LocalizedText` decisions. | **Complete.** Shipped 13.2.0. |
| [SAVE-VALIDATION-DESIGN-2026-07-18.md](design/SAVE-VALIDATION-DESIGN-2026-07-18.md) | The save validation/management pass: the versioned v2 save envelope (tamper-protected metadata), the strict-with-recovery tamper posture + backup-generation ladder, default-on encoding, fsync + rotation in the write path, and `WorldPersistence`'s validate-and-quarantine hooks. | **In flight.** Program issue [#224](https://github.com/APKiwiOrg/KhaozEngine/issues/224). Design approved, implementation pending. |
| [SCREEN-COMPONENT-DESIGN-2026-07-18.md](design/SCREEN-COMPONENT-DESIGN-2026-07-18.md) | `IScreenComponent` + `ScreenComponentList`, the composition unit below `Screen`: why an interface rather than a base class, why `bounds` is per-call, why `Screen` was not modified, why font/white are not on `Draw`, and why the three Screen+View pairs [#226](https://github.com/APKiwiOrg/KhaozEngine/issues/226) names were NOT migrated (one collaborator each). | **Complete.** Shipped 13.7.0. Consumer adoption is [SpaceGame#69](https://github.com/APKiwiOrg/SpaceGame/issues/69). |
| [GUI-TEXT-SCALE-DESIGN-2026-07-19.md](design/GUI-TEXT-SCALE-DESIGN-2026-07-19.md) | The text-scale family for the three retained widgets that draw text: `Button.LabelScale`, `TabBar.TextScale`, per-line `TooltipLine.Scale` plus `Tooltip.TitleScale`. Corrects two sub-claims in [#232](https://github.com/APKiwiOrg/KhaozEngine/issues/232) (`Toggle`/`Slider` render no text at all, and `TabBar` did not already scale its text), and records the deferred `Dropdown`/`TextInput`/`TreeView`/`ProgressBar` gap plus the `Tooltip` parity audit against SpaceGame's `TooltipRenderer`. | **Complete.** Shipped 14.1.0, closing [#232](https://github.com/APKiwiOrg/KhaozEngine/issues/232) (Button/TabBar half, the Toggle/Slider half closed as not-planned) and [#237](https://github.com/APKiwiOrg/KhaozEngine/issues/237). The doc's own section 7 called this a coordination artifact needing no INDEX row by default, listed here since the file already landed in `docs/design/` on this branch, per the maintainer-disagrees path the doc itself names. |

**Adding a design doc.** Put it in `design/`, add a row above, and give it a status line. A doc for a program
that is in flight carries its open work in GitHub Issues (`kind/backlog` / `kind/roadmap`), never as a private
backlog here: a follow-up recorded only in a design doc is invisible to the ledger and gets lost.

## Process docs

- [../AGENTS.md](../AGENTS.md) - concurrent-dev rule (worktree per change), the release ritual, build/test commands.
- Release ritual, short form: bump `Directory.Build.props` `<KhaozEngineVersion>` -> add the `CHANGELOG.md` entry
  (its newest `## X.Y.Z` heading must be the new version) -> update every `<PackageReference>` example in `README.md`
  and `USING-KHAOZENGINE.md` -> `dotnet pack -c Release -o ./local-feed` -> commit -> `scripts/tag-release.sh` (annotated `vX.Y.Z`) ->
  push `main` + tag. (Don't hand-type `git tag vX.Y.Z`: a lightweight tag is rejected by the `pre-push` hook.)
