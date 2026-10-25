# Window stack and pointer occlusion

Status: **Implemented in 21.3.0.** The design was approved by the owner in conversation on 2026-10-16. Consumer
adoption follows the release. Baseline: engine `e0dbb6da4` (v21.1.0). First consumer: Grimhollow at `780670d7`
on 21.1.0. Tracking: [#1423](https://github.com/APKiwiOrg/KhaozEngine/issues/1423), consumer adoption
[Grimhollow #557](https://github.com/APKiwiOrg/Grimhollow/issues/557).

## Problem

`KhaozEngine.Gui` ships the parts of a non-modal window but not the part that decides which window is on top.
`WindowDrag` moves a window and `PanelFrame` paints its chrome, while the `WindowDrag.Update` remarks leave
arbitration to the caller: "which window is on top is the caller's to settle: update the top one first and
skip the rest once the gesture is consumed."

That rule is not enough to build correct overlapping windows.

- `ConsumeGesture` only stops later `IsTapIn` reads. Hover, wheel gating, drag latching and press reads still
  reach a covered window.
- `BlockRegion` is advisory. No `Pointer` hit query consults it, and widgets such as `GuiSurface.Button` never
  check `IsBlocked`.
- `WindowDrag` latches on a raw `grip.Contains(PressOrigin)`, so two overlapping grips both latch one press.
- Drop targets use raw geometry (`SlotGrid.SlotAt`, `GuiDragContext.OfferTargetIn`), so a covered slot still
  accepts a drop.
- Draw order and update order are two hand-written lists that each consumer must keep reversed by hand.

Grimhollow shows every symptom. With its Codex and Stats windows open at their default places, the Stats close
square lies inside the Codex title grip. The Codex drag latches the press and consumes the gesture, so the
Stats close tap never fires. A title drag moves both windows (Grimhollow #383). Nothing raises a window that
the player clicks.

## Intended result

A consumer registers its windows once with a `WindowStack`. The stack owns their z-order, raises a window when
it opens or is pressed, updates windows top to bottom so that each one sees only its uncovered area, and draws
them bottom to top. A covered region of a window is dead to every pointer read, including the engine widgets
inside it, without the window doing anything special.

## Behaviour contract

1. **Bands.** Every window belongs to one band: `Docked`, `Floating` or `Topmost`, in that order bottom to top.
   No window ever rises above a higher band. Within a band the stack keeps an order.
2. **Front on open.** A window that becomes open is raised to the front of its band. While the left button is
   held, that raise waits until the gesture has ended: it is skipped by `Update` on the release frame too and
   applied by that frame's `Draw`, so the release's taps and drops resolve against the order the player saw and
   a drag in progress never loses its window.
3. **Front on press.** On a fresh left or right press, the front-most open window whose area contains the
   press origin is raised to the front of its band. The test uses the windows' places from before this
   frame's updates, which is what the player saw when pressing. No press raises anything while a window
   captures the pointer, while the other mouse button is held, or on a frame with `receivesInput: false`.
4. **Only what is seen answers.** Each window is updated with every window above it occluded. A hit query
   whose test point lies in an occluded area misses. The uncovered part of a covered window works normally.
5. **A gesture keeps its owner.** Queries that test the press origin (`IsDragStartIn`, `IsDraggingIn`,
   `GetDragDelta`) keep answering for the window the press began in for the whole gesture, because rule 3
   raised that window and rule 2 defers any raise above it until release.
6. **Capture.** A window that reports `CapturesPointer`, normally because a context menu it owns is standing,
   is updated first with nothing occluded. Every other window is then updated with the whole surface occluded.
   If several capture, the top-most capturing window wins.
7. **No input.** `Update(..., receivesInput: false)` still updates every open window, with the whole surface
   occluded and `WindowUpdate.ReceivesInput` false. A window must not read keyboard input on such a frame,
   the same contract `Screen.Update(dt, receivesInput)` already sets.
8. **Escape.** `CloseFront()` closes the top-most open window that closes on Escape, searching `Topmost`, then
   `Floating`, then `Docked`, and reports whether it closed one. Keyboard routing is otherwise the consumer's.
9. **Below the stack.** After `Update`, every open window's area stays occluded and reserved with `BlockRegion`
   for the rest of the frame, so anything below the stack that reads the same pointer, including
   `ScreenStack.PressBeganOverUi`, treats windows as UI. Callers update the stack before the layers beneath
   read the pointer, the same ordering rule `BlockRegion` already carries.
10. **Above the stack.** Content drawn after `WindowStack.Draw` (tooltips, context menus, drag ghosts) draws
    above every window, so `Draw` clears occlusion when it finishes.

## Engine API

### `Pointer` occlusion (`KhaozEngine.Windowing`)

```csharp
public void Occlude(Rect region);      // add an occluded area for the rest of the frame
public void OccludeAll();               // occlude the whole surface for the rest of the frame
public bool IsOccluded(Vector2 point);
public void ClearOcclusion();
public bool IsRightPressOriginFresh { get; }   // the right-button twin of IsPressOriginFresh
```

The occluder list is cleared at the start of every `Pointer.Update`, as `_blocked` is. With an empty list every
query behaves exactly as in 21.1.0, so consumers that never occlude see no change. `BlockRegion` and
`IsBlocked` keep their meaning.

Hit queries and the points they now test for occlusion:

| Query | Occluded point means a miss |
| --- | --- |
| `IsTapIn`, `IsTapFromTo`, `IsPressingIn` | press origin or current position |
| `IsRightTapIn`, `IsRightPressingIn` | right press origin or current position |
| `IsPointerIn`, `IsHoveringIn` | current position |
| `IsDraggingIn`, `GetDragDelta`, `IsDragStartIn` | press origin |

`IsReleasedOutside` reports true when the release lands in an occluded area, so a click on a window in front
dismisses a dropdown or text focus in a window behind it.

The `InputManager` pass-throughs forward to these queries and inherit occlusion unchanged.

### `WindowStack` (`KhaozEngine.Gui`)

```csharp
public enum WindowBand { Docked, Floating, Topmost }

public interface IStackWindow
{
    bool IsOpen { get; }
    Rect Bounds { get; }
    bool CapturesPointer => false;
    bool ClosesOnEscape => true;
    bool Contains(Vector2 point) => Bounds.Contains(point);
    void Occlude(Pointer pointer) => pointer.Occlude(Bounds);
    void Close();
    void Update(in WindowUpdate update);
    void Draw(SpriteBatch batch, Pointer pointer);
}

public readonly struct WindowUpdate
{
    public Pointer Pointer { get; }
    public InputState Input { get; }
    public float Dt { get; }
    public bool ReceivesInput { get; }
    public bool IsFront { get; }      // top-most open window of its band
}

public sealed class WindowStack
{
    public void Add(IStackWindow window, WindowBand band = WindowBand.Floating);
    public void Remove(IStackWindow window);
    public void BringToFront(IStackWindow window);
    public IReadOnlyList<IStackWindow> Order { get; }   // bottom to top, bands in order
    public bool IsCapturing { get; }   // the last Update ran in capture mode
    public void Update(Pointer pointer, InputState input, float dt, bool receivesInput = true);
    public void Draw(SpriteBatch batch, Pointer pointer);
    public bool Covers(Vector2 point);
    public bool CloseFront();
}
```

`Contains` and `Occlude` exist for windows that are not one rectangle, such as a docked panel whose tab strip
and body are separate rects. `Add` places a window at the front of its band. A closed window stays registered
and is skipped by update, draw, `Covers` and `CloseFront`.

**Update, per frame.**

1. Raise windows that became open since the last sync (rule 2), unless the left button is held or was released
   this frame.
2. On `IsPressOriginFresh` or `IsRightPressOriginFresh`, raise the front-most open window containing the
   relevant origin (rule 3), unless a window captures, the other button is held or input is off.
3. If a window captures, update it with no occlusion, then `OccludeAll` and update the rest top to bottom.
4. Otherwise update open windows top to bottom. After each one, call its `Occlude` and reserve its `Bounds`
   with `BlockRegion`, so every window below sees only its uncovered area. This holds for a window that
   closed during its own update too: the player saw it this frame, so the tap that closed it must not reach
   the window behind. It is skipped from the next frame on.
5. With `receivesInput: false`, `OccludeAll` first and update every open window with `ReceivesInput` false.

**Draw.** Sync open edges, skipping only while the left button is held, then draw open windows bottom to top. Before each window the
stack clears occlusion and re-applies the occluders of the windows above it, so draw-time hover reads, for
example a close square lighting under the cursor, normally agree with what Update decided. Draw mirrors the mode of
the last Update: after a capture frame the captor draws with nothing occluded and every other window draws
with the whole surface occluded, and after a `receivesInput: false` frame every window draws fully occluded.
`ClearOcclusion` at the end. A window removed from the stack during another window's update is skipped for
the rest of that frame.
A frame with ten windows applies at most forty-five occluders, which is not worth optimising.

### Widgets made occlusion-aware

Every engine widget must decide pointer behaviour through a `Pointer` hit query or `IsOccluded`. Raw
`Position` may be read only after such a gate. This sweep brings the existing raw reads into line.

| Widget | Change |
| --- | --- |
| `WindowDrag.Update` | Latch on `IsPressOriginFresh && IsDragStartIn(grip)` instead of `grip.Contains(PressOrigin)`. The caller-arbitration remark is replaced by a pointer to `WindowStack`. |
| `SlotGrid` drop target | `SlotAt(pointer.Position)` yields no slot when the position is occluded. |
| `GuiDragContext.OfferTargetIn` | Refuses when the live pointer position is occluded. The context keeps the `Pointer` it was given in `BeginFrame`. |
| `TreeView.ComputeDrop`, `TrackDrag` | No drop position while occluded. |
| `ScrollablePanel` drag edge scroll | No edge scroll while the pointer is occluded. |
| `PropertyGrid` | The manual `Bounds.Contains(pointerPos)` becomes `IsPointerIn(Bounds)`. |
| `WorldMapViewCore` hover overlays | No hover overlay rebuild for an occluded position. |
| `GuiSurface.HoverCaptured` | Also true when the pointer is occluded. |
| `DraggableBottomSheet.Admits` | Also refuses an occluded press origin. |
| `GuiSurface.PointerCaptured` | Also true when the press origin is occluded. |
| `ScreenStack.PressBeganOverUi` | Also latches when the press origin is occluded. |
| `PannableCanvas.TryGetTap` | No tap when the press origin or the release is occluded. |
| `PanZoomController` | Admits no press, touch or gesture at an occluded point and records no tap on an occluded release. |

Wheel handling needs no change. Every widget that reads the wheel gates it on `IsPointerIn` or receives it from
a caller that does (`ScrollablePanel`, `TreeView`, `PatchNotesView`, `PannableCanvas`, `PopupPanel`).

`RadialMenu` keeps its raw `EntryAt` reads. A radial menu is a popup drawn above every window, and its reads
already follow a `Pointer` gate.

## Tests

All headless, in `KhaozEngine.Render.Tests/Windowing` for `Pointer` (next to `PointerTests`) and
`KhaozEngine.Gui.Tests/Gui` for the stack and widgets, using `MouseFrames`.

- Each hit query misses for an occluded test point and is unchanged with no occluders. `IsReleasedOutside`
  reports an occluded release as outside. Occlusion clears on `Update`. `IsRightPressOriginFresh` matches the
  left flag, including the same-frame tap.
- Stack: open raises to the band front, open during a held press waits for release, a press raises the
  front-most window under the origin, a docked window never rises above a floating one, update runs top to
  bottom and draw bottom to top, a covered window sees no hover or tap in the covered area and full input in
  the uncovered area, capture updates the captor first and occludes the rest, `receivesInput: false` occludes
  everything, `CloseFront` respects bands and `ClosesOnEscape`, `Covers` ignores closed windows, a window that
  closes during its update still occludes for the rest of that frame and is not drawn, a press while the
  other button is held raises nothing, and draw-time hover agrees with update.
- Two overlapping `WindowDrag` windows: only the front one moves, and a press on the front close square inside
  the back grip is the front window's tap.
- A drop onto a covered `SlotGrid` slot is refused and an uncovered slot accepts it. `OfferTargetIn` refuses an
  occluded position. The wheel over a covered `ScrollablePanel` does not scroll it.

## Documentation and release

- `docs/USING-KHAOZENGINE.md`: the hit-testing rules gain the window stack and the gate rule above, and the
  `Pointer` section gains occlusion.
- `KhaozEngine.Gui/README.md` and `KhaozEngine.Windowing/README.md`: the new types and members.
- `CHANGELOG.md`: a 21.3.0 entry, minor because every change is additive. Behaviour with no occluders is
  unchanged. The `WindowDrag` latch change is behaviour-preserving for a single window.
- A row for this document in `docs/INDEX.md`.

## Out of scope

Saving window order or position across sessions, cascade placement, docking and snapping, minimising, keyboard
focus routing beyond `IsFront`, modal dialogs (`PopupPanel` is unchanged) and promoting a consumer's window
chrome to `PanelFrame`.

## First consumer

Grimhollow adopts the stack after 21.3.0 is released, tracked in
[Grimhollow #557](https://github.com/APKiwiOrg/Grimhollow/issues/557). It moves the side stone
(docked), Codex, Stats, skill guide, world map, bank, shop and dialogue (floating) and its debug menu
(topmost) onto the stack, makes its bank and shop movable, replaces its local `WindowDrag` copy with this one,
and deletes its hand-written hover and escape arbitration.
