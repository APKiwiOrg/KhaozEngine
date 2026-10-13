# Pan, Zoom and Sheet Gestures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task by task. Steps use checkbox syntax for tracking.

**Goal:** Deliver engine #1328 with native macOS gestures, Windows modifier-wheel compatibility,
deterministic camera-independent pan/zoom, and a draggable bottom sheet.

**Architecture:** AppWindow owns native event delivery. An immutable Windowing payload feeds a pure
controller and shared drag-motion core. Gui supplies a Camera2D adapter and a sheet with its own
headless detent/scroll model. Existing consumer overloads retain their behavior.

**Tech Stack:** C#, .NET 10, existing Silk.NET 2.23.0, AppKit and Win32 interop, xUnit.

**Spec:** [Approved engine design](../../design/PAN-ZOOM-SHEET-GESTURES-DESIGN-2026-10-13.md).

## Global Constraints

- Worktree `/Users/antonio/.t3/worktrees/KhaozEngine/feature-trackpad-map-gestures`, branch
  `feature/trackpad-map-gestures`. Verify directory and branch before every write task.
- Native macOS magnify and precise pan plus Windows Ctrl+wheel compatibility. Full native Windows
  precision-touchpad pan is outside this release. The sheet remains in the same release.
- Raw input stays inside AppWindow. No new third-party dependency, GLFW fork or ScreenStack routing change.
- Do not change existing constructor signatures. Add overloads or a typed payload overload.
- All target coordinates use Pointer's coordinate space. Raw anchors are framebuffer pixels,
  precise deltas are window points, wheel deltas are wheel units, scale identity is 1.
- Preserve unrelated changes. Stage and commit explicit paths. No Flightline edits or pin adoption.
- All builds/tests/pack runs use build-slot. No stress runs or manual app launches.
- No tag or release without a separate owner instruction. Re-read current main/version/tags at Task 7.
- New behavior belongs in cohesive new types. Do not increase the size baseline or exemptions.
- CLI verification is synchronous. Redirect verbose logs to a task-local temporary path and inspect
  actual exit status plus failure/summary lines. Never pipe a gating dotnet command.

## Review Focus

1. Ctrl goes down, wheel arrives, Ctrl goes up before one frame. Zoom must still happen once (Task 2).
2. A gesture starts on a sheet that disappears before release. The map must remain inert (Tasks 3, 5).
3. Two windows coexist or a constructor fails after registering a monitor. No callbacks reach a dead
   window and another window's events remain untouched (Task 2).
4. Mouse wheel and native magnify share a frame at different anchors. Neither input is dropped,
   duplicated or applied at the final cursor position (Tasks 1, 3).
5. A suppressed held touch survives a resize or modal transition. It must not restart until all
   contacts lift, and sheet residual movement must not leak to the map (Tasks 3, 5).

## Delivery map

Task order is 1, 2, 3, 4, 5, 6, 7. Tasks 2 and 3 share payload contracts but need not write the same
files. Default to sequential execution to avoid concurrent edits to InputManager and AppWindow.
Each task gets red/green focused tests and its own explicit-path commit. Push validated commits to
the feature branch before moving to another task. Only the root orchestrator integrates or packages.

### Task 1: Immutable gesture payload and full snapshot plumbing

**Files:** Create `KhaozEngine.Windowing/PointerGestureInput.cs`,
`PointerGestureAccumulator.cs`, `InputState.Gestures.cs`, `PointerCoordinates.cs`.
Modify `Input.cs`, `InputState.PointerMotion.cs`, `InputAccumulator.cs`, `InputManager.cs`,
`Pointer.cs` and `KhaozEngine.Automation/AutomationInputInjector.cs` at their constructors/copy paths.
Tests in `KhaozEngine.Render.Tests/Windowing/PointerGestureInputTests.cs` and
`KhaozEngine.Automation.Tests/AutomationGestureInputTests.cs`.

**Interfaces:**

- `PointerGestureKind` values `Wheel`, `PreciseScroll`, `Magnify`.
- `PointerGestureInputPhase` values `None`, `Began`, `Changed`, `Ended`, `Cancelled`.
- `PointerGestureSource` values `UnknownWheel`, `MacOSNative`, `ModifierWheelCompatibility`, `Synthetic`.
- `PointerGestureSample` immutable value with `Kind`, `Source`, `Phase`, `PositionPixels`,
  `ScrollDelta`, `ScaleFactor`, `CommandDown`, `IsMomentum`. ScrollDelta is wheel units for Wheel
  and window points for PreciseScroll. Unused fields are zero, scale is 1.
- `PointerGestureFrame` owns copied samples and exposes `IReadOnlyList<PointerGestureSample> Samples`.
  Its constructor validates finite coordinates/deltas and positive finite factors. `Empty` is shared.
- `InputState.Gestures`, `HorizontalScrollDelta`, `HasPreciseScroll`, `PinchScaleFactor` and
  `WithoutPointerGestures()`. PinchScaleFactor summarizes only actual Magnify samples, not wheel policy.
- `PointerCoordinates` maps positions and window-point vectors through the current IDesignViewport.
  Pointer exposes this immutable mapping for the controller. Include scale/viewport identity for cancellation.
- `InputManager.PointerInputSuppressed` is read-only. Gesture convenience access honors suppression.

- [x] Add tests that freeze one frame, collect another, and assert the first frame remains equal.
  Pin summary behavior and retain the old constructor overload using reflection.

```csharp
Assert.Equal(1f, InputState.Empty.PinchScaleFactor);
Assert.Equal(1.32f, frame.PinchScaleFactor, 5); // factors 1.1 then 1.2
Assert.Equal(2, first.Gestures.Samples.Count); // unchanged after next collection
Assert.DoesNotContain(frame.WithoutScroll().Gestures.Samples, s => s.Kind != PointerGestureKind.Magnify);
Assert.Equal(frame.PinchScaleFactor, frame.WithoutScroll().PinchScaleFactor);
```

- [x] Run the new Render test filter through build-slot, expect failure from the missing contract.
- [x] Implement payload, point/vector mapping and optional snapshot construction without changing old
  signatures. Preserve every field through WithoutScroll and automation composition. Clear pending
  gestures at focus/capture transitions. Do not attach native sources yet.
- [x] Add mixed-source, nonuniform framebuffer scale, offset viewport, suppressed manager, immutable
  copy and automation tests. Assert no pixel/point double scaling and no empty-frame allocation.
- [x] Run `build-slot --label gesture-input -- dotnet test KhaozEngine.Render.Tests -c Release --filter
  'FullyQualifiedName~PointerGestureInputTests|FullyQualifiedName~InputState|FullyQualifiedName~InputAccumulator|FullyQualifiedName~InputManagerSuppression'`.
  Run `build-slot --label gesture-automation -- dotnet test KhaozEngine.Automation.Tests -c Release`.
  Both must exit zero. Commit explicit files and push.

### Task 2: Native macOS delivery and Windows compatibility

**Files:** Create `KhaozEngine.Windowing/AppWindow.MacOSGestures.cs`,
`AppWindow.WindowsGestures.cs`, `NativeGestureNormalization.cs`.
Modify AppWindow construction, WireInput, BuildInput and Dispose only at the wiring points.
Create `KhaozEngine.Windowing.Tests/KhaozEngine.Windowing.Tests.csproj`, referencing only Windowing
and the repository's existing test packages, with IsPackable false and namespaces under
`KhaozEngine.Tests.Windowing`. Add the project to KhaozEngine.slnx and grant its internal access in
Windowing.csproj. Tests are `KhaozEngine.Windowing.Tests/NativeGestureNormalizationTests.cs` and
`NativeGestureLifetimeTests.cs`. Keep existing Render.Tests regression coverage in place.

**Interfaces:** Consume Task 1 samples/accumulator. AppWindow platform wiring owns install/remove and
per-event forwarding. `NativeGestureNormalization` is pure conversion of plain values, with no native
calls. A narrowly scoped internal registration abstraction permits a fake lifecycle test without
constructing AppWindow or loading a native library. Raw AppKit/Win32 calls remain inside AppWindow.
Extend PointerGestureFrame with `IsAvailable`. Its public Empty and constructed frames represent
an authoritative stream, even with no samples. An internal unavailable empty singleton is the
default for old InputState constructors. Existing snapshot copies preserve that frame identity.
Task 3 uses legacy wheel fallback only when IsAvailable is false. Add tests that cleared native
samples cannot revive retained legacy scroll, while copied old synthetic frames retain fallback.
Add immutable `NativeGestureFailure` with stage Install/Remove/Release/Callback, message and optional
original exception. Registration retains its first Failure and AppWindow exposes NativeGestureFailure.
False install/removal must be observable. No attempted backend means normal fallback, not a failure.

- [x] Add failing normalizer tests for precise scroll units, Cocoa Y inversion, magnify factors,
  zero-delta phases, event-time modifiers, native momentum and rejected non-finite events.
  Pin Ctrl+wheel as one compatibility sample even when final KeysDown is empty.

```csharp
Assert.True(sample.CommandDown);
Assert.Empty(snapshot.KeysDown);
Assert.Single(snapshot.Gestures.Samples);
Assert.Equal(PointerGestureSource.ModifierWheelCompatibility, sample.Source);
Assert.Equal(new Vector2(24, 30), precise.ScrollDelta); // no GLFW 0.1 conversion
```

- [x] Run `build-slot --label gesture-native-red -- dotnet test KhaozEngine.Windowing.Tests -c Release
  --filter 'FullyQualifiedName~NativeGesture'`, expect failure on the missing delivery contracts.
- [x] Implement a window-scoped AppKit local monitor with rooted block storage/delegates, exact
  NSWindow filtering, original-event return and disposal before input/window teardown. Capture the
  matching event metadata while GLFW dispatches, preserving legacy ScrollDelta without a duplicate
  new gesture. Never replace process-wide Objective-C methods. Add both wheel axes on fallback paths.
- [x] Implement a chained Windows procedure using message-time MK_CONTROL for wheel compatibility.
  Restore the procedure safely at teardown. Emit one new sample and retain legacy wheel delivery.
  Other platforms produce unclassified wheel samples, never inferred precision.
- [x] Test install failure, partial initialization, disposal twice, callback after disposal, another
  window's event and begin/end with zero delta through the actual registration owner and pure decoder.
  Assert first-failure diagnostics for false install/removal and thrown install/remove/release/callback
  errors, while proving roots and storage retain their safe lifetime. Assertions belong outside
  exception-contained callbacks so a failing test cannot be swallowed as a native callback exception.
  Record separately that these tests do not prove physical OS delivery.
- [x] Repeat the focused command green. Inspect native ABI and lifetime against Apple/Windows docs
  and existing interop patterns. Commit explicit files and push.

### Task 3: Deterministic pan/zoom controller and shared drag motion

**Files:** Create `KhaozEngine.Windowing/IPanZoomTarget.cs`, `PanZoomController.cs`,
`PanZoomOptions.cs`, `DragMotionTracker.cs`, `WheelZoomAnimation.cs`, `TouchPanZoomGesture.cs`.
Modify `InputAccumulator.cs` and `NativeGestureRegistration.cs` for explicit stream capability.
Extend existing recognizers only for additive reset/cancel support where reused.
Tests in `KhaozEngine.Windowing.Tests/PanZoomControllerTests.cs`,
`PanZoomTouchTests.cs`, `PanZoomLifecycleTests.cs`, `DragMotionTrackerTests.cs`.

**Interfaces:**

- `IPanZoomTarget.PanByScreenDelta(Vector2 delta)` and `ZoomByFactor(float factor, Vector2 anchor)`.
- `PanZoomController.Update(InputManager input, IPanZoomTarget target, Rect viewport, float dtSeconds,
  bool hasKeyboardFocus = false, Func<Vector2, bool>? isBlocked = null)`.
- Explicit overload takes `Pointer pointer, InputState input` in place of InputManager and a
  `bool inputSuppressed` argument. Both enter the same core and use Pointer's coordinate mapping.
- `Cancel()` clears animation/history and inhibits the current contact sequence until released.
- Read-only per-frame `Tapped`, `TapPosition`, `DoubleTapped`, `DoubleTapPosition` results.
- Options defaults: drag slop 8 pointer units, tap maximum 0.3 seconds, double-tap interval 0.3 seconds,
  double-tap radius 24 units, wheel factor 1.1 per unit, wheel duration 0.12 seconds,
  inertia decay 8 per second, stationary-release cutoff 0.08 seconds. Keyboard disabled by default,
  with 400 units/second pan and factor 2 per second zoom when enabled.
- Public DragMotionTracker exposes accepted drag delta, release velocity, tap eligibility and reset.
  Sheet and controller consume its outcomes, with no public camera dependency.
- `InputAccumulator.EnablePointerGestures()` declares an authoritative source for its lifetime.
  NativeGestureRegistration calls it on construction, including managed wheel fallback. Calling
  OnPointerGesture also declares the source before focus/capture filtering. A legacy accumulator
  using only OnScroll keeps an unavailable stream so the controller can use its scalar fallback.
- Wheel options: `WheelMode` (`PanZoomWheelMode.Pan`/`Zoom`, default Zoom), `WheelPanSpeed`
  (40 target screen units per wheel unit), nullable `Func<float, float, float> WheelZoomFactorResolver`
  (wheel delta and proposed factor to resolved factor), and `PreciseScrollZoomLogFactorPerPoint`
  (0.01f). Resolve custom wheel factors once when their queued operation becomes active.
  Do not coalesce custom-resolver samples. Reject non-finite/non-positive resolved factors.
  Reset pending wheel animation when mode/resolver policy changes.

- [x] Add failing single-pointer tests: from origin (20,20), move to (26,20), then (30,20), and assert
  zero followed by (10,0). Release after 0.08 seconds stationary has zero inertia. New press cancels
  existing inertia before movement. Blocked start stays inert after blocker removal.
  Preserve Pointer's existing same-frame press/release tap contract. A snapshot containing both
  button edges with no held button must still tap once when admitted, and never tap when blocked.
  Prove legacy OnScroll + Snapshot input zooms through fallback, while an enabled native source
  whose samples were cleared never revives retained scalar scroll.
  Clear the synthesized-mouse latch when release is observed, including terminal-touch and focused
  suppressed frames. Do not mistake an unfocused synthetic clear for physical release. Previously
  unobserved touch IDs require Began, so an already-held contact cannot acquire a newly visible view.
- [x] Run `build-slot --label pan-zoom-red -- dotnet test KhaozEngine.Windowing.Tests -c Release
  --filter 'FullyQualifiedName~PanZoom|FullyQualifiedName~DragMotionTracker'` and inspect failure.
- [x] Implement drag/admission/cancellation and analytic inertia. With velocity v and decay k,
  displacement over dt is `v * (1 - exp(-k*dt)) / k`, then velocity is `v * exp(-k*dt)`.
  Reject invalid options/time. Zero dt applies input without advancing velocity estimates or animation.
- [x] Implement event-ordered scroll/magnify and wheel animation. Animate log factor over the exact
  configured duration. Coalesce only equal-anchor compatible steps. Native momentum gets no extra
  release inertia. Event modifiers decide behavior even if frame-level modifiers differ.
  Opposite directions are not compatible for coalescing, because target clamps preserve their order.
  Partial-animation bookkeeping uses only the remaining factor, never the already-applied prefix.
  A resolved identity factor consumes no animation time, so saturated snap requests cannot delay
  a later reversal. The resolver still runs exactly once for each sample.
  Zero-duration operations drain in order even at zero dt or at an exact timed-operation boundary.
  Coalescing must leave the resulting factor finite and positive. Otherwise preserve the valid
  operations separately, including when their combined factor would overflow or underflow.
  Use legacy scalar wheel input only when `input.Gestures.IsAvailable` is false, never merely when
  Samples is empty. Native cancellation can intentionally publish an empty authoritative stream.
  Command+Wheel overrides Pan mode. Command+PreciseScroll uses exp(rawPointY * pointSensitivity),
  independently of DPI, viewport scale and notch factor. Before a subsequent direct pan/magnify
  operation, complete pending wheel remainders in order, including at zero dt. This is input-driven
  completion, not idle animation advancement. Cancellation instead discards remaining wheel effects.
- [x] Implement touch arbitration with stable IDs. More than two contacts cancels until all release.
  Rebase coincident pairs and contact replacement. A second touch arriving within 0.1 seconds may
  qualify for a two-finger tap, subject to the 0.3-second duration and slop for both contacts.
  First tap reports immediately. Second qualifying tap reports double-tap and zooms by factor 2.
  Two-finger tap zooms by factor 0.5. Do not duplicate a touch as a mouse gesture.
  An active pinch returning to one finger resumes dragging without a jump, a new slop delay or a tap.
  A pinch candidate that has not committed remains distinguishable for two-finger tap recognition.
- [x] Add numeric oracle tests using an affine fake target. Its pan and scale must retain the point
  under cursor/centroid. Add one-frame mixed-anchor wheel/magnify, touch reordering/replacement,
  suppression, resize, focus loss, capture, held-contact restart inhibition and focused keyboard tests.
  Compare inertia at 0.1 versus 0.04+0.06 seconds within 1e-4. Compare same-anchor animation final
  factors across representative time partitions. Zero/invalid input must never emit NaN.
  Assert Wheel(A), Magnify(B), Wheel(C) against an ordered affine oracle, including partially animated
  A. Test the wheel resolver receives each wheel sample once and never native/touch pinch. Two
  samples select two stops while one magnitude-100 sample selects one. Precise command zoom must
  have equal factors across DPI/design scales and remain continuous between snap stops.
- [x] Run the focused command green, then existing GestureRecognizer/PointerGesture/PinchRecognizer
  filters once if those types changed. Commit explicit files and push.

### Task 4: PannableCanvas adoption with compatibility tests

**Files:** Modify `KhaozEngine.Gui/PannableCanvas.cs`. Create `PannableCanvasGestures.cs` containing
the cohesive Camera2D target adapter and frame-aware gesture owner. Modify
`KhaozEngine.Gui.Tests/Gui/PannableCanvasTests.cs`, add `PannableCanvasGestureTests.cs`.

**Interfaces:** `PannableCanvas.Update(InputManager input, float dtSeconds, bool hasKeyboardFocus = false,
Func<Vector2, bool>? isBlocked = null)`, `CancelGestures()`, and read-only GestureController/GestureOptions access.
Keep `Update(Pointer, float)` unchanged. A new `TryGetGestureTap(out Vector2 world)` returns accepted
controller taps. Legacy TryGetTap remains unchanged and is documented as legacy release hit testing.

- [x] Add tests retaining all existing wheel Pan/Zoom and snap-stop assertions. New overload receives
  native magnify, mapped precise scroll and touch pinch. Assert a blocked start never moves the camera.
- [x] Run `build-slot --label canvas-red -- dotnet test KhaozEngine.Gui.Tests -c Release
  --filter 'FullyQualifiedName~PannableCanvas'`, expect new-test failure.
- [x] Implement the adapter, admitting before self-reservation. Retain clamps, EnablePan semantics
  and legacy defaults. Frame-aware mode uses the configured wheel policy and zoom stops, while
  precise pan and native pinch remain distinct sources. Avoid applying raw wheel after the controller.
  Map CanvasWheelMode and ScrollPanSpeed to wheel options. For snap stops, supply the resolver using
  the existing adjacent-stop calculation, clamp the destination, then divide by current camera zoom.
  Native magnify and command+precise zoom bypass snapping. Keep the legacy overload immediate.
- [x] Add offset/scaled viewport, clamp-at-edge, drag-release-not-tap and lifecycle tests. Run the same
  filter green. Commit explicit files and push.

### Task 5: Draggable sheet, detents and exact content handoff

**Files:** Create `KhaozEngine.Gui/DraggableBottomSheet.cs`, `BottomSheetMotion.cs`,
`BottomSheetDetent.cs`, `BottomSheetOptions.cs`. Tests in
`KhaozEngine.Gui.Tests/Gui/BottomSheetMotionTests.cs` and `DraggableBottomSheetTests.cs`.

**Interfaces:**

- `BottomSheetDetent` has stable `Id` and a height resolver receiving available height and content
  height. Provide fixed, fractional and content-capped factories. `Dismissible` is explicit.
- `BottomSheetMotion` owns detent resolution, current/target heights, content offset and shared drag
  motion. It accepts scalar content movement and returns consumed/residual values for tests.
- `DraggableBottomSheet.Update(InputManager input, Rect availableBounds, SafeAreaInsets safeArea,
  float contentHeight, float dtSeconds)` and `CancelGestures()`.
- Expose `Bounds`, `HandleBounds`, `ContentBounds`, `CurrentDetentId`, `TargetDetentId`,
  `ScrollOffset`, `DetentChanged`, `Dismissed`, and `SetDetent(string id, bool animated)`.
- `Draw(SpriteBatch batch, Action<Rect, float> drawContent)` clips caller content. Caller supplies
  theme/localized labels. No raw player text. Handle height defaults to 32 pointer units.

- [ ] Add failing pure tests for fixed/fractional/content detents, exhausted safe areas and resize
  retaining detent identity. Reject duplicate IDs, non-finite/negative resolved heights and no detents.
  Clamp valid resolved heights to available bounds and resolve equal-height ties by declaration order.
- [ ] Run `build-slot --label sheet-red -- dotnet test KhaozEngine.Gui.Tests -c Release
  --filter 'FullyQualifiedName~BottomSheet'`, expect new contract failure.
- [ ] Implement the pure sheet model. Content upward movement expands first, then scrolls. Downward
  movement unwinds scroll first, then collapses. Handle input moves only the sheet. At release,
  select the nearest detent to `height - velocityY / 8`, clamped to the valid range. Animate to that
  detent over 0.18 seconds with cubic ease-out. New press stops the snap before movement.

```csharp
Assert.Equal(600f, result.Height);       // starts at 580, expands by 20
Assert.Equal(30f, result.ScrollOffset); // remaining 30 of an upward 50
Assert.Equal(570f, reverse.Height);     // starts expanded, downward 50
Assert.Equal(0f, reverse.ScrollOffset);// first unwinds content offset 20
```

- [ ] Add pointer/touch tests for handle ownership, two-finger cancellation, stationary-release
  velocity, wheel content scrolling only when expanded, handle tap toggle, configured dismissal,
  blocker removal, no map leak and resize/suppression cancellation. Native precise scroll transfers
  movement within sheet/content. Discrete wheel only scrolls expanded content, never flings a sheet.
- [ ] Implement the Gui wrapper using the shared tracker and region reservation, with rendering
  built from existing Gui primitives. Do not feed the same movement to ScrollablePanel.Update.
- [ ] Run the filter green, then existing ScrollablePanel tests once if shared math was extracted.
  Commit explicit files and push.

### Task 6: Consumer-path proofs, docs and owner-run harness

**Files:** Add `KhaozEngine.Gui.Tests/Gui/PanZoomConsumerContractTests.cs`.
Add an opt-in `tools/GestureInputProbe/` desktop head with its csproj/Program.cs/README.md,
referencing only the engine packages it uses. Add it to `KhaozEngine.slnx`.
Update `docs/USING-KHAOZENGINE.md`, Windowing/Gui READMEs and all Markdown matches for affected APIs.

**Interfaces:** The probe displays current event source/phase, wheel/precise/magnify values and a
grid plus draggable sheet. It logs developer diagnostics and uses no game assets or Flightline code.
Headless tests use the same controller and target adapter contract as the probe.

- [ ] Add failing consumer tests for a wrapping geographic target and a bounded camera target,
  driven through InputManager, overlays, then controller. Assert cursor-fixed date-line wrap and
  sheet-start cancellation through actual public APIs. These targets remain fixtures, not engine
  geographic camera APIs.
- [ ] Run `build-slot --label gesture-consumer -- dotnet test KhaozEngine.Gui.Tests -c Release
  --filter 'FullyQualifiedName~PanZoomConsumerContract'`, confirm red then implement fixture wiring
  and correct integration defects. Run green.
- [ ] Build the opt-in probe through build-slot, but do not launch it. Provide the exact absolute
  dotnet run command in the eventual owner handoff. Clearly distinguish headless evidence from
  physical macOS event delivery and Windows compatibility awaiting owner validation.
- [ ] Publish API examples covering the generic bounded and wrapping targets, full snapshot update, overlay ordering,
  explicit cancellation, sheet configuration, new versus legacy canvas APIs, units and platform limits.
  The Flightline handoff uses a Client-side IPanZoomTarget adapter. Its existing viewport Zoom method
  is centre-based, so the adapter must implement anchored zoom with the game's geographic constraints.
  Do not imply that forwarding the existing Zoom method provides anchor preservation.
  Run `rg` across Markdown for `PannableCanvas|PinchRecognizer|Touches|ScrollDelta|pinch|trackpad` and
  update every affected living reference. Preserve historical design statements as history.
- [ ] Run staged guards. Commit explicit files and push. Do not post a consumer completion claim.

### Task 7: Independent review, version reconciliation and engine finish

**Files:** Directory.Build.props, CHANGELOG.md and all guarded version declarations as needed.
Design/plan status and consumer handoff documentation. No Flightline paths.

- [ ] Review the entire branch against the approved spec with one fresh reviewer. Verify every
  reported finding against reachable code before acting. Native callback lifetime, duplicate input,
  blocked-start behavior and test oracle independence receive explicit attention. Fix confirmed
  findings, rerun focused tests and obtain a targeted re-review.
- [ ] Fetch and inspect current main, origin/main, tags and worktrees. Ride an existing unreleased
  version. Otherwise select the next additive minor, expected 20.35.0 from the observed 20.34.0 tag.
  Update version and changelog together plus all guarded declarations. Never create a tag.
- [ ] Merge current main into the feature branch and resolve conflicts here. Re-run focused tests
  affected by reconciliation. Do not claim validation from an earlier branch state.
- [ ] Create local-feed before restore. Run `build-slot --label gestures-release-build -- dotnet build
  KhaozEngine.slnx -c Release`, then `build-slot --heavy --label gestures-release-suite -- dotnet test
  KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'`. Both must exit zero.
  Full suite runs once at finish unless new failures/changes require another verification.
- [ ] Run whole-tree dash, prose, file-size, agent-instruction and doc-version guards. Check the
  explicit diff and commit the validated finish. Push the branch before integration.
- [ ] The root orchestrator fast-forwards clean main, pushes main, verifies origin/main containment,
  then runs `build-slot --heavy --label gestures-pack -- scripts/pack-local-feed.sh` from main.
  Verify the guarded feed. No bare pack, no tag, no release.
- [ ] Close engine #1328 only when its agreed engine scope is complete. Provide the resolving sha,
  exact APIs and required release version to the owner. Preserve the cross-repo consumer issue URL.
  Remove the worktree/feature branch only after both ancestry and unique-commit checks prove safety.
  Owner validation starts from the merged main probe command. If it fails, keep the release untagged
  and fix the observed defect. If it passes, a separate explicit release approval is still required.
