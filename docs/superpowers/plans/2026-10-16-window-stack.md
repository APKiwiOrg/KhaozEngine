# Window Stack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `KhaozEngine.Gui.WindowStack` and opt-in `Pointer` occlusion so overlapping non-modal windows have a z-order, raise on open and press, and only the uncovered area of each window answers the pointer.

**Architecture:** `Pointer` gains an occluder list that every hit query consults for the point it tests. `WindowStack` owns window order in three bands, updates windows top to bottom while accumulating occluders and draws bottom to top while re-applying the occluders above each window. Engine widgets that read raw pointer geometry are brought under the same gate.

**Tech Stack:** C# / .NET 10, xUnit, `KhaozEngine.Windowing`, `KhaozEngine.Gui`, `KhaozEngine.WorldMap.Gui`.

**Spec:** `docs/design/WINDOW-STACK-DESIGN-2026-10-16.md`. Read it before any task. It is the contract and this plan only sequences it.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/window-stack`, branch `feature/window-stack`. Never commit on `main`, never push, never tag, never pack. The orchestrator integrates.
- Every local build or test runs through `build-slot --label <what> -- <command>` (add `--heavy` for the full solution). One focused test run at a time. No loops of test runs.
- `TreatWarningsAsErrors` is on. Zero warnings.
- Headless tests only, namespace `KhaozEngine.Tests.*`, using `KhaozEngine.Tests.MouseFrames` for button edges.
- With no occluders, every `Pointer` query must behave exactly as at v21.1.0. Existing tests must stay green unchanged.
- No em dashes, en dashes or prose semicolons in Markdown or comments.
- New behaviour goes in new types or a `partial` file for the same type (KESIZE). No file above 800 lines.
- Version: 21.2.0 (minor). Another thread (tree clearance, KhaozEngine #1422 area) may stage 21.2.0 on `main` first. If `main` already carries 21.2.0 when Task 5 runs, ride it: append to its CHANGELOG entry, do not bump.

## Review Focus

- A window that closes itself during its own `Update` (close square tapped) must add no occluder and must not draw this frame. Pinned in Task 2.
- A window opened by another window's update in the same frame (a tab opens a window) must be raised before `Draw`, so it never draws one frame behind. Pinned in Task 2 (`Draw` syncs open edges).
- Draw-time hover reads (`IsHoveringIn` inside `Draw`) must agree with update: the front window still lights its close square, a covered one does not. Pinned in Task 2.
- A same-frame right tap (press and release inside one frame) must still raise the window under it. Pinned in Task 1 (`IsRightPressOriginFresh` on a synthesised tap) and Task 2.
- A drag started in a back window's visible title must keep moving that window when the cursor crosses the front window. Pinned in Task 3.

---

### Task 1: Pointer occlusion

**Files:**
- Modify: `KhaozEngine.Windowing/Pointer.cs` (make the class `partial`, clear occluders in `Update`, latch the right fresh flag, make hit queries consult `IsOccluded`)
- Create: `KhaozEngine.Windowing/Pointer.Occlusion.cs`
- Test: `KhaozEngine.Render.Tests/Windowing/PointerOcclusionTests.cs`

**Interfaces:**
- Produces:
  - `public void Occlude(Rect region)`
  - `public void OccludeAll()`
  - `public bool IsOccluded(Vector2 point)`
  - `public void ClearOcclusion()`
  - `public bool IsRightPressOriginFresh { get; }`

- [ ] **Step 1: Write the failing tests** in `PointerOcclusionTests` (class in `KhaozEngine.Tests.Windowing`), with a `Frame(Vector2 at, IReadOnlySet<MouseButton> held)` helper built like `PointerTests.Frame` (960 by 540 `InputState`, `MouseFrames.Advance`). Rect `A = new(100, 100, 200, 200)`, cover `C = new(150, 150, 100, 100)`. Facts:
  - `NoOccluders_QueriesUnchanged`: tap at (160,160) gives `IsTapIn(A) == true`.
  - `TapIn_MissesWhenOriginOrReleaseOccluded`: press at (160,160), `Occlude(C)`, release at (160,160) gives `IsTapIn(A) == false`. Press at (120,120), release at (160,160) with `C` occluded gives false. Press and release at (120,120) with `C` occluded gives true.
  - `HoverAndPointerIn_MissInOccludedArea`: pointer at (160,160), `Occlude(C)`: `IsHoveringIn(A)` and `IsPointerIn(A)` false. At (120,120): both true.
  - `PressOriginQueries_MissWhenOriginOccluded`: press at (160,160) then hold, `Occlude(C)`: `IsDragStartIn(A)`, `IsDraggingIn(A)` false and `GetDragDelta(A) == Vector2.Zero`. Same with origin (120,120): true and non-zero delta after a move.
  - `RightQueries_MissWhenOccluded`: right press and release at (160,160) with `C` occluded: `IsRightTapIn(A)` false. Right held at (160,160): `IsRightPressingIn(A)` false.
  - `ReleasedOutside_TrueWhenReleaseOccluded`: press at (120,120), release at (160,160) with `C` occluded: `IsReleasedOutside(A) == true`.
  - `OccludeAll_MissesEverywhere`: `OccludeAll()` then `IsPointerIn(A)` false and `IsOccluded(new Vector2(-5000, 9000))` true.
  - `Occlusion_ClearsOnUpdate_AndOnClear`: `Occlude(C)`, next `Update` then `IsOccluded((160,160))` false. `Occlude(C)` then `ClearOcclusion()` gives false.
  - `BlockRegion_Unaffected`: `Occlude(C)` does not make `IsBlocked((160,160))` true, and `BlockRegion(C)` does not make `IsTapIn(A)` false.
  - `RightPressOriginFresh_MatchesLeft`: true on the right press frame, false on the next held frame, true on a same-frame right tap (build it as `PointerTests.TapFrame` does).

- [ ] **Step 2: Run to verify they fail**

Run: `build-slot --label pointer-occlusion -- dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~PointerOcclusionTests"`
Expected: compile failure (members missing).

- [ ] **Step 3: Implement.** `Pointer.Occlusion.cs` holds a `List<Rect> _occluders` and a `bool _occludeAll`. `IsOccluded` is `_occludeAll || any occluder contains point`. `Update` clears both next to `_blocked.Clear()`. `_rightPressOriginFresh` is set where `_rightPressOrigin` is latched (`IsRightJustPressed || rightTapped`) and cleared by `SuppressButtonsUntilRelease`. Each query adds `!IsOccluded(point)` for exactly the points in the spec table. `IsReleasedOutside` becomes `IsJustReleased && (!bounds.Contains(_pos) || IsOccluded(_pos))`. Keep the queries one-liners and update their XML docs to name occlusion.

- [ ] **Step 4: Run to verify they pass**, plus the existing pointer tests unchanged:

Run: `build-slot --label pointer-occlusion -- dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Windowing.Pointer"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Windowing/Pointer.cs KhaozEngine.Windowing/Pointer.Occlusion.cs KhaozEngine.Render.Tests/Windowing/PointerOcclusionTests.cs
git commit -m "feat(windowing): opt-in pointer occlusion for hit queries"
```

### Task 2: WindowStack

**Files:**
- Create: `KhaozEngine.Gui/WindowBand.cs`, `KhaozEngine.Gui/IStackWindow.cs`, `KhaozEngine.Gui/WindowUpdate.cs`, `KhaozEngine.Gui/WindowStack.cs`
- Test: `KhaozEngine.Gui.Tests/Gui/WindowStackTests.cs`, `KhaozEngine.Gui.Tests/Gui/FakeStackWindow.cs`

**Interfaces:**
- Consumes: Task 1 `Occlude`, `OccludeAll`, `IsOccluded`, `ClearOcclusion`, `IsRightPressOriginFresh`.
- Produces: exactly the spec's `WindowBand`, `IStackWindow` (with its default members), `WindowUpdate` (public constructor `WindowUpdate(Pointer pointer, InputState input, float dt, bool receivesInput, bool isFront)`) and `WindowStack` signatures. `Draw(SpriteBatch batch, Pointer pointer)`.

- [ ] **Step 1: Write the test double.** `FakeStackWindow : IStackWindow` with settable `IsOpen`, `Bounds`, `CapturesPointer`, `ClosesOnEscape`, a shared `List<string> Log` that records `"update:<name>"` and `"draw:<name>"`, and per-update captures of `update.IsFront`, `update.ReceivesInput`, `update.Pointer.IsHoveringIn(Bounds)`, `update.Pointer.IsTapIn(Bounds)` and `update.Pointer.IsHoveringIn(Probe)` for a settable `Probe` rect. `Draw` records `pointer.IsHoveringIn(Bounds)` as `DrawHover`. An optional `Action<WindowUpdate>? OnUpdate` lets a fact close the window or open another during update. `Close()` sets `IsOpen = false` and counts calls. Pass `null!` for the `SpriteBatch` in tests.

- [ ] **Step 2: Write the failing facts** in `WindowStackTests`. Back window `B = (100,100,400,300)`, front window `F = (200,150,400,300)`, both floating and open, `F` added after `B`, a `Frame(at, held)` helper as in `WindowDragTests` that calls `stack.Update(pointer, input, 1/60f)`.
  - `Add_PlacesAtFrontOfBand`: `Order` is `[B, F]`.
  - `Update_RunsTopToBottom_DrawRunsBottomToTop`: log is `update:F, update:B` then `draw:B, draw:F`.
  - `CoveredArea_IsDeadForBackWindow`: hover at (300,200) (inside both): `F` sees hover, `B` does not. Hover at (150,120) (only `B`): `B` sees hover.
  - `Press_RaisesFrontMostWindowUnderOrigin`: press at (150,120) raises `B`, `Order` becomes `[F, B]` on that frame and `B` updates first.
  - `Press_InOverlap_KeepsFrontWindow`: press at (300,200) keeps `[B, F]`.
  - `RightPress_Raises`, including a same-frame right tap.
  - `Open_RaisesToFront`: `B` closed, `F` open, then `B.IsOpen = true`: next `Update` gives `[F, B]`.
  - `Open_DuringHeldPress_WaitsForRelease`: press and hold on `F`'s title, open `B` mid-hold: `Order` stays with `F` on top until the release frame, then `B` rises.
  - `Open_ByAnotherWindowDuringUpdate_RaisedBeforeDraw`: `F.OnUpdate` opens a third window `T`: `draw:T` is last in the same frame.
  - `Bands_DockedNeverAboveFloating_TopmostAlwaysAbove`: docked `D` pressed stays below `F`, topmost `X` stays above after `B` is pressed.
  - `ClosedDuringUpdate_AddsNoOccluderAndDoesNotDraw`: `F.OnUpdate` closes `F` on a tap: `B` sees the hover at (300,200) the next frame, and `draw:F` is absent on the closing frame.
  - `Capture_UpdatesCaptorFirstWithNothingOccluded_OccludesRest`: `B.CapturesPointer = true`: `B` updates first and sees hover at (300,200), `F` sees no hover anywhere, and a press does not reorder.
  - `ReceivesInputFalse_OccludesEverything`: `Update(..., receivesInput: false)`: both update with `ReceivesInput == false` and no hover.
  - `IsFront_IsTopOpenWindowOfBand`.
  - `CloseFront_RespectsBandsAndClosesOnEscape`: topmost first, a window with `ClosesOnEscape == false` is skipped, returns false when nothing closes.
  - `Covers_IgnoresClosedWindows`.
  - `DrawHover_AgreesWithUpdate`: pointer at (300,200): `F.DrawHover == true`, `B.DrawHover == false`. After `Draw`, `pointer.IsOccluded((300,200)) == false`.
  - `AfterUpdate_WindowsStayOccludedAndBlocked`: after `Update`, `pointer.IsOccluded((150,120))` and `pointer.IsBlocked((150,120))` are true.
  - `BringToFront_And_Remove`.

- [ ] **Step 3: Run to verify they fail**

Run: `build-slot --label window-stack -- dotnet test KhaozEngine.Gui.Tests -c Release --filter "FullyQualifiedName~WindowStackTests"`
Expected: compile failure.

- [ ] **Step 4: Implement** in the four files, following the spec's Update and Draw sequences exactly. Keep one `List<IStackWindow>` per band and a `HashSet<IStackWindow>` of windows seen open at the last sync for open-edge detection. A pending-raise list holds edges seen while `pointer.IsDown`. Record each window's pre-update `Contains` decision for the press raise before any update runs. Iterate over a snapshot so a window opening or closing another mid-update cannot break enumeration.

- [ ] **Step 5: Run to verify they pass** (same command). Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Gui/WindowBand.cs KhaozEngine.Gui/IStackWindow.cs KhaozEngine.Gui/WindowUpdate.cs KhaozEngine.Gui/WindowStack.cs KhaozEngine.Gui.Tests/Gui/WindowStackTests.cs KhaozEngine.Gui.Tests/Gui/FakeStackWindow.cs
git commit -m "feat(gui): window stack with bands, raise on open and press, occluded updates"
```

### Task 3: WindowDrag under the stack

**Files:**
- Modify: `KhaozEngine.Gui/WindowDrag.cs:44-60`
- Test: `KhaozEngine.Gui.Tests/Gui/WindowStackDragTests.cs`

**Interfaces:**
- Consumes: Task 2 `WindowStack`, `IStackWindow`, `WindowUpdate`.
- Produces: `WindowDrag.Update(Pointer, in Rect)` latching on `pointer.IsPressOriginFresh && pointer.IsDragStartIn(grip)`. Public surface unchanged.

- [ ] **Step 1: Write the failing facts.** A `DragWindow : IStackWindow` test double owns a `WindowDrag`, a natural rect and a close square at its title's right end, places itself with `Place` in `Update`, closes on `IsTapIn(closeSquare)` before updating its drag, and uses grip = title row less the close square. Two windows: back `B` natural (300,150,520,520) and front `F` natural (340,150,440,520), so `F`'s close square lies inside `B`'s grip (the Grimhollow geometry).
  - `TitleDrag_MovesOnlyFrontWindow`: drag `F`'s title by (+50,+30): `F` moves by that, `B` does not move.
  - `FrontCloseSquare_InsideBackGrip_ClosesFront`: tap `F`'s close square: `F.IsOpen == false`, `B` open and unmoved.
  - `BackVisibleTitle_DragsBack_AcrossFront`: drag from `B`'s visible title (left of `F`) across `F`: `B` follows the whole gesture, `F` does not move, and `B` is raised on the press.
  - Existing `WindowDragTests` stay green unchanged.

- [ ] **Step 2: Run to verify they fail**

Run: `build-slot --label window-drag -- dotnet test KhaozEngine.Gui.Tests -c Release --filter "FullyQualifiedName~WindowStackDragTests|FullyQualifiedName~WindowDragTests"`
Expected: the two-window facts FAIL (both windows move, close never fires).

- [ ] **Step 3: Implement.** Replace `grip.Contains(pointer.PressOrigin)` with `pointer.IsDragStartIn(grip)`. Replace the remarks paragraph that leaves arbitration to the caller with one that says a window registered on a `WindowStack` gets arbitration from occlusion, and a caller without a stack still updates the top window first.

- [ ] **Step 4: Run to verify they pass** (same command). Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/WindowDrag.cs KhaozEngine.Gui.Tests/Gui/WindowStackDragTests.cs
git commit -m "fix(gui): window drag latches only on an unoccluded grip"
```

### Task 4: Raw pointer reads under the occlusion gate

**Files:**
- Modify: `KhaozEngine.Gui/SlotGrid.cs:425` (drop target `SlotAt(pointer.Position)`), `KhaozEngine.Gui/GuiDragContext.cs:197-201,281-282`, `KhaozEngine.Gui/TreeView.cs:333,382-383`, `KhaozEngine.Gui/ScrollablePanel.DragEdgeScroll.cs` (the raw `dragContext.PointerPosition` read near line 54), `KhaozEngine.Gui/PropertyGrid.cs:550-551`, `KhaozEngine.WorldMap.Gui/WorldMapViewCore.cs:155`, `KhaozEngine.Gui/GuiSurface.cs` (`HoverCaptured`, about line 365), `KhaozEngine.Gui/DraggableBottomSheet.cs:313-314` (`Admits`)
- Test: `KhaozEngine.Gui.Tests/Gui/OcclusionGateTests.cs`, and one fact in `KhaozEngine.WorldMap.Gui.Tests/WorldMapViewOverlayTests.cs`

**Interfaces:**
- Consumes: Task 1 `IsOccluded`.
- Produces: no new public members. `GuiDragContext` keeps the `Pointer` passed to `BeginFrame` in a private field and `OfferTargetIn` refuses when `pointer.IsOccluded(pointer.Position)`.

- [ ] **Step 1: Write the failing facts** in `OcclusionGateTests`:
  - `SlotGrid_DropOntoOccludedSlot_Refused`: a live drag over slot 0 with that slot's rect occluded: `OnSlotDropped` is not raised on release. Unoccluded: it is.
  - `OfferTargetIn_RefusesOccludedPosition`: returns false over an occluded rect while dragging, true when not occluded.
  - `ScrollablePanel_WheelUnderCover_Ignored`: wheel over content with the content occluded leaves the scroll offset unchanged. Unoccluded it scrolls. (This pins existing behaviour inherited from `IsPointerIn`.)
  - `ScrollablePanel_EdgeScrollUnderCover_Ignored`: a drag held at the content's bottom edge with that point occluded does not edge-scroll.
  - `TreeView_NoDropPositionWhileOccluded`.
  - `PropertyGrid_HoverUnderCover_Ignored`.
  - `GuiSurface_HoverCaptured_TrueWhenOccluded`.
  - `BottomSheet_RefusesOccludedPressOrigin`.
  - In `WorldMapViewOverlayTests`: `HoverOverlay_NotRebuiltForOccludedPointer`.
  Build each fact's widget with the same setup its existing test file uses (`SlotGrid` tests, `GuiDragContext` tests, `ScrollablePanel` tests, `TreeView` tests, `PropertyGrid` tests, `GuiSurface` tests, `DraggableBottomSheet` tests). Find them with `grep -rl "new SlotGrid(" KhaozEngine.Gui.Tests` and the like, and copy their arrange code.

- [ ] **Step 2: Run to verify they fail**

Run: `build-slot --label occlusion-gate -- dotnet test KhaozEngine.Gui.Tests -c Release --filter "FullyQualifiedName~OcclusionGateTests"` and the WorldMap fact with `dotnet test KhaozEngine.WorldMap.Gui.Tests -c Release --filter "FullyQualifiedName~HoverOverlay_NotRebuiltForOccludedPointer"`.
Expected: the new facts FAIL except the wheel fact, which already passes and stays as a pin.

- [ ] **Step 3: Implement** each change as listed in the spec's widget table. Each is a guard on the existing raw read, not a rewrite.

- [ ] **Step 4: Run to verify they pass**, then the full `KhaozEngine.Gui.Tests` and `KhaozEngine.WorldMap.Gui.Tests` projects once each. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui KhaozEngine.WorldMap.Gui KhaozEngine.Gui.Tests KhaozEngine.WorldMap.Gui.Tests
git commit -m "fix(gui): drop targets and raw pointer reads honour occlusion"
```

### Task 5: Documentation and version

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md` (hit-testing rules near lines 141-160, `Pointer` section near 899-910), `KhaozEngine.Gui/README.md`, `KhaozEngine.Windowing/README.md`, `CHANGELOG.md`, `Directory.Build.props:25`, `docs/INDEX.md` (status of the window stack row), `docs/design/WINDOW-STACK-DESIGN-2026-10-16.md` (status line), and every declaration `scripts/check-doc-versions.sh` reports

- [ ] **Step 1: Re-read `main`.** `git fetch origin && git show origin/main:Directory.Build.props | grep KhaozEngineVersion` and `git tag --sort=-v:refname | head -3`. If `origin/main` already stages 21.2.0, merge `origin/main` into this branch first and ride that version. Otherwise bump `<KhaozEngineVersion>` to `21.2.0`.
- [ ] **Step 2: Write the docs.** USING-KHAOZENGINE: a window-stack rule in the hit-testing list (register overlapping windows on a `WindowStack`, update it before the layers beneath read the pointer, and decide every pointer behaviour through a `Pointer` hit query or `IsOccluded` with raw `Position` read only after such a gate), and an occlusion paragraph in the `Pointer` section. READMEs: the new types and members, one line each. CHANGELOG `## 21.2.0`: full-sentence bullets for `WindowStack`, `Pointer` occlusion and `IsRightPressOriginFresh`, the `WindowDrag` latch, and the widget gate sweep. Status lines: "Implemented in 21.2.0".
- [ ] **Step 3: Run the doc guards**

Run: `sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree && bash scripts/check-doc-versions.sh`
Expected: all exit 0.

- [ ] **Step 4: Full verification**

Run: `mkdir -p local-feed && build-slot --heavy --label window-stack-build -- dotnet build KhaozEngine.slnx -c Release` then `build-slot --heavy --label window-stack-test -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`
Expected: 0 warnings, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add -A docs CHANGELOG.md Directory.Build.props KhaozEngine.Gui/README.md KhaozEngine.Windowing/README.md
git commit -m "release(version): stage 21.2.0 with the window stack"
```
(When riding an already staged 21.2.0, use `docs(gui): document the window stack in 21.2.0` instead.)
