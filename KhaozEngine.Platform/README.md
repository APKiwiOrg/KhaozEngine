# KhaozEngine.Platform

Game-agnostic native platform interop. Pure BCL P/Invoke, no MonoGame dependency.

## Clipboard

`Clipboard` is a cross-platform clipboard facade. Each call tries the platform backends in order and
returns a best-effort result; nothing throws.

```csharp
using KhaozEngine.Platform;

string pasted = Clipboard.TryGetClipboardText();        // "" if nothing / unavailable
bool ok       = Clipboard.TrySetClipboardText("hello"); // false if it could not be set

// Images:
Clipboard.TrySetClipboardImagePng(pngBytes);                 // macOS + mobile
Clipboard.TrySetClipboardImageRgba32(w, h, rgbaPixels);      // Windows (CF_DIB)
```

Backend dispatch:

- **Text get/set:** SDL2 first (the clipboard MonoGame's DesktopGL window already owns), then a macOS
  `NSPasteboard` fallback, then the mobile bridge on Android/iOS.
- **PNG image:** macOS `NSPasteboard` and the mobile bridge. Windows is intentionally not attempted for
  PNG (no reliable image-paste path), so it returns `false`.
- **RGBA32 image:** Windows only, written as a bottom-up `CF_DIB`. Other platforms return `false`.

The SDL2 / `user32` / `kernel32` / `libobjc` entry points are resolved at runtime by the host process
(MonoGame.Framework.DesktopGL ships SDL2). When a backend is missing the call falls through and
ultimately returns the empty/`false` result rather than throwing.

### Mobile bridge (Android / iOS)

The mobile backends live in the platform head projects (which reference the OS SDKs), not in this
package. `Clipboard` resolves them by reflection. Point it at your bridge type once at startup:

```csharp
Clipboard.MobileBridgeTypeName = "MyGame.Platform.MobileClipboardBridge";
```

The named type must expose these static methods (public or non-public):

```csharp
static bool TryGetClipboardText(out string text);
static bool TrySetClipboardText(string text);
static bool TrySetClipboardImagePng(byte[] pngBytes);
```

It is resolved across all loaded assemblies on first use and cached. Setting `MobileBridgeTypeName`
again clears the cache so a later assignment is picked up. Leave it `null`/empty (the default) and the
mobile fallbacks are simply skipped.
