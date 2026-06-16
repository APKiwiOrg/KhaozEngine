# P1 batch 1 — Gui styling unification + Scene3D mesh lifecycle (5.29.0-experimental)

First P1 release from `docs/ENGINE-AUDIT-5x-2026-06-16.md`: two contained, locally-verifiable fixes. No
cross-platform/decision dependency.

## Part A — Gui styling unification (audit P1#6)

Problem: the retained `Button` hardcodes its own `Color`/`HoverColor`/`PressColor`/`TextColor` fields, while the
immediate `GuiSurface.Button` uses `GuiStyle` (with disabled/selected states). The colours are duplicated by
hand and documented as "matching" — they will drift. The retained `Button` also does NOT reserve its rect, so a
game mixing retained widgets with world input can get click-through (the exact bug the press-origin invariant
was meant to kill).

Fix — single source of truth for button visuals + state:
- Add an internal shared helper `GuiDraw.DrawButton(SpriteBatch batch, Texture2D white, SpriteFont font, Rect
  rect, string label, in GuiStyle style, bool enabled, bool selected, bool hover, bool press)` that draws the
  fill (priority: `!enabled`→DisabledFill, `selected`→SelectedFill, press→Press, hover→Hover, else Fill),
  border (selected→SelectedBorder else Border, thickness from style), and centred label (enabled→Text else
  DisabledText). Have BOTH `GuiSurface.Button` and the retained `Button.Draw` call it (so there is ONE visual
  implementation).
- Retained `Button`: replace the four colour fields with `public GuiStyle Style = GuiStyle.Default;` and add
  `public bool Enabled = true;` + `public bool Selected;`. `Draw` calls `GuiDraw.DrawButton(...)`. `Update`:
  reserve the rect with `pointer.BlockRegion(Bounds)` (so the retained path gets the click-through gate too),
  and only fire `OnClick`/return true when `Enabled`. Keep the `Update`/`Draw` two-call shape + `OnClick`.
- Update `GuiSample` (and any other retained-Button consumer) for the API change (colour fields → `Style`).
- Tests: retained `Button.Update` reserves its bounds on the pointer (a subsequent `pointer.IsBlocked(center)`
  is true); a disabled Button never fires; the press-origin invariant still holds. (Mirror the existing
  `ButtonTests`/`GuiSurfaceTests` headless input idiom.)

## Part B — Scene3D mesh lifecycle (audit P1#7b)

Problem: `Scene3D.LoadMesh` appends to `_meshes` and returns the index as the `MeshHandle`; there is no
`UnloadMesh`, and the handle is a raw append-only index — so a game that streams/swaps content leaks GPU
buffers and can never free a slot without invalidating later handles.

Fix — slot-map handles with generations:
- `MeshHandle` gains a generation: `readonly struct MeshHandle { int Index; int Generation; }` (the public ctor
  stays for opaque use; consumers don't construct it meaningfully). A `default`/invalid handle has Generation 0;
  valid handles start at Generation 1.
- `Scene3D` mesh storage becomes a slot list: each slot holds the `Mesh` (Vb/Ib/IndexCount) or is free, plus a
  generation counter, plus a free-list (stack of freed indices). `LoadMesh` reuses a free slot if available
  (bumping that slot's generation) else appends; returns `new MeshHandle(index, slot.Generation)`.
- New `public void UnloadMesh(MeshHandle h)`: validates the handle (Index in range AND `Generation` matches the
  slot's current generation — a stale/invalid handle throws `ArgumentException` or is a no-op; pick throw for a
  double-free, no-op for `default`), disposes the slot's Vb/Ib, marks the slot free, bumps its generation, and
  pushes the index to the free-list.
- The draw path (`RenderInternal` grouping + `Scene3DBinder` lookups) resolves a handle to its slot by Index and
  validates the generation; a stale handle (mesh unloaded) is skipped (don't draw a freed mesh). `Scene3D.Draw`
  with a stale handle: skip silently (a destroyed entity may linger a frame) — document it.
- Factor the pure slot-map allocation logic (allocate index, free index, validate generation — no GPU) into a
  small internal helper so it's HEADLESS-unit-testable: load N → handles distinct; unload one → its handle
  becomes invalid (generation mismatch) while others stay valid; a reloaded slot reuses the freed index with a
  new generation; double-unload is rejected; `default` handle is invalid.
- Keep `Scene3D.Dispose` freeing all live slots.

## Files / Release
- Modify `KhaozEngine.Gui/Button.cs`, `KhaozEngine.Gui/GuiDraw.cs`, `KhaozEngine.Gui/GuiSurface.cs`, `GuiSample`.
- Modify `KhaozEngine.Render3D/MeshHandle.cs`, `Scene3D.cs` (+ the binder/grouping lookups if they assume a bare
  index). New internal slot-map helper + its test; extend `ButtonTests`.
- Bump 5.28.0 → 5.29.0-experimental, CHANGELOG, pack 7 pkgs.

## Verification
- `dotnet test` green (default; report count). New headless tests for both parts.
- `KE_GPU_TESTS=1 dotnet test --filter FullyQualifiedName~Golden` — BOTH goldens pass pixel-identical (the
  Button visual unification must not change the 2D golden — but the 2D golden uses bare SpriteBatch, not
  retained Button, so it's unaffected; the 3D golden must stay identical after the MeshHandle/slot-map change).
  Do NOT re-bake.
- Controller eyeballs the GuiSample (retained buttons) + a 3D scene that loads/unloads a mesh.
