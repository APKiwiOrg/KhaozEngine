using System;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

internal static class AttachmentSetJsonRefusalAssertions
{
    internal static void V1(string name, AttachmentSetDataException error)
    {
        (AttachmentSetRefusal reason, string? path, string? property) = name switch
        {
            "duplicate-id" or "id-zero" => (AttachmentSetRefusal.PartIdInvalidOrDuplicate, null, null),
            "duplicate-root" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$", "formatVersion"),
            "duplicate-part" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$.parts[0]", "id"),
            "duplicate-mesh" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$.parts[0].mesh", "key"),
            "version-2" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0]", "kind"),
            "version-string" or "version-overflow" => (AttachmentSetRefusal.FormatVersionUnsupported, "$.formatVersion", null),
            "id-overflow" or "id-string" or "id-negative" => (AttachmentSetRefusal.JsonPartIdInvalid, "$.parts[0].id", null),
            "missing-mesh" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0]", "mesh"),
            "missing-key" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0].mesh", "key"),
            "missing-hash" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0].mesh", "sha256"),
            "missing-joint" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0]", "joint"),
            "missing-local" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0]", "local"),
            "missing-version" => (AttachmentSetRefusal.JsonPropertyMissing, "$", "formatVersion"),
            "missing-parts" => (AttachmentSetRefusal.JsonPropertyMissing, "$", "parts"),
            "empty-parts" => (AttachmentSetRefusal.PartsEmpty, null, null),
            "malformed-hash" or "uppercase-hash" => (AttachmentSetRefusal.MeshHashInvalid, null, null),
            "malformed-string" => (AttachmentSetRefusal.JsonValueShapeInvalid, null, null),
            "nan" => (AttachmentSetRefusal.JsonMalformed, null, null),
            "overflow" => (AttachmentSetRefusal.LocalTransformComponentInvalid, "$.parts[0].local[0]", null),
            "matrix-string-component" => (AttachmentSetRefusal.LocalTransformComponentInvalid, "$.parts[0].local[1]", null),
            "singular" or "non-affine-matrix" => (AttachmentSetRefusal.LocalTransformInvalid, null, null),
            "short-matrix" or "long-matrix" or "matrix-not-array" => (AttachmentSetRefusal.LocalTransformLengthMismatch, "$.parts[0].local", null),
            "unknown-property" => (AttachmentSetRefusal.JsonPropertyUnknown, "$", "extra"),
            "unknown-mesh-property" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0].mesh", "extra"),
            "unknown-part-property" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0]", "extra"),
            "wrong-root" => (AttachmentSetRefusal.JsonObjectRequired, "$", null),
            "wrong-part" => (AttachmentSetRefusal.JsonObjectRequired, "$.parts[0]", null),
            "wrong-mesh" => (AttachmentSetRefusal.JsonObjectRequired, "$.parts[0].mesh", null),
            "wrong-parts" => (AttachmentSetRefusal.PartsNotArray, "$.parts", null),
            "wrong-joint" => (AttachmentSetRefusal.JsonStringRequired, "$.parts[0].joint", "joint"),
            "empty-joint" => (AttachmentSetRefusal.JointNameEmpty, null, null),
            _ => throw new ArgumentException(name),
        };
        Common(error, name, reason, path, property);
        switch (name)
        {
            case "version-string":
            case "version-overflow": Assert.Null(error.FormatVersion); break;
            case "empty-parts": Assert.Equal(0, error.PartCount); break;
            case "duplicate-id": Part(error, 1); Assert.Equal(1, error.PartIndex); break;
            case "id-zero": Part(error, 0); Assert.Equal(0, error.PartIndex); break;
            case "malformed-hash":
            case "uppercase-hash":
                Part(error, 1);
                Assert.Equal("parts/plate.glb", error.MeshKey);
                Assert.Equal(name == "malformed-hash" ? "abcd" : AttachmentFixtures.Hash.ToUpperInvariant(), error.DeclaredHash);
                break;
            case "singular":
            case "non-affine-matrix":
            case "empty-joint": Part(error, 1); break;
            case "short-matrix": Length(error, 1); break;
            case "long-matrix": Length(error, 17); break;
            case "matrix-not-array": Length(error, null); break;
            case "overflow": Assert.Equal(0, error.ComponentIndex); break;
            case "matrix-string-component": Assert.Equal(1, error.ComponentIndex); break;
        }
    }

    internal static void V2(string name, AttachmentSetDataException error)
    {
        (AttachmentSetRefusal reason, string? path, string? property) = name switch
        {
            "unknown-kind" or "case-changed-kind" => (AttachmentSetRefusal.JsonPartKindUnsupported, "$.parts[0].kind", null),
            "non-string-kind" or "null-kind" => (AttachmentSetRefusal.JsonStringRequired, "$.parts[0].kind", "kind"),
            "missing-kind" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0]", "kind"),
            "v1-kind" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0]", "kind"),
            "skinned-joint" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0]", "joint"),
            "skinned-local" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0]", "local"),
            "duplicate-root" or "escaped-duplicate-root" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$", "formatVersion"),
            "duplicate-part" or "duplicate-skinned-part" or "escaped-duplicate-part" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$.parts[0]", "id"),
            "duplicate-kind" or "escaped-duplicate-kind" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$.parts[0]", "kind"),
            "duplicate-mesh" or "escaped-duplicate-mesh" => (AttachmentSetRefusal.JsonPropertyDuplicate, "$.parts[0].mesh", "key"),
            "unknown-root" => (AttachmentSetRefusal.JsonPropertyUnknown, "$", "extra"),
            "unknown-rigid" or "unknown-skinned" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0]", "extra"),
            "unknown-mesh" => (AttachmentSetRefusal.JsonPropertyUnknown, "$.parts[0].mesh", "extra"),
            "duplicate-id-across-kinds" or "zero-id" => (AttachmentSetRefusal.PartIdInvalidOrDuplicate, null, null),
            "missing-mesh" => (AttachmentSetRefusal.JsonPropertyMissing, "$.parts[0]", "mesh"),
            "unsupported-version" => (AttachmentSetRefusal.FormatVersionUnsupported, "$.formatVersion", null),
            "conflicting-hashes" => (AttachmentSetRefusal.MeshHashConflict, null, null),
            _ => throw new ArgumentException(name),
        };
        Common(error, name, reason, path, property);
        switch (name)
        {
            case "unknown-kind": Assert.Equal("cloth", error.Kind); break;
            case "case-changed-kind": Assert.Equal("Rigid", error.Kind); break;
            case "duplicate-id-across-kinds": Part(error, 1); Assert.Equal(1, error.PartIndex); break;
            case "zero-id": Part(error, 0); Assert.Equal(0, error.PartIndex); break;
            case "unsupported-version": Assert.Equal(3, error.FormatVersion); break;
            case "conflicting-hashes":
                Part(error, 2);
                Assert.Equal("parts/plate.glb", error.MeshKey);
                Assert.Equal(new string('b', 64), error.DeclaredHash);
                Assert.Equal(AttachmentFixtures.Hash, error.ExistingHash);
                break;
        }
    }

    static void Common(AttachmentSetDataException error, string name, AttachmentSetRefusal reason, string? path, string? property)
    {
        Assert.Equal(reason, error.Reason);
        Assert.Equal(name + ".json", error.SourceName);
        Assert.Equal(path, error.JsonPath);
        Assert.Equal(property, error.PropertyName);
        Assert.NotNull(error.InnerException);
        if (error.InnerException is AttachmentSetArgumentException argument)
        {
            Assert.Equal(reason, argument.Reason);
            Assert.Equal("parts", error.ParamName);
            Assert.Equal(argument.ParamName, error.ParamName);
        }
        else if (error.InnerException is AttachmentSetDataException data)
            Assert.Equal(reason, data.Reason);
    }

    static void Part(AttachmentSetDataException error, uint partId)
    {
        Assert.Equal(partId, error.PartId);
        Assert.Equal("parts", error.ParamName);
        Assert.IsType<AttachmentSetArgumentException>(error.InnerException);
    }

    static void Length(AttachmentSetDataException error, int? actual)
    {
        Assert.Equal(16, error.ExpectedCount);
        Assert.Equal(actual, error.ActualCount);
    }
}
