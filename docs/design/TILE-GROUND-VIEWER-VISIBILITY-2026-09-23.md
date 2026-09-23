# Per-viewer ground item visibility

Grimhollow needs a book dropped on the floor to remain visible to its character while no other player receives the ground entity. The tile server currently serves every ground item in a viewer's plane and area of interest. The game can authorize pickup but cannot prevent another client from seeing the entity or its item ID.

## Decision

`TileWorldServerConfig` gains an optional synchronous `GroundItemVisibleToSlot` predicate. It receives the authenticated viewer slot and the ground item's net ID. A null predicate preserves current public visibility. The server applies a supplied predicate to ground items in the per-viewer interest set before snapshot and delta projection. The callback runs on the simulation tick and must use stable server-owned facts. A false answer excludes the entire entity and all its components. A later true answer enters through the normal interest delta. A later false answer leaves through the same delta.

The engine owns only per-viewer replication. It never stores account IDs, classifies items, or authorizes pickup. A consumer that marks a drop private must also guard its own pickup path. Existing public ground items, other entities, plane filtering, and area-of-interest behavior keep their current wire shape. The optional callback is configured before the server starts serving clients.

## Why this boundary

| Approach | Privacy | Reuse | Consumer fit | Delivery | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Engine interest predicate | 10 | 9 | 8 | 5 | 32 |
| Game-only fake ground marker | 5 | 2 | 3 | 8 | 18 |

Scores are 1 to 10, higher is better. A client-only marker would require a second ground targeting and rendering route and would still need a server pickup gate. The interest predicate reuses the existing ground entity, snapshot delta, click, and render paths.

## Proof

Headless two-seat tests place a private ground item inside both players' plane and interest radius. The owner receives it, the other seat receives neither the item nor an instance component, and the other seat receives no entry after reconnecting or moving into range. A policy change from visible to hidden sends removal, and the reverse sends entry. A public item remains visible to both. The test reads the actual client mirrors from snapshots, not only the server's internal interest set.

Grimhollow is the first consumer. Its companion design is `docs/design/BOOKSHELF-DESIGN-2026-09-23.md` in the Grimhollow repository.
