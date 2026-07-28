# Gui drag and drop: the cross-widget primitive

Design record for [#315](https://github.com/APKiwiOrg/KhaozEngine/issues/315), shipped 17.9.0. This is the
why. What shipped is in `CHANGELOG.md` and `docs/USING-KHAOZENGINE.md`, and the API lives in
`KhaozEngine.Gui/README.md`.

## The gap it closes

`KhaozEngine.Gui` had exactly one drag-and-drop, `TreeView`'s same-widget row reorder. Everything else that
looked like a drag (slider thumb, `NumberField` scrub, `PannableCanvas` pan, the scroll drags) is a
single-widget gesture that never leaves the widget it started in. So there was no way to pick an icon out of
one `SlotGrid` slot and put it down somewhere else, and every game that wanted an inventory had to hand-roll
press-origin tracking, a ghost, cross-widget hit testing, and drop validation.

Three Ruinborne features were blocked on it, one of them load-bearing:
[295](https://github.com/APKiwiOrg/Ruinborne/issues/295) (drag a stack out of the bag to destroy it, the only
intended way to free a slot now that a full bag refuses pickups),
[262](https://github.com/APKiwiOrg/Ruinborne/issues/262), [263](https://github.com/APKiwiOrg/Ruinborne/issues/263).

## Decision 1: a standalone context object, not widget-base-class members

`GuiDragContext` is its own object the participating widgets consult. Two reasons, and the second is the real
one.

The mechanical reason: there is no widget base class to put members on. `Button`, `Slider`, `SlotGrid`,
`TreeView`, `Dropdown` and the rest are independent `sealed` classes sharing conventions (a `Bounds` field, an
`Update(Pointer)`, a `Draw(batch, white, font)`), not a type. Introducing a base now would be a breaking change
to every widget and to every consumer that derives from or wraps one, in exchange for nothing this feature needs.

The structural reason: a drag is not the state of a widget. It is the state BETWEEN two of them, and there is
exactly one of it at a time per pointer. Hanging it off a base class would put N copies of a 1-instance concept
in the type system and then need a static or an owner to arbitrate which copy is real. The source widget and
the target widget are usually not the same object and may not even be on the same screen, so the state has to
outlive both of their update calls either way. An object both sides are handed models that honestly.

The cost is one extra thing to thread through `Update`, which is why the drag-aware entry points are
overloads (`Update(pointer, drag)`) and passing null is exactly the old behaviour. A widget that never sees a
context has no drag code path at all.

## Decision 2: a rejected drop flies the ghost home

`ReturnDuration` (default 0.12 s) eases the ghost from where it was released back to the rect it was grabbed
from, then it disappears.

Considered and rejected:

- **Vanish on release.** Cheapest, and wrong: an item that disappears from under the cursor reads as
  destroyed, which in an inventory is the one outcome you must never imply by accident.
- **Fade in place.** Communicates "nothing happened" but not *where* the thing went. The player then has to
  find it again, and in a 25-slot bag that is a real cost.
- **Fly home.** Says both things at once: the drop failed, and here is where it went back to. It costs one
  captured rect (which the source already passed for ghost sizing) and one eased lerp.

The return is a cosmetic tail and nothing is load-bearing on it. `IsDragging` goes false the instant the drag
ends, so no target can still claim a returning ghost; only `IsReturning` stays true. `ReturnDuration = 0`
turns it off entirely, and every drop/cancel result is reported on the frame the gesture ended, never at the
end of the animation.

Refusal is shown BEFORE the release, not corrected after it: a target passes its verdict to `OfferTarget` on
every frame the drag hovers it, so `ShowRejectOverlay` washes the ghost while the button is still down. A drop
that would be refused simply never commits, so there is no accept-then-undo path to get wrong.

## Decision 3: TreeView is deliberately left alone

`TreeView`'s reorder is NOT refactored onto the new primitive, and should not be.

They are different gestures wearing the same word. `TreeView` moves a row within one sibling list: the payload
is a `TreeNode` reference it already holds, the drop geometry is an insertion point BETWEEN two rows
(`_dropIndex`, drawn as a line), and the constraint is same-parent-only. `GuiDragContext` moves an opaque
token from one widget to a target cell in another, where the target decides whether it will take it. Folding
one into the other means either bloating the primitive with an "insert between two things" concept that no
other consumer wants, or losing `TreeView`'s constrained same-parent rule, which is the only thing making its
drop indices meaningful.

There is also nothing to gain. `TreeView` is shipped, is wired into the map editor's outline
(`ReorderFeatureCommand`), and is covered by tests. A refactor buys no consumer anything and risks a live
editor feature.

The one piece they genuinely share is the arm rule, and they already share it: both go through
`Pointer.IsDragStartIn` plus a 6 px threshold, now named once as `GuiDragContext.ShouldBeginDrag` and
`GuiDragContext.DragThreshold`, matching `TreeView.DragThreshold`. That duplication is one comparison, not a
subsystem.

The boundary, for the next reader tempted to unify them: **same-widget ordinal reorder is `TreeView`;
cross-widget payload transfer is `GuiDragContext`.**

## Decision 4: the right-click bounds helper ships in the same release

`Pointer` had `IsRightDown` / `IsRightJustPressed` / `IsRightJustReleased` but no right-button BOUNDS helper,
so a consumer could not hit-test a right-click without pairing a raw position test with a button read, which
the engine's one hard input rule forbids. That made right-click context menus unbuildable for the same
structural reason drag was, which is why #315's 2026-07-28 comment raised it here.

Folded in rather than split out. It is the same class, the same press-origin invariant, and the same headless
harness that the drag work already stands up, and splitting it would cost a whole engine release for three
helpers. Ruinborne gets both halves off one pin bump.

`IsRightTapIn` needed real state, not an alias: `_pressOrigin` was only latched on the LEFT press edge, so the
right button had no origin to enforce the invariant against. It gets `_rightPressOrigin`, and its own
`_rightConsumed` latch, because a left-gesture `ConsumeGesture` must not blind a right-click and consuming the
right-click that opened a menu must not cancel an unrelated left tap. Right-button DRAG helpers are
deliberately not added: nothing asked for one.

## What is not in scope

- No multi-select drag (one payload per gesture). Nothing has asked for it, and a payload token is opaque
  enough that a game can carry a set in it if it ever does.
- No drop-between-cells / insertion-line semantics. That is `TreeView`'s job, per Decision 3.
- No touch or gamepad drag. The whole thing rides `Pointer`, so it inherits whatever `Pointer` gains.
- No auto-scroll of a `ScrollablePanel` while a drag hovers its edge. Worth doing when a game has a bag long
  enough to need it; filed rather than guessed at.
