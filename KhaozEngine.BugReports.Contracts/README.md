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
contents or enforce the bounds. `BundleValidator` does, as the next section describes.

## Bundle validation

`BundleValidator.Validate(zip, maxBytes)` checks a bundle against the version 1 contract and returns
`BundleValidationResult(IsValid, Error, Manifest)`. `maxBytes` defaults to
`BugReportLimits.MaxBundleBytes`. Relays run it on untrusted uploads, so it never throws for any
input bytes, never inflates an entry and never reads past an entry's declared length plus one byte.
A non-seekable stream is copied up to `maxBytes + 1` bytes first.

Checks run in this order and the first failure wins:

1. Bundle size.
2. Zip structure.
3. Entry count.
4. Per entry, in zip order: name whitelist, duplicate name or both screenshot formats, encryption,
   then compression. No entry stream is opened before these pass.
5. The sum of entry lengths against the archive length, before any entry is hashed.
6. Manifest presence, then manifest parse.
7. Manifest fields: format version, report id, game id, description, category, then kind.
8. The listed entry names against the zip entries other than the manifest, as a set.
9. Size, then SHA-256, per entry in zip order.
10. Image headers, in zip order.
11. The `clip.mp4` file type box.
12. Sections, in zip order.
13. The clip index.

`Error` is either the code alone or the code, a colon, a space and an entry name. Every `BundleErrors`
constant:

| Code | Meaning |
|---|---|
| `too_large` | The bundle is over `maxBytes`. |
| `not_a_zip` | The bytes are not a readable zip. With an entry name, that entry could not be read. |
| `too_many_entries` | The archive has more than 128 entries. |
| `entry_not_allowed` | The name is outside the `BundleEntryNames` whitelist. |
| `duplicate_entry` | A name appears twice, or `screenshot.png` and `screenshot.jpg` are both present. The pair always names `screenshot.jpg`. |
| `encrypted_entry` | The entry is encrypted. |
| `compressed_entry` | The entry is not stored, because its compressed and uncompressed lengths differ. |
| `overlapping_entries` | Entry lengths sum past the archive length, so central directory records share stored bytes. |
| `manifest_missing` | There is no `manifest.json`. |
| `manifest_invalid` | The manifest does not parse, holds a null entry or names an undefined kind. |
| `format_version` | `formatVersion` is not `1`. |
| `report_id_invalid` | `reportId` fails `ReportIds.IsValid`. |
| `game_id_invalid` | `gameId` does not match `[a-z0-9][a-z0-9-]{0,31}`. |
| `description_invalid` | `description` is blank or longer than 4000 UTF-16 units. |
| `category_invalid` | `category` is not 1 to 64 characters long. |
| `entry_mismatch` | A name is listed but absent, present but unlisted, or listed twice. |
| `size_mismatch` | The entry's bytes differ in length from its manifest size. |
| `hash_mismatch` | The entry's SHA-256 differs from its manifest hash. |
| `image_invalid` | A PNG or JPEG header does not parse or an edge is outside 1 to 2560 pixels. |
| `media_invalid` | `clip.mp4` does not carry `ftyp` at bytes 4 to 8. |
| `section_invalid` | A section is not a JSON object within depth 64. |
| `sections_too_large` | Section entries total more than 1 MiB. |
| `clip_index_invalid` | The clip index is missing while frames or `clip.mp4` exist, does not parse, holds a null frame, has more than 120 frames, has negative, decreasing or non-finite timing, or names entries wrongly. |

Clip index entries follow one of two shapes. With JPEG frames, frame `i` names `clip/<iii>.jpg`, that
entry exists with the index's width and height, and every JPEG frame has an index row. With
`clip.mp4`, every frame names `clip.mp4` and no `clip/<iii>.jpg` entry may exist.

Entry names in `Error` come from the upload, so they are sanitised. Control characters, including
CR, LF, tab, DEL and the C1 range, become `?`. A name longer than 64 characters is cut to its first
61 characters followed by `...`. A surrogate pair split by the cut becomes `?`.

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
