# SnapshotTool

The canonical reference for the KhaozEngine snapshot harness: a runnable one-2D-one-3D acceptance
sample, and the exact shape every game's `tools/SnapshotTool` mirrors. The whole `Program` is
`SnapshotHost.Main(args, Register)`, so the engine owns arg parsing, the `<temp>/ke-snapshots` default
output dir, and the summary, and the tool is just the shots.

See [`../KhaozEngine.Snapshot/README.md`](../KhaozEngine.Snapshot/README.md) for the harness API
(`SnapshotRunner` / `SnapshotHost`, and the `Shot3D` extension in `KhaozEngine.Snapshot.Render3D`).

## Requirements

- .NET 10.
- A GPU device (Veldrid/Metal). The captures run offscreen (no window) but still need a real device, so
  this runs on a dev box, not in headless CI.

## Run

```bash
dotnet run --project SnapshotTool -- /tmp/ke-snapshot-demo
```

The argument is the output directory. With no argument it writes to `<temp>/ke-snapshots`. Each shot
logs its path; a `done -> <dir> (N shots)` line ends the run. Writes `hello2d.png` (flat rects) and
`hello3d.png` (a lit box).
