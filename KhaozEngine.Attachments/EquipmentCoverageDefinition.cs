using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>A contiguous run of inward support triangles, <c>[FirstTriangle, FirstTriangle + Count)</c>, in one source
/// mesh primitive of a rigid part's displayed GLB.</summary>
public readonly record struct EquipmentSupportRange(int Mesh, int Primitive, int FirstTriangle, int Count);

/// <summary>
/// One conformation command: rigid part <see cref="PartId"/> keeps host region <see cref="HostRegion"/> behind its
/// actual inward support sheet. <see cref="CoverageLocal"/> is a row-major, affine, positively oriented frame relative
/// to the part with positive Z outward. <see cref="CollarWidth"/>, <see cref="MinZ"/> and <see cref="MaxZ"/> are in
/// coverage coordinates. <see cref="Clearance"/> and <see cref="MaxWorldRecession"/> are world-space distances. Support
/// ranges are an owned snapshot sorted by source ordinal.
/// </summary>
public sealed class EquipmentCoverageCommand
{
    public const int MaxSupportTriangles = 1_024;

    public EquipmentCoverageCommand(uint id, uint partId, string hostRegion, Matrix4x4 coverageLocal,
        IEnumerable<EquipmentSupportRange> inwardSupport, float collarWidth, float minZ, float maxZ, float clearance, float maxWorldRecession)
    {
        ArgumentNullException.ThrowIfNull(hostRegion);
        ArgumentNullException.ThrowIfNull(inwardSupport);
        string context = $"Coverage command {id}";
        if (id == 0)
            throw new ArgumentException($"{context}: IDs must be positive.", nameof(id));
        if (partId == 0)
            throw new ArgumentException($"{context}: part IDs must be positive.", nameof(partId));
        if (string.IsNullOrWhiteSpace(hostRegion) || !AttachmentText.IsWellFormedUtf16(hostRegion))
            throw new ArgumentException($"{context}: host region must be non-blank, well-formed UTF-16.", nameof(hostRegion));
        if (!IsSupportedFrame(coverageLocal))
            throw new ArgumentException($"{context}: coverageLocal must be finite, affine, invertible and not mirrored.", nameof(coverageLocal));

        EquipmentSupportRange[] support = inwardSupport.ToArray();
        if (support.Length == 0)
            throw new ArgumentException($"{context}: at least one inward support range is required.", nameof(inwardSupport));
        foreach (EquipmentSupportRange range in support)
            if (range.Mesh < 0 || range.Primitive < 0 || range.FirstTriangle < 0 || range.Count < 1
                || (long)range.FirstTriangle + range.Count > int.MaxValue)
                throw new ArgumentException($"{context}: invalid support range {Describe(range)}.", nameof(inwardSupport));
        Array.Sort(support, (left, right) =>
            (left.Mesh, left.Primitive, left.FirstTriangle, left.Count).CompareTo((right.Mesh, right.Primitive, right.FirstTriangle, right.Count)));
        for (int index = 1; index < support.Length; index++)
        {
            EquipmentSupportRange last = support[index - 1], range = support[index];
            if (last.Mesh == range.Mesh && last.Primitive == range.Primitive && (long)last.FirstTriangle + last.Count > range.FirstTriangle)
                throw new ArgumentException($"{context}: support ranges {Describe(last)} and {Describe(range)} overlap.", nameof(inwardSupport));
        }
        if (support.Sum(range => (long)range.Count) > MaxSupportTriangles)
            throw new ArgumentException($"{context}: more than {MaxSupportTriangles} inward support triangles.", nameof(inwardSupport));

        if (!float.IsFinite(collarWidth) || collarWidth <= 0f)
            throw new ArgumentException($"{context}: collarWidth must be finite and positive.", nameof(collarWidth));
        if (!float.IsFinite(minZ) || !float.IsFinite(maxZ) || minZ >= maxZ)
            throw new ArgumentException($"{context}: caps must be finite with minZ below maxZ.", nameof(minZ));
        if (!float.IsFinite(clearance) || clearance <= 0f)
            throw new ArgumentException($"{context}: clearance must be finite and positive.", nameof(clearance));
        if (!float.IsFinite(maxWorldRecession) || maxWorldRecession < 0f)
            throw new ArgumentException($"{context}: maxWorldRecession must be finite and non-negative.", nameof(maxWorldRecession));

        Id = id;
        PartId = partId;
        HostRegion = hostRegion;
        CoverageLocal = coverageLocal;
        InwardSupport = Array.AsReadOnly(support);
        CollarWidth = collarWidth;
        MinZ = minZ == 0f ? 0f : minZ;
        MaxZ = maxZ == 0f ? 0f : maxZ;
        Clearance = clearance;
        MaxWorldRecession = maxWorldRecession == 0f ? 0f : maxWorldRecession;
    }

    public uint Id { get; }
    public uint PartId { get; }
    public string HostRegion { get; }
    public Matrix4x4 CoverageLocal { get; }
    public IReadOnlyList<EquipmentSupportRange> InwardSupport { get; }
    public float CollarWidth { get; }
    public float MinZ { get; }
    public float MaxZ { get; }
    public float Clearance { get; }
    public float MaxWorldRecession { get; }

    // First scope refuses mirrored frames, so the determinant must be strictly positive as well as invertible.
    static bool IsSupportedFrame(Matrix4x4 matrix) => AttachmentSetDefinition.Values(matrix).All(float.IsFinite)
        && matrix.M14 == 0f && matrix.M24 == 0f && matrix.M34 == 0f && matrix.M44 == 1f
        && matrix.GetDeterminant() is > 0f and var determinant && float.IsFinite(determinant)
        && Matrix4x4.Invert(matrix, out Matrix4x4 inverse) && AttachmentSetDefinition.Values(inverse).All(float.IsFinite);

    static string Describe(EquipmentSupportRange range) => $"mesh {range.Mesh} primitive {range.Primitive} triangles {range.FirstTriangle}+{range.Count}";
}

/// <summary>
/// Immutable equipment coverage companion, the in-memory form of strict equipment coverage JSON format 1
/// (<see cref="EquipmentCoverageJson"/>). It binds the exact attachment definition bytes
/// (<see cref="AttachmentSha256"/>) to one host map semantic identity (<see cref="HostMapSha256"/>) and lists up to
/// <see cref="MaxCommands"/> commands ordered by unique positive ID. Part, region and support resolution against the
/// actual assets happens where those assets are bound.
/// </summary>
public sealed class EquipmentCoverageDefinition
{
    public const int MaxCommands = 4;

    readonly string semanticSha256;

    public EquipmentCoverageDefinition(string attachmentSha256, string hostMapSha256, IEnumerable<EquipmentCoverageCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!AttachmentSetDefinition.ValidHash(attachmentSha256))
            throw new ArgumentException("attachmentSha256 must be a lowercase SHA-256.", nameof(attachmentSha256));
        if (!AttachmentSetDefinition.ValidHash(hostMapSha256))
            throw new ArgumentException("hostMapSha256 must be a lowercase SHA-256.", nameof(hostMapSha256));
        EquipmentCoverageCommand[] owned = commands.ToArray();
        if (owned.Length is 0 or > MaxCommands)
            throw new ArgumentException($"A coverage companion must contain 1 to {MaxCommands} commands.", nameof(commands));
        if (Array.IndexOf(owned, null) >= 0)
            throw new ArgumentException("A coverage command cannot be null.", nameof(commands));
        Array.Sort(owned, (left, right) => left.Id.CompareTo(right.Id));
        for (int index = 1; index < owned.Length; index++)
            if (owned[index].Id == owned[index - 1].Id)
                throw new ArgumentException($"Coverage command {owned[index].Id}: IDs must be unique.", nameof(commands));

        AttachmentSha256 = attachmentSha256;
        HostMapSha256 = hostMapSha256;
        Commands = Array.AsReadOnly(owned);
        semanticSha256 = EquipmentCoverageJson.ComputeSemanticSha256(this);
    }

    /// <summary>Lowercase SHA-256 of the exact attachment definition file bytes this companion applies to.</summary>
    public string AttachmentSha256 { get; }

    /// <summary>Semantic SHA-256 of the host coverage map this companion targets.</summary>
    public string HostMapSha256 { get; }

    public IReadOnlyList<EquipmentCoverageCommand> Commands { get; }
    internal string SemanticSha256 => semanticSha256;
}
