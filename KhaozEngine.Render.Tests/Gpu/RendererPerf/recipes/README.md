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
