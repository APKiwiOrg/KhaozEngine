# KhaozEngine.PropSurface.Tool

The `ke-propbake` dotnet tool. Bakes physics data for every prop in a kit manifest: a 3D collision
shape (`.coll`) for each prop, plus a top-surface walkable heightmap (`.surf`) for walkable-solid
props (rocks, logs, buildings). Stamps the manifest and writes the binaries next to the glTF files.
Run as the last kit-ingest step (re-ingest = re-bake). Idempotent: re-running re-bakes and restamps.
Author-time dev tool, not a runtime package.

Install:

```bash
dotnet tool install --global KhaozEngine.PropSurface.Tool
```

## Usage

```bash
ke-propbake path/to/props.manifest.json
```

One positional argument, the kit's props manifest. `-h` / `--help` prints usage. For each prop it:

- loads the glTF (normalized to the entry's `heightMeters`),
- bakes the collision shape and writes `<id>.coll` next to the manifest (kind depends on the mesh:
  compound, triangle-mesh, cylinder, or convex-hull, and trees get a leaning trunk-hull collider),
- stamps `"collisionShape"` on the prop entry,
- for walkable solids, also writes `<id>.surf` (top-surface heightmap) and stamps
  `"surface": true` + `"heightmap"`. Thin blockers get a `.coll` only.

Props with a `collisionProxy` (an authored compound-of-convex proxy glTF) get their `.coll` baked
from the proxy in the render mesh's frame instead of from the render mesh itself.

The `.coll` files feed `PropCollisionLoader` for physics wiring, the `.surf` heightmaps feed the
walkable-surface system in `KhaozEngine.Collision`.

Exit codes: 0 baked, 1 manifest parse/load failure, 2 bad arguments or manifest not found.
Per-prop bake failures are logged and skipped, the rest of the kit still bakes.
