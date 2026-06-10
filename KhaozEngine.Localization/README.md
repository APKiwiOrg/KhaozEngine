# KhaozEngine.Localization

Game-agnostic localization helper. Pure BCL, no MonoGame dependency.

`LocalizationManager` does two things:

- **Discover supported cultures** from a `ResourceManager` you inject (the cultures that
  actually have a satellite resource set), always including the invariant culture.
- **Set the current thread culture** (both `CurrentCulture` and `CurrentUICulture`).

```csharp
using System.Resources;
using KhaozEngine.Localization;

// Point it at YOUR game's resources (your assembly owns the satellite .resx files):
var rm = new ResourceManager("MyGame.Core.Localization.Resources", typeof(MyGameMarker).Assembly);
var loc = new LocalizationManager(rm);

List<CultureInfo> cultures = loc.GetSupportedCultures();

LocalizationManager.SetCulture("en-US");
// Want a fallback instead of an exception on empty input? Do it at the call site:
LocalizationManager.SetCulture(code ?? LocalizationManager.DefaultCultureCode);
```

`SetCulture` throws on null/empty input. `DefaultCultureCode` is `"en-US"`.
