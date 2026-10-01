# Round 1 C: Pointer Capture and Camera Gestures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Games can mouse-look with a captured cursor, and the follow camera can tell a tap from a drag per button and report when the body should turn to face the camera, with today's behaviour unchanged by default.

**Architecture:**
- **Capture:** `InputState` learns whether the pointer is captured, and `InputAccumulator` reports captured deltas in window points with zero deltas on the capture edges. A new `AppWindow` partial drives GLFW's cursor mode through a pure, tested policy. `GameApp` forwards the request the way it forwards rumble.
- **Gestures:** Ruinborne's right mouse gesture becomes the engine's per-button `PointerGesture`. `FollowCameraController` gains two optional gestures and plain outputs.

**Tech Stack:** C#/.NET 10, Silk.NET 2.23 with GLFW 3.4, xUnit, riding 20.17.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-1-DESIGN-2026-10-01.md`, section 3. Branch 3 of 4. It is independent of plans A and B and can start once plan A has opened 20.17.0 on `main`.

## Outcome

Executed 2026-10-02 on `feature/round1-pointer-capture`, commits `5153fe73d` to the merge, riding 20.17.0. Full suite at `0eaa33a49`: 22899 passed, 0 failed. Later commits are docs and the final fix wave, with the focused input, gesture and camera tests rerun. The manual cursor check in the CHANGELOG is owed by the owner before the tag.

| Ruling | What was decided | Why |
| --- | --- | --- |
| C1 | The `AppWindow.cs` cap is a line count. `BuildInput` is edited in place and new members live in `AppWindow.PointerCapture.cs` | The file sits at the cap |
| C2 | `GameApp.SetPointerCaptured` stays protected beside `Rumble`. The Showcase forwards the active room's `WantsPointerCapture` from `ShowcaseApp` | Scenes reach neither seam today, and no consumer has asked for a scene-level one |
| C3 | Automation's `Compose` forwards `PointerCaptured` | Automation must not report a captured pointer as released |
| C4 | Focus is sampled once per frame | Nobody can alt-tab out and back within one poll, and GLFW re-disables on refocus |
| C5, C5a | The camera orbits by at most one delta a frame, and each mouse movement is applied once. A crossing replay counts only if the camera has not orbited since that press | Both gestures read the same delta, so summing or replaying double-counts the both-buttons run |
| C6 | The zero-threshold resume behaviour stays as lifted from Ruinborne, and the docs qualify the inert rule | The lift changes only the four sanctioned points |
| C7, C7a | While captured, `MousePosition` holds the capture-start point. `OrbitTap` and `LookTap` count only when the camera did not turn during that press | A hidden cursor must not select, hover or block, and a both-buttons release is never a select |
| C8 | Camera gestures are gated by `UiBlocked = gui.HoverCaptured` | `GuiSurface.PointerCaptured` tracks the left button only |
| C9 | Gesture thresholds are documented as framebuffer pixels before capture and window points after | A scale-aware gesture needs new API (KhaozEngine #1228) |
| C10 | The capture-start zero delta stays as a safety net, with its reason corrected | GLFW does not jump on capture start |
| C11 | The manual check covers macOS, Windows, Linux X11 and Linux Wayland, with left, right and both-buttons drags, alt-tab, refocus and Retina sensitivity | Cursor modes differ across those platforms |

Deferred:
- PascalCase test names against the file's underscore style. The plan mandated them.
- An `in` parameter on a class. The plan mandated it.
- No test pins the C5a undercount while a gesture is pending.
- The Showcase room does not use `TurnBodyActive` or taps.

Follow-ups:
- KhaozEngine #1227 (the USING contents anchors)
- KhaozEngine #1228 (scale-aware gesture thresholds)

Note for Grimhollow at P3:
- Forward `WantsPointerCapture` from the app.
- Set `UiBlocked` from `HoverCaptured`.
- Read taps through `OrbitTap` and `LookTap`.
- Hand `TurnBodyActive` to `MoveCommand.FaceCamera` with `Camera.Yaw` as `CameraYaw`.

## Global Constraints

- Work in `/Users/antonio/KhaozEngine/.worktrees/round1-c` on `feature/round1-pointer-capture`, from current `origin/main`. Read `AGENTS.md` and `docs/CONTRIBUTOR-RULES.md` first.
- `AppWindow` stays the only type that calls GLFW or Silk input. All other code reads `InputState`. New code goes in a new partial `AppWindow.PointerCapture.cs`. `AppWindow.cs` is 787 lines against the 800 cap, so it changes by one line at most.
- Use `glfw.SetInputMode(handle, CursorStateAttribute.Cursor, CursorModeValue.CursorDisabled)` directly. Never Silk's `CursorMode` setter, which writes raw motion even when unsupported and raises a GLFW error on macOS. Set raw motion only when `glfw.RawMouseMotionSupported()` is true.
- With no gesture set, `FollowCameraController` behaves exactly as today: `OrbitButton` orbits from the first frame of the press. Existing tests stay unchanged and green.
- `InputState` gains its value through an optional trailing constructor parameter. Engine sites that copy an `InputState` forward it: `Input.cs:117-120` `WithoutScroll`, `InputAccumulator.cs:147` and `KhaozEngine.Automation/AutomationInputInjector.cs:126-143`.
- No environment variables in engine code. The gesture threshold is a constructor argument, default 4 pixels.
- Ride the staged 20.17.0. Extend its changelog, do not bump.
- One building agent at a time. Focused tests per task, the full suite once in Task 5. Never loop tests. Workers never push, pack or tag.
- No em or en dashes, no prose semicolons.

## Review Focus

1. **The frame capture ends.** GLFW restores the cursor, and the position jumps by the virtual drift. That frame must report a zero delta, and so must the frame capture starts. Owned by Task 1 `InputAccumulatorTests.CaptureEdgesReportAZeroDelta`.
2. **Focus loss while captured.** Capture must drop on the same edge that releases held buttons, or the cursor stays hidden and stuck. Owned by Task 2 `PointerCapturePolicyTests.FocusLossDropsCaptureAndRefocusDoesNotRestoreIt`.
3. **A 2x display.** Captured deltas are window points, so the same hand movement turns the camera the same amount at 1x and 2x. Owned by Task 1 `InputAccumulatorTests.CapturedDeltasAreWindowPointsNotFramebufferPixels`.
4. **A press that starts over UI, or focus lost mid-press.** Neither is a tap nor a drag. Owned by Task 3's lifted `APressThatBeganUnderTheUi_IsNeitherATapNorADrag`, and by Task 4 `FollowCameraGestureTests.LosingFocusDuringAHoldIsNotATap`.
5. **Ruinborne and Grimhollow unchanged.** A controller with no gestures, middle-button orbit or right-button orbit, behaves byte for byte as before. Owned by Task 4 `FollowCameraGestureTests.NoGesturesKeepsTodaysOrbitExactly`, plus the unchanged existing `FollowCameraControllerTests`.

---

### Task 1: `InputState.PointerCaptured` and captured deltas

**Files:**
- Modify: `KhaozEngine.Windowing/Input.cs` (constructor around 89-96, `WithoutScroll` around 117-120)
- Modify: `KhaozEngine.Windowing/InputAccumulator.cs` (`Snapshot` around 139-168)
- Modify: `KhaozEngine.Automation/AutomationInputInjector.cs` (`Compose` around 126-143)
- Test: `KhaozEngine.Render.Tests/Windowing/InputAccumulatorTests.cs`

**Interfaces:**
- `InputState` gains `public bool PointerCaptured { get; }`, set from a new optional trailing constructor parameter `bool pointerCaptured = false`.
- `InputAccumulator.Snapshot` gains two optional trailing parameters, `bool pointerCaptured = false` and `Vector2 framebufferScale = default`, where default means 1.
  - While captured, the delta is `(position - last) / framebufferScale`.
  - On the frame `pointerCaptured` changes, in either direction, the delta is `Vector2.Zero`.
  - The position stays in framebuffer pixels.

- [ ] **Step 1: Write the failing tests.**
  - `CaptureEdgesReportAZeroDelta`
  - `CapturedDeltasAreWindowPointsNotFramebufferPixels`
  - `UncapturedDeltasAreUnchanged`
  - `WithoutScrollKeepsPointerCaptured`
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~InputAccumulatorTests"`. Expected: FAIL.
- [ ] **Step 3: Implement**, and forward the value at the three copy sites.
- [ ] **Step 4: Run.** Expected: PASS, non-zero.
- [ ] **Step 5: Commit.** Message: `feat(windowing): input says when the pointer is captured`.

### Task 2: `AppWindow.SetPointerCaptured` through a pure policy

**Files:**
- Create: `KhaozEngine.Windowing/PointerCapturePolicy.cs` (internal, pure)
- Create: `KhaozEngine.Windowing/AppWindow.PointerCapture.cs`
- Modify: `KhaozEngine.Windowing/AppWindow.cs` (at most one line, for example wiring the per-frame apply)
- Modify: `KhaozEngine.Game/GameApp.cs` (a forwarding member beside `Rumble` around 302)
- Test: `KhaozEngine.Render.Tests/Windowing/PointerCapturePolicyTests.cs`

**Interfaces:**
- `internal static class PointerCapturePolicy` has `PointerCaptureAction Decide(bool requested, bool focused, bool currentlyCaptured, bool rawMotionSupported)`.
- `internal readonly record struct PointerCaptureAction(bool SetDisabled, bool SetNormal, bool SetRawMotion)`.
  - Capture only while requested and focused.
  - Drop on focus loss.
  - Never re-capture on refocus until the request is renewed (a false-then-true edge).
- `public void AppWindow.SetPointerCaptured(bool captured)` records the request. Each frame, before `BuildInput`, the partial applies the decided action with the `AppWindow.GlfwKeyboard.cs` native-handle pattern, and passes `pointerCaptured` and the framebuffer scale to `Snapshot`.
- `public bool AppWindow.PointerCaptured { get; }`.
- `protected void GameApp.SetPointerCaptured(bool captured) => Window.SetPointerCaptured(captured);` or the existing forwarding style.

- [ ] **Step 1: Write the failing tests.**
  - `ARequestWhileFocusedCaptures`
  - `RawMotionIsSetOnlyWhenSupported`
  - `FocusLossDropsCaptureAndRefocusDoesNotRestoreIt`
  - `ReleasingTheRequestRestoresTheNormalCursor`
  - `NoChangeMeansNoGlfwCall`
- [ ] **Step 2: Run** the filter `FullyQualifiedName~PointerCapturePolicyTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the policy, the partial and the forwarding.
- [ ] **Step 4: Run.** Expected: PASS. Then `sh scripts/check-file-size.sh --tree` passes.
- [ ] **Step 5: Commit.** Message: `feat(windowing): capture the pointer for mouse-look`.

### Task 3: `PointerGesture`

**Files:**
- Create: `KhaozEngine.Windowing/PointerGesture.cs`
- Test: `KhaozEngine.Render.Tests/Windowing/PointerGestureTests.cs`

**Interfaces:**
- `public enum PointerGesturePhase { Idle, Pending, Dragging }`.
- `public sealed class PointerGesture(MouseButton button, float thresholdPixels = 4f)` with:
  - `MouseButton Button`, `float ThresholdPixels`, `PointerGesturePhase Phase`
  - `Vector2 DragDelta`, `bool TapThisFrame`, `Vector2 TapPosition`
  - `void Advance(in InputState input, bool uiBlocked)`
- It is Ruinborne's `RightMouseGesture.Advance` algorithm (`/Users/antonio/Ruinborne/Ruinborne.Client/Interaction/RightMouseGesture.cs:122-190`), unchanged, with these differences:
  - It reads its button's down state, the cursor position and `MouseDelta` from `input`.
  - It treats `!input.WindowFocused` as blocked.
  - A threshold of 0 or less means drag from the first frame and never tap.

- [ ] **Step 1: Write the failing tests.** Lift Ruinborne's facts from `/Users/antonio/Ruinborne/Ruinborne.Tests/Client/RightMouseGestureTests.cs`, keeping their names:
  - `PressAndReleaseWithoutMoving_TapsForExactlyOneFrame`
  - `Tap_ReportsThePressOriginNotTheRelease`
  - `DragPastTheThreshold_AllowsOrbitAndNeverTaps`
  - `Travel_AccumulatesAcrossFrames_RatherThanPerFrame`
  - `Travel_IsPathLengthNotNetDisplacement`
  - `CrossingTheThreshold_ReplaysTheTravelAccumulatedWhilePending`
  - `AStillCursorAfterTheThreshold_KeepsDragging`
  - `ZeroThreshold_OrbitsFromFrameOneAndNeverTaps`
  - `APressThatBeganUnderTheUi_IsNeitherATapNorADrag`
  - `AModalOpeningMidDrag_StopsTheOrbit`

  Drop the environment-variable tuning fact. Add `TheGestureWatchesOnlyItsOwnButton`.
- [ ] **Step 2: Run** the filter `FullyQualifiedName~PointerGestureTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: `feat(windowing): tell a tap from a drag per mouse button`.

### Task 4: `FollowCameraController` gestures and outputs

**Files:**
- Modify: `KhaozEngine.Render3D/Camera/FollowCameraController.cs`
- Modify: `KhaozEngine.Showcase/Room3D.cs`. This one room opts in to `OrbitGesture` (left) and `LookGesture` (right) and forwards `WantsPointerCapture` to `SetPointerCaptured`, so the manual check in Task 5 has something to run. The other rooms are unchanged.
- Test: `KhaozEngine.Render.Tests/Render3D/FollowCameraGestureTests.cs` (the existing `FollowCameraControllerTests.cs` stays untouched)

**Interfaces:**
- New public members:
  - `PointerGesture? OrbitGesture` and `PointerGesture? LookGesture`, as fields in the class's existing style
  - `bool TurnBodyActive { get; }`, true while `LookGesture` is dragging
  - `bool WantsPointerCapture { get; }`, true while either gesture is dragging
  - `bool UiBlocked`, a field the game sets each frame
- When either gesture is set, `Update`:
  - advances each set gesture
  - orbits by the dragging gesture's `DragDelta` with the existing speed and invert fields (both dragging means sum them)
  - ignores `OrbitButton`
  - keeps scroll zoom, target damping and boom
- When neither is set, `Update` runs today's body unchanged.
- Taps are read from each gesture's `TapThisFrame` and `TapPosition`.

- [ ] **Step 1: Write the failing tests.**
  - `NoGesturesKeepsTodaysOrbitExactly`
  - `AnOrbitDragPastTheThresholdOrbitsWithoutTurningTheBody`
  - `ALookDragOrbitsAndTurnsTheBody`
  - `WantsPointerCaptureFollowsAnyDrag`
  - `AQuickClickIsATapAndDoesNotOrbit`
  - `LosingFocusDuringAHoldIsNotATap`
  - `UiBlockedPressesNeverOrbit`
  - `ScrollStillZoomsWithGesturesSet`
- [ ] **Step 2: Run** the filter `FullyQualifiedName~FollowCamera`. Expected: the new tests FAIL and the old ones pass.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS for all.
- [ ] **Step 5: Commit.** Message: `feat(render3d): follow camera gestures for orbit, look and tap`.

### Task 5: Docs, changelog and full verification

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md`:
  - `## Input` around 762: the `InputState` block and captured deltas
  - `### Gamepad rumble` around 956: the capture forwarding beside it
  - `## Third-person follow camera` around 6504: the gestures, outputs and the `MoveCommand.FaceCamera` hand-off with `Camera.Yaw` as `CameraYaw`
- Modify: `KhaozEngine.Windowing/README.md` (around 160, 176 and 250), `KhaozEngine.Render3D/README.md` (around 14 and 52), `KhaozEngine.Game/README.md` (near 105)
- Modify: `CHANGELOG.md` (extend 20.17.0). Name the manual check below as owed before release.

- [ ] **Step 1:** Write the docs and the changelog. Commit message: `docs(input): pointer capture and follow camera gestures`.
- [ ] **Step 2:** Full verification, as plan A Task 5 Step 2.
- [ ] **Step 3:** Hand the owner the manual check, which cannot be automated. On macOS, Windows and Linux, run the Showcase `Room3D` (wired in Task 4) and confirm four things:
  - the cursor hides and holds during a right-drag
  - no camera jump on release
  - capture drops on alt-tab
  - equal sensitivity on a Retina and a 1x display

  Record the results in the release notes.
