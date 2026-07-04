# KhaozEngine.Platform

Game-agnostic native platform interop. Pure BCL P/Invoke, no MonoGame dependency.

## Application icon (macOS Dock)

`ApplicationIcon.TrySetMacDockIcon(byte[] pngBytes)` sets the running app's macOS **Dock / Cmd-Tab** icon at
runtime from PNG bytes, via `NSApplication.setApplicationIconImage:`. This matters because GLFW cannot set the
Cocoa Dock icon (so `KhaozEngine.Windowing.AppWindow.SetIcon` is a no-op on macOS) and an app launched via
`dotnet run` has no `.app` bundle `.icns` to supply one - so without this it shows the generic document icon.

```csharp
using KhaozEngine.Platform;

bool ok = ApplicationIcon.TrySetMacDockIcon(File.ReadAllBytes("assets/icon.png"));
```

Returns `false` (never throws) off macOS, on null/empty input, or on any Cocoa failure. Call once at startup,
after the window (hence the shared `NSApplication`) exists. Most games never call it directly: `GameApp` does it
automatically from `GameAppOptions.WindowIconPath`, and `AppWindow.SetMacDockIcon` is the windowing-layer wrapper.
Interop is self-contained (its own libobjc `objc_msgSend` + autorelease pool), so it never destabilises the
clipboard path. Windows/Linux have no equivalent runtime Dock icon (their taskbar icon is the GLFW window icon and
the Windows Explorer icon is `<ApplicationIcon>`), so this is a no-op there.

## Windows taskbar identity (AppUserModelID)

`WindowsAppId.TrySetProcessAppUserModelId(string? appId)` sets the running process's explicit Windows
**AppUserModelID** via shell32's `SetCurrentProcessExplicitAppUserModelID`. This is the Windows counterpart to
the macOS Dock icon above: on Windows 10/11 the taskbar groups, pins, and resolves a running window's icon by
the process's explicit AUMID, and a .NET apphost that never sets one gets a process-derived identity that fails
to resolve the window/exe icon - so the running app's taskbar button shows the generic `.exe` placeholder even
though the title-bar icon and the Explorer `<ApplicationIcon>` are correct.

```csharp
using KhaozEngine.Platform;

// Call ONCE at startup, BEFORE the first window is created:
bool ok = WindowsAppId.TrySetProcessAppUserModelId("APKiwi.Nullwake"); // dotted CompanyName.ProductName
```

Returns `false` (never throws) off Windows, on a null/empty id, or on a failed shell call. Must run before the
process creates its first window (that is when the taskbar button is keyed). Most games never call it directly:
set `GameAppOptions.AppUserModelId` and `GameApp` calls `AppWindow.TrySetProcessAppUserModelId` (a windowing-layer
forwarder) before creating the window. macOS/Linux have no equivalent taskbar-identity call, so it is a no-op
there.

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

- **Text get/set:** a registered window/GLFW text provider first, then a macOS `NSPasteboard` fallback,
  then the mobile bridge on Android/iOS. `KhaozEngine.Windowing.AppWindow` registers the GLFW provider at
  startup, so a windowed game has a working text clipboard on Windows, Linux, and macOS. A windowless or
  headless consumer registers no provider: on macOS it still falls back to `NSPasteboard`, but on
  Windows/Linux text get/set returns the empty/`false` result. (The inherited SDL2 text path was removed:
  it needed an `SDL_Init` the GLFW host never calls, so it did nothing on the shipped runtime.)
- **PNG image:** macOS `NSPasteboard` and the mobile bridge. Windows is intentionally not attempted for
  PNG (no reliable image-paste path), so it returns `false`.
- **RGBA32 image:** Windows only, written as a bottom-up `CF_DIB`. Other platforms return `false`.

The `user32` / `kernel32` / `libobjc` entry points are resolved at runtime by the host process. When a
backend (or the GLFW provider) is missing the call falls through and ultimately returns the empty/`false`
result rather than throwing.

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
