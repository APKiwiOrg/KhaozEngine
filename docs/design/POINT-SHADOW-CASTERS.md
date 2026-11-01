# Point-shadow caster layers

Static point lights combine a cached base with a current-frame transient contribution. Dynamic point lights
clear and redraw all their rigid and skinned casters in the selected base row. The key-light cascade policy is
independent of point-shadow mobility.

## Explicit rigid mobility

`RigidInstanceDraw.ShadowMobility` uses `ShadowCasterMobility.Static = 0` by default, including legacy draw
routes and the zero value. Static rigid geometry participates in the cached signature by mesh identity, absolute
transform, cast kind and quantized dissolve. Changing its transform still invalidates the base. Independent
per-mesh-run hashes are added before the final mix, so a moving actor introducing or replacing a shared mesh
cannot change the static signature through first-seen run order. Within each run, static caster order and the
existing one-word-sensitive mixer remain intact.

`ShadowCasterMobility.Dynamic = 1` routes rigid geometry into current-frame depth. It does not enter the static
signature. Unsupported enum values are refused before queue mutation. Motion identity, `CastsShadows`, and
`ShadowOnly` remain independent. A motion key alone never selects this path.

Grouping scatters mobility with each uploaded slot through retention, mesh grouping and interior partitioning.
The existing absolute-space point caster index supplies both signatures and draw spans. Layer filtering never
removes an opted-in caster from color or key-light rendering. Off-camera and shadow-only rigid casters remain
eligible. Existing terrain, dissolve, complement, interior-cut, near-radius and exclusion-box policies apply.
Point-light MASK alpha testing remains [#1098](https://github.com/APKiwiOrg/KhaozEngine/issues/1098).

## Split rows and sampling

A pure static base contains Static rigid geometry and rest-pose casting foliage. A transient row contains
Dynamic rigid geometry and current skinned poses, through CPU or GPU skinning. Both use the same face
resolution, bias, dissolve noise and six-face placement. Receivers compare the nearer base and transient depth
per hard or soft filter tap. Filtering two visibility results separately would darken overlapping casters, so
the nearer-depth combination happens before comparison. An unassigned transient row takes the base-only path.

Base ownership and transient assignment are independent. Transient rows are dense and selected nearest first,
then by stable key and uploaded light index. Only sampleable static base rows, or new rows selected for their
first ordinary render, demand a transient row. Dynamic lights always publish `transientRow = -1`. Their ordinary
per-frame light budget remains intact and includes eligible skinned geometry in each selected row.

## Complete-base compatibility fallback

Moving rigid shadows must remain present before transient storage exists or when growth is refused. Each base
row tracks its current owner, its rendered static signature, previous moving-rigid demand and whether its depth
contains transient geometry. Ownership replacement and atlas reset or disposal clear this state.

When a sampleable or selected static row touches Dynamic rigid geometry and has no assigned transient row, the
pass clears and redraws a complete current base with all rigid, eligible skinned and foliage casters. It repeats
that complete redraw while fallback is required. It never publishes an unrelated or stale transient row.

When transient storage becomes available, the pass first clears and redraws a pure base, then renders and
publishes the current transient contribution. If storage is lost later, it redraws a complete current base in
that frame. A fallback row whose actors disappear or stop casting receives a pure-base cleanup. A fallback row
with only skinned actors remaining continues complete redraws until those actors can move to a transient row.
The existing skinned-only omission during allocation delay is unchanged for rows that never entered fallback.

A changed rendered static signature while current or previous Dynamic rigid demand exists also requires a pure
base transition. This handles both mobility switches at zero ordinary static budget. It can conservatively
rebuild simultaneous scenery changes. Disappearance without a static-signature change leaves an already pure
base cached. Legacy rows without opted-in demand keep their ordinary static budget semantics.
The conservative dirty flag may also require a transition after geometry returns to its rendered signature.
Selection and rendering use the same transition rule, including that dirty state.

Fallback and pure-base transitions are mandatory correctness work outside `MaxStaticRebuildsPerFrame`. One
shared plan deduplicates them with ordinary work, with at most one base render per affected owned light per
frame and at most the live base row count. Existing mandatory rows leave the full ordinary allowance available
for independent dirty rows and new admissions. First admission always consumes an ordinary selection, even
when that render uses complete-base fallback. Admission never refunds that selection to admit another new row.
Transient assignment includes admitted rows before final layer planning, which advances demand history once.
This may cost a full scenery redraw when storage fails. It preserves shadows, face resolution and current actor
poses without a quality knob.

## Resource lifecycle

Allocation and receiver rebinding happen at `Scene3D.Begin`, outside an open command list. First demand records
the exact row count for the next boundary. Capacity grows to the observed high-water demand, capped by live base
rows. It does not shrink within a compatible base layout. A smaller live transient atlas serves a deterministic
subset during growth while affected moving-rigid rows use complete-base fallback.

Build, bind and commit remain transactional. Allocation refusal keeps the previous compatible transient atlas
or white default. Receiver-set refusal frees candidates and preserves both live bindings. Refused shapes latch
until demand or face resolution changes. Base replacement invalidates every cached content state. Base shrink,
disable and disposal retain the existing safe resource retirement and default-binding rules. No receiver samples
a disposed texture. `ResolvedPointShadows` reports base, transient and total live storage and degradation.

## Diagnostics

`ShadowPassDiagnostics` is a value-only snapshot. `PointStaticRebuilds` counts ordinary budgeted static renders.
`PointRigidFallbackRowsRendered` counts complete-base compatibility renders and
`PointRigidTransitionRowsRendered` counts mandatory pure-base renders. These categories are disjoint.
`PointStaticTransientRigidDrawCalls` and `PointStaticTransientSkinnedDrawCalls` count actual transient face draw
calls by caster type. `PointStaticFallbackSkinnedDrawCalls` reports skinned face draws into complete static bases.
`PointDynamicSkinnedDrawCalls` retains its dynamic-base meaning. `PointFaceDrawCalls` includes every caster face
draw in every category, including fallback and transitions. It excludes row-clear quads. Demand counts eligible
static lights before live transient capacity is applied.

## Verification

CPU fixtures drive actual `Scene3D` scheduling through the established fake GPU and inspect submitted slot
ranges and draw streams. They cover default compatibility, mixed mobility, both skinning paths, dynamic lights,
zero ordinary budget, positive ordinary refresh and admission beside mandatory work, allocation and bind refusals,
growth, recovery, removal, mobility changes, slot ownership,
off-camera retention, mesh changes, dissolve and rebasing.

`RigidPointShadowGpuTests` compares full-resolution split images byte for byte with a separate scene whose
Dynamic lights redraw complete current geometry. The oracle matches base atlas height and light row placement
to control viewport-coordinate rounding. The split scene still has different base and transient row indices
and counts. Hard and soft cases cover mixed same-mesh rigid mobility, skinned casters, first demand, growth,
recovery, both mobility switches and disappearance with zero ordinary refresh budget. Direct receiver probes
disable casting while retaining the actor bodies, so body color cannot satisfy the negative control. Fixed
scenery probes and work counters accompany the image comparisons. A fresh-scene parity check validates reuse
of the lazy fixture after configuration changes. These fixed frames verify behavior without timing thresholds.
Shader and binding changes require the backend checks in `docs/CROSS-PLATFORM.md`.
