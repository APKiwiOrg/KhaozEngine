# Instanced-path per-instance dissolve, design (2026-07-20)

Status: approved for build (issue #253, rescoped 2026-07-20). Consumers: the prop draw-distance
fade band (#44) and the AoI presentation fade (#37) build their policy on this primitive later. The
skinned path already has its dissolve (CharDissolve + the DrawSkinned dissolve overload) and is not
touched by this work.

## Problem

The rigid/instanced path (props, dungeon kit, every plain `Scene3D.Draw`) has no per-instance fade
or dissolve. Tint alpha is plumbed to the shader and dropped (`ModelFrag` writes `oColor =
vec4(lit, 1.0)`, ShaderSources.cs:302). #44 and #37 both anticipate a per-instance fade value
threaded through `InstanceData` reusing the dissolve alpha-clip so instancing and draw order stay
untouched.

## Decisions

1. **Dissolve semantics, not opacity.** The new field means 0 = fully drawn, 1 = fully dissolved,
   matching `DrawSkinned`'s dissolve parameter and `SkinnedSceneInstances.DissolveThreshold`. Both
   `InstanceData` construction sites (Scene3D.cs:1935 CPU-skinned, :3081 rigid) zero-fill via
   `instanceData.Add(default)`, so a zero-default field keeps every existing draw and every GPU
   golden byte-identical with no construction-site edits. Opacity semantics (1 = default) were
   rejected: a missed construction site renders everything invisible, and the `IsDynamic` addition
   (#235) set the zero-default precedent.

2. **Fold the discard term into `ModelFrag`, do not reuse `ModelDissolveFrag`.** The CharDissolve
   fragment deliberately drops base `Material.Emissive` in favour of the edge colour, which would
   silently kill emissive materials on faded props. Instead `ModelFrag` gains the noise-threshold
   discard plus edge-emissive term gated to be inert when the dissolve field is 0. One pipeline,
   always bound: the rigid main-pass loop (Scene3D.cs:2010-2032) keeps zero per-run pipeline
   switches, preserving the single-instanced-draw-per-mesh property #44 depends on.

3. **New trailing `InstanceData` fields, not `SpecParams.z/w`.** In the normal pipeline
   `SpecParams.z` is the MASK alpha-cutout threshold (set per mesh-run by `ApplyAlphaCutoffs`), so
   overloading it would collide for cutout foliage, exactly the instances #44 wants to fade. Add
   `public Vector2 Dissolve` (x = threshold 0..1, y = edge width), growing the struct 116 to 124
   bytes: update `SizeInBytes`, the pinned `UboLayoutTests` size test, and the vertex layout with
   one Float2 instance element. Edge colour rides the existing `Emissive` field: when a draw
   carries dissolve > 0 the overload substitutes the edge colour for the material emissive
   engine-side, the same trade the skinned dissolve path already makes at Scene3D.cs:1913-1919.

4. **API mirrors the skinned overload.** `Scene3D.Draw(MeshHandle, Matrix4x4, Color, Material,
   float dissolve, float edgeWidth, Color edgeColor)` threading through a widened
   `SceneInstances.Instance` and `GroupInstances`. `PropRenderer` is deliberately untouched: the
   two-radius band, hysteresis, and streaming-coupling guard are #44's own scope on top of this.

## Testing

- `UboLayoutTests` size pin updated (116 to 124) plus a marshal-offset assertion for the new field.
- Full `KE_GPU_TESTS=1` Render.Tests run must stay green with ZERO golden drift (the field defaults
  to 0 and the shader term is inert at 0, so any drift is a defect).
- One new pixel-presence GpuFact (not a golden, no bake): a half-dissolved box shows discard holes
  and edge tint against a fully drawn box at dissolve 0 rendering identically to the old path.

## Rejected alternatives

- True alpha-blended fade on the rigid path: requires moving faded instances to the transparent
  pass with sorting (#48 territory), breaks the one-draw-per-mesh batching, and the stochastic
  discard reads equivalently at fade speeds the consumers use. Revisit only if a consumer needs
  slow, close-up crossfades.
- A separate dissolve pipeline for rigid meshes (mirroring `_dissolvePipeline` usage per-run):
  costs a pipeline switch per faded run and loses batching when solid and fading instances of the
  same mesh coexist, the common case in a fade band.
