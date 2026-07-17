# KhaozEngine docs index

Start here. These are the **living docs**, kept current and edited in place. When two docs state the same
fact, the source-of-truth column says which one wins.

## Living docs

| Doc | What it's for | Source of truth for |
|---|---|---|
| [README.md](../README.md) | Top-level overview: the package table (granular packages + umbrella metapackages), the one hard rule, quickstart wiring, repo layout. | The package list + what each package owns. |
| [USING-KHAOZENGINE.md](USING-KHAOZENGINE.md) | The consumer contract: hard rules, the data-flow model, per-layer API reference, headless-test patterns. Read before wiring a game in. | How a game must use the engine. |
| [CHANGELOG.md](../CHANGELOG.md) | Newest-first, every version; each entry leads with a one-line summary, so the file doubles as the scannable "story over time". | Release history (full detail + the high-level digest). The single source for the per-version story; nothing else restates it. |
| [CROSS-PLATFORM.md](CROSS-PLATFORM.md) | Desktop GPU story: platform → Veldrid backend mapping (Metal/D3D11/Vulkan), the golden-snapshot net, the CI matrix. | How rendering is verified per OS. |
| [SECURITY-BASELINE.md](SECURITY-BASELINE.md) | The engine-wide security posture: threat model (where untrusted bytes enter), layered defenses (managed memory-safety, input validation, patched deps, signed updates, the CETCompat tradeoff), what's out of scope, and per-game vs engine responsibilities. | The security posture every game inherits. |
| [UPDATER.md](UPDATER.md) | How a consuming game wires up the signed auto-updater (`KhaozEngine.Updates`): why Azure Blob over GitHub Releases, key generation + the embedded public key, the signed manifest + feed layout, the OIDC/Key-Vault publish CI flow, the apply/rollback shim, and the macOS `.app` re-sign caveat. | How a game consumes the updater. Per-game specifics live in each game's own `docs/UPDATER.md`. |
| [ROADMAP.md](ROADMAP.md) | Future work only: planned features and missing pieces, newest priorities first. No shipped/done sections (that history is in CHANGELOG). | The forward backlog. |
| [TODO.md](TODO.md) | Discovered follow-ups, known gaps left by a release, and consumer pulls from the games. Raised at discovery, actioned at a checkpoint, deleted by the release sweep. | The discovered-work ledger. Programs live in ROADMAP, shipped history in CHANGELOG. |
| [RENDER-PIPELINE.md](RENDER-PIPELINE.md) | High-level (container-level) Mermaid map of how a C# draw call becomes rendered triangles: Render2D/Render3D -> the Gpu seam -> Veldrid -> backend -> GPU, plus the geometry-upload and GLSL->SPIR-V side-flows. | Understanding the render path. |
| [PHYSICS-PIPELINE.md](PHYSICS-PIPELINE.md) | High-level (container-level) Mermaid map of how a character move becomes a collision-resolved position: Locomotion -> the `IPhysicsWorld` seam -> Physics.Bepu -> BepuPhysics, plus the shape-bake and authoritative-vs-predicted side-flows. | Understanding the physics path. |
| [DEPENDENCY-SEAMS.md](DEPENDENCY-SEAMS.md) | The convention RENDER-PIPELINE and PHYSICS-PIPELINE are instances of: how every third-party library (GPU, physics, netcode, persistence, audio, windowing) is wrapped behind a dependency-free seam with an opt-in backend, and how to add a new backend. | The seam + swappable-backend pattern. |
| [MAP-EDITOR-DESIGN.md](MAP-EDITOR-DESIGN.md) | The approved map-editor program design: the MapDoc zone-document format, the MapEditor GUI runtime + per-game editor heads, the ke-mapedit MCP tool, phasing, and the Ruinborne adoption path. | The map-editor design while the program is in flight (shipped pieces move to CHANGELOG/USING/README as they land). |
| [DUNGEON-GENERATOR-DESIGN.md](DUNGEON-GENERATOR-DESIGN.md) | The approved procedural dungeon generator design: `KhaozEngine.Dungeon` (deterministic grow-and-embed layout, completability by construction + always-on solver), MapDoc bake + runtime stamp sinks, the greybox kit contract, and the ke-dungeon CLI / MCP verbs. | The dungeon-generator design while the feature is in flight (shipped pieces move to CHANGELOG/USING/README as they land). |
| [PARTICLES-VFX-DESIGN-2026-07-16.md](PARTICLES-VFX-DESIGN-2026-07-16.md) | The approved particles/VFX modernization design: the three-package split (`KhaozEngine.Particles` sim, Render3D's modern particle pass, the `KhaozEngine.Particles.Render3D` adapter + presets), the additive `EmitterConfig` evolution (curves, shapes, variance, spin, turbulence, trails, authored effects), procedural SDF shapes, and the soft-depth instanced particle pass. | The particles/VFX design rationale (the shipped API lives in CHANGELOG/USING/README + the package READMEs). |
| [AAA-VFX-TIER1-DESIGN-2026-07-16.md](AAA-VFX-TIER1-DESIGN-2026-07-16.md) | The AAA VFX Tier 1 design (complete): the HDR float16 chain + ACES filmic tonemap + pre-tonemap bloom (default on, byte-identical legacy opt-out, unclamped `Color` as the over-1.0 authoring surface), flipbook particles with motion-vector blending, and the screen-space distortion pass. All three sub-features shipped (10.128.0 / 10.129.0 / 10.130.0). | The Tier 1 VFX design rationale. Tier 1 is complete, the shipped API lives in CHANGELOG/USING/README + the package READMEs, and Tiers 2/3 are on ROADMAP. |
| [BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md](BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md) | The approved two-release design: moving the procedural starfield out of the final blit into a real background pass (plus the `BackgroundMode` enum folding `Post.Starfield` / `Sky.Enabled`), then the opt-in `GroundDecal.VoidFallback` virtual-plane projection that the first release unblocks. | The background-pass + void-decal design while the program is in flight (shipped pieces move to CHANGELOG/USING/README as they land). |
| `../<Package>/README.md` | One-paragraph purpose + a snippet per package. | Per-package quick reference. |

The **engine current version** lives in `../Directory.Build.props` (`<KhaozEngineVersion>`). Docs that restate it
(ROADMAP "Current released version", the README PackageReference example) are guarded by
[`../scripts/check-doc-versions.sh`](../scripts/check-doc-versions.sh), which CI runs on every push. Consumer
*pins* are allowed to lag and are not checked.

## Process docs

- [../AGENTS.md](../AGENTS.md) - concurrent-dev rule (worktree per change), the release ritual, build/test commands.
- Release ritual, short form: bump `Directory.Build.props` `<KhaozEngineVersion>` -> add the `CHANGELOG.md` entry ->
  update the engine-version line in `ROADMAP.md` (+ README) ->
  `dotnet pack -c Release -o ./local-feed` -> commit -> `scripts/tag-release.sh` (annotated `vX.Y.Z`) ->
  push `main` + tag. (Don't hand-type `git tag vX.Y.Z`: a lightweight tag is rejected by the `pre-push` hook.)
