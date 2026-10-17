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
`IsAllowed` check incoming names without accepting paths outside this whitelist. `IsValidGameId` checks
a game id against `[a-z0-9][a-z0-9-]{0,31}`. `MaxClipFrames` is
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
| Server snapshot envelope reserve | 16 KiB |
| Category | 64 characters |
| Game id | 32 characters |
| Section JSON depth | 64 |
| Clip frame offset | 600000 milliseconds |
| Clip frame render time | 60000 milliseconds |

These constants describe the wire contract. The records and JSON helpers do not validate bundle
contents or enforce the bounds. `BundleValidator` enforces the bundle bytes, entries, total section
bytes, description, screenshot long edge, category, game id, section depth and clip timing limits, as the next
section describes. It does not check
the other three:

- `MaxPngScreenshotBytes` is a client capture limit that decides between PNG and JPEG.
- `MaxTitleChars` applies to the issue title, which is not part of the bundle.
- `MaxServerSnapshotBytes` applies to `ServerSnapshotWire`, which is not part of the bundle.

`ServerSnapshotPoster` leaves `ServerSnapshotEnvelopeReserveBytes` for metadata and framing when
finishing server sections. Client sections keep the full `MaxSectionBytesTotal` budget.

## Bundle validation

`BundleValidator.Validate(zip, maxBytes)` checks a bundle against the version 1 contract and returns
`BundleValidationResult(IsValid, Error, Manifest)`. `maxBytes` defaults to
`BugReportLimits.MaxBundleBytes`. Relays run it on untrusted uploads, so it never throws for any
input bytes and never reads more than an entry's declared length plus one byte. A non-seekable stream
is copied up to `maxBytes + 1` bytes first.

Entries must be stored. The validator checks this as equal compressed and uncompressed lengths, not by
compression method. A Deflate entry crafted to pass that test is decompressed only up to the same
bound and then fails its size or hash check unless its output matches the manifest. An entry using a
method the runtime cannot read gives `not_a_zip: <entry>`.

Besides the `BugReportLimits` values above, the validator enforces these bounds:

- 64 KiB each for `manifest.json` and `clip/index.json`, checked by declared length before reading.
- At most 120 clip index frames.
- 1 to 2560 pixels for both edges of every image, and for the index width and height of an MP4 clip.
- Unique property names in every section object.
- Every string value and property name in a section decodes. Valid JSON can still hold invalid UTF-8 or an
  escaped lone surrogate such as `"\uD800"`, which would otherwise fail only when a later reader decodes it.

Checks run in this order and the first failure wins:

1. Bundle size.
2. The end of central directory record, read from the tail before the zip is parsed. Zip64 records,
   sentinel fields, multiple disks, or a directory that does not end at the end record give
   `not_a_zip`. A declared count, or a walk of the directory headers, over 128 entries gives
   `too_many_entries`.
3. Zip structure, then entry count.
4. Per entry, in zip order: name whitelist, duplicate name or both screenshot formats, encryption,
   then compression. No entry stream is opened before these pass.
5. The sum of entry lengths against the archive length, before any entry is hashed.
6. Manifest presence, then manifest length, then manifest parse.
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
| `not_a_zip` | The bytes are not a readable zip, or the end record uses Zip64, sentinels, multiple disks or an inconsistent directory. With an entry name, that entry could not be read. |
| `too_many_entries` | The end record declares, or the central directory holds, more than 128 entries. |
| `entry_not_allowed` | The name is outside the `BundleEntryNames` whitelist. |
| `duplicate_entry` | A name appears twice, or `screenshot.png` and `screenshot.jpg` are both present. The pair always names `screenshot.jpg`. |
| `encrypted_entry` | The entry is encrypted. |
| `compressed_entry` | The entry is not stored, because its compressed and uncompressed lengths differ. |
| `overlapping_entries` | Entry lengths sum past the archive length, so central directory records share stored bytes. |
| `manifest_missing` | There is no `manifest.json`. |
| `manifest_invalid` | The manifest is over 64 KiB, does not parse, repeats a property name, holds a null entry or names an undefined kind. |
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
| `section_invalid` | A section is not a JSON object within depth 64, repeats a property name in any object, or holds a string or property name that does not decode. |
| `sections_too_large` | Section entries total more than 1 MiB. |
| `clip_index_invalid` | The clip index is missing while frames or `clip.mp4` exist, is over 64 KiB, does not parse, repeats a property name, holds a null frame, has more than 120 frames, has negative, decreasing or non-finite timing, an offset over 600000 ms or a render time over 60000 ms, names entries wrongly, or gives an MP4 frame an edge outside 1 to 2560. |

Clip index entries follow one of two shapes. With JPEG frames, frame `i` names `clip/<iii>.jpg`, that
entry exists with the index's width and height, and every JPEG frame has an index row. With
`clip.mp4`, every frame names `clip.mp4` and no `clip/<iii>.jpg` entry may exist.

Entry names in `Error` come from the upload, so they are sanitised. Characters in the control,
format, line separator and paragraph separator categories become `?`. That covers CR, LF, tab, DEL,
the C1 range, bidirectional overrides such as U+202E, zero width characters, and U+2028 and U+2029.
An unpaired surrogate becomes `?` too. A name longer than 64 characters is cut to its first 61
characters followed by `...`, and a surrogate pair split by the cut becomes `?`.

## Shared rules for writers

A bundle writer checks its input through the same helpers the validator uses, so a written bundle
passes validation:

- `ImageHeaders.TryReadSize(mediaType, bytes, out width, out height)` reads the pixel size from a PNG
  IHDR chunk or a JPEG start-of-frame segment. It returns false, with both sizes zero, for a media type
  other than `ImageHeaders.PngMediaType` (`image/png`) or `ImageHeaders.JpegMediaType` (`image/jpeg`),
  or for a header that does not parse. It does not range check the size.
- `BugReportJson.IsValidSection(json)` is the one section rule the validator and writers share: exactly one
  JSON object within `BugReportLimits.MaxSectionDepth`, no repeated property name in any object, and every
  string and property name decodable. It never throws.
- `BugReportJson.SectionDocumentOptions` is the `JsonDocumentOptions` for reading a section: depth
  `BugReportLimits.MaxSectionDepth` and no repeated property names.
- `BundleEntryNames.IsValidGameId` and `IsValidSectionKey`, and the `BugReportLimits` constants.

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
JSON, a null root, a missing property, a null value in a non-nullable property or a property name
repeated in any object, including section dictionaries and their JSON values. The server snapshot
deserializer also throws `JsonException` when any string or property name in it, section values included,
does not decode. `Options` applies the
same duplicate rule. Every constructor
property is required, including the nullable `handle`, which is written as `null`. `Options` exposes the
same read-only source-generated settings, including the reporter identity shape. Copy the options if a
caller needs different settings.

JSON is compact with camelCase property names and camelCase enum strings. Integer enum values are
rejected. Serialization and deserialization both normalize `DateTimeOffset` values to UTC, so a time read
with another offset comes back as the same instant with offset zero. The encoder is `JavaScriptEncoder.Create(UnicodeRanges.All)`, so
non-ASCII text such as `Café` stays literal while HTML-sensitive characters are escaped.

The manifest golden is one line of UTF-8 with no trailing newline:

```json
{"formatVersion":1,"reportId":"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f","gameId":"grimhollow","kind":"bug","category":"Gameplay","description":"Fell through\nthe floor","capturedAt":"2026-10-17T03:04:05.678+00:00","entries":[{"name":"sections/build.json","size":42,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
```

The two-frame clip-index golden uses the same encoding and newline rule:

```json
{"frames":[{"offsetMilliseconds":0,"renderMilliseconds":16.5,"width":960,"height":540,"entry":"clip/000.jpg"},{"offsetMilliseconds":83.25,"renderMilliseconds":41,"width":960,"height":540,"entry":"clip/001.jpg"}]}
```
