# Character asset

`Player.glb` is a rigged + animated CC0 character built from **Quaternius** "Universal" packs:

- **Universal Base Characters** (the *Superhero Male* body mesh)
- **Universal Animation Library** (`UAL1_Standard.glb`, in-place locomotion clips)

Both share one 65-bone universal rig, so the body and the clips compose without retargeting.

- Author: Quaternius (https://quaternius.com / https://poly.pizza/u/Quaternius)
- License: **CC0 1.0 Universal** (public domain dedication, no attribution required; credited here
  as a courtesy, free for personal / educational / commercial use).

## Bake (how this was prepared)

Produced offline by Ruinborne's `scripts/bake-character.py`, which merges the Superhero Male body
with the Universal Animation Library onto the shared rig and exports one glb containing the body plus
exactly five clips named `Idle` / `Walk` / `Run` / `Jump` / `Fall` (in-place; the engine drives world
position, there is no root motion). Textures are 1024. It is ingested through the engine's SKINNED
path (`GltfLoader.LoadSkinnedWithMaterial` + `GltfLoader.LoadAnimations`) so the rig and animation
channels are preserved (it is NOT flattened like the props).
