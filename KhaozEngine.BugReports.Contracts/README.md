# KhaozEngine.BugReports.Contracts

BCL-only contracts shared by report clients, headless servers and relays. The package targets the
repository's .NET version and has no project or third-party package dependencies. It is opt-in and
stays outside every umbrella.

## Format and entries

`BugReportFormat.Version` is `1`. `BugReportManifest` records the report id, game id, kind (`bug` or
`crash`), category, description, UTC capture time and ordered `BundleEntry` records. Each entry carries
its name, byte size and a 64-character lowercase SHA-256 hash.

`manifest.json` is the first zip entry and never lists itself. The manifest lists the other entries in
zip order: sections in registration order, screenshot, clip index, then frames.

`BundleEntryNames` permits exactly:

- `manifest.json`
- `screenshot.png` or `screenshot.jpg`
- `sections/<key>.json`, where the key matches `[a-z0-9][a-z0-9._-]{0,47}`
- `clip/index.json`
- `clip/000.jpg` through `clip/119.jpg`
- `clip.mp4`

Use `Section(key)` and `ClipFrame(index)` to build validated entry names. `IsValidSectionKey` and
`IsAllowed` check incoming names without accepting paths outside this whitelist. `MaxClipFrames` is
120. `ClipIndexFrame` carries offset and render time in milliseconds, width, height and entry name.
An MP4 clip uses `clip.mp4` for every frame's entry, with timing in the index.

## Limits

`BugReportLimits` defines the shared bounds:

| Limit | Value |
|---|---|
| Bundle bytes | 16 MiB |
| Entries | 128 |
| Total section bytes | 1 MiB |
| Description | 4000 characters |
| Title | 70 characters |
| Screenshot long edge | 2560 pixels |
| PNG screenshot bytes | 4 MiB |
| Server snapshot bytes | 1 MiB |

These constants describe the wire contract. The records and JSON helpers do not validate bundle
contents or enforce the bounds.

## Ids and relay wire

`ReportIds.NewId()` returns a version 7 GUID as 32 lowercase hexadecimal characters. `IsValid`
requires that canonical form, version 7 and the RFC variant. `ReferenceCode` converts the last 30
bits to six Crockford base32 characters prefixed by `R-`. For example,
`0192f1c2a3b47c4d8e9f0a1b2c3d4e5f` has reference `R-P3TKJZ`.

`RelayRoutes.Intake` is `bug-reports`. `RelayRoutes.ServerSnapshot(id)` validates the id and returns
`bug-reports/<id>/server`. Both routes are relative to the relay base URI. `RelayHeaders` names
`Idempotency-Key` and `X-Report-Service-Key`.

`IntakeAccepted` carries the report id and reference. `RelayError` carries the stable error string.
`ServerSnapshotWire` carries the format version, report id, subject, capture time and a dictionary of
JSON section objects. `ReporterIdentity` carries the subject, display name and nullable provider handle.

## JSON

`BugReportJson` exposes typed UTF-8 serialize and deserialize helpers for the manifest, clip index,
intake acceptance, relay error and server snapshot. Deserializers throw `JsonException` for malformed
JSON, a null root, a missing property or a null value in a non-nullable property. Every constructor
property is required, including the nullable `handle`, which is written as `null`. `Options` exposes the
same read-only source-generated settings, including the reporter identity shape. Copy the options if a
caller needs different settings.

JSON is compact with camelCase property names and camelCase enum strings. Integer enum values are
rejected. Serialization normalizes
`DateTimeOffset` values to UTC. The encoder is `JavaScriptEncoder.Create(UnicodeRanges.All)`, so
non-ASCII text such as `Café` stays literal while HTML-sensitive characters are escaped.

The manifest golden is one line of UTF-8 with no trailing newline:

```json
{"formatVersion":1,"reportId":"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f","gameId":"grimhollow","kind":"bug","category":"Gameplay","description":"Fell through\nthe floor","capturedAt":"2026-10-17T03:04:05.678+00:00","entries":[{"name":"sections/build.json","size":42,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
```

The two-frame clip-index golden uses the same encoding and newline rule:

```json
{"frames":[{"offsetMilliseconds":0,"renderMilliseconds":16.5,"width":960,"height":540,"entry":"clip/000.jpg"},{"offsetMilliseconds":83.25,"renderMilliseconds":41,"width":960,"height":540,"entry":"clip/001.jpg"}]}
```
