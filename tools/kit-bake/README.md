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

## What it does

For each source glb, `bake.mjs`:

1. Decodes `EXT_meshopt_compression` (implicit on read, once the extension + a `meshopt.decoder`
   dependency are registered) and runs `dequantize` (drops `KHR_mesh_quantization`, restores float
   POSITION/NORMAL).
2. Re-encodes every `EXT_texture_webp` image to PNG via `sharp` and drops the `EXT_texture_webp`
   extension (SharpGLTF cannot decode webp).
3. Keeps every material's `baseColorTexture` / `normalTexture` / `metallicRoughnessTexture` as-is (no
   flattening to a factor). Images are embedded in the output glb.
4. Disposes the three extensions this recipe expects to be left dangling (unused but still declared) by
   steps 1-2: `EXT_meshopt_compression`, `KHR_mesh_quantization`, `EXT_texture_webp`. This is an explicit
   allowlist, not "dispose everything used" - if a source glb still declares any OTHER extension after
   dequantize/textureCompress (draco, a material extension, `KHR_texture_transform`, ...), the bake fails
   loudly (nonzero exit, naming the extension) instead of silently stripping something this recipe was
   never designed to touch. A second check after disposal confirms zero extensions remain, so the output
   is provably plain glTF 2.0.
5. Prints per-file verification: material count, image count, image formats, extension list, and the
   input-to-output byte size.

The pipeline is idempotent: every run reads fresh from the source glb and overwrites the output, no
state carries between runs.

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
