# KhaozEngine.Game.Render3D (5.x)

The 3D integration for `KhaozEngine.Game`, split out so a 2D game pulls **no 3D renderer**. Three pieces:

- **`GameApp3D : GameApp`** - a `GameApp` that builds a `Render3DSurface` and drives the 3D pass in the
  `OnRenderWorld` seam (before the 2D HUD). Subclass it and override `OnDraw3D(Scene3D)`.
- **`IGameScene3D`** - a `GameScene` implements this (in addition to deriving `GameScene`) to submit a 3D world
  pass. Keeps 3D out of the base `GameScene`.
- **`SceneManager.Draw3D(scene)`** extension - draws the visible scenes that implement `IGameScene3D`, the same
  visible set as `Draw2D`.

```csharp
sealed class MatchScene : GameScene, IGameScene3D
{
    public void OnDraw3D(Scene3D scene) { /* submit the board + entities */ }
    public override void OnDraw2D(SpriteBatch batch) { /* HUD */ }
}

// in the frame loop, between scene.Begin() and the 2D pass:
scenes.Draw3D(scene);
```

## ReplicatedCharacterAnimators

A render-free bridge that drives one skinned-character brain per networked entity from a per-frame list of
`CharacterSample`. By default it derives planar speed / facing / air state from the position stream (windowed, so a
plateauing position does not strobe the state). Richer constructors let the local player pass exact signals it
already knows: `(id, position, isLocal, grounded, verticalVelocity)` for the exact grounded flag + vertical
velocity, and `(id, position, isLocal, grounded, verticalVelocity, planarSpeed)` to ALSO drive the idle/walk/run
state and clip-speed sync off the exact planar speed (`WorldClient.LocalHorizontalSpeed`) instead of the
finite-differenced render position - so the local avatar's animation does not flicker walk&lt;-&gt;idle when it
decelerates to a stop. Facing still follows the derived heading. See `docs/USING-KHAOZENGINE.md`.
