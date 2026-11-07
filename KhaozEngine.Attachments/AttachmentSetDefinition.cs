using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace KhaozEngine.Attachments;

public sealed class AttachmentSetDefinition
{
    public AttachmentSetDefinition(IEnumerable<AttachmentPartDefinition> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        AttachmentPartDefinition[] owned = parts.ToArray();
        if (owned.Length == 0)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartsEmpty, "An attachment set must contain parts.", nameof(parts)) { PartCount = owned.Length };

        var ids = new HashSet<uint>();
        var meshes = new Dictionary<string, AttachmentMeshRef>(StringComparer.Ordinal);
        for (int partIndex = 0; partIndex < owned.Length; partIndex++)
        {
            AttachmentPartDefinition part = owned[partIndex];
            if (part is null)
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartNull, "An attachment part cannot be null.", nameof(parts))
                { PartIndex = partIndex };
            string context = $"Attachment part {part.Id}";
            if (part.Id == 0 || !ids.Add(part.Id))
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartIdInvalidOrDuplicate, $"{context}: IDs must be positive and unique.", nameof(parts))
                { PartId = part.Id, PartIndex = partIndex };
            if (part is not (RigidAttachmentPartDefinition or SkinnedAttachmentPartDefinition))
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.PartKindUnsupported, $"{context}: unsupported attachment part kind.", nameof(parts))
                { PartId = part.Id, ActualType = part.GetType() };
            string? keyError = AttachmentAssetKey.Error(part.Mesh.Key);
            if (keyError is not null)
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.MeshKeyInvalid, $"{context}: mesh key '{part.Mesh.Key}' {keyError}.", nameof(parts))
                { PartId = part.Id, MeshKey = part.Mesh.Key };
            if (!ValidHash(part.Mesh.Sha256))
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.MeshHashInvalid, $"{context}: mesh '{part.Mesh.Key}' requires a lowercase SHA-256.", nameof(parts))
                { PartId = part.Id, MeshKey = part.Mesh.Key, DeclaredHash = part.Mesh.Sha256 };
            if (part is RigidAttachmentPartDefinition rigid)
            {
                if (string.IsNullOrWhiteSpace(rigid.Joint))
                    throw new AttachmentSetArgumentException(AttachmentSetRefusal.JointNameEmpty, $"{context}: a joint name is required.", nameof(parts))
                    { PartId = part.Id };
                if (!AttachmentText.IsWellFormedUtf16(rigid.Joint))
                    throw new AttachmentSetArgumentException(AttachmentSetRefusal.JointNameMalformedUtf16, $"{context}: joint '{rigid.Joint}' must contain well-formed UTF-16.", nameof(parts))
                    { PartId = part.Id, JointName = rigid.Joint };
                if (!ValidMatrix(rigid.Local))
                    throw new AttachmentSetArgumentException(AttachmentSetRefusal.LocalTransformInvalid, $"{context}: local must be finite, affine and invertible.", nameof(parts))
                    { PartId = part.Id };
            }
            if (meshes.TryGetValue(part.Mesh.Key, out AttachmentMeshRef existing) && existing.Sha256 != part.Mesh.Sha256)
                throw new AttachmentSetArgumentException(AttachmentSetRefusal.MeshHashConflict, $"{context}: mesh '{part.Mesh.Key}' has conflicting hashes.", nameof(parts))
                { PartId = part.Id, MeshKey = part.Mesh.Key, DeclaredHash = part.Mesh.Sha256, ExistingHash = existing.Sha256 };
            meshes[part.Mesh.Key] = part.Mesh;
        }
        Array.Sort(owned, (left, right) => left.Id.CompareTo(right.Id));
        Parts = Array.AsReadOnly(owned);
        Meshes = Array.AsReadOnly(meshes.Values.OrderBy(mesh => mesh.Key, StringComparer.Ordinal).ToArray());
        SemanticSha256 = Convert.ToHexStringLower(SHA256.HashData(AttachmentSetJson.Write(this)));
    }

    public IReadOnlyList<AttachmentPartDefinition> Parts { get; }
    public IReadOnlyList<AttachmentMeshRef> Meshes { get; }
    public string SemanticSha256 { get; }

    public void ValidateReferences(IReadOnlySet<string> joints, IReadOnlyDictionary<string, string> meshSha256)
    {
        ArgumentNullException.ThrowIfNull(joints);
        ArgumentNullException.ThrowIfNull(meshSha256);
        // Reference names stay ordinal even if the caller supplies a case-insensitive collection.
        var knownJoints = new HashSet<string>(joints, StringComparer.Ordinal);
        var knownMeshes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> mesh in meshSha256)
            knownMeshes.Add(mesh.Key, mesh.Value);
        foreach (AttachmentPartDefinition part in Parts)
        {
            if (part is RigidAttachmentPartDefinition rigid && !knownJoints.Contains(rigid.Joint))
                throw new AttachmentSetDataException(AttachmentSetRefusal.JointReferenceUnresolved, $"Attachment part {part.Id}: joint '{rigid.Joint}' is unresolved.")
                { PartId = part.Id, JointName = rigid.Joint };
            if (!knownMeshes.TryGetValue(part.Mesh.Key, out string? actual) || actual != part.Mesh.Sha256)
                throw new AttachmentSetDataException(AttachmentSetRefusal.MeshReferenceMissingOrHashMismatch, $"Attachment part {part.Id}: mesh '{part.Mesh.Key}' is missing or its SHA-256 differs.")
                { PartId = part.Id, MeshKey = part.Mesh.Key, ExpectedHash = part.Mesh.Sha256, ActualHash = actual };
        }
    }

    internal static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    static bool ValidMatrix(Matrix4x4 matrix) => Values(matrix).All(float.IsFinite)
        && matrix.M14 == 0 && matrix.M24 == 0 && matrix.M34 == 0 && matrix.M44 == 1
        && Matrix4x4.Invert(matrix, out Matrix4x4 inverse) && Values(inverse).All(float.IsFinite);

    internal static float[] Values(Matrix4x4 matrix) =>
    [
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44,
    ];
}
