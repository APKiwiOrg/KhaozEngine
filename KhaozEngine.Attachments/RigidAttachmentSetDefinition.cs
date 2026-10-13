using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace KhaozEngine.Attachments;

public readonly record struct AttachmentMeshRef(string Key, string Sha256);
public sealed record RigidAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh, string Joint, Matrix4x4 Local);

public sealed class RigidAttachmentSetDefinition
{
    public RigidAttachmentSetDefinition(IEnumerable<RigidAttachmentPartDefinition> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        RigidAttachmentPartDefinition[] owned = parts.ToArray();
        if (owned.Length == 0)
            throw new ArgumentException("An attachment set must contain parts.", nameof(parts));

        var ids = new HashSet<uint>();
        var meshes = new Dictionary<string, AttachmentMeshRef>(StringComparer.Ordinal);
        foreach (RigidAttachmentPartDefinition part in owned)
        {
            if (part is null)
                throw new ArgumentException("An attachment part cannot be null.", nameof(parts));
            string context = $"Attachment part {part.Id}";
            if (part.Id == 0 || !ids.Add(part.Id))
                throw new ArgumentException($"{context}: IDs must be positive and unique.", nameof(parts));
            string? keyError = AttachmentAssetKey.Error(part.Mesh.Key);
            if (keyError is not null)
                throw new ArgumentException($"{context}: mesh key '{part.Mesh.Key}' {keyError}.", nameof(parts));
            if (!ValidHash(part.Mesh.Sha256))
                throw new ArgumentException($"{context}: mesh '{part.Mesh.Key}' requires a lowercase SHA-256.", nameof(parts));
            if (string.IsNullOrWhiteSpace(part.Joint))
                throw new ArgumentException($"{context}: a joint name is required.", nameof(parts));
            if (!AttachmentText.IsWellFormedUtf16(part.Joint))
                throw new ArgumentException($"{context}: joint '{part.Joint}' must contain well-formed UTF-16.", nameof(parts));
            if (!ValidMatrix(part.Local))
                throw new ArgumentException($"{context}: local must be finite, affine and invertible.", nameof(parts));
            if (meshes.TryGetValue(part.Mesh.Key, out AttachmentMeshRef existing) && existing.Sha256 != part.Mesh.Sha256)
                throw new ArgumentException($"{context}: mesh '{part.Mesh.Key}' has conflicting hashes.", nameof(parts));
            meshes[part.Mesh.Key] = part.Mesh;
        }
        Array.Sort(owned, (left, right) => left.Id.CompareTo(right.Id));
        Parts = Array.AsReadOnly(owned);
        Meshes = Array.AsReadOnly(meshes.Values.OrderBy(mesh => mesh.Key, StringComparer.Ordinal).ToArray());
        SemanticSha256 = Convert.ToHexStringLower(SHA256.HashData(RigidAttachmentSetJson.Write(this)));
    }

    public IReadOnlyList<RigidAttachmentPartDefinition> Parts { get; }
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
        foreach (RigidAttachmentPartDefinition part in Parts)
        {
            if (!knownJoints.Contains(part.Joint))
                throw new InvalidDataException($"Attachment part {part.Id}: joint '{part.Joint}' is unresolved.");
            if (!knownMeshes.TryGetValue(part.Mesh.Key, out string? actual) || actual != part.Mesh.Sha256)
                throw new InvalidDataException($"Attachment part {part.Id}: mesh '{part.Mesh.Key}' is missing or its SHA-256 differs.");
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
