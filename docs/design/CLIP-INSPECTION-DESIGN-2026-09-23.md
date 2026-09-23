# Clip inspection

## Status

In flight under [#1055](https://github.com/APKiwiOrg/KhaozEngine/issues/1055). `PoseProbe` and the
shared generated test rig land first. The other measures use the contracts below in later chunks.

## Purpose

Animation import preserves authored data, but a valid file can still contain a foot that crosses the
floor, a held foot that slides, an equipped segment that crosses a limb, or tracks that violate a
game's clip policy. The inspection API is pure, headless, and independent of a graphics device so
asset pipelines and tests can run the same checks.

All positions, distances, radii, heights, and translations are in model-space metres. Angles and
quaternions use `System.Numerics` conventions. Clip time is in seconds. Normalised phase is in the
closed interval `[0, 1]`, where `0` samples time zero and `1` samples the authored end key at
`AnimationClip.Duration`. Callers that need looping wrap their phase before calling the inspection
API.

The skeleton uses row vectors. A node model matrix is `local * parentModel`. A segment attached to a
node is `segmentLocal * nodeModel`. Inspection never applies inverse-bind matrices or a game object's
world transform.

## PoseProbe

`PoseProbe` owns one local pose and one model matrix per skeleton node. Construction starts at the
rest pose. `SampleClip` multiplies a finite normalised phase by the clip duration without wrapping.
`SampleClipAtSeconds` clamps a finite time to the closed authored range. Both sampling methods reject
a clip whose duration is negative or non-finite. A zero-duration clip samples at zero.

`SetLocals` requires exactly one pose per skeleton node and copies the input before composing. Model
matrices are composed for every node, including named hierarchy nodes that are absent from the skin
palette. Name lookup uses `Skeleton.IndexOf`, including its precise missing-name and duplicate-name
diagnostics. Index lookup rejects values outside `[0, NodeCount)`.

`JointModel` returns the current model-space matrix. `JointPosition` returns its translation. The type
keeps no device, mesh, inverse-bind, or world-transform state.

## FootPlant

`FootPlantReport.MinSoleHeight` is the minimum model-space Y coordinate reached by any requested foot
after applying `soleOffset` in that foot's local frame. `MinSolePhase` is the first sampled phase that
reaches that minimum. Ties use foot-list order, then increasing sample index. Height is in metres and
phase is unitless.

Sampling uses `samples` uniformly spaced looping phases `i / samples` for `i` in `[0, samples)`. At
least two samples are required. A stance sample is one whose sole height is less than or equal to
`groundHeight`. A stance interval is a maximal circular run of stance samples for one foot. The run
at the end of the sample array joins the run at the beginning. A circular run is unwrapped so its
phases increase beyond `1` after the join.

`strideMetres` is the forward distance covered by one complete clip cycle along model-space `+Z`. For
an unwrapped phase `p`, distance-driven inspection places the character at `(0, 0, p *
strideMetres)`. The inspected sole position is therefore its sampled model position plus that virtual
translation. This makes an in-place foot that moves backward by one stride during stance stationary
in travel space. `MaxStanceSlide` is the largest horizontal XZ distance from the first travel-space
sole position in each stance interval to any later position in that interval. It is zero when no
interval contains two samples. Negative or non-finite stride values are rejected.

## SegmentClearance

`Capsule` names two skeleton nodes and gives a non-negative radius in metres. At each uniformly
spaced closed phase `i / (samples - 1)`, the riding segment endpoints are transformed by
`segmentLocal * ridingNodeModel`. Each capsule axis joins its two current node positions.

`SegmentClearance.Min` returns the smallest Euclidean segment-to-segment distance minus the capsule
radius across every phase and capsule. The result is a signed distance in metres. Positive means
clearance, zero means contact, and negative means penetration. Input order and increasing sample
index break equal-value ties, though the public result contains only the distance. At least two
samples and one capsule are required.

## ClipHygiene

`ClipHygiene.Check` emits value findings in skeleton node order, then the fixed rule order below.
Details use invariant culture. Unknown track targets sort after known nodes by target logical index.

The rules are:

1. `unknown-node` reports a track target that does not resolve to the skeleton.
2. `allowed-node` reports every track whose resolved node name is outside non-null `AllowedNodes`.
3. `translation` reports a translation track whose node name is outside `TranslationAllowed`.
4. `scale` reports any scale track.
5. `rotation-unit` reports a quaternion key that is non-finite or whose length squared differs from
   one by more than `0.0001`.
6. `key-times` reports each present channel whose key times are non-finite or are not strictly
   increasing.
7. `key-density` reports each present channel where `(keyCount - 1) / clip.Duration` is below
   `MinKeysPerSecond`. A non-positive or non-finite duration fails every present channel.
8. `loop` applies when `Looping` is true. It samples the complete local pose at time zero and at
   `clip.Duration` and compares them. Translation and scale components may differ by at most
   `0.0001`. Rotation passes when the absolute quaternion dot product is at least `0.99999`, so `q`
   and `-q` are the same orientation. The authored end pose is compared directly and is never
   wrapped to the start pose.

`AllowedNodes` and `TranslationAllowed` use ordinal, case-sensitive skeleton names. An unresolved
target produces `unknown-node` and is not silently treated as an allowed node.

## ClipReport

`ClipReport.Write` returns a canonical text report. If encoded as UTF-8, its byte contract has no byte
order mark, uses LF line endings, and ends with one LF. Clips, phases, and position nodes retain
caller order. Phases are normalised closed phases and therefore preserve the authored end at `1`.
For each phase the report records every skeleton node's sampled local-space rotation in skeleton node
order, followed by requested model-space socket positions in caller order. Local rotation is the
`JointPose.Rotation` value after clip sampling. It is not converted through a model matrix.

The grammar is tab-separated:

```text
KhaozEngine ClipReport v1
clip\t<clip-name>
phase\t<phase>
rotation\t<node-name>\t<x>\t<y>\t<z>\t<w>
position\t<node-name>\t<x>\t<y>\t<z>
```

Each `phase` line is followed by one `rotation` line per skeleton node and then one `position` line
per requested node. Every float uses invariant culture and fixed six-decimal `F6` formatting. Values
that round to negative zero are written as
`0.000000`. Names escape backslash, tab, carriage return, and line feed as `\\`, `\t`, `\r`, and
`\n`. Non-finite phases, sampled coordinates, or rotation components are rejected. These rules make
two calls over the same values byte-identical under every process culture.

## PoseBlend

`PoseBlend.BlendInto` blends each existing `into` local toward the corresponding `from` local.
Translation and scale use component-wise linear interpolation. Rotation uses shortest-arc quaternion
spherical interpolation followed by normalisation. The effective node weight is `weight` when no
mask is supplied and `weight * mask.Weight(node)` otherwise, clamped to `[0, 1]`.

The spans must have equal length. A mask must contain that node count. A non-finite weight is
rejected. Weight zero leaves the destination unchanged. Weight one with no mask copies the source
values. The method allocates no managed memory in steady state.

## Test asset

Tests generate a named two-legged skeleton and a walk clip in memory. The hierarchy includes a
named weapon socket that is not part of the skin palette. Its asymmetric local transforms make
matrix multiplication order observable. The walk clip contains explicit start, mid-cycle, and end
keys. A companion non-looping endpoint clip gives phase `1` a value distinct from phase `0`. No
authored file or graphics device participates.
