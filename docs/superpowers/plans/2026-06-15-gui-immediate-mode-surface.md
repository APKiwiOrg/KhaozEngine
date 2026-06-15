# Immediate-mode Gui surface — implementation plan

> For agentic workers: execute task-by-task. TDD, frequent commits. Do NOT create a new branch — commit on the
> current worktree branch.

**Goal:** add `GuiSurface` + `GuiStyle`/`GuiAlign` to `KhaozEngine.Gui`, headless-tested, demoed in GuiSample,
shipped as 5.17.0-experimental.

**Architecture:** an immediate-mode surface drawing into a caller-owned `SpriteBatch`, reusing `GuiDraw` and
the `Pointer` press-origin tap invariant. Spec: `docs/superpowers/specs/2026-06-15-gui-immediate-mode-surface-design.md`.

---

### Task 1: `GuiStyle` + `GuiAlign`

**Files:** Create `KhaozEngine.Gui/GuiStyle.cs`.

- Define `public enum GuiAlign { Left, Center, Right }`.
- Define `public struct GuiStyle` with the fields in the spec and a `static GuiStyle Default { get; }` whose
  palette matches the existing `Button.cs` defaults (Fill 0.18/0.30/0.42, Hover 0.26/0.50/0.66, Press
  0.20/0.40/0.55, Text = white). Pick sensible DisabledFill (dim grey-blue), DisabledText (mid grey), Border
  (0.30/0.38/0.52), SelectedFill (0.28/0.46/0.66), SelectedBorder (0.55/0.80/1.0), BorderThickness 1.5.
- No test needed for the struct alone; it is covered through GuiSurface tests. Commit.

### Task 2: `GuiSurface` (TDD)

**Files:** Create `KhaozEngine.Gui/GuiSurface.cs`; Create `KhaozEngine.Tests/Gui/GuiSurfaceTests.cs`.

Follow the spec's API + behaviour rules exactly. Implementation notes:
- Hold `Texture2D _white`, `GuiStyle Style`, a `SpriteBatch? _batch`, a `Pointer? _pointer`, and a
  `List<Rect> _blocked`.
- `Begin(batch, pointer)`: store both, `_blocked.Clear()`.
- Every Panel/Swatch/Button records its rect into `_blocked` (so it gates) before drawing.
- Draw via `GuiDraw.Fill`/`Border`; guard every draw with `if (_batch is null) return;` AFTER recording the
  rect + computing interaction, so headless tests still get capture + return values.
- `Button` interaction: `bool clicked = enabled && _pointer!.IsTapIn(rect);` fill/border/text per the rules;
  centered label via `font.Measure`.
- `PointerCaptured` => `_pointer is not null && _blocked.Any(r => r.Contains(_pointer.PressOrigin))`. (Match
  the semantics of `Pointer.IsBlocked`; simplest is to reuse `Pointer.BlockRegion`+`IsBlocked` instead of a
  private list — acceptable and preferred if cleaner. Decide in implementation; keep one source of truth.)

Write the headless tests FIRST (mirror `ScrollablePanelTests` input helper). Cover every bullet in the spec's
Testing section: tap inside returns true on release only; press-origin-outside returns false; disabled never
returns true but captures; PointerCaptured true/false by press-origin; a Panel reserves capture.

Run `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. All green. Commit.

### Task 3: GuiSample immediate-mode demo

**Files:** Modify `GuiSample` (add an immediate-mode demo screen/section; wire a key to reach it or add it to
the existing sample's screen rotation — follow GuiSample's existing structure).

- Construct one `GuiSurface`, and each frame inside the sample's `batch.Begin(viewport)`/`End()` call
  `ui.Begin(batch, pointer)` then draw: a titled Panel, a couple of Labels (Left/Center/Right aligned), a row
  of Swatches, an enabled Button, a disabled Button, and a selected Button; flip a bool on the enabled
  button's click to show state change.
- Build the sample (`dotnet build GuiSample/GuiSample.csproj`). Do not run it headless-blocked; a build is
  enough here (the controller will visually verify). Commit.

### Task 4: (controller-run, not a subagent) Release ritual 5.17.0-experimental

Bump `<KhaozEngine5xVersion>` to 5.17.0-experimental, newest-first CHANGELOG entry under a new
`## 5.17.0-experimental` heading (KhaozEngine.Gui additive: GuiSurface immediate-mode), `dotnet pack -c Release
-o ./local-feed` for the 5.x packages, commit, then merge/tag/push handled at finish.
