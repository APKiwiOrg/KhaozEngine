# Public Blender rigid-subset fixture

This is one synthetic five-joint rig and twelve exported vertices. It contains no consumer mesh,
private wardrobe data or extracted consumer numbers. `source.blend` is editable. `author.py` creates
the rig, evaluates Blender's actual Preserve Volume modifier, exports `rigid.glb`, inspects its
accessors and animation keys, and writes `expected.json`. No renderer or live Blender session is used.

The retained export used Blender **5.2.2 LTS**, build `d13f752e3b9c`, and the bundled glTF exporter
**5.2.40**. Provenance records the generator, exporter primitive-extraction source hash, GLB hash,
export options, sample frames and times. A different Blender or exporter version needs a fresh
comparison, not an assumption of identical fixture bytes. Blender files also contain save metadata.

From the repository root, regenerate into scratch with the same installed versions:

```bash
build-slot --heavy --label dq-blender-export -- blender -b --factory-startup -P KhaozEngine.Render.Tests/Fixtures/dual-quaternion/blender-rigid/author.py -- --output /tmp/khaozengine-dq-skinning/blender-rigid
```

Copy only `source.blend`, `rigid.glb` and `expected.json` back beside the script after inspecting the
exporter order and numerical comparisons. The exported mesh references the single uninstanced rig
through its Armature modifier. Blender's exporter reports its non-parented-armature warning and
resolves that rig by name. Tests check the resulting joint identities, binds and skin inputs.

## Coordinate and input provenance

Blender column-vector positions `(X,Y,Z)` convert to engine/glTF `(X,Z,-Y)` without unit scaling.
`coordinateTransform.rowMatrixWords` records this conversion for System.Numerics row vectors.
Every matrix word is an unsigned IEEE binary32 bit pattern. A glTF column-major MAT4 has the same
stored sequence as its transposed System.Numerics row-major matrix.

`inverseBindWords` comes directly from the exported skin accessor. Each sample's `jointWorldWords`
comes from actual exported animation keys composed through the exported node hierarchy. The tests
also load and sample the GLB with the engine's animation path, so the recorded input palette cannot
hide a changed export or import convention. `blenderPositions` comes independently from Blender's
evaluated mesh and is then coordinate converted.

The JSON records global source vertex-group order, each vertex's source order and weights, the
exported vertex mapping, and actual JOINTS_0 and WEIGHTS_0 order and weights. No influence is inferred
from a bone name or silently sorted in the engine. All active weights are exact binary fractions.

## What passes and what diverges

Frames 1, 13 and 25 exercise two influences with nonzero quaternion dot, four influences with positive
pairwise dots, and an off-origin pivot with translation. Each of the nine vertices is compared in
source and exported order. The test uses the ordinary componentwise bound `2e-5 * L` after coordinate
conversion, where `L` is the maximum of one, source-position norms and prepared translation norms.

Frame 37 deliberately uses four ambiguous rotations. The source order is joints 0, 1, 2, 3 and the
exporter sorts their unequal weights into joints 3, 2, 1, 0. A second mesh on the same rig evaluates
Blender with that exported vertex-group order. Its actual positions are retained separately as
`reorderedBlenderPositions`. Engine source-order results match source Blender and engine exported-order
results match reordered Blender within the same bound. The two orders diverge by up to about 1.473
model units. `divergentOutcome` explicitly classifies this as an influence-order difference outside
the matching-input promise, never a passing source-to-export parity row.

Frame 49 supplies actual nonuniform-scale and inherited-shear composed matrices. The tests require
rigid palette validation to reject both. A five-positive-influence mesh and an Armature plus Simple
Deform modifier mesh remain in the blend as labelled exclusions. They are omitted from the exported
parity mesh and recorded as `outside-promise`, with reasons. The five influences are recorded before
any exporter reduction. Envelopes are disabled and this fixture has no segmented B-Bones or group mask.

```bash
build-slot --label dq-blender-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~DqBlenderParityTests
```

This evidence covers the public rigid subset only. It makes no renderer completeness, coverage,
continuous no-clipping or private wardrobe claim.
