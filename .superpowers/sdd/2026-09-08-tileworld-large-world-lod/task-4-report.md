# Task 4 report: TileWorld prop definitions and immutable snapshots

## Outcome

Task 4 adds opt-in, disjoint `TilePropLayerDefinition` groups and immutable region-plane prop snapshots. Selected
placements are removed from ordinary and animated-foliage draw inputs, sorted by stable document object ID, and
published with a monotonic generation. Ordinary behavior remains unchanged when `PropLayers` is empty.

`ITileLodMeshResolver` is an optional sibling capability implemented by `GltfMeshResolver`. Full handles,
optional LOD handles, and flattened HLOD CPU sources resolve during view construction. A plain
`ITileMeshResolver` remains valid, keeps LOD0, and makes its selected layers HLOD-ineligible. No concrete resolver
downcast was added.

Per-object archetype changes rebuild only the affected loaded region-plane snapshot when prop layers are enabled.
The replacement advances that snapshot's generation and leaves other planes, document objects, picking data,
ground meshes, and collision state untouched.

## TDD evidence

- Initial RED: the focused Debug build failed with CS0246 for missing `TilePropLayerDefinition` and
  `ITileLodMeshResolver`.
- First GREEN: 39 passed, 0 failed, 0 skipped for `TileWorldPropLayerTests|TileObjectPropsTests` in Debug.
- Final focused Debug: 60 passed, 0 failed, 0 skipped.
- Final focused Release: 60 passed, 0 failed, 0 skipped.
- Broader TileWorld Render Debug slice: 269 passed, 0 failed, 5 expected GPU skips.

Focused command:

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Debug --filter "FullyQualifiedName~TileObjectPropsTests|FullyQualifiedName~TileWorldPropLayerTests|FullyQualifiedName~TileWorldViewTests"
```

The same filter passed in Release.

## Behavior pinned

- Default options carry no prop layers.
- The approved 576, 64, 16, 192, 32, and 1.5 metre tree profile retains every value.
- Duplicate archetype selection, invalid distance ordering, negative widths, and negative weld cells fail before
  any scene upload.
- Snapshot placement values are detached from later document and object mutations.
- Selected and ordinary snapshot entries sort by document object ID.
- Current presentation overrides decide layer membership and placement transforms.
- Override and clear operations replace only the affected region-plane and advance its generation.
- Selected archetypes never reach ordinary `DrawProps` or the animated-foliage split.
- Resolver LOD and flattened CPU methods run during view construction, not during snapshot rebuilds.
- Missing optional resolver capability retains LOD0 and disables HLOD without failing.

## Verification

- Full Release solution build: 0 warnings, 0 errors.
- `scripts/check-file-size.sh --tree`: passed.
- `scripts/check-dashes.sh --tree`: passed.
- `scripts/check-prose.sh --tree`: passed.
- `scripts/check-doc-versions.sh`: passed for engine version 18.36.0.
- `git diff --check`: passed.

## Concerns

- Task 4 publishes resolved snapshots only. Task 5 owns background residency and queueing, while Task 7 owns
  translating snapshots into shared cluster requests and drawing live clusters.
- One missing flattened source makes the whole layer HLOD-ineligible. This keeps every selected archetype on its
  individual representation and prevents a partial merged mesh from creating a visual hole.
