# KhaozEngine.Updates.Tool

The `ke-updater` dotnet tool. Generates, signs, and verifies KhaozEngine update manifests
(RSA-2048 / SHA-256). Thin CLI over `KhaozEngine.Updates` (`UpdateManifest.GenerateFromDirectory`,
`ManifestSigner`, `ManifestVerifier`) for publish pipelines. The update client refuses unsigned
manifests, so sign everything you publish.

Install:

```bash
dotnet tool install --global KhaozEngine.Updates.Tool
```

## Usage

```bash
ke-updater manifest --dir <path> --platform <id> --version <v> [--required] [--output <path>]
ke-updater genkey --out <dir>
ke-updater sign --manifest <manifest.json> --key <private.pem>
ke-updater verify --manifest <manifest.json> --sig <manifest.json.sig> --key <public.pem>
```

- `manifest` - walk `--dir` and generate a manifest JSON for the given platform + version. Writes
  to `--output` if given, otherwise stdout. Pass `--required` to mark the build a mandatory update
  (`"required": true` in the manifest): the client then auto-downloads and auto-restarts to apply it
  with no keypress. Sign the manifest as usual; the client trusts `required` only from the SIGNED
  manifest, never the unsigned `latest-<rid>.json` pointer.
- `genkey` - generate an RSA-2048 key pair, writing `private.pem` and `public.pem` into `--out`.
  Keep `private.pem` secret (a CI secret). Embed `public.pem` in the game's `TrustedPublicKeys`.
- `sign` - sign the exact manifest bytes with the private key. Writes `<manifest>.sig` next to it.
- `verify` - check a signature against a public key. Exit 0 = valid, 2 = invalid.

Typical publish step:

```bash
ke-updater manifest --dir ./publish/win-x64 --platform win-x64 --version 1.4.2 --output ./publish/manifest.json
ke-updater sign --manifest ./publish/manifest.json --key "$UPDATE_SIGNING_KEY_PATH"
```

No environment variables required. See the `KhaozEngine.Updates` package README for the client
side (trusted keys, delta apply, the external shim).
