# Item Rarity Display Colour Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store a colour on each published rarity rule and resolve a visible item instance's rarity to RGB without adding colour to player state.

**Architecture:** Append an optional three-byte RGB field to the existing client-visible `rarity_rule` schema, retaining its seven-field wire baseline. The catalog bundle writes these bytes as six lower-case hex digits. A renderer-free read object indexes colours by rarity ID from one loaded `ContentRuntime` and reads kind 130 from a canonical instance payload. Missing data returns white.

**Tech Stack:** .NET 10, C#, KhaozEngine.Catalog, KhaozEngine.ItemInstances, xUnit

**Spec:** `docs/design/ITEM-RARITY-PRESENTATION-AND-EVENT-REPLAY-DESIGN-2026-09-23.md`, Rarity colour contract and Compatibility and verification

## Global Constraints

- Work in the existing `feature/item-rarity-color` worktree and preserve concurrent branches.
- The current staged engine version is `20.2.0` and newest release tag is `v20.1.0`. Re-read both before finishing. Ride a staged version if it remains ahead of the newest tag.
- The engine `rarity_rule` type ID is 258, and item property kind 130 stores a one-byte rarity ID. Do not renumber either.
- Append `display_rgb` after `upgrade_from`. Preserve `ContentFieldSchema.BaselineFieldCount == 7` and old row bytes when the tail field is absent.
- RGB is exactly three opaque bytes, with opaque alpha at presentation. The bundle writes `000000` for black and `ffffff` for white. Missing rarity, missing field, malformed payload, and absent catalog row display white `0xFFFFFF`. A retired row still supplies its authored colour.
- No player journal, item payload, or database table format changes in this plan. The companion event replay plan changes new journal event versions.
- Every code task gets a focused Release test and an explicit commit. Do not raise the KESIZE baseline or bypass hooks.
- No em dash or en dash glyphs or prose semicolons in shipped text.

## Review Focus

- A valid black `display_rgb` value must stay black. It must not be mistaken for an absent field. Task 1 tests it.
- An old seven-field rarity row must encode to its original bytes after decoding under the new schema. Task 1 tests it.
- An instance payload with another property before or after kind 130 must still find the rarity. Task 2 tests it.
- A malformed, truncated, or quarantined payload must draw white without throwing or changing bytes. Task 2 tests it.
- A stored item referencing a retired rarity row must retain the row's colour. Task 2 tests it.

## File Structure

- `KhaozEngine.ItemInstances/Content/RarityRuleContentType.cs` owns the appended field and its range check.
- `KhaozEngine.ItemInstances/Content/RarityDisplayColors.cs` is the one runtime colour index and payload reader. It returns RGB, not a rendering type.
- `KhaozEngine.ItemInstances.Tests/Content/RarityFamilyTests.cs` pins the schema, old bytes, and colour bounds.
- `KhaozEngine.ItemInstances.Tests/Content/RarityDisplayColorsTests.cs` pins payload and catalog fallback behavior.
- `KhaozEngine.ItemInstances/README.md` and `docs/USING-KHAOZENGINE.md` describe the public colour read.

---

### Task 1: Append the optional rarity colour field

**Files:**

- Modify: `KhaozEngine.ItemInstances/Content/RarityRuleContentType.cs`
- Modify: `KhaozEngine.ItemInstances.Tests/Content/RarityFamilyTests.cs`
- Modify: `KhaozEngine.ItemInstances.Tests/Content/InstanceContentTypeFixtures.cs`

**Interfaces:**

- Consumes: `ContentFieldSchema`, `ContentRowTailRule`, `ContentRowCodecBase.CheckFieldValue`
- Produces: `RarityRuleContentType.DisplayRgbField`, `DisplayRgbIndex == 7`, `BaselineFieldCount == 7`, `DisplayRgbBytes == 3`

- [ ] **Step 1: Make the schema and old-byte tests red.** Change the field-for-field fact to expect eight fields, baseline seven, and `display_rgb` at index seven as optional `OpaqueBytes` with client visibility. Add a fact that encodes the seven-field `rare` row, decodes it under the new schema, and asserts byte equality after re-encode. Use `ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes)` for an old row's tail. Pin explicit `000000` and `ffffff` colours, then reject present values of one, two, or four bytes through the existing `Encode` and `TryDecode` helpers.

```csharp
ContentTypeRegistration registration = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);
ContentFieldSchema rule = registration.Schema;
Assert.Equal(7, rule.BaselineFieldCount);
AssertField(rule, 7, "display_rgb", ContentFieldKind.OpaqueBytes,
    null, ContentVisibility.Client, false);
byte[] oldBytes = Forge(registration, "normal", 4, 6, 3, 3, 2, 0);
Assert.Equal(oldBytes, Encode(registration, Decode(registration, oldBytes)));
```

- [ ] **Step 2: Run the focused tests and confirm the expected failure.**

```bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~RarityFamilyTests
```

Expected: the schema count or field lookup fails before code changes.

- [ ] **Step 3: Append and validate the field.** Keep all old indices unchanged. Extend the test fixture's populated row with an absent colour so generic round trips still cover all eighteen registered types.

```csharp
public const int BaselineFieldCount = 7;
public const string DisplayRgbField = "display_rgb";
public const int DisplayRgbIndex = 7;
public const int DisplayRgbBytes = 3;

// Last entry of CreateSchema, after upgrade_from.
new ContentFieldEntry(DisplayRgbField, ContentFieldKind.OpaqueBytes, null,
    ContentVisibility.Client, false),
], BaselineFieldCount);

// New Codec.CheckFieldValue switch arm.
DisplayRgbField when !value.IsAbsent && value.Bytes.Length != DisplayRgbBytes => ReasonFieldMalformed,
```

- [ ] **Step 4: Run the focused tests green and commit.** Include old seven-field byte equality, black, white, and one, two, and four byte refusals.

```bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~RarityFamilyTests
git add KhaozEngine.ItemInstances/Content/RarityRuleContentType.cs KhaozEngine.ItemInstances.Tests/Content/RarityFamilyTests.cs KhaozEngine.ItemInstances.Tests/Content/InstanceContentTypeFixtures.cs
git commit -m "catalog(rarity): store optional display colour"
```

### Task 2: Resolve rarity colour from a loaded pack and an item payload

**Files:**

- Create: `KhaozEngine.ItemInstances/Content/RarityDisplayColors.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Content/RarityDisplayColorsTests.cs`

**Interfaces:**

- Consumes: `ContentRuntime.Rows`, `RarityRuleContentType.DisplayRgbIndex`, `ItemInstancePayload.TryDecode`, `InstancePropertyKind.Rarity`
- Produces: `public sealed class RarityDisplayColors`, `public const int DefaultRgb = 0xFFFFFF`, `public static RarityDisplayColors Over(ContentRuntime runtime)`, `public int RgbOf(ReadOnlySpan<byte> payload)`

- [ ] **Step 1: Add failing colour read tests.** Build a runtime from `ContentSnapshotBuilder` and `InstanceContentTypes.Register`. Author live blue (`75b7f0`), retired coral (`ff9e7a`), and absent-colour rows as three-byte fields. Use `ItemInstancePayloadBuilder` to make a payload with kind 130 and a neighboring property, then assert the resolved RGB integer. Also test empty, truncated, unknown-ID, and quarantine-wrapper bytes returning `DefaultRgb`.

```csharp
RarityDisplayColors colours = RarityDisplayColors.Over(runtime);
Assert.Equal(0xFFFFFF, colours.RgbOf(ReadOnlySpan<byte>.Empty));
Assert.Equal(0x75B7F0, colours.RgbOf(bluePayload));
Assert.Equal(0xFF9E7A, colours.RgbOf(retiredPayload));
```

- [ ] **Step 2: Run the focused tests and confirm they fail on the missing type.**

```bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~RarityDisplayColorsTests
```

- [ ] **Step 3: Implement the one read object.** At construction, resolve the schema index by field name and fill a 256-element RGB array with white. Walk `runtime.Rows` without dropping retired rows, packing each present three-byte value as `(red << 16) | (green << 8) | blue`. Store one `InstancePropertyRegistry.CreateV1()` in the resolver. `RgbOf` decodes into a stack span, finds kind 130, checks its one-byte body, and indexes the cached array. Return white for any decode or reference miss without mutating input.

```csharp
public int RgbOf(ReadOnlySpan<byte> payload)
{
    Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
    if (!ItemInstancePayload.TryDecode(_properties, payload, fields,
            out int count, out _)) return DefaultRgb;
    for (int i = 0; i < count; i++)
        if (fields[i].Kind == InstancePropertyKind.Rarity && fields[i].BodyLength == 1)
            return _rgbById[payload[fields[i].BodyStart]];
    return DefaultRgb;
}
```

- [ ] **Step 4: Run the focused tests green and commit.**

```bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~RarityDisplayColorsTests
git add KhaozEngine.ItemInstances/Content/RarityDisplayColors.cs KhaozEngine.ItemInstances.Tests/Content/RarityDisplayColorsTests.cs
git commit -m "items(rarity): resolve display colour from instance payload"
```

### Task 3: Publish the colour API documentation

**Files:**

- Modify: `KhaozEngine.ItemInstances/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`

**Interfaces:**

- Consumes: `RarityRuleContentType.DisplayRgbField`, `RarityDisplayColors.Over`, `RgbOf`
- Produces: one public description of the optional field, white fallback, and code sample using an explicit runtime

- [ ] **Step 1: Add the exact consumer example to the package README and the matching concept to the consumer guide.** Show the field as three RGB bytes exported in six-digit hex, explain why a missing row or instance property reads white, and state that the payload stores an ID rather than colour bytes.

```csharp
RarityDisplayColors colours = RarityDisplayColors.Over(runtime);
int rgb = colours.RgbOf(slot.Payload.Span);
```

- [ ] **Step 2: Run the focused tests and documentation checks.**

```bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter "FullyQualifiedName~RarityFamilyTests|FullyQualifiedName~RarityDisplayColorsTests"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
bash scripts/check-doc-versions.sh
```

- [ ] **Step 3: Commit the public documentation.** The companion event replay plan handles the cumulative changelog, Release build and tests, local feed pack, and engine release.

```bash
git add KhaozEngine.ItemInstances/README.md docs/USING-KHAOZENGINE.md
git commit -m "docs(items): describe rarity display colours"
```
