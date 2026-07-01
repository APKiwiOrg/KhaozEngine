# KhaozEngine.Snapshot

Headless art/UI screenshot harness for KhaozEngine 2D. A game's screenshot tool should be its
scenes, not capture/encode/write/log boilerplate. `SnapshotRunner` absorbs the pipeline: capture ->
PNG encode (via `KhaozEngine.Imaging`) -> write `<outDir>/<name>.png` -> log the path. Deterministic
output (no timestamps) and window-free, though the underlying capture still needs a GPU device.

- `SnapshotRunner` - the named-shot runner. `Shot2D(name, w, h, clear, draw)` captures via
  `Render2DSnapshot.Capture` and saves. `Save(name, rgba, w, h)` is the shared sink for a buffer
  captured some other way. `Count` tracks shots written, `Done()` emits the summary line.
- `SnapshotHost` - one-call CLI top-level. Resolves the output directory from `args[0]` (falling
  back to a deterministic temp default), builds the runner, runs your registration delegate, prints
  the summary. Your `Program.cs` becomes just the shots.

```csharp
return SnapshotHost.Main(args, shots =>
{
    shots.Shot2D("main-menu", 640, 360, Color.Black, ctx => DrawMenu(ctx));
    shots.Shot2D("hud", 640, 360, Color.Transparent, ctx => DrawHud(ctx));
});
```

2D-only on purpose: this package has NO Render3D dependency, so a 2D game's tooling stays lean.
The `Shot3D` extension lives in `KhaozEngine.Snapshot.Render3D`. Add that package only when a tool
needs 3D shots.
