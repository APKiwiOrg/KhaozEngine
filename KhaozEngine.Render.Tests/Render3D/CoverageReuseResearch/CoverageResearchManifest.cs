using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>One payload named by an absolute path and its exact SHA-256. The bytes are read once and checked.</summary>
internal sealed record CoverageResearchFile(string Path, string Sha256)
{
    internal byte[] Read()
    {
        byte[] bytes = File.ReadAllBytes(Path);
        string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (actual != Sha256) throw new InvalidDataException($"Research payload hash mismatch: expected {Sha256}, read {actual}.");
        return bytes;
    }
}

/// <summary>A rigid equipment part by attachment part ID. Anchor frames list part worlds in manifest order.</summary>
internal sealed record CoverageResearchPart(uint Id, CoverageResearchFile Glb);

/// <summary>Settings for a footprint proposal of one host region and one command when no accepted mapping exists. The
/// support is the selected mesh's triangles facing inward along coverage -Z. The region is the connected outward-facing
/// host patch within the footprint margin and depth window in the reference anchor. Core triangles lie inside the
/// support domain by the core inset, away from the patch boundary. An optional dominant-joint allowlist restricts every
/// source corner before patch selection. All distances are coverage or world metres.</summary>
internal sealed record CoverageResearchProposal(
    uint PartId, int SupportMesh, double InwardCosine, float[] CoverageLocal, string ReferenceAnchor, string RegionKey,
    double CoreInset, double PatchMargin, double PatchDepth, int RefinementLevel,
    float CollarWidth, float MinZ, float MaxZ, float Clearance, float MaxWorldRecession, string OutputDirectory,
    int[]? EligibleDominantJoints = null);

/// <summary>Research fixture manifest format 1. Every payload is external and hash checked, so a private fixture never
/// enters the repository and the loader stays generic. Either both HostMap and Coverage are supplied, or Proposal.</summary>
internal sealed record CoverageResearchManifest(
    int FormatVersion, string Fixture, CoverageResearchFile Host, CoverageResearchFile Attachment, CoverageResearchPart[] Parts,
    CoverageResearchFile? HostMap, CoverageResearchFile? Coverage, CoverageResearchProposal? Proposal,
    CoverageResearchFile Anchors, string Output)
{
    internal const string Variable = "KE_COVERAGE_RESEARCH_MANIFEST";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal static CoverageResearchManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<CoverageResearchManifest>(File.ReadAllBytes(path), Options)
            ?? throw new InvalidDataException("Empty research manifest.");
        if (manifest.FormatVersion != 1) throw new InvalidDataException("Research manifest format must be 1.");
        if (manifest.Parts is not { Length: > 0 } || manifest.Parts.Select(p => p.Id).Distinct().Count() != manifest.Parts.Length)
            throw new InvalidDataException("Research manifest parts must be present with unique IDs.");
        bool supplied = manifest.HostMap is not null && manifest.Coverage is not null;
        if (supplied == (manifest.Proposal is not null) || (manifest.HostMap is null) != (manifest.Coverage is null))
            throw new InvalidDataException("Supply either a host map and coverage companion, or a proposal.");
        if (!Path.IsPathRooted(manifest.Output)) throw new InvalidDataException("Research output must be an absolute path.");
        return manifest;
    }
}

/// <summary>One posed frame: actor model, skin palette and each manifest part's rigid draw world.</summary>
internal sealed record CoverageResearchFrame(float[] Model, float[][] Palette, float[][] Parts)
{
    internal Matrix4x4 ModelMatrix => CoverageResearchAnchors.Matrix(Model);
    internal Matrix4x4[] PaletteMatrices => Palette.Select(CoverageResearchAnchors.Matrix).ToArray();
    internal Matrix4x4 PartWorld(int index) => CoverageResearchAnchors.Matrix(Parts[index]);
}

/// <summary>A named anchor pose, with the frame before it when the source sampled one.</summary>
internal sealed record CoverageResearchAnchor(string Key, CoverageResearchFrame Current, CoverageResearchFrame? Previous);

/// <summary>Anchor file format: an object with an <c>anchors</c> array. Matrices are System.Numerics fields M11..M44.
/// Other members are ignored, so a source may carry its own sampler descriptions beside the frames.</summary>
internal static class CoverageResearchAnchors
{
    sealed record Document(CoverageResearchAnchor[] Anchors);

    internal static CoverageResearchAnchor[] Read(byte[] json)
    {
        var document = JsonSerializer.Deserialize<Document>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty anchor file.");
        if (document.Anchors is not { Length: > 0 }) throw new InvalidDataException("Anchor file has no anchors.");
        return document.Anchors;
    }

    internal static Matrix4x4 Matrix(float[] m)
    {
        if (m is not { Length: 16 } || m.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Anchor matrices need 16 finite values.");
        return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    }
}
