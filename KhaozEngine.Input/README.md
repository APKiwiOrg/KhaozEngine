# KhaozEngine.Input

Game-agnostic, headless-testable input for MonoGame. A unified mouse+touch pointer with edge
detection, the `IsTapIn` press-origin invariant (the click-through fix), per-frame region
blocking, drag/scroll/pinch gestures, keyboard + gamepad + menu-navigation, and a
coordinate-transform seam - all behind an injectable `IRawInput`.

```csharp
var input = new InputManager(isMobile: false);
input.Update(rawInput.Read(), IsActive);          // rawInput = new MonoGameRawInput(Window)
if (input.IsTapIn(buttonRect)) { /* click-through-safe tap */ }
```

**Rule:** `MonoGameRawInput` is the only class that may touch the MonoGame input statics. Read all
input through this package; never poll `Mouse`/`Keyboard`/`GamePad`/`TouchPanel` directly.

Full docs: [KhaozEngine README](https://github.com/APKiwi/KhaozEngine) and
[the consumer contract](https://github.com/APKiwi/KhaozEngine/blob/main/docs/USING-KHAOZENGINE.md).
