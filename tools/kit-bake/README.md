# kit-bake: textures-ON prop re-ingest

Bakes CC0 prop-kit glTF sources (meshopt-compressed, `KHR_mesh_quantization` + `EXT_texture_webp`) into
plain glb files that `KhaozEngine.Render3D.GltfLoader` (SharpGLTF-based) can load with real per-material
textures. See `KhaozEngine.Showcase/assets/props/CREDITS.md` ("Textures-ON re-ingest") for the canonical
recipe writeup and provenance. This tool implements that recipe. See also `docs/USING-KHAOZENGINE.md`
("Decompress kit glTF offline" and "Manifest-driven textured opt-in") for how a re-baked kit's
`"textured": true` manifest entries flow through `PropLoader.LoadPropAuto` at runtime.

## Requirements

Node.js (tested with node 21, npx 10) and npm. Install dependencies once:

```
cd tools/kit-bake
npm install
```

`package.json` pins exact versions of `@gltf-transform/core`, `@gltf-transform/extensions`,
`@gltf-transform/functions`, `meshoptimizer`, and `sharp`, with `package-lock.json` committed for a
reproducible install. `node_modules/` is gitignored, never committed.

`package.json` also carries an `overrides` block collapsing every `sharp` in the tree onto the one this
tool pins:

```json
"overrides": {
  "sharp": "$sharp"
}
```

Without it npm installs a **second, nested** `sharp` under `node_modules/ndarray-pixels/`, because
`@gltf-transform/functions` reaches `ndarray-pixels`, which still asks for `sharp@^0.34.0`. That nested
copy is what carried GHSA-f88m-g3jw-g9cj (four high-severity libvips CVEs fixed in `sharp` 0.35.0), and
Dependabot could not fix it on its own: the only resolution it could find was downgrading
`@gltf-transform/functions` to 3.4.2, so its update run failed with `security_update_not_possible` rather
than opening a PR. The override is the documented npm escape hatch for exactly that shape. `$sharp` means
"whatever the direct dependency above pins", so bumping `sharp` in `dependencies` carries the override
with it and there is no second version number to keep in sync.

`ndarray-pixels` only calls `sharp(buf).ensureAlpha().raw().toBuffer()` and
`sharp(data, { raw }).toFormat(...).toBuffer()`, both unchanged across 0.34 to 0.35, and `bake.mjs`
already hands its own top-level `sharp` to `textureCompress` as the encoder. Re-baking the seven kits in
`foliage-map.json` after the override lands reproduces the committed glbs byte-for-byte.

Drop the override once `ndarray-pixels` ships a release that asks for `sharp@^0.35`.

## What it does

For each source glb, `bake.mjs`:

1. Decodes `EXT_meshopt_compression` (implicit on read, once the extension + a `meshopt.decoder`
   dependency are registered) and runs `dequantize` (drops `KHR_mesh_quantization`, restores float
   POSITION/NORMAL).
2. Re-encodes every `EXT_texture_webp` image to PNG via `sharp` and drops the `EXT_texture_webp`
   extension (SharpGLTF cannot decode webp).
3. Keeps every material's `baseColorTexture` / `normalTexture` / `metallicRoughnessTexture` as-is (no
   flattening to a factor), and preserves each material's `alphaMode` / `alphaCutoff`. The leaf materials
   are `alphaMode: MASK` (the runtime alpha-cutout relies on that surviving the bake), which the transform
   passes through untouched. The per-file report prints the alpha modes so a regression is visible.
   Images are embedded in the output glb.
4. Alpha-bleeds (dilates) each baseColor texture that has transparency: floods the RGB of the visible
   (alpha at or above a small threshold) texels outward into the fully transparent texels, leaving the
   alpha channel untouched. The Quaternius leaf-card textures store **black** RGB under their transparent
   texels, so plain box-filter mip generation (and bilinear sampling at the cutout edge) folds that black
   into the leaf colour: dark fringes and foliage that darkens with distance. Bleeding leaf colour into
   those texels makes every mip average leaf-on-leaf, so the fringe is gone and colour is stable at range.
   Because alpha is preserved to the bit, the runtime alpha-cutout (`alphaMode: MASK`) discards exactly the
   same silhouette as before - only the colour that survives at the edges changes. Fully opaque textures
   (bark, rock) have no transparent texels, so this is a no-op for them (they re-encode bit-identically).
   This is the engine-side alternative (alpha-weighted mip generation at load) done once, offline, at the
   data source instead: it also fixes the bilinear edge at mip 0, needs no per-backend mip code, and leaves
   the flat-load `AverageAlbedo` (opaque-only) untouched.
5. Disposes the three extensions this recipe expects to be left dangling (unused but still declared) by
   steps 1-2: `EXT_meshopt_compression`, `KHR_mesh_quantization`, `EXT_texture_webp`. This is an explicit
   allowlist, not "dispose everything used" - if a source glb still declares any OTHER extension after
   dequantize/textureCompress (draco, a material extension, `KHR_texture_transform`, ...), the bake fails
   loudly (nonzero exit, naming the extension) instead of silently stripping something this recipe was
   never designed to touch. A second check after disposal confirms zero extensions remain, so the output
   is provably plain glTF 2.0.
6. Prints per-file verification: material count, image count, image formats, per-material alpha modes, the
   alpha-bled materials (and their transparent-texel count), extension list, and the input-to-output byte
   size.

The pipeline is idempotent and deterministic: every run reads fresh from the source glb and overwrites the
output with no state carried between runs, and the alpha-bleed uses a fixed neighbour order and iteration
so a re-bake round-trips bit-for-bit.

## Usage

```
node bake.mjs --input <sourceDir|sourceFile> --out <outDir> --map <mapping.json>
```

`mapping.json` is a flat `{ "<sourceBaseName>": "<kitId>" }` object. When `--input` is a directory, each
key resolves to `<sourceBaseName>.glb` inside it. When `--input` is a single file, the mapping must have
exactly one entry. Output files are written as `<outDir>/<kitId>.glb`.

### This round's mapping

`foliage-map.json` is the mapping used to re-bake the engine's Showcase foliage subset from the
`world-of-claudecraft` reference project's `public/models/foliage/`:

```json
{
  "pine_1": "pine_a",
  "pine_2": "pine_b",
  "pine_3": "pine_c",
  "oak_1": "oak_a",
  "oak_2": "oak_b",
  "rock_1": "rock_a",
  "rock_2": "rock_b"
}
```

Re-bake it with:

```
node bake.mjs --input /path/to/world-of-claudecraft/public/models/foliage --out /path/to/KhaozEngine.Showcase/assets/props --map foliage-map.json
```

The signpost, buildings, and grass are not part of this mapping: the signpost is a procedurally
generated demo (`tools/TestModelGen`), the buildings were verified untextured upstream (nothing to
re-bake), and grass has no upstream source (an original engine asset). Ruinborne's own groundcover
copies (bush, bush_flowers, fern, mushroom) get baked with this same script during that repo's adopt
step, with their own mapping file, not here.

## Provenance

Source license and attribution: `KhaozEngine.Showcase/assets/props/CREDITS.md`.
