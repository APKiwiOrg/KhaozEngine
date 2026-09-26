# macOS clipboard interop paydown

Approved under the 2026-09-26 oldest-backlog pass and implemented for staged 20.6.1 on
2026-09-27. This is one cohesive slice of [#259](https://github.com/APKiwiOrg/KhaozEngine/issues/259).

## Problem

`KhaozEngine.Platform/ClipboardInterop.cs` is 824 lines and is frozen in `.filesize-baseline`. It owns four concerns at once. The pure provider and fallback dispatch is tested headlessly, Windows DIB conversion and clipboard writes live there, the Android and iOS reflection bridge lives there, and the macOS Objective-C pasteboard bindings and operations live there. The macOS block alone contains its own constants, native imports, text and PNG entry points, autorelease pool, and pasteboard helpers.

## Decision

Move the complete macOS pasteboard concern into one internal `MacPasteboardBackend` type in `KhaozEngine.Platform/MacPasteboardBackend.cs`. `ClipboardInterop` keeps the platform-neutral dispatch and provider registration, Windows DIB path, and mobile bridge. Its three macOS delegate arguments point to the new backend's `TryGetText`, `TrySetText`, and `TrySetImagePng` methods.

The move preserves the exact P/Invoke declarations, selectors, ownership of the autorelease pool and pinned PNG bytes, exception-to-false behavior, and `OperatingSystem.IsMacOS()` entry guards. There is no new public API, backend registration, package dependency, or change to the fallback order. The new file stays below the 800-line cap. Ratchet the old file's baseline down and remove its entry if it is below the cap.

This is a type boundary, not a split at an arbitrary line. The moved code has one OS API and one resource-lifecycle model. Extracting a general native-clipboard interface would create a seam with no second implementation need, while leaving the Objective-C imports in the dispatch type preserves the current mixed responsibility.

## Verification and release

Existing `ClipboardTests` cover provider precedence, macOS and mobile fallback dispatch, image routing, and Windows DIB bytes without native access. Run the focused tests and full Release suite after the move. The native macOS pasteboard path has no automated live test in this change, since reading or writing the user's clipboard would touch personal state. Its method bodies and import signatures move unchanged, and the Mac runtime check remains a manual follow-up if one is needed.

The 20.6.0 release tag already exists. This package-bearing refactor starts the next free patch version, selected as 20.6.1 after rechecking current main and tags. The version bump and changelog entry belong in the same commit. Update every guarded documentation declaration, run the full guards and `scripts/pack-local-feed.sh`, then reconcile and push `main`. No release tag is created by this work.
