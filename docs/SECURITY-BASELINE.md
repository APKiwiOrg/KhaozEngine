# KhaozEngine security baseline

Evergreen reference for the engine's security posture. The point is that every game on KhaozEngine
(Hardpoint, Nullwake, SpaceGame, and future ones) inherits one documented threat model and one
layered-defense story instead of re-deriving it per game.

Audience: the engine maintainer and game authors, not end users. A security reviewer should be able
to check each claim here against the code. Where a claim names a package or file, that is the place
to audit it.

This doc states the posture and the tradeoffs. It does not restate the update-channel internals
(see [the updater-hardening spec](superpowers/specs/2026-06-20-updater-hardening-7.0.0-design.md))
or the engine layering (see [README.md](../README.md) and [INDEX.md](INDEX.md)).

## Posture summary

KhaozEngine is MonoGame-free, targets `net10.0`, and is overwhelmingly **managed, memory-safe** code.
That memory-safety is the foundational mitigation: the bulk of the engine (rendering submission,
ECS, content, persistence, netcode logic, particles, gameplay glue) cannot be made to corrupt memory
by hostile input the way a C/C++ stack can. Whole classes of attack (stack/heap overflow, use-after-free
of game state, type confusion) do not apply to the managed surface.

The residual native attack surface is small and bounded to three third-party libraries reached through
thin seams:

| Native lib | Reached via | What it parses / handles |
|---|---|---|
| Silk.NET + GLFW (natives bundled per-RID) | `KhaozEngine.Windowing` (`AppWindow` only) | OS window + input events |
| Veldrid | `KhaozEngine.Gpu` (the only graphics-API-aware layer) | GPU command submission (Metal/D3D11/Vulkan) |
| Silk.NET.OpenAL | `KhaozEngine.Audio` | audio device output |

These are the residual surface: a memory-safety bug in one of them is reachable from a game, so they
are kept patched via self-contained publish (see Layered defenses). The mitigation for the native
surface is "ship a patchable, pinned copy and update it", not "audit their internals here".

## Threat model: where untrusted bytes enter a game

Three categories of untrusted input cross into a game. Everything else (the game's own embedded assets,
its own code) is trusted by construction.

### 1. Hostile network input (multiplayer)

Packages: `KhaozEngine.Netcode`, `KhaozEngine.Netcode.LiteNetLib`, `KhaozEngine.Netcode.Abstractions`.

Wire data arrives from untrusted peers or clients. Two shapes:

- **Client -> authoritative server.** A server running the sim consumes commands from clients it does
  not trust. The netcode primitives are built to not trust the wire: `RemoteCommandQueue<TCommand>`
  dedups retransmits, rejects negative sequence numbers and any seq at or below a slot's processed
  high-water mark (so a replayed or stale seq cannot be reprocessed or regress the acknowledged seq),
  caps the per-slot buffer and the number of distinct slots (so a flood cannot grow memory without
  bound), and returns a neutral command for an empty slot, so a malformed or replayed command stream
  degrades to "no input" rather than corrupting or unbounding the queue. The high-water mark is scoped to a
  live session: `Forget(slot)` clears it when a slot is released, so the next connection that recycles the
  slot (a new session whose seqs legitimately restart at 0) is accepted while replay protection still holds
  within each session. `UnitAxisQuantizer` is a
  fixed-range 8-bit codec: `Dequantize` clamps its input, so a decoded axis is always in `[-1, 1]`
  regardless of the byte received and a hostile byte cannot push an out-of-range magnitude into the sim.
- **Server -> predicting client.** `ClientPrediction<TState, TCommand>` reconciles against an
  authoritative basis; a client cannot be trusted to self-report its own state.

What the engine provides: transport-free primitives that bound and normalize wire values. What stays
the game's job: **authoritative validation of game semantics** (is this move legal, is this rate
plausible, is this slot allowed to act). The engine does not know the game's rules and does not enforce
them. Treat every field off the wire as attacker-controlled and validate it server-side. The netcode
layer is transport-free and makes no claim about encryption or authentication of the channel itself;
that is the transport's and the game's responsibility.

### 2. Untrusted content (config, assets, saves, meshes)

- **Config / content JSON** (`KhaozEngine.Content`): `ConfigLoader.Load<T>` reads embedded-resource
  or disk JSON; `JsonSchemaValidator` validates against JSON Schema, and `KhaozEngine.Content.Validator`
  enforces schemas at build time when `KhaozContentDataDir` is set. Content a game ships is trusted, but
  the schema path is the gate for any content a game chooses to load from a less-trusted location (user
  config, downloaded data packs). Validation is opt-in per game: the engine gives the validator; the
  game must point it at its schemas and decide what is trusted.
- **Saves / settings** (`KhaozEngine.Persistence`): `AtomicJsonWriter` + `PersistenceQueue` give
  crash-safe atomic writes; `SaveEncoder` wraps save payloads in Base64 + an HMAC-SHA256 tag. Read the
  HMAC claim precisely: it is a **tamper-deterrent, not a security boundary**. The HMAC key ships in the
  game binary, and decode is lenient (it recovers the JSON even on an HMAC mismatch, logging a warning).
  It detects casual save edits; it does not defend against an attacker who has the binary (they have the
  key). See `KhaozEngine.Persistence/SaveEncoder.cs` (the source says so in its summary).
- **Serialization** (`KhaozEngine.Serialization`): shared `System.Text.Json` defaults (tolerant-read).
  The engine does not use `BinaryFormatter` or any known-unsafe deserializer; JSON is the wire/disk format.
- **glTF meshes** (`KhaozEngine.Render3D`): `GltfLoader.Load` / `LoadSkinned` parse mesh data. Meshes a
  game ships are trusted. `LoadSkinned` validates the rig at load (rejects a joint count over the
  128-bone per-draw cap and any `JOINTS_0` index outside `[0, jointCount)`) so a malformed skin fails
  cleanly at load instead of indexing past the bone palette mid-frame. A game that loads meshes from an
  untrusted source is still parsing untrusted bytes in managed code (bounded by managed memory-safety,
  but it can throw or produce degenerate geometry); the game owns that decision.

### 3. The update channel (highest impact)

Package: `KhaozEngine.Updates`. This is the highest-impact surface in the engine: it downloads files and
replaces the running game's executables. A spoofed or compromised feed that the client accepts is remote
code execution across every game that adopts the updater.

Because of that impact, the update channel was hardened to close a full audit (10 findings, P0-P2) and
shipped as engine **7.0.0**. The deep dive is the canonical reference:

**[Updater hardening (7.0.0) design](superpowers/specs/2026-06-20-updater-hardening-7.0.0-design.md).**

The short version of what that spec mandates (do not rely on this summary for detail; read the spec):
mandatory RSA-2048 / PKCS#1 v1.5 / SHA-256 signed manifests with no unsigned path, file-URL origin
locking, path-traversal and symlink/reparse guards on apply, per-file/total/free-disk size caps, strict
downgrade rejection on the signed version, and macOS `codesign` re-verify before relaunch.

## Layered defenses

The posture is defense in depth: no single control is the whole story.

1. **Managed memory-safety (foundational).** The managed core is the primary mitigation; see Posture
   summary. Independently, **DEP and ASLR remain in force** on the process regardless of the CETCompat
   decision below. CETCompat only governs the CET shadow-stack feature, not DEP/ASLR.

2. **Hostile-input validation.** Netcode primitives bound and normalize wire values; the content layer
   gives JSON-schema validation (build-time and runtime). These are the gates for categories 1 and 2 of
   the threat model. They are primitives the game wires up, not automatic guarantees.

3. **Patched dependencies via self-contained publish.** Each game ships as a self-contained build: its
   own pinned .NET runtime plus the native libs (GLFW, Veldrid's backend, OpenAL) for its RID. That is
   what makes the residual native surface patchable: when a runtime or native-lib CVE lands, the game
   re-publishes a self-contained build to pick up the fix and ships it (the signed updater is the
   delivery mechanism). Adoption path: a game bumps its `KhaozEngine.*` pin and re-publishes; the runtime
   version travels with the publish, not the engine packages.

4. **Signed, integrity-checked updates.** The update channel's mandatory signing + the apply-time guards
   (origin lock, path/symlink guards, size caps, downgrade rejection) are what keep category 3 from being
   an open RCE. See [the updater spec](superpowers/specs/2026-06-20-updater-hardening-7.0.0-design.md). A
   game must wire this correctly (next section) for the guarantee to hold.

5. **CET / CETCompat decision (engine 7.23.0).** The x64 apphost defaults `CETCompat=false`, inherited
   from `KhaozEngine.Foundation` by every game head (directly or transitively through
   `Game2D`/`Game3D`/`Server`). This is a **deliberate, reversible tradeoff**, not an oversight:

   - CET (Control-flow Enforcement Technology) is a *hardware* ROP/JOP mitigation. It protects against
     return-oriented programming over the **native** surface. KhaozEngine games are overwhelmingly
     managed, so the surface CET would protect is small.
   - DEP, ASLR, and the signed auto-updater remain in force whether CET is on or off.
   - .NET 9+ marks the x64 apphost CET-compatible by default, which **hard-aborts at boot** on Windows 10
     builds with only partial CET / shadow-stack support (e.g. 20H2): *"Your Windows doesn't fully
     support CET."* Disabling it buys broad old-Windows compatibility.
   - The net trade: give up a hardware ROP mitigation over a small native surface (DEP/ASLR/signed
     updates still standing) to run on more machines. For a game, that is the right default.
   - **Reversible per head:** set `<CETCompat>true</CETCompat>` in a head's `Directory.Build.props` or
     `.csproj` and that head's value wins. The mechanism (how the default is packed and inherited) is
     documented in [USING-KHAOZENGINE.md "Game head build settings"](USING-KHAOZENGINE.md#game-head-build-settings-cetcompat);
     the full rationale is in the [CHANGELOG 7.23.0 entry](../CHANGELOG.md).

## Out of scope / not claimed

To keep the doc from overpromising, these are explicitly **not** provided or claimed:

- **Anti-cheat.** The engine does not detect or prevent cheating. Client prediction is a smoothing/UX
  feature, not a cheat-proofing one; cheat resistance comes from server-authoritative validation the
  game writes, not from the engine.
- **DRM / copy protection.** None. `SaveEncoder` is a tamper-deterrent (key ships in the binary), not
  DRM and not a security boundary.
- **Sandboxing untrusted mods or scripts.** The engine runs no untrusted code in a sandbox. A game that
  loads third-party mods/plugins is running them with full process trust; isolating them is the game's
  problem and out of scope here.
- **Side-channel resistance.** No constant-time guarantees, no timing/cache/power side-channel hardening.
  The crypto in use (RSA verify for updates, HMAC for saves) uses BCL primitives as-is.
- **Confidentiality of the network channel.** Netcode is transport-free; the engine does not encrypt or
  authenticate the multiplayer channel. That is the transport's/game's responsibility.
- **Protection against a malicious local user.** Anyone with the binary has the embedded HMAC key and the
  embedded update public key. The trust model defends players from a hostile *feed/peer/content source*,
  not the machine owner from their own client.

## Per-game responsibilities vs engine-provided guarantees

The engine provides primitives and one hardened channel; a game still has to use them correctly.

**Engine-provided guarantees:**

- A memory-safe managed core; native surface bounded to three patchable libs.
- The update channel's signing + apply-time guards, *once wired* (signed manifests, origin lock, path/
  symlink guards, size caps, downgrade rejection).
- Input-bounding netcode primitives (`RemoteCommandQueue` dedup + high-water seq-reject + per-slot/slot
  caps, `UnitAxisQuantizer` clamp).
- A JSON-schema validation primitive (runtime + build-time) for content.
- An HMAC tamper-deterrent on saves (deterrent, not a boundary).
- The CETCompat default + DEP/ASLR on every head.

**Per-game responsibilities (the game author must still do these):**

- **Validate your own content.** Write JSON schemas for any content you load from a non-trusted location
  and run the validator against it. The engine won't guess your data's shape.
- **Validate game semantics server-side.** For multiplayer, treat every wire field as attacker-controlled
  and enforce your game's rules on an authoritative server. The netcode layer bounds values; it does not
  know your rules.
- **Wire the signed updater correctly.** Generate a keypair, embed the trusted public key in
  `UpdateServiceOptions.TrustedPublicKeys`, sign the manifest at publish, and hardcode `ServerBaseUrl` in
  release builds (do not read a feed-URL env override in release). An unsigned or misconfigured feed is
  the whole RCE surface. Follow the [updater spec](superpowers/specs/2026-06-20-updater-hardening-7.0.0-design.md).
- **Keep dependencies patched.** Re-publish self-contained to pick up .NET runtime and native-lib
  security fixes, and ship the new build through the updater. The engine pins versions; the game owns the
  publish.
- **Decide CETCompat per head** if you have a reason to re-enable it (e.g. you only target current Windows
  and want the hardware mitigation back).
- **Don't treat the save HMAC as security.** If you need real integrity/confidentiality of player data,
  put it server-side; the client-side key is not a secret.

## Reporting a vulnerability

See [SECURITY.md](../SECURITY.md) in the repo root for how to report. Report privately; do not open a
public issue for a suspected vulnerability.
