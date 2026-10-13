# Pan, zoom and bottom-sheet gestures

Status: engine design and implementation plan approved by the owner on 2026-10-13. Implementation is in progress.

Engine issue: [#1328](https://github.com/APKiwiOrg/KhaozEngine/issues/1328).
Consumer adoption: [Flightline #3](https://github.com/APKiwiOrg/Flightline/issues/3).

## Intent and agreed scope

A MacBook trackpad must pan and zoom Flightline's map through reusable engine input and gesture
contracts. The engine supplies native input, camera-independent gesture interpretation and a
draggable sheet. Games supply cameras, geographic wrapping, selection and presentation policy.

The owner confirmed native macOS magnify and precise pan plus Windows Ctrl+wheel compatibility
for this first release. Full native Windows precision-touchpad pan is outside this release.
The sheet remains in the same release as the controller. Native mobile hosts, soft keyboards,
consumer camera flights and changes to ScreenStack routing remain outside scope.

The existing Flightline design already settles its sheet policy: content-sized peek capped at
45 percent of available height, expanded below the HUD, content scrolling only when expanded,
and preserved detent across selection changes. These are consumer configuration, not engine defaults.

## Verified failure and existing contracts

- `AppWindow.WireInput` forwards only `wheel.Y` to `InputAccumulator.OnScroll`.
  `BuildInput` never supplies touch points. `InputState.Touches` is empty on desktop.
  No native magnify signal reaches a consumer reading that snapshot.
- `InputState.ScrollDelta` is the existing vertical wheel quantity. `MousePosition` is framebuffer
  pixels. `MouseDeltaPoints` is window points. `Pointer` maps positions into its supplied viewport.
- GLFW's Cocoa scroll callback scales precise deltas by 0.1, then exposes only X and Y.
  Precision, gesture phases and magnification cannot be recovered from that callback.
- `GestureRecognizer` preserves movement from the press origin when slop is crossed.
  `PinchRecognizer` supplies two-point scale and centroid motion. Neither owns the full lifecycle,
  admission, inertia or gesture arbitration required here.
- `PannableCanvas` currently applies drag and wheel immediately. Its public docs explicitly defer pinch.
- `ScrollablePanel` clamps wheel and drag scrolling internally, returning no residual movement.
  Header resize is latched to avoid also scrolling content under a moving header.
- Pointer reservations and GuiSurface reservations are separate. Bounds helpers do not implicitly
  consult either. Admission must be explicit without changing ScreenStack routing.

## Architecture choice

Scores are 1 to 10, higher is better. Criteria have equal weight.

| Approach | Reuse | Existing-consumer compatibility | Deterministic testing | Delivery scope | Total |
|---|---:|---:|---:|---:|---:|
| Windowing gesture core, Gui adapters and sheet | 10 | 9 | 10 | 8 | 37 |
| Add gestures directly to PannableCanvas and ScrollablePanel | 5 | 7 | 8 | 9 | 29 |
| Deliver native input only, keep recognition in consumers | 3 | 9 | 6 | 10 | 28 |

Recommend the first approach. It gives both Flightline cameras one contract and keeps sheet motion
consistent without introducing a dependency on a camera or GUI package into Windowing. It costs an
explicit input payload and gesture state machine. Direct GUI changes would leave non-canvas cameras
reimplementing recognition. Native input alone would not satisfy the agreed engine ownership.

## Native input and immutable snapshots

Add engine-native, ordered pointer gesture samples to InputState. Each sample records kind, source,
phase, event-time anchor, scroll units or scale factor, modifiers and native-momentum status.
Kinds cover discrete wheel, precise scroll and magnify. Phases cover unphased, began, changed, ended
and cancelled. The batch preserves multiple event origins and mixed wheel/pinch input within one frame.
Its storage cannot be mutated when the next frame is collected. Empty input uses a shared empty batch.

Expose horizontal scroll and precise-scroll/pinch summaries for simple consumers. The ordered batch
is authoritative for the new controller. Summaries are compatibility conveniences and are never
applied in addition to their samples. Preserve the numeric meaning of existing ScrollDelta.
Do not change the existing public constructor signatures. Add overloads or a typed payload overload.
Old synthetic snapshots with only ScrollDelta still work through one documented fallback path.
The payload exposes stream availability so an intentionally empty native stream never falls back
to legacy scroll that was retained only for existing consumers.

Anchors use framebuffer pixels like MousePosition. Precise pan deltas are window points, discrete
wheel deltas are wheel units and scale is dimensionless with identity 1. Transform event anchors and
touch coordinates through the same viewport as Pointer. Transform precise deltas as vectors, never
as positions. Keep touch identity stable across list ordering and convert into pointer coordinates
before measuring centroids or distance. All public target coordinates use Pointer's coordinate space.

Every snapshot copy, scale, automation merge and suppression path must preserve or intentionally
remove the payload. WithoutScroll clears both scroll axes and scroll samples, while retaining magnify.
Provide a separate operation for removing all pointer gestures. InputManager suppression cancels and
filters gesture delivery even though its raw State remains readable. Document the raw-state escape.

### macOS

AppWindow owns a local NSEvent monitor filtered to its own NSWindow. Collect magnify, precise scroll,
event phases, momentum phases, event-time modifiers and location before GLFW discards the metadata.
Keep raw AppKit access in AppWindow's platform-specific implementation. No public native types,
GLFW fork, process-wide method replacement or additional third-party dependency is introduced.

Return the original event so GLFW continues its normal delivery. Enrich the existing scroll path
without counting the monitor and GLFW callback twice. Magnify emits only the new gesture payload.
Preserve native scroll direction and units. Convert Cocoa's location origin to engine coordinates.
Reject non-finite values and non-positive scale factors before they enter the snapshot.

Root callback delegates and block storage for the monitor lifetime. Remove the monitor before input
or window teardown, including partial-initialization failure. Filter other windows and ignore input
while unfocused or captured. Native begin/end/cancel events must survive even when their delta is zero.
Contain managed exceptions at native callback boundaries, but retain the first setup, callback,
removal or storage-release failure as a queryable AppWindow diagnostic. False native registration
or removal results are failures too. A backend that is intentionally unsupported is normal fallback.

### Windows and other desktop backends

Preserve X and Y wheel values. On Windows capture the wheel message's event-time Ctrl modifier,
including synthetic Ctrl+wheel input, so release of Ctrl before the frame boundary cannot lose zoom.
Chain the original window procedure and preserve legacy wheel delivery. Consume the normalized
gesture only once. Label this source as modifier-wheel compatibility, not proven native pinch.

Plain wheel input retains wheel semantics. Do not infer precision from fractional values or device
names. Other desktop backends use wheel and Ctrl/Cmd+wheel support with the same deterministic
controller. Windows native precise pan requires a future platform backend and device proof.

## Reusable controller and admission

`IPanZoomTarget` in Windowing has two operations:

```csharp
void PanByScreenDelta(Vector2 delta);
void ZoomByFactor(float factor, Vector2 anchor);
```

Positive pan moves displayed content in the positive screen direction. A zoom factor greater than
one enlarges content, with the target keeping the world point under the anchor fixed. The target
owns clamping and wrapping. The controller never reads camera state or assumes a projection.

`PanZoomController` accepts Pointer, the matching InputState, a viewport, real elapsed seconds,
the target and optional admission/focus policy. An InputManager convenience overload respects
manager suppression and the current viewport conversion. Configuration is validated and exposes
slop, tap timing, wheel step/duration, inertia decay and keyboard speeds. Keyboard navigation is off
unless explicitly enabled and focused. Results expose accepted tap and double-tap events/positions.

One gesture owns a contact sequence. Admission tests its start against viewport, focus, pointer
capture, consumed state and blocked regions. An optional predicate includes GuiSurface or consumer
blockers. Overlays update first, the controller admits next, and a canvas reserves its own region
after admission. A rejected start stays rejected until the entire sequence ends, even if the overlay
disappears or fingers move out. Touch admission checks every participating contact, not only the
centroid. New contacts cannot rescue a rejected sequence.

Cancel on focus loss, capture, suppression, viewport/scale change or explicit lifecycle cancellation.
Discard inertia, pending zoom and tap history. Held contacts remain inhibited until released.
Callers cancel when hiding or replacing a view. No synthetic release may become a tap or fling.

## Deterministic gesture behavior

- Drag crossing slop emits all displacement from the press origin, including sub-slop travel.
  Release includes final displacement. A short stationary hold before release clears stale velocity.
- Release inertia uses an elapsed-time velocity estimate and analytic exponential integration.
  New press or direct gesture input cancels it before applying movement. Native momentum is applied
  directly and never gets a second engine-generated fling.
- Discrete wheel zoom is anchored to the event cursor and animated over a short configured duration.
  Preserve order when anchors differ. Coalesce only compatible adjacent steps. Cancellation clears
  remaining steps. Total factors compose multiplicatively rather than by summing scale deltas.
  Before a later direct pan or magnify event, finish the pending wheel remainder in event order.
  This input-driven completion also applies at zero dt, keeping direct gestures immediate without
  moving an earlier wheel effect after them. Idle zero-dt updates still advance no animation.
- Precise scrolling pans directly. Magnify zooms directly. Ctrl/Cmd scrolling zooms once instead of
  also panning. Mixed events in one frame retain their order and event-time modifiers.
- Two touches zoom about the previous centroid, then pan by centroid movement, preserving the
  material point under the fingers. Coincident fingers, reordered IDs and contact replacement rebase
  without singular scale or jumps. Lifting one finger rebases the remaining drag and cannot tap.
- A double tap zooms in at the second tap. A two-finger tap zooms out at its centroid. Movement,
  cancellation or extra contacts disqualify taps. Single taps are reported immediately, the second
  qualifying tap reports double-tap instead of another single. Document this selection tradeoff.
- Optional focused keyboard arrows pan and plus/minus zoom about the viewport centre. No wall clock,
  background task or platform timing is read by the controller. Zero elapsed time advances no animation
  or velocity estimate. Reject negative/non-finite elapsed time and invalid configuration.

Put shared drag/slop/velocity mechanics in one cohesive headless type used by both the pan/zoom
controller and sheet. Reuse existing recognizers where their contracts fit. Preserve their old public
behavior with additive cancellation/reset support where required. Do not force a sheet through a camera.

## PannableCanvas compatibility

Add a frame-aware update overload using the controller and a Camera2D target adapter. Preserve the
legacy `Update(Pointer, float)` overload's immediate wheel behavior, Pan default, zoom limits and
SnapZoomLevels. The adapter owns clamp and cursor anchoring. Both paths share camera transform math.
The new path exposes inertia and gesture configuration, consumes full input, and reports accepted
taps instead of treating drag release as a tap. Document which update and tap APIs form each path.
Wheel policy is explicit in controller options. An optional wheel-factor resolver lets the canvas
choose adjacent snap stops when each ordered wheel operation becomes active, without exposing camera
state to Windowing. Custom-resolver samples are not coalesced. Native pinch and precise-scroll zoom
bypass that resolver. Command+precise-scroll zoom has a separate sensitivity in raw window points.

## DraggableBottomSheet

Add a Gui primitive with configurable named detents resolved against caller bounds, content height
and host-supplied SafeAreaInsets. Expose sheet, handle and content rectangles, current/target detent,
content scroll offset and changed/dismissed results. Caller-supplied available bounds exclude HUD
and tab bars. Peek caps and expanded height are configurable. No OS safe-area probing is added.

The handle always owns sheet drag. A content drag first expands the sheet to its highest detent,
then passes leftover upward movement to content in the same update. At full expansion, downward
movement first unwinds content scroll to zero, then passes the remainder to sheet collapse. Preserve
every unit at the boundary. Horizontal motion does not resize. A tap handle action toggles configured
detents for mouse/keyboard accessibility. Dismissal requires an explicit dismissible detent.

Release projects the shared drag velocity onto the vertical axis and selects a bounded detent.
Snap motion is driven only by supplied elapsed time. New press interrupts it. Bounds changes cancel
motion and resolve the same named detent against the new safe area. Selection changes do not reset it.

The sheet owns a headless scroll/handoff model and can render caller content through a clipped
callback. It does not require a ScrollablePanel to process the same drag again. Shared panel geometry
or scroll math may be extracted only where it is actually reused. Existing ScrollablePanel callers
retain their current behavior. The sheet reserves its visible region before lower map input updates,
keeps its admitted gesture through release, and never hands residual motion to the map.

## Verification and delivery

| Layer | Required evidence |
|---|---|
| Native normalization | Magnify composition, both axes, event modifiers/anchors, phase-only events, duplicate avoidance, other-window rejection, focus and dispose paths |
| Snapshot plumbing | Immutability, default identity, frame reset, copies, scaling, automation merge, WithoutScroll and manager suppression |
| Controller | Blocked-start latch, no lost drag travel, cursor/centroid anchoring, stable IDs, tap arbitration, interruption, native momentum without double inertia, cancellation |
| Time behavior | Equivalent final animation/inertia results for representative time partitions within float tolerance, finite output under degenerate input |
| Canvas | Existing overload compatibility, new snapshot path, clamp and snap stops, scaled/offset viewport, gesture tap filtering |
| Sheet | Detent resolution, safe area, exact residual transfer in both directions, handle/content ownership, fling/snap interruption, resize, blocking |
| Consumer contract | Fake geographic target with date-line wrapping and a bounded target, driven through the actual public controller and InputManager path |

Headless tests cannot prove delivery of a physical AppKit event or judge trackpad feel. A later owner
run must verify pinch, precise scroll, native momentum, focus/resize cancellation and sheet feel on
macOS, plus Ctrl+wheel compatibility on Windows. This session must not launch an app. The final
implementation handoff will supply an exact engine harness command, and Flightline's adoption thread
will validate the shipped client after its own release adoption.

All builds and tests run through build-slot. Run focused Release tests during implementation, then
one full Release build and suite at finish, excluding LiveSocket as prescribed by AGENTS.md. No
stress runs. Review actual native lifetime handling and consumer-visible behavior independently.
Run all repository guards and the full Markdown sweep for changed API names and old pinch behavior.

Deliver as one additive engine release line. At investigation, main and this branch are at
`10072b404`, version 20.34.0, with tag v20.34.0 present. If no other unreleased version is staged by
implementation, select 20.35.0. Recheck main and tags before editing the version. Documentation alone
does not bump it. Integrate and push validated implementation, then pack from main through the guarded
wrapper. No tag or release is authorized in this thread without a separate owner instruction.

Flightline must later adopt the released Windowing and Gui APIs, implement IPanZoomTarget for each
view, use the full snapshot path instead of a Touches-only UpdatePinch, update overlays before map
admission, configure the sheet, and cancel on view lifecycle changes. This thread edits no Flightline
files and performs no pin adoption. Engine #1328 stays open until its agreed engine work is complete.

## Platform references

- [Apple event monitors](https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/EventOverview/MonitoringEvents/MonitoringEvents.html)
- [Apple magnification](https://developer.apple.com/documentation/appkit/nsevent/magnification)
- [Apple precise scrolling](https://developer.apple.com/documentation/appkit/nsevent/hasprecisescrollingdeltas)
- [GLFW 3.4 Cocoa source](https://github.com/glfw/glfw/blob/3.4/src/cocoa_window.m)
- [GLFW 3.4 Windows source](https://github.com/glfw/glfw/blob/3.4/src/win32_window.c)
- [Microsoft precision touchpad input](https://learn.microsoft.com/en-us/windows/win32/input-precisiontouchpad/precision-touchpad-portal)
