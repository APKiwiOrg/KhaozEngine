# Prop kit credits

The prop meshes in this folder (`pine_a/b/c.glb`, `oak_a/b.glb`, `rock_a/b.glb`) are **CC0 (public
domain)** stylized nature assets by **Quaternius**.

- Author: Quaternius (https://quaternius.com / https://poly.pizza/u/Quaternius)
- License: CC0 1.0 Universal (public domain dedication, no attribution required; credited here as a
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
