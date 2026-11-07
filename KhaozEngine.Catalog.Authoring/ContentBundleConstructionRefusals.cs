using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Maps a lower constructor's typed finding to the bundle document's own refusal. Each keeps the bundle-format
/// token and the message the reader always wrote, its prefix and the constructor's words, and keeps the
/// constructor's exception as the inner exception. The reason comes from the finding, never from the message, and
/// a rule no bundle can reach is an internal invariant rather than a generic refusal.
/// </summary>
internal static class ContentBundleConstructionRefusals
{
    const string Token = ContentAuthoringException.BundleFormatReason;

    /// <summary>The refusal for a family <see cref="ContentFamily"/> refused.</summary>
    /// <param name="finding">The family's own finding for the same arguments.</param>
    /// <param name="malformed">The exception the constructor threw.</param>
    /// <param name="familyPath">The family's rooted path.</param>
    /// <param name="familyKey">The family key the bundle declares.</param>
    /// <param name="blocks">The blocks the bundle declares, in document order.</param>
    public static ContentAuthoringException Family(
        ContentFamilyFinding? finding,
        ArgumentException malformed,
        string familyPath,
        string familyKey,
        IReadOnlyList<ContentFamilyBlock> blocks)
    {
        string message = "A bundle family is malformed: " + malformed.Message;
        return finding?.Rule switch
        {
            ContentFamilyRule.KeyBlank => new(ContentAuthoringRefusal.BundleFamilyKeyBlank, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(familyPath, "familyKey"),
            },
            ContentFamilyRule.BlockSizeOutsideRange => new(ContentAuthoringRefusal.FamilyBlockSizeOutsideRange, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(familyPath, "blockSize"),
                FamilyKey = familyKey,
                ActualBlockSize = finding.BlockSize,
                MinimumBlockSize = ContentFamily.MinBlockSize,
                MaximumBlockSize = ContentFamily.MaxBlockSize,
            },
            ContentFamilyRule.BlockSizeNotPowerOfTwo => new(ContentAuthoringRefusal.FamilyBlockSizeNotPowerOfTwo, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(familyPath, "blockSize"),
                FamilyKey = familyKey,
                ActualBlockSize = finding.BlockSize,
            },
            ContentFamilyRule.BlockUnaligned => new(ContentAuthoringRefusal.BundleFamilyBlockAlignmentInvalid, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(
                    ContentBundleJson.Element(ContentBundleJson.Member(familyPath, "blocks"), finding.BlockIndex), "baseId"),
                ActualInteger = blocks[finding.BlockIndex].BaseId,
            },

            // An internal invariant no input reaches: the reader builds every block with the family's own block
            // size, and the key and block list are never null.
            _ => throw new InvalidOperationException("A bundle family failed a rule its reader cannot produce.", malformed),
        };
    }

    /// <summary>The refusal for a field list <see cref="ContentFieldSchema"/> refused.</summary>
    /// <param name="finding">The schema's own finding for the same fields.</param>
    /// <param name="malformed">The exception the constructor threw.</param>
    /// <param name="typePath">The type's rooted path.</param>
    public static ContentAuthoringException Schema(ContentFieldSchemaFinding? finding, ArgumentException malformed, string typePath)
    {
        string message = "A bundle type's schema is malformed: " + malformed.Message;
        string fieldPath = ContentBundleJson.Element(ContentBundleJson.Member(typePath, "fields"), finding?.Index ?? 0);
        ContentFieldEntry? entry = finding?.Entry;
        return finding?.Rule switch
        {
            ContentFieldSchemaRule.NameBlank => new(ContentAuthoringRefusal.BundleFieldNameBlank, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "name"),
            },
            ContentFieldSchemaRule.NameDuplicate => new(ContentAuthoringRefusal.BundleFieldNameDuplicate, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "name"),
                FieldName = entry!.Name,
            },
            ContentFieldSchemaRule.KeyReferenceTargetMissing => new(ContentAuthoringRefusal.BundleReferenceTargetMissing, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "referenceTarget"),
                FieldName = entry!.Name,
            },
            ContentFieldSchemaRule.TagReferenceTargetInvalid => new(ContentAuthoringRefusal.BundleTagReferenceTargetInvalid, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "referenceTarget"),
                FieldName = entry!.Name,
            },
            ContentFieldSchemaRule.ReferenceTargetUnexpected => new(ContentAuthoringRefusal.BundleUnexpectedReferenceTarget, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "referenceTarget"),
                FieldName = entry!.Name,
                ActualKind = entry.Kind,
            },
            ContentFieldSchemaRule.ScaleBelowOne => new(ContentAuthoringRefusal.BundleScaledIntegerScaleInvalid, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "scale"),
                FieldName = entry!.Name,
                ActualInteger = entry.Scale,
                MinimumInteger = 1,
            },
            ContentFieldSchemaRule.ScaleNotOne => new(ContentAuthoringRefusal.BundleUnscaledKindScaleInvalid, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(fieldPath, "scale"),
                FieldName = entry!.Name,
                ActualKind = entry.Kind,
                ActualInteger = entry.Scale,
                MinimumInteger = 1,
                MaximumInteger = 1,
            },

            // An internal invariant no input reaches: the reader never builds a null entry, and it declares no
            // baseline, so every field is a baseline field and none counts as appended.
            _ => throw new InvalidOperationException("A bundle schema failed a rule its reader cannot produce.", malformed),
        };
    }

    /// <summary>The refusal for a rule <see cref="RemapRule"/> refused.</summary>
    /// <param name="malformed">The exception the constructor threw.</param>
    /// <param name="rulePath">The rule's rooted path.</param>
    /// <param name="kind">The kind the bundle declares, already a defined kind.</param>
    /// <param name="payload">The payload the bundle declares.</param>
    public static ContentAuthoringException Rule(ArgumentException malformed, string rulePath, RemapRuleKind kind, byte[] payload)
    {
        string message = "A bundle rule is malformed: " + malformed.Message;

        // The constructor's checks in its own order: the four plain non-negative checks, whose parameter is the
        // member of the same name and whose actual value is the integer, then the kind, then the payload shape.
        if (malformed is ArgumentOutOfRangeException
            {
                ParamName: ("sequence" or "introducedIn" or "fromId" or "toId") and string member,
                ActualValue: int actual,
            })
        {
            return new(ContentAuthoringRefusal.BundleRuleIntegerNegative, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(rulePath, member),
                ActualInteger = actual,
                MinimumInteger = 0,
            };
        }

        if (!RemapRule.IsKnownKind((byte)kind))
        {
            // An internal invariant no input reaches: the reader admits only the four defined kinds, which are
            // the kinds IsKnownKind accepts.
            throw new InvalidOperationException("A bundle rule kind passed the reader and failed the rule.", malformed);
        }

        if (!RemapRule.IsPayloadWellFormed(kind, payload))
        {
            return new(ContentAuthoringRefusal.BundleRulePayloadMalformed, message, default, 0, Token, malformed)
            {
                JsonPath = ContentBundleJson.Member(rulePath, "payload"),
                RuleKind = kind,
                ActualBytes = payload.Length,
            };
        }

        // An internal invariant no input reaches: the constructor has no other check.
        throw new InvalidOperationException("A bundle rule failed a rule its reader cannot produce.", malformed);
    }

    /// <summary>The refusal for a row field value <see cref="ContentFieldValue.OfBytes"/> refused.</summary>
    /// <param name="malformed">The exception the factory threw.</param>
    /// <param name="fieldPath">The row field's rooted path.</param>
    public static ContentAuthoringException MarkerValue(ArgumentException malformed, string fieldPath)
        => new(ContentAuthoringRefusal.BundleMarkerValueAuthored, "A bundle field value is malformed: " + malformed.Message, default, 0, Token, malformed)
        {
            JsonPath = ContentBundleJson.Member(fieldPath, "bytes"),
        };
}
