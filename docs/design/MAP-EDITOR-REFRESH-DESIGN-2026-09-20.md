# Map editor interaction and terrain workflow refresh

Status: proposed. The owner selected middle-mouse orbit around the terrain point under the cursor.
The remaining scope below is a proposal for review, not a shipped contract.

## Purpose

Make terrain authoring comfortable in the shared engine editor and its consumer heads, including
Ruinborne. Improve navigation, terrain feedback, visibility access and measured responsiveness while
preserving map data, undo semantics and the existing consumer integration.

## Current evidence

- `MapEditorScene.UpdateCamera` delegates to `FlyCameraController`. Its default look button is right
  mouse and its wheel changes flight speed. The scene currently gates command modifiers and modals.
- `BuildLayersInspector` exposes groups and named scatter layers when nothing is selected. Sculpting
  replaces that inspector, so visibility is not continuously accessible during terrain work.
- Scatter visibility calls `RebuildWorldForVisibility`, which rebuilds `ViewportWorld`.
- `SculptCursor` samples a 64-segment terrain-following ring. The scene renders it with debug lines.
- Ruinborne's editor head constructs the shared `MapEditorScene`.

These are code observations. No frame-time measurements or visual reproduction have been captured yet.

## Approach decision

Scores are design judgments from 1 to 10, with higher better. Workflow quality and maintainability
each have double weight. Delivery speed has single weight.

| Approach | Workflow | Maintainability | Delivery speed | Weighted total / 50 |
| --- | --- | --- | --- | --- |
| Refresh existing editor through focused components | 9 | 9 | 7 | 43 |
| Remap mouse and rearrange existing inspector only | 5 | 7 | 10 | 34 |
| Replace the editor shell and rendering integration | 9 | 5 | 2 | 30 |

Recommend focused components. This retains tested document and command behavior while addressing
the expensive visibility path. A minimal patch is faster but leaves terrain workflow friction.
A replacement offers layout freedom but creates much more integration and regression work.

## Navigation

- Middle-mouse press inside the viewport captures the terrain hit as the orbit pivot. Hold that
  pivot for the entire gesture. Never repick it as the cursor crosses the map.
- A miss uses the last valid navigation pivot, or a point ahead at the configured initial orbit
  distance when no pivot exists. No gesture waits for unloaded terrain or jumps to world origin.
- Shift+middle-mouse pans in the camera plane. Choose orbit or pan at press time and keep that mode
  until release. Pan translates both camera and pivot.
- Wheel dollies toward or away from the navigation pivot with bounded distance and pitch. It must
  not also change flight speed. Retain right-mouse fly navigation with explicit speed controls.
- Focus selection establishes a useful pivot and framing distance. Existing shortcut bindings must
  be checked before assigning a key. Do not silently replace an editing shortcut.
- A navigation gesture owns pointer input until release or cancellation. It cannot start a sculpt,
  selection or gizmo gesture. Chrome, text fields and modals prevent navigation acquisition.
- Cancel navigation on focus loss and release capture cleanly. All input comes through engine input
  snapshots. Put navigation policy in a new headless-testable component, not more scene branches.

## Terrain feedback

Draw a surface-following outer footprint, a falloff guide and a centre marker with consistent
screen-space readability. Distinguish hover, active stroke and invalid target states. Operation
colour is supplementary to a visible operation label. Do not rely on colour alone.

Indicators use the same live terrain and bounds as the brush. They hide over chrome and modals and
while navigating. Do not show a valid footprint over unloaded or unpickable terrain. Preserve the
current brush mathematics and one-undo-step-per-stroke behavior.

Use a dedicated overlay component and bounded reusable geometry. Verify depth handling on slopes,
water edges and distant terrain across the supported graphics backends before accepting it.

## Visibility workflow

Provide an always-accessible View panel independent of selection and brush inspectors. Include
authored props, trees, rocks, water, named scatter layers and editor markers. Keep existing individual
hide controls. Show All and Terrain Only are immediate, discoverable actions.

Terrain Only is a temporary visibility override. Switching it off restores the underlying choices.
Underlying choices may still be edited while the override is active. Hidden objects cannot intercept
viewport picking. Visibility never dirties the map, creates undo entries or changes saved content.

Use explicit consumer-supplied category metadata for trees and rocks rather than guessing from asset
names. Unclassified assets remain in Other Props. Preserve named layer controls for mixed content.
The implementation plan must trace existing registry metadata before selecting the additive API.

Apply visibility at draw submission and picking without regenerating terrain or scatter. Carry
category identity through prop submissions where needed. Existing caches stay resident while hidden
for fast restoration. Hiding is not a promise to reclaim memory or stop all background streaming.

## Performance and validation

Capture a repeatable route on a representative Ruinborne map with fixed camera poses, viewport size,
render distance and warm-up. Compare idle, orbit, pan, sculpt, visibility toggles and undo separately.
Record median and p95 frame times, allocation rate, rebuild counts and stroke-to-visible latency.
Keep benchmark map copies separate from authored content and record the tested engine build.

Acceptance requires zero terrain/scatter rebuilds for visibility-only changes, no navigation-induced
document mutations, bounded overlay buffers, and no p95 regression outside measurement noise on the
same machine and workload. Report actual improvements rather than selecting an unsupported FPS claim.
Optimise measured bottlenecks, preserving local terrain invalidation and avoiding unrelated rewrites.

Headless tests cover input ownership, fixed orbit pivots, misses, cancellation, restoration of
visibility choices, hidden-object picking, unchanged serialization and rebuild counts. Run Release
tests and repository guards. Rendering changes require branch GPU verification on supported backends.
The final user playtest covers real mouse gestures, readable indicators and terrain-only editing.

## Delivery boundaries

Implement in order: navigation and capture, persistent visibility controls and draw filtering,
terrain overlays, measured performance fixes, then consumer adoption and playtest. Each step retains
a focused verification boundary. New scene responsibilities get new cohesive types.

No map-format migration, terrain algorithm redesign or replacement editor shell is proposed.
After approval, create the implementation plan, link the work to the existing editor issue programme,
and follow the package release and consumer repin rules. This design-only change does not bump a version.
