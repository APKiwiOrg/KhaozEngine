# Map editor refresh implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkboxes for tracking.

**Goal:** Deliver the approved shared editor refresh with stable navigation, readable terrain feedback, discoverable visibility and verified responsiveness.

**Architecture:** Keep map documents and commands intact. Add cohesive navigation, view-panel and brush-overlay components, with small scene integration calls. Filter prop draw submission using editor view state rather than rebuilding terrain.

**Tech Stack:** C# net10.0, engine InputState and GUI, native engine rendering backends, xUnit.

**Spec:** [Map editor refresh design](MAP-EDITOR-REFRESH-DESIGN-2026-09-20.md).

## Global Constraints

- Worktree: `/Users/antonio/KhaozEngine/.worktrees/map-editor-refresh`, branch `feature/map-editor-refresh`.
- Read AGENTS.md and relevant sections of docs/CONTRIBUTOR-RULES.md before edits.
- Input comes through InputState, InputManager and Pointer. Only AppWindow touches raw input APIs.
- New behavior gets headless tests under KhaozEngine.Tests.*. Use Release and meaningful red/green evidence.
- No baseline growth or exemptions. New behavior belongs in new cohesive types. MapEditorScene.cs must shrink or remain under its existing ratchet.
- Visibility never dirties maps, changes serialization, or creates undo history. Hidden elements cannot intercept picking.
- Middle-mouse captures a terrain pivot at press and holds it for the gesture. Navigation cannot mutate documents.
- New UI prose uses StringId catalog resolution. No em dash, en dash or prose semicolon characters.
- Commit explicit owned paths. Preserve unrelated edits. No stash, subagents, releases, main merges, pushes or cleanup by implementers.
- The orchestrator owns this plan, design/index updates, version/changelog, full integration, GPU CI and consumer reconciliation.
- Each implementer reads only its extracted brief plus the spec, writes a report with commands and exit codes, and commits its implementation.

## Task 1: Navigation and gesture ownership

**Files:** Create EditorNavigationController.cs and MapEditorScene.Navigation.cs under KhaozEngine.MapEditor. Modify MapEditorScene.cs only to replace camera policy with the new partial and gate tools. Add MapEditorSceneNavigationTests.cs and EditorNavigationControllerTests.cs in KhaozEngine.MapEditor.Tests/MapEditor. Update focused navigation prose in KhaozEngine.MapEditor/README.md and docs/USING-KHAOZENGINE.md.

**Interfaces:** The controller consumes FlyCamera3D, InputState, viewport eligibility and a terrain hit supplied by the scene. It exposes IsNavigating, Pivot and Cancel(). The scene exposes a private NavigationOwnsPointer boolean for subsequent overlay and view-panel tasks. Keep the generic FlyCameraController unchanged for other consumers.

- [ ] Write red tests for stable pivot, pan mode captured at press, dolly limits, no acquisition over chrome/text, no reacquisition after cancelled held button, focus loss, modal cancellation, no sculpt/selection mutation during navigation, and right-mouse fly coexistence.

Use InputState's headless constructor and the existing MouseFrames fixture style. Core assertions include:

```csharp
Assert.Equal(pivotAtPress, navigation.Pivot);
Assert.False(navigation.IsNavigating); // after focus loss
Assert.Equal(beforeHash, document.ComputeHash()); // use the existing hash API in scene fixture
```

The hash line expresses the invariant. Resolve the exact existing MapDocumentHash fixture API rather than adding a production helper solely for the test.

- [ ] Run `dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter FullyQualifiedName~Navigation` and retain expected failures.
- [ ] Implement a new controller with middle orbit, Shift+middle pan, wheel dolly and right-button fly. Orbit speed is 0.005 radians per pixel, pitch clamped to avoid poles, initial fallback distance 25 metres, dolly distance bounded 0.5 to 100000 metres. Validate finite inputs and keep all camera coordinates finite. Hold pivot and pan/orbit mode for the gesture. A miss uses the last pivot or camera-forward fallback. Wheel changes distance, not fly speed. Keep fly speed independently adjustable through explicit settings.

```csharp
// Gesture arbitration at the scene boundary:
// acquire only in viewport, with no focused field, modal or tool gesture
// NavigationOwnsPointer suppresses all editor pointer edges until release
// cancel clears capture and requires a fresh press before reacquiring
```

- [ ] Add focus-selection on unmodified F after verifying no existing binding. Compute a framing pivot and distance for supported selections. No selection is a no-op. Do not consume F while typing. Movement keys only control fly while right-button navigation is acquired.
- [ ] Move camera-specific state and methods into the new cohesive partial if required to stay under the ratchet. Wire camera cancellation before early modal returns. Tests exercise real scene UpdateCamera and BuildFrameInput flow, not controller-only calls.
- [ ] Update navigation help and document wheel/pan/fly behavior. Run the focused tests, then the complete MapEditor Release project. Commit explicit paths and report red/green commands, counts and SHA.

## Task 2: Persistent visibility and draw-only filtering

**Files:** Add EditorViewPanel.cs, EditorPropCategory.cs and MapEditorScene.Visibility.cs. Extend EditorVisibility.cs and MapEditorOptions.cs. Modify ViewportWorld.cs and its downstream Terrain.Render3D prop draw seam as needed. Add EditorViewPanelTests.cs and visibility/render filtering tests. Update visibility and rebuild semantics in package README and consumer guide.

**Interfaces:** Consume NavigationOwnsPointer from Task 1 for panel/input arbitration. Produce `EditorVisibility.TerrainOnly`, `ShowAll()`, `GetCategory(EditorPropCategory)` and `SetCategory(EditorPropCategory, bool)`. EditorPropCategory contains OtherProps, Trees and Rocks. Expose an optional consumer callback on MapEditorOptions resolving kit identity to category, defaulting to OtherProps. Do not infer categories from names. All downstream draw and pick gates consult effective visibility.

- [ ] Write failing tests for temporary TerrainOnly restoration, editing underlying choices while soloed, ShowAll, hidden selection picking, no map hash/undo changes, and zero RebuildWorldForVisibility calls when a layer toggle changes. Exercise toggles while sculpting and while a selection inspector is active.

```csharp
var visibility = new EditorVisibility();
visibility.SetGroup(VisibilityGroup.Water, false);
visibility.TerrainOnly = true;
visibility.TerrainOnly = false;
Assert.False(visibility.GetGroup(VisibilityGroup.Water));
```

- [ ] Run the new visibility tests in Release and retain the expected red result.
- [ ] Implement the visibility model with an overlay mask for TerrainOnly. ShowAll clears hide overrides and solo mode. Preserve existing group/layer/per-element behavior, including rename and reorder semantics.
- [ ] Create a permanently reachable View button and independent panel with Terrain Only, Show All, categories, water, marker groups and named layers. Reuse engine controls and layout primitives. The panel must neither replace nor destroy the active tool inspector. Include it in chrome hit testing and keyboard focus routing. All new labels resolve through the catalog.
- [ ] Trace per-kit/per-layer identity from MapDoc to prop submission. The exact path is ViewportWorld.BuildPropLayers -> Scene3DChunkSink.Draw -> PropClusterRenderer.Draw -> PropRenderer.Emit/EmitParts. AssetEntry.Category is explicit authored metadata, but ViewportWorld.KindCategories also has a filename fallback that must not be used to classify trees or rocks. Use the callback or explicitly authored category values only. Mixed-category HLOD clusters must fall back to visible individual submissions while filtered, not display a merged mesh containing hidden kinds. Retain identity in the renderer-facing batch metadata and filter before draw submission, including textured variants and companions. Build every layer once and stop invoking world rebuild for visibility-only changes. Terrain streaming, placement cache and loaded assets remain intact. Implement authored placement picking with the same category gates used for drawing.

```csharp
// At submission, not generation:
// if (!visibility.GetLayer(layerName) || !visibility.GetCategory(category)) skip batch
// authored placements use the same effective category predicate during ray picking
```

- [ ] Keep legacy APIs compatible with default-visible optional filters. Read docs/DEPENDENCY-SEAMS.md before changing a dependency edge. Do not introduce a dependency from terrain rendering onto MapEditor. Pass neutral draw predicates or metadata at the existing boundary.
- [ ] Run MapEditor and affected terrain rendering headless tests in Release. Update docs and commit explicit paths. Report the exact filter API for later benchmark and consumer tasks.

## Task 3: Terrain overlay readability

**Files:** Add SculptBrushOverlay.cs and focused scene rendering partial if required. Modify SculptCursor.cs and MapEditorScene.Sculpt.cs. Add SculptBrushOverlayTests.cs and GPU pixel tests under the appropriate MapEditor/Render test project. Update sculpt/overlay documentation.

**Interfaces:** Consume NavigationOwnsPointer and effective visibility. Reuse the brush controller's live field, radius, brush and stroke state. Keep brush mathematics and stroke undo behavior unchanged. Expose pure geometry generation into caller-owned buffers so tests and rendering use the same shape.

- [ ] Write failing tests for outer footprint and falloff guide on sloped terrain, centre marker, clipped document bounds, invalid/non-finite samples, modal/chrome/navigation suppression, operation states and bounded buffer use.

```csharp
// Tests compare generated vertices to the same field used by sculpting.
Assert.All(vertices, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
// A cancelled or navigation-owned frame emits no valid brush footprint.
Assert.Equal(0, countWhenNavigating);
```

- [ ] Run focused tests in Release and retain expected failures.
- [ ] Implement outer and falloff rings plus a centre cross with consistent screen-space thickness. Use bounded reusable geometry and the existing overlay drawing facilities. Sample enough terrain points to follow slopes, clamp to document bounds, and do not create a valid footprint for a missed pick. Use an operation label and distinct hover/active/invalid presentation, not colour alone. Add StringId entries for labels.
- [ ] Add GPU verification that draws the actual overlay geometry at near/far views and across a slope, asserts visible pixels and suppression, and can save a PNG for inspection. Use absolute pixel assertions where possible to avoid self-authored golden circularity. Respect GPU environment gates and native lifecycle collection rules.
- [ ] Run focused headless and locally supported GPU tests, preserve images under the task scratch directory, update docs and commit. Report commands, skips, tested backend and image paths. The orchestrator dispatches the full backend matrix.

## Task 4: Reproducible responsiveness measurement

**Files:** Add focused editor benchmark/test harness in KhaozEngine.MapEditor.Tests/MapEditor and a reproduction recipe in docs/design alongside the refresh design. Change only measured hot paths in relevant editor components. Avoid unrelated rendering refactors.

**Interfaces:** Exercise Task 1 navigation, Task 2 visibility and Task 3 geometry through their real policy components and scene integration fixtures. Measurements must identify commit, runtime, workload and machine. Use a copied representative Ruinborne map for real-map observations without modifying source content.

- [ ] Establish deterministic camera/input sequences for idle, orbit, pan, sculpt, visibility changes and undo, with fixed viewport and warm-up. Record median and p95 duration, allocation bytes and rebuild counts. A CPU-only harness must be labelled CPU-only and cannot claim frame-time or rendered stroke latency.
- [ ] Add correctness assertions separately from timing thresholds so CI is not flaky. Visibility toggles must cause zero terrain/scatter rebuilds and navigation must leave map hash/history unchanged.

```csharp
long before = GC.GetAllocatedBytesForCurrentThread();
// Execute the warmed deterministic operation sequence.
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Assert.Equal(0, rebuildsForVisibilityOnly);
```

- [ ] Compare the original implementation at the branch base where equivalent actions exist. Reproduce workload and configuration. Record unsupported comparisons honestly. If measuring requires a windowed consumer launch, do not launch it: provide one copy-paste command with inline environment and retain the automated headless/renderer evidence.
- [ ] Optimise only demonstrated repeated allocations or recomputation in the changed paths, with regression tests. Keep geometry capacity bounded and avoid refilling unchanged visibility lists each frame where evidence supports caching.
- [ ] Run the covering Release tests, commit the harness and measurement recipe/results without machine-specific personal paths or secrets. Report remaining manual measurements explicitly.

## Task 5: Integration, release preparation and consumer adoption

**Owner:** Orchestrator, with a bounded consumer implementer and final task reviewer when needed.

**Files:** Directory.Build.props, CHANGELOG.md, guarded version declarations, full Markdown sweep, design/index statuses. Consumer adoption occurs in a separate Ruinborne worktree under its own instructions.

- [ ] Review all implementation task diffs and test evidence. Run full Release build and tests with Category!=LiveSocket. Run the whole-tree dash, prose, size, agent-instruction and doc-version guards. Preserve actual exit codes in logged commands.
- [ ] Fetch current main/tags, ride any staged version or select one free additive minor. Put changelog and version in the same commit. Update all guarded declarations and stale editor controls/visibility descriptions across Markdown.
- [ ] Run scripts/pack-local-feed.sh and scripts/check-local-feed.sh. Push the feature branch and dispatch cross-platform-gpu.yml against that branch with all three backend legs. Inspect actual new overlay PNGs and verify the full matrix before merging rendering changes.
- [ ] Read Ruinborne's rules and current pin, create an isolated matching consumer worktree, then use its existing vendor/repin machinery to adopt the built engine. Add explicit tree/rock category mapping from existing authored asset metadata. Evict the staged pin from the NuGet cache before validating the consumer to prevent stale bytes. Do not alter maps.
- [ ] Run the consumer's required build/tests and headless editor startup checks. Provide one final manual command launching its editor from the correct worktree or merged main. Do not launch the windowed client automatically.
- [ ] Run a whole-branch spec and quality review. Fix verified findings through a subagent and scoped re-review. Reconcile concurrent main changes in the feature branch and rerun affected checks, then merge and push per standing authorization. Never tag on initiative.
- [ ] Mark design/plan status accurately, close only issues actually resolved, preserve measurement limitations, and report delivery with manual validation and pass/fail next steps.
