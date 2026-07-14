# Prop kit credits

The prop meshes in this folder (`pine_a/b/c.glb`, `oak_a/b.glb`, `rock_a/b.glb`) are **CC0 (public
domain)** stylized nature assets by **Quaternius**.

- Author: Quaternius (https://quaternius.com / https://poly.pizza/u/Quaternius)
- License: CC0 1.0 Universal (public domain dedication, no attribution required, credited here as a
  courtesy)
- Obtained via the `world-of-claudecraft` reference project
  (https://github.com/levy-street/world-of-claudecraft), `public/models/foliage/`
  (`pine_1/2/3`, `oak_1/2`, `rock_1/2`).

## Ingest (how these were prepared)

The originals are meshopt-compressed and use `KHR_mesh_quantization` + `EXT_texture_webp`.
KhaozEngine's glTF loader (`KhaozEngine.Render3D.GltfLoader`, SharpGLTF-based) loads **plain glTF
2.0** and does not decode meshopt. So they were baked to plain glTF offline with
[`gltf-transform`](https://gltf-transform.dev):

1. decode meshopt + `dequantize` (back to float POSITION/NORMAL).
2. flatten each material's `baseColorTexture` to a single representative `baseColorFactor` (average
   of opaque texels) and drop all textures + the texture extensions, so the multi-material trees
   render correctly as one flat-colored mesh (the engine flattens primitives into a single mesh and
   reads the per-material base-color factor).

Result: each `.glb` is plain glTF 2.0 (no extensions, f32 attributes) with brown-trunk / green-leaf
/ grey-rock flat colors. The loader then scales each to its manifest `heightMeters` and drops the
origin to the base. See `props.manifest.json` and `docs/USING-KHAOZENGINE.md` (Prop scatter +
asset pipeline).

### Textures-ON re-ingest (multi-texture-per-primitive)

Step 2 above deliberately DROPS textures because the flat single-mesh loader (`PropLoader.LoadProp`)
reads only a per-material base-color factor. To keep real per-material textures instead, bake with a
textures-ON recipe and load through the multi-material path (`PropLoader.LoadPropParts` ->
`Scene3D.LoadProp`), which splits the prop into one textured sub-mesh per source material:

1. decode meshopt + `dequantize` (as above).
2. re-encode `EXT_texture_webp` textures to PNG (a `gltf-transform` image step) so the loader can
   decode them, and DROP the `EXT_texture_webp` extension.
3. KEEP per-material `baseColorTexture` (and normal / metallicRoughness where present). Do NOT
   flatten to `baseColorFactor`.

Baked on 2026-07-14 from `world-of-claudecraft` main (`public/models/foliage/`), with the checked-in
tool `tools/kit-bake/` (a `@gltf-transform` script implementing this recipe, see its README.md).
Source files and their kit ids (`tools/kit-bake/foliage-map.json`):

- `pine_1.glb` -> `pine_a.glb`
- `pine_2.glb` -> `pine_b.glb`
- `pine_3.glb` -> `pine_c.glb`
- `oak_1.glb` -> `oak_a.glb`
- `oak_2.glb` -> `oak_b.glb`
- `rock_1.glb` -> `rock_a.glb`
- `rock_2.glb` -> `rock_b.glb`

Each re-baked `.glb` keeps its source materials (trees: a bark material with baseColor + normal, and a
leaves material with baseColor only. Rocks: one material with baseColor only), all images embedded as
PNG. `props.manifest.json` sets `"textured": true` on these 7 entries, so prop-rendering call sites
that honor the flag (`PropLoader.LoadPropAuto`) load them through the multi-material path. The same
files still flat-load via `PropLoader.LoadProp` (load-time averaged albedo), so nothing that reads
these ids without opting into textures breaks.

## signpost.glb (multi-material textured demo)

`signpost.glb` is a **procedurally generated, fully original** two-material prop (a wood-grain post +
a checker sign board, each its own baseColor texture), emitted by `tools/TestModelGen`
(`dotnet run --project tools/TestModelGen -- signpost <out>.glb`). Being original generated content it
is **CC0 / public domain**. It demonstrates the multi-texture-per-primitive path end to end
(`GltfLoader.LoadPartsWithMaterials` -> `PropLoader.LoadPropParts` -> `Scene3D.LoadProp` /
`Scene3D.PropHandle`) and is placed near spawn in the 3D World room (`Room3D`).
