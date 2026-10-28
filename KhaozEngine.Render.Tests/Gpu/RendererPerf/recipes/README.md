# Frozen performance recipe identity

The frozen-recipe envelope uses `khaozengine.renderer-perf-frozen-recipe/2`.
`Authored` contains one portable construction for every platform. It retains geometry,
settings, assets, camera fields, per-frame camera and light inputs, pose bend angles,
absolute block times, clipmap states, prop positions and yaw angles, tube construction
parameters and the expected schedules. Changes to these inputs
fail on every platform.

`DerivedByPlatform` freezes the actual CPU renderer outputs for each runtime OS and
process architecture, such as `osx-arm64`, `linux-x64` and `win-x64`. Camera outputs,
frame matrix and pose digests, rest and inverse-bind matrices and their digests, seeded
prop rotation matrices, draw-world copies, and joint palettes
are excluded from `Authored`. Renderer math is unchanged. Portable render math remains
[D1 in #1530](https://github.com/APKiwiOrg/KhaozEngine/issues/1530).

Each derived entry hashes the complete live geometry, the complete frame/block sequence
and the complete live construction. Those hashes include all computed camera and
skinning values, even where a value happens to match across families. A missing family
or a mismatching digest throws with the family and all actual derived digests. Other
families never provide a fallback.

`RendererPerfRecipeFacts.RecipeSha256` and `GeometrySha256` identify portable authored
inputs. `PlatformFamily` and `Derived` identify the running family's computed construction.
`SequenceSha256` remains the complete live sequence digest. `Describe()` returns the
complete live construction for diagnostics and assertions, rather than the frozen envelope.

Freeze the local family from cold construction. Collect any new family's digests from a
hosted `cross-platform-gpu` failure, review that portable inputs match, commit the new
entry and run the complete matrix again. Never accept missing entries or copy a digest
from another family without measuring it. Keep GPU caches enabled and the render filter
empty for the full gate. The public engine repository uses hosted runners only.

## Water optics v2 recipes

The `water-optics-v2` recipes measure the opt-in water optics. Their ids contain slashes, so
`water-optics-v2/village/off` freezes at `recipes/water-optics-v2/village/off.json`.
`RendererPerfRecipe.IdFromVersion` keeps everything before the last `/` of a version as the id.
`RendererPerfWaterOptics.RecipeIds` lists the six in scene order, the village pool under `off`, `m2-on`
and `m5-on`, then the outdoors pool under the same three profiles.

The three profiles of one scene share geometry, assets and sequence. Their settings differ only in
`UnderwaterSunlight` and `WaterColumnLight`. Each pool is 1.5 m deep with a sloping bed, rocks resting
on the bed, wholly submerged casters and floaters straddling the surface. The wave clearance is a
guaranteed bound on the surface height plus 0.15 m. The procedural bound is `SwellAmplitude`, which the
swell components share exactly. The FFT bound sums the absolute amplitude of every mode of the 256-point
field. The v1 sea state's bound exceeds 30 m, so the outdoors pool shortens the fetch to 20 m, which bounds
the surface at 0.0955 m. Tests check both bounds against the engine's own surface heights. Both cameras target the
shoreline centre and frame the pool so its surface covers about half of every measured frame.

The v2 cameras cull part of the land, so their `Expected` block records it. Terrain visibility comes from
the engine's main-pass rule (`Scene3D.IntersectsMainPass`) and is constant over the sequence. Rigid
draws and foliage patches move by a few as the camera drifts, so they are ranges over all 256 sequence
frames. Foliage keeps its intended instance count and adds the patches tested, submitted and culled and
the candidate instances. The
range narrows and widens the frustum by 0.1 percent, so a draw at the frame edge cannot make the authored
counts differ between platforms. v1 recipes frame all their content and keep their original block.

The six files were frozen on `osx-arm64`. Their `linux-x64` and `win-x64` families come from a hosted
`cross-platform-gpu` run, as for any new family. Until then the frozen recipe theory and every test that
constructs these recipes with verification fail on Linux and Windows.
