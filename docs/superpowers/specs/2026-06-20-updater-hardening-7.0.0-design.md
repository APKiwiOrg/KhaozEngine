# KhaozEngine.Updates hardening (7.0.0) - design

## Context

`KhaozEngine.Updates` is already a full delta auto-updater (check -> resumable SHA-256 staged
download -> external-shim apply with backup/rollback). It was promoted out of SpaceGame, which is
the only current consumer. Hardpoint and Nullwake do not reference it yet.

The package downloads files and replaces the running game's executables, so its threat model is
"a compromised or spoofed update feed pushes arbitrary code to all players." A security audit of
the 6.4.0 code found integrity rests entirely on TLS plus trusting the blob host: per-file SHA-256
is verified, but the manifest itself is unsigned, the server response can redirect file downloads
to an arbitrary host, and the apply step has no path-traversal guard.

Before widening adoption to Hardpoint and Nullwake (where any weakness becomes RCE across three
games), this pass closes every audit finding. Centralizing the last-mile glue (generic shim,
reusable Gui overlay, publish-script template) is a **separate follow-up spec**, not in scope here.

## Goal

Make the existing updater safe to adopt across all games. Close all ten audit findings (P0+P1+P2).
Signing is mandatory with no unsigned path. This is a breaking change, shipped as engine **7.0.0**.

## Non-goals

- Centralizing the updater shim, the Gui notification overlay, or the publish tooling beyond what
  signing requires (separate follow-up spec).
- Hardpoint / Nullwake adoption (follow-up).
- Changing the download/diff/staging algorithm, the resumable-staging design, or the
  external-shim apply model. Those stay as-is; we add guards around them.
- Any unrelated breaking change riding the 7.0.0 boundary. Scope stays the updater.

## Audit findings this closes

| # | Finding | Sev | Status today |
|---|---------|-----|--------------|
| 1 | Manifest not signed | P0 | ABSENT |
| 2 | File-URL origin not validated (`ResolveFileUrl` trusts server `ManifestUrl` host) | P0 | ABSENT |
| 3 | Path traversal on copy (`Path.Combine(InstallDir, relativePath)`) | P0 | ABSENT |
| 4 | Path traversal on delete | P0 | ABSENT |
| 5 | Downgrade / rollback not strictly enforced; `Required` is unsigned | P1 | PARTIAL |
| 6 | No symlink / reparse-point guard on staged files | P1 | ABSENT |
| 7 | No per-file / total / free-disk size caps | P1 | ABSENT |
| 8 | macOS only strips quarantine bit, no `codesign -v` re-verify | P2 | PARTIAL |
| 9 | Feed URL overridable by env var in release | P2 | PRESENT (by design) |
| 10 | No post-download declared-size check | P2 | ABSENT |

## Design

### Signing (findings 1, 5)

- **Scheme:** RSA-2048, PKCS#1 v1.5, SHA-256. Pure .NET BCL (`System.Security.Cryptography.RSA`),
  no new dependency. `Updates` stays dependent only on `KhaozEngine.Diagnostics`.
- **What is signed:** the exact manifest bytes. The manifest already lists every file's SHA-256,
  so a valid manifest signature transitively protects the whole payload.
- **Transport:** detached `manifest.json.sig` (base64 of the RSA signature) placed next to
  `manifest.json`, fetched the same way (same-origin, see finding 2).
- **Client verification order:** download manifest bytes -> download `.sig` -> verify against an
  embedded trusted public key -> only then `UpdateManifest.Deserialize`. Verify over the **raw
  received bytes**, never a re-serialization (canonical-bytes rule).
- **Mandatory, no escape hatch.** `UpdateServiceOptions.TrustedPublicKeys` must contain at least
  one key; constructing `UpdateService` without one throws. There is no "unsigned allowed" mode.
  Dev/test use a dev keypair, not a bypass flag.
- **Rotation:** `TrustedPublicKeys` is a list. A signature is accepted if it validates against any
  one key. Roll by shipping the new key alongside the old, switching the signer, then dropping the
  old key in a later release. Signature format is a bare detached signature; try-all-keys on verify
  (no `keyId` envelope, kept simple - revisit only if the key list grows large).
- **Trust only signed fields for security decisions.** Add `Required` to the signed
  `UpdateManifest` (next to existing `Version` / `Platform` / `PublishedAtUtc`). The downgrade
  check (finding 5) runs against the **signed manifest's** `Version`, not the unsigned
  `LatestVersionInfo`. The unsigned `/latest` response is demoted to a pure hint ("a newer build
  may exist, fetch this manifest"); nothing security-relevant is decided from it. A feed that lies
  in `/latest` can at worst trigger a manifest fetch that then fails signature or downgrade checks.

### Origin lock (finding 2)

- In `HttpUpdateSource`, reject any `ManifestUrl` (from the server response) and any resolved file
  URL whose scheme is not `https` or whose host differs from the configured `ServerBaseUrl` host.
- `ResolveFileUrl` keeps resolving files relative to the manifest URL, but the manifest URL is now
  pinned to the configured origin first, so files cannot be redirected to another host.

### Path-traversal guard (findings 3, 4)

- One shared validator used by both the copy list and the delete list in `UpdateApplier`.
- Reject a relative path if it: is rooted / absolute, contains a drive-letter prefix, contains a
  `..` segment, or contains a null byte.
- Post-check: `Path.GetFullPath(Path.Combine(InstallDir, native))` must remain under the
  canonicalized `InstallDir`; otherwise abort that entry and fail the apply. Same check applied to
  the staging-source side.

### Symlink / reparse guard (finding 6)

- Before copying a staged entry, refuse it if the staged file is a reparse point
  (`FileAttributes.ReparsePoint`).
- When the destination exists and is a reparse point, do not follow it (remove the link entry
  rather than overwriting through it).

### Size and disk caps (findings 7, 10)

- Per-file size cap and total-download size cap (configurable on `UpdateServiceOptions`, with
  conservative defaults).
- Free-disk check before staging begins; refuse if the declared total would not fit with headroom.
- During streaming, stop trusting the manifest's declared `Size`: abort if bytes written exceed
  the declared size. After download, assert on-disk size equals declared size (cheap pre-check
  before the SHA-256 verify that already runs).

### macOS code-sign re-verify (finding 8)

- After quarantine is cleared and before relaunch, run `codesign -v` (or
  `--verify --deep --strict`) on the game bundle/executable. Fail the apply closed if verification
  fails, so a tampered or corrupted payload never launches. Keep this best-effort-logged on
  non-macOS (no-op) consistent with the existing `ClearQuarantine` shape.

### Feed-URL lockdown (finding 9)

- Mandatory signing already neutralizes the repoint vector: a repointed feed cannot produce a
  validly signed manifest. Retain a documented recommendation that release builds hardcode
  `ServerBaseUrl` and do not read an env override (the env-var pattern stays a dev convenience).
  This is documentation plus the signing guarantee, not new engine code.

## Public API changes (`KhaozEngine.Updates`)

- `UpdateServiceOptions.TrustedPublicKeys` (new, required, list of RSA public keys as PEM/SPKI).
- `UpdateServiceOptions` size/disk cap knobs (new, defaulted).
- `UpdateManifest` gains `Required`; new signing/verification helpers
  (`ManifestSigner.Sign` / `ManifestVerifier.Verify`, BCL RSA + SHA-256).
- `HttpUpdateSource` origin-lock behavior (rejects off-origin / non-https; behavior change).
- `UpdateApplier` path + reparse guards, size enforcement (rejects malicious input; behavior
  change).
- `IUpdateSource` / `IUpdaterEnvironment` may gain a member if the codesign re-verify or `.sig`
  fetch needs a seam (kept minimal; prefer reusing existing members).

These are breaking (mandatory signing rejects every existing unsigned feed), so the whole engine
version line bumps to **7.0.0**.

## Signing / key tooling

The build-time `ManifestGenerator` tool gains:

- `--genkey <dir>` - emit an RSA-2048 keypair (private PEM + public PEM/SPKI).
- `--sign --key <private.pem>` - after generating `manifest.json`, write `manifest.json.sig`.

Private key lives as a CI / GitHub-Actions secret (and a local copy for manual publish). Public
key(s) ship embedded in each game and are passed to `UpdateServiceOptions.TrustedPublicKeys`.
(Generalizing the shim and publish script is the follow-up spec; only the signing hooks land here.)

## Testing

Headless tests in `KhaozEngine.Tests/Updates/` (extend existing `FakeUpdateSource` /
`FakeUpdaterEnvironment`):

- reject an unsigned manifest
- reject a manifest signed by an untrusted key
- accept a manifest signed by a rotation (secondary) key
- reject an off-origin / non-https file URL and an off-origin `ManifestUrl`
- reject `..` / absolute / drive-letter entry in the copy list and in the delete list
- reject a reparse-point staged file
- reject an over-cap per-file and over-cap total download; refuse on insufficient free disk
- reject a downgrade (signed version not strictly newer); `Required` cannot force a rollback
- `ManifestSigner` / `ManifestVerifier` round-trip
- `codesign -v` failure fails the apply closed (via `IUpdaterEnvironment` fake)

## Release (engine ritual, 7.0.0)

1. Bump `<KhaozEngineVersion>` to `7.0.0` in `Directory.Build.props`.
2. `CHANGELOG.md` newest-first entry (breaking: mandatory signed manifests; origin lock; path,
   reparse, size guards; downgrade enforcement; macOS codesign re-verify).
3. `CHANGENOTES.md` one-line digest.
4. Update the guard-checked version declarations: `docs/CONSUMERS.md` engine-current line,
   `docs/ROADMAP.md` current-released line, `README.md` `<PackageReference>` example.
5. `dotnet pack -c Release -o ./local-feed`.
6. Commit, `git tag v7.0.0`, push main + tag.
7. `scripts/check-doc-versions.sh` must pass.

## Adoption (out of scope, noted for sequencing)

SpaceGame is the only current consumer. After 7.0.0 ships it must: generate a keypair, embed the
public key, sign at publish, re-publish a signed manifest, and bump its pin. Until SpaceGame
re-publishes signed, its already-deployed unsigned feed will be rejected by a 7.0.0 client, so the
SpaceGame adoption + re-publish lands in lockstep with the bump. Hardpoint / Nullwake adoption is
the follow-up centralization spec.

## Open risk

The 7.0.0 major drags every engine package and all three consumers across a major boundary for an
updater-only change (shared single version line). Accepted: signing must be unconditional, and the
audit guards are worth a clean major. No other breaking change is bundled in.
