# `IScreenComponent`: the composition unit below `Screen`

Shipped 13.6.0. Closes [#226](https://github.com/APKiwiOrg/KhaozEngine/issues/226). Consumer adoption is
https://github.com/APKiwiOrg/SpaceGame/issues/69 (its phase 3 was blocked on the decision recorded here).

This doc holds the WHY. The shipped API is in `CHANGELOG.md` and `docs/USING-KHAOZENGINE.md`, and the per-member
contract is in the XML docs, which are the reference surface. What is here is the set of decisions that will be
re-litigated by the next reader if the reasoning is not written down: why the three Screen+View pairs #226 names
were NOT migrated, why `Screen` was not modified, and why `SpriteFont`/`Texture2D` are not on `Draw`.

---

## 1. The gap, and what #226 got wrong about it

The gap is real and #226 states it correctly. `KhaozEngine.Gui` shipped no interface at all, and there was no
composition unit below `Screen`. The asymmetry with the sim side is the sharpest way to see it: `Ecs.ISystem` is
the unit below `World`, and nothing was the unit below `Screen`. Consumers hit it hard, SpaceGame's
`GameplayScreen.cs` at 2485 lines and Nullwake's at 1649.

Three of the issue's supporting claims did not survive contact with the code. Each one changed the design, so
each is recorded.

**1.1 The proposed `Update(dt, Rect) -> bool` matches none of the three pairs it cites.**

| Type | Actual `Update` signature | What the bool means |
|---|---|---|
| `UpdateOverlayView` | `Update(IUpdateStatus, InputState, float)` | visible |
| `PatchNotesView` | `Update(Pointer, InputState, float, Rect, ITextMeasurer)` | still open |
| `ConnectionStatusController` | `Update(ConnectionStatusSignals, float)` | returns a struct, not a bool |

None takes `(dt, Rect)`. None returns "consumed input". This matters because `Screen.Update`'s bool DOES mean
consumed, documented at length in its XML doc, and the whole point of the new type is to compose into that. A
component forwarding `UpdateOverlayView`'s "visible" bool as "consumed" would starve every screen below it for as
long as the panel showed, which is exactly the dormant-overlay trap `Screen.Update` spends thirteen lines warning
about and `UpdateOverlayScreen` exists to avoid.

So the new bool is defined as consumed-input, in the identical words as `Screen.Update`, with an explicit warning
in the XML doc that it is NOT the bool the existing views return.

**1.2 The third "pair" is not a pair.** `ConnectionStatusController` has no `Draw` at all. It is a headless policy
state machine that returns a `ConnectionStatusView` struct, and `ReconnectScreen` draws inline while polling it
through a `Func<ConnectionStatusView>`. There is no `ReconnectView`. The engine's "three instances of the pattern"
are actually three different splits, and only two of them are the same thing.

**1.3 The count is low, and the missed one is the important one.** `DiagnosticsHud` is a fourth. The fifth is
`ToolkitPage` in `KhaozEngine.Showcase/Room2DGui.cs`, a private abstract class in the engine's own reference
sample with FIVE implementations and a host that fans out to them across four lifecycle moments. That is the
engine having already written this type, privately, without naming it, and it is much stronger evidence than the
three cited pairs because it is the only place in the engine with actual multiplicity, which is the problem the
type exists to solve. It became the migration target (section 4).

---

## 2. The design decisions

### 2.1 An interface, not an abstract class

- Mirrors `Ecs.ISystem`, which is the exact symmetry #226 names as missing.
- The contract carries no state. Everything arrives as a parameter, so a base class would buy nothing. (Contrast
  `Screen`, a class only because it carries `Manager`/`DrawOrder`/`State`/`TransitionAlpha`.)
- **It does not consume the consumer's single base-class slot.** This is the decisive one. SpaceGame already has
  `RunPopup` and the Showcase already has `ToolkitPage`. A base class would force both to reparent. An interface
  lets each keep its own domain base and add the contract on top, which is the intended layering: the interface is
  the fan-out contract, a consumer's own abstract base adds domain lifecycle above it. The Showcase migration
  demonstrates precisely this.

### 2.2 `LoadContent`/`UnloadContent` as default interface members

`Screen.LoadContent`/`UnloadContent` are virtual no-ops and `GameScene.OnEnter`/`OnExit` are too, so optionality
is the established convention. Requiring them would force most components to write two empty methods, which is the
noise the issue is trying to delete.

The DIM risk is nil here because `ScreenComponentList` is the only caller and always calls through the
`IScreenComponent` reference. A component that DOES declare them gets them callable both ways as normal. Only
`concreteType.LoadContent()` on a type that omitted them fails to compile, and nothing does that. (The Showcase's
`ToolkitPage` declares `UnloadContent` for exactly this reason: its host calls it through `ToolkitPage`, not
through the interface.) net10.0, so DIMs are fully supported.

### 2.3 `bounds` is a parameter on BOTH `Update` and `Draw`, not a property

- It removes any need for a `Resize`/`OnResize` hook. `GameScene.OnResize` exists precisely because `GameScene`
  has no per-call bounds. Not repeating that is the point.
- Every non-sample engine screen already re-reads `IDesignViewport.WindowBounds` fresh each frame
  (`UpdateOverlayScreen`, `PatchNotesScreen`, `ReconnectScreen`). `WindowBounds` genuinely changes with the
  window, since it is derived from the live scale and letterbox offsets. A component that captured it would go
  stale on the first resize, with no compiler help and no obvious symptom. Per-call bounds makes that class of bug
  unrepresentable.
- It is on `Update` as well as `Draw` because hit-testing needs it. `PatchNotesView.Update` already takes a
  `Rect viewport` and uses it to block a pointer region.
- `Rect` is a `readonly record struct`, so passing it per frame is free, and cheap value equality is what lets a
  component cache a derived layout and recompute only when the bounds actually change.

**One correction to the spec this was built from, recorded because it changes a claim rather than the design.**
The spec asserted that the Showcase's captured `ToolkitPage.Content` was a LIVE stale-bounds-on-resize bug. It is
not. `Content` derives from `IDesignViewport.DesignBounds`, and `DesignViewport.Width`/`Height` are get-only,
assigned once in the constructor. `GameApp` constructs the viewport once and never replaces it, so `DesignBounds`
is constant for the life of the app and the captured value never goes stale. The design argument above is
unaffected, because it rests on `WindowBounds` (which does change) and on removing the resize hook, not on a bug
in the sample. The migration in section 4 is therefore a proof of fit and a removal of a fragile pattern, NOT a
bug fix, and the CHANGELOG says so.

### 2.4 `InputManager`, not `Pointer` + `InputState`, and not nothing

One parameter subsumes both (`InputManager.Pointer`, `InputManager.State`). `ScreenStack.Pointer` IS the
`InputManager`'s pointer, so click-through blocking composes across components, widgets and screens with no extra
wiring. It is also the object the keyboard/gamepad widget overloads need (`Slider.Update(InputManager, focused)`),
and it satisfies the engine's hard input rule: hit-test via the bounds helpers, never raw position plus button.

Passing it per frame rather than capturing it at construction is what makes a component testable with a bare
`new InputManager()` and no `ScreenStack` at all. Capturing would also introduce a load-order dependency, because
`Screen.Manager` is only assigned in `ScreenStack.Add`, not at screen construction. `ToolkitPage` worked around
exactly that by taking the `ScreenStack` in its own `Load`.

### 2.5 No `SpriteFont` / `Texture2D white` on `Draw`

Genuinely close, so both sides are recorded.

*For including them (rejected):* four Gui types already draw with exactly `(batch, font, white, Rect)`, and
SpaceGame's dominant draw shape includes them.

*Against, and this wins:*

1. Those four are the SINGLE-view case (a screen holding one presenter, resources handed in per call). The only
   actual multi-component fan-out in the engine, `ToolkitPage`, passes neither and keeps resources in a field. So
   does SpaceGame's one shape-matching component. This type is for the fan-out case.
2. Fonts and textures are stable for a component's lifetime. Anything that stores them anyway gets two dead
   parameters on every call forever.
3. A non-null `SpriteFont` forces components that never draw text to accept one. A nullable one forces a null
   check at every use site under `Nullable=enable` plus warnings-as-errors.
4. It makes the interface prescriptive about the 2D resource model. A component drawing from an atlas, or through
   a `GuiSurface` (whose constructor already takes the white texture), gets nothing from them.
5. It is a half-measure on the real consumer pain. The smuggled set is font, white, `GuiSurface`, viewport,
   content loading, and a service provider. Carrying two of six invites a context object later.

Components receive resources through their own constructor, which is what every engine screen already does. The
Showcase's `ImmediatePage` is the worked counter-example that proves the shape holds: it needs a `Pointer` at DRAW
time for its immediate-mode surface, and gets it from its own host reference rather than from the interface.

### 2.6 `Screen` is NOT modified

#226 asks for "a way for a `Screen` to hold a collection of them and fan out once". A composed field IS that way,
and it is the better one.

A `Screen.Components` property would have to be torn down from `Screen.UnloadContent`, which is `virtual` and
routinely overridden. A subclass that overrides it and forgets `base.UnloadContent()` silently leaks every
component's assets, with no compiler help. That is a worse footgun than the four lines it saves.

Leaving `Screen` untouched also means zero risk to the existing screen contract, no binary-compatibility question,
and a `ScreenComponentList` that works unchanged inside a `GameScene`, inside a non-`Screen` host, and in a test
with no stack at all. Adding `Screen.Components` later is additive and cheap under SemVer. Removing it would be a
major bump. Ship the smaller, reversible thing.

### 2.7 What was excluded, and why

An unrecorded decline gets re-raised, so the whole list is here.

| Excluded | Why |
|---|---|
| Layout / anchoring on the interface | `Layout.Resolve` already exists and is all that is needed. A component calls it against `bounds`. |
| A widget tree, parent/child nesting | No tree. A component that wants children holds its own `ScreenComponentList` and forwards, which composes recursively at zero API cost. |
| Data binding | Out of scope for this engine entirely. |
| A `Bounds` property | Section 2.3. Per-call bounds is what removes the resize hook. |
| `Enabled` / `Visible` flags | The component's own business, and the existing views already do it internally (`UpdateOverlayView.Draw` no-ops when hidden, `DiagnosticsHud.Draw` no-ops while faded out). Flags on the interface would need state, which is what keeps it a class. |
| A `DrawOrder` sort key | Registration order, matching `ISystem`. `ScreenStack` needs a sort key because screens are pushed from unrelated places. A screen constructs its own components in one place, in the order it wants. |
| A modal flag (`PassUpdateThrough` analogue) | A component that stops its SIBLINGS updating is incoherent: they are peers within one screen, all part of the same surface. It would also re-create the dormant-overlay starvation trap one level down. Something that needs to be modal should be a `Screen`. |
| `AlwaysReceivesInput` analogue | Only meaningful alongside the modal break, which is excluded. |
| `SpriteFont` / `Texture2D white` on `Draw` | Section 2.5, five reasons. Constructor-injected instead. |
| A context / services object | Section 3. It is the correct fix for the closure smuggling and it is a different, larger piece of work that would make this a framework. |
| A `Resize` / `OnResize` hook | Per-call bounds makes it unnecessary. |
| Async lifecycle (`LoadContentAsync`) | Nothing in the engine's Gui is async. `ScreenStack.Add` is synchronous. |
| Touch/gesture handling | `Pointer` already normalizes it. |
| Any change to `Screen`, `ScreenStack`, `GameScene`, `SceneManager` | Section 2.6. |
| Any change to the retained widgets or `GuiSurface` | Per #226: "The stateful containers already shipped are fine as they are." They are the leaf level and a component owns several of them. |
| A mandated `*Component` type-name suffix | The interface is the signal. #226's complaint is that nothing NAMED the choice, not that the class names were wrong. Suggested in docs, not enforced. |

---

## 3. Two limits, stated up front

**Only a minority of a big screen's fields are components.** The field census of SpaceGame's `GameplayScreen` is 5
components with `Update`/`Draw`, 10 collaborators with a tick or pump surface under some other name, 2 child
`Screen`s, and 39 plain data fields (textures, fonts, dictionaries, bools, timers). A component interface addresses
the first group. It does not address the plain data, which is most of it. #226's "N x 5 cross product becomes 5
loops" arithmetic is right in principle and overstated for that specific file.

**This does not remove the closure smuggling, and is not meant to.** `GameplayScreen` hands `XpUiFeedbackController`
four lambdas purely to tunnel service-locator access downward (a viewport-size getter, a `worldToScreen` closure
over the camera, an SFX callback, a volume getter), and `GameplayRunViewAssets` takes ten constructor arguments,
two of them `Content.Load` lambdas. Those exist because the screen is the sole `ScreenManager` holder, which is a
service-locator problem, not a composition problem. Solving it would require a context object carrying font, white
pixel, `GuiSurface`, viewport, content access and a service provider. That is the UI framework #226 explicitly
rules out. Out of scope, deliberately, and said in the CHANGELOG so nobody expects it.

---

## 4. Migration: no for the three named pairs, yes for one the issue missed

### 4.1 Why the three named pairs were NOT migrated

**`UpdateOverlayView` + `UpdateOverlayScreen`.** `UpdateOverlayView` is `public sealed`. Its
`Update(IUpdateStatus, InputState, float)` would have to capture the status (currently a per-call argument, also
passed to `Draw`) and change its bool's meaning from "visible" to "consumed". That is a breaking public API change,
which is a MAJOR bump, for a screen that is already 62 lines. Worse, `UpdateOverlayScreen.Update` encodes modality
logic a generic fan-out cannot express: it recomputes `PassUpdateThrough` from
`_status.IsRequired || _status.State == UpdateState.Applying` and returns `modal ? receivesInput :
_view.TriggeredThisFrame`. Routing that through `ScreenComponentList` makes the screen LONGER, not shorter.

**`PatchNotesView` + `PatchNotesScreen`.** `PatchNotesView.Update` takes an `ITextMeasurer` that drives its scroll
clamp and is not, and should not be, on the interface. Same breaking public change, and `PatchNotesScreen` is 61
lines.

**`ReconnectScreen` + `ConnectionStatusController`.** Not a Screen+View pair at all (section 1.2). Migrating it
would mean inventing a `ReconnectView` that does not exist, which is new work, not a migration. `ReconnectScreen`
does have a genuine three-moment fan-out over a `List<Button>`, but `Button` is a retained leaf widget with bounds
as a field and `Update(Pointer) -> bool(clicked)`. Making `Button : IScreenComponent` would be a category error and
would drag the leaf level into the composition level.

**The unifying reason, which is the real finding.** All three screens have exactly ONE collaborator, and a
one-collaborator screen is already 56 to 62 lines. **They do not have the problem this type solves.** The type is
for the twelve-collaborator screen. That is also why the engine never needed to name it: none of the engine's own
screens is multi-component. The need is entirely consumer-side, which is worth knowing before anyone reads the
absence of engine adoption as the type not being used.

### 4.2 What was migrated instead: `ToolkitPage`

`KhaozEngine.Showcase/Room2DGui.cs`'s `ToolkitPage` is the engine's own hand-rolled version of this interface, with
five implementations. Migrating it was the proof of fit and it was nearly free: `KhaozEngine.Showcase` is
`IsPackable=false` and `WinExe`, so nothing there is public API and there is no breaking change and no major bump.

What it demonstrates, all of it load-bearing:

- **The intended layering.** `abstract class ToolkitPage : IScreenComponent` keeps its own `A`/`Stack` fields and
  its `Activated()`/`Deactivated()` tab lifecycle while gaining the engine contract on top. That is exactly the
  message for SpaceGame's `RunPopup`.
- **The DIM decision.** `ToolkitPage` declares `UnloadContent` on the base, for the reason section 2.2 gives (its
  host calls it through `ToolkitPage`, not through the interface), so every page inherits a declaration and only
  `InputAudioPage`, which owns an `AudioSystem`, has anything to put in it. No page is registered through
  `ScreenComponentList.Add` either, so the `LoadContent` default goes unexercised here and is covered by
  `MinimalComponent` in the tests instead.
- **Per-call bounds, with state preserved.** The captured `Content` field is gone. Because three of the five pages
  build retained widgets at absolute positions, the base class splits construction (`OnLoad`, runs once, before any
  bounds exist) from placement (`OnLayout`, re-runs only when the bounds actually change, using `Rect`'s value
  equality). A page therefore keeps its typed text, scroll offset and field values across a re-layout. This split
  is a deviation from the letter of the implementation spec, which assumed the pages could read `bounds` directly
  in `Update`/`Draw`; they could not, because the widgets need a position at construction time. The template-method
  shape (`Update`/`Draw` resolve layout, then call `OnUpdate`/`OnDraw`) is how the base absorbs that.
- **Honest consumed-input answers, and `receivesInput` read BEFORE the input is touched.** Pages with no
  interactive widgets return `false` outright rather than a bare `true`. Pages with widgets OR the widgets' own
  return values, and gate the widget calls themselves, not only the answer: a widget's `Update` hit-tests and fires
  its callbacks, so calling one on a blocked page runs the click and merely reports false afterwards. What keeps
  ticking while blocked is the page's own timers and animation, which is what "a component still updates every
  frame" means. `InputAudioPage` shows both halves in one method: its clock, audio and fading tap marks run
  regardless, its keys, gestures and pad sit behind the gate.
- **Two widget bools are not consumed-input answers**, so the page derives around them. `Dropdown.Update` means
  "the selection changed" and `NumberField.Update` means "the value changed", so an open dropdown swallowing a
  click, or a field being scrubbed onto the value it already held, would report false. `WidgetsPage` reads
  `IsOpen` either side of the call, and `IsEditing`/`IsScrubbing` alongside it, which is the same "owns input"
  answer `TextInput.Update` already returns while focused. The widgets' own return semantics are public API with
  existing callers and were deliberately left alone.
- **When NOT to use `ScreenComponentList`.** The tab host keeps its own array and its lazy per-tab load, and does
  NOT get a list, because the pages are mutually exclusive TABS with exactly one running. A fan-out list would
  update and draw all five. The interface is the per-component contract, the list is one collection over it, and a
  host with different collection semantics keeps its own collection and still speaks the same contract. That
  distinction is worth a reference sample demonstrating it.

### 4.3 Consumer adoption

https://github.com/APKiwiOrg/SpaceGame/issues/69 phase 3 is the real proof and the real beneficiary. Separate repo,
separate release, not started here.
