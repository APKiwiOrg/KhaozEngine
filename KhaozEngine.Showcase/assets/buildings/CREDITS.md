# Town building kit credits

Modular medieval village buildings used in the KhaozEngine.Showcase 3D World room.

- **Models:** Quaternius "Medieval Village" / modular kit (CC0, public domain).
- **Source:** obtained from the public CC0 set bundled in
  `github.com/levy-street/world-of-claudecraft` (`public/models/props`). Those models are CC0
  Quaternius assets (the host repo's own code is MIT, that does not change the asset licence).
- **License:** CC0 1.0 (no attribution required, credited here anyway).

Files: `inn.glb`, `bell_tower.glb`, `blacksmith.glb`, `house_1.glb`, `house_2.glb`, `house_3.glb`,
`well.glb`.

## Ingest (how these were prepared)

The originals are `EXT_meshopt_compression` + `KHR_mesh_quantization` (no textures: each model is flat
per-material `baseColorFactor` colours). KhaozEngine's glTF loader (`KhaozEngine.Render3D.GltfLoader`,
SharpGLTF-based) reads **plain glTF 2.0** and does not decode meshopt or quantized attributes, so each
file was baked to plain glTF offline with [`gltf-transform`](https://gltf-transform.dev):

```bash
# 1. drop EXT_meshopt_compression (re-encode uncompressed)
npx --yes @gltf-transform/cli@latest cp <in>.glb tmp.glb
# 2. dequantize KHR_mesh_quantization back to f32 POSITION/NORMAL
npx --yes @gltf-transform/cli@latest dequantize tmp.glb <out>.glb
```

3. **Bake node transforms into the geometry.** These models carry a -90 deg X (Z-up -> Y-up) rotation
   plus a uniform scale as a root-node transform. KhaozEngine's rigid loader (`GltfLoader.BuildRigid`)
   reads mesh POSITION accessors in **local** space and does NOT apply node world matrices (only the
   skinned path uses `WorldMatrix`), so an un-baked model loads on its side and `PropLoader` normalizes
   the wrong axis. The node transform was baked into the vertices with the gltf-transform JS API
   (`flatten()` to push parent transforms down, then `clearNodeTransform(node)` per mesh node), leaving
   identity node transforms and Y-up geometry. After this the mesh node rotation is `[0,0,0,1]` and the
   local Y-extent equals the upright height (verified per file).

No texture-flatten step was needed (the kit has no textures, the per-material base-colour factors
render directly). Result: each `.glb` is plain glTF 2.0 with no required extensions and identity node
transforms (verified with `gltf-transform inspect` / `validate`). `PropLoader` then scales each to its
manifest `heightMeters` and drops the origin to the base. See `buildings.manifest.json`.
