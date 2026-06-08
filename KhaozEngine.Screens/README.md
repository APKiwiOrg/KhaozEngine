# KhaozEngine.Screens

A game-agnostic screen stack for MonoGame. Routes input top-to-bottom with
`receivesInput` / `PassUpdateThrough` / `AlwaysReceivesInput`, supports two consumption policies
(`ConsumeWhenVisible` / `ConsumeWhenHandled`), and drives screen transitions. Depends on
[`KhaozEngine.Input`](https://www.nuget.org/packages/KhaozEngine.Input).

```csharp
var screens = new ScreenManager(input) { ExitRequested = Exit };
screens.Add(new MyScreen());
screens.Update(gameTime);                 // after input.Update(...)
screens.Draw(gameTime, spriteBatch);
```

A screen returns from `Update` whether it consumed input this frame (`true` blocks screens below).

Full docs: [KhaozEngine README](https://github.com/APKiwi/KhaozEngine) and
[the consumer contract](https://github.com/APKiwi/KhaozEngine/blob/main/docs/USING-KHAOZENGINE.md).
