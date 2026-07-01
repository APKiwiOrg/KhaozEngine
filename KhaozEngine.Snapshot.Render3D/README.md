# KhaozEngine.Snapshot.Render3D

The 3D arm of the KhaozEngine snapshot harness. One extension method, kept in its own package
(it depends on Render3D) so a 2D-only game referencing `KhaozEngine.Snapshot` never drags in the
3D renderer. Add this only when a screenshot tool needs 3D shots.

- `SnapshotRunner3DExtensions.Shot3D` - extension on `SnapshotRunner`. Captures a 3D scene via
  `Render3DSnapshot.Capture` (`setup` runs once, `drawFrame` runs per frame, `frames` defaults
  to 1) and saves it through the same encode -> write -> log path as the 2D shots.

```csharp
return SnapshotHost.Main(args, shots =>
{
    shots.Shot2D("hud", 640, 360, Color.Transparent, ctx => DrawHud(ctx));
    shots.Shot3D("arena", 640, 360,
        setup: scene => BuildArena(scene),
        drawFrame: scene => scene.Draw());
});
```

The namespace is `KhaozEngine.Snapshot` either way, so adding the package lights up `Shot3D` on an
existing runner with no code change beyond the reference. MonoGame-free, deterministic, no window
(the capture still needs a GPU device).
