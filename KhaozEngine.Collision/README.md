# KhaozEngine.Collision

Game-agnostic 2D collision + broadphase primitives.

- **`CircleCollision`** - circle/circle overlap (`Intersects`) with optional per-pixel precise refinement
  (`DoCollidersCollide`). Touching circles count as intersecting (`distanceSquared <= combined^2`).
- **`SpatialHashGrid`** - uniform spatial hash for broadphase candidate queries. Rebuild each tick with
  `BeginRebuild(capacity)` + one `Add(index, position, radius)` per item, then `QueryCandidates` /
  `GetQueryIndex`.
- **`GridRay`** - exact 2D grid line-of-sight / segment-raycast. `IsClear(from, to, cellSize, blocks)` walks
  every cell the segment touches (Amanatides&Woo 4-connected supercover, not fixed-step sampling) and returns
  true when none satisfy the caller's `blocks(x, y)` predicate; endpoint cells are excluded by default
  (`includeEndpointCells` to opt in). `Trace(...)` enumerates the touched cells. Decoupled from game types,
  deterministic, allocation-free on the hot path.
- **`ICircleCollider`** (`Position` + `Radius`) and **`IPreciseCircleCollisionTarget`**
  (`IntersectsCircle`) - implement on your entity type.

## Determinism

The float math and iteration order are deterministic and intended for lockstep sims:
`DistanceSquared <= combined^2`, cell coordinate = `(int)MathF.Floor(world / cellSize)`, queries walk
cells Y-outer / X-inner, and each cell chain is LIFO (head insertion). Rebuild items in a fixed order
(e.g. ascending index over live rows) to reproduce a stable query order.

```csharp
var grid = new SpatialHashGrid(cellSize: 64f);
grid.BeginRebuild(entities.Count);
for (int i = 0; i < entities.Count; i++)
    if (entities[i].Alive) grid.Add(i, entities[i].Position, entities[i].Radius);

int n = grid.QueryCandidates(center, radius);
for (int q = 0; q < n; q++)
{
    int i = grid.GetQueryIndex(q);
    // precise per-pair test against entities[i]
}
```

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
