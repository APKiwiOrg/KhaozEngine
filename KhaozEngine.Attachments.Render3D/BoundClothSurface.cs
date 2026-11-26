using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>An embedding indexed within one skinned material draw.</summary>
/// <param name="VertexIndex">Index into the material's full fitted vertex array.</param>
/// <param name="Triangle">Index into the shared material rig.</param>
/// <param name="Barycentric">Weights on the shared triangle.</param>
/// <param name="SignedOffset">The authored distance along the midsurface normal.</param>
/// <param name="FittedOffset">The full offset in the fitted triangle frame. The axes are normalized B minus A,
/// the oriented normal crossed with that axis, and the oriented normal. It includes garment lift.</param>
public readonly record struct BoundClothEmbedding(int VertexIndex, int Triangle, Vector3 Barycentric, float SignedOffset,
    Vector3 FittedOffset);

/// <summary>An intrinsic pattern attachment for one moving vertex in a skinned material draw.</summary>
public readonly record struct BoundClothMaterialEmbedding(int VertexIndex, int Triangle, Vector3 Barycentric, float SignedOffset);

/// <summary>The garment's fitted vertices and selected triangle indices in one material slot.</summary>
public sealed class BoundClothMaterial
{
    readonly bool[] _moving;

    internal BoundClothMaterial(ImmutableArray<SkinnedVertex> vertices, ImmutableArray<uint> visibleIndices,
        ImmutableArray<BoundClothEmbedding> embeddings, ImmutableArray<BoundClothMaterialEmbedding> materialEmbeddings)
    {
        (Vertices, VisibleIndices, Embeddings) = (vertices, visibleIndices, embeddings);
        MaterialEmbeddings = materialEmbeddings;
        _moving = new bool[Vertices.Length];
        foreach (BoundClothEmbedding embedding in embeddings) _moving[embedding.VertexIndex] = true;
    }

    internal bool HasMovingVertices(ReadOnlySpan<uint> indices)
    {
        foreach (uint vertex in indices)
            if (_moving[vertex]) return true;
        return false;
    }

    public ImmutableArray<SkinnedVertex> Vertices { get; }
    public ImmutableArray<uint> VisibleIndices { get; }
    public ImmutableArray<BoundClothEmbedding> Embeddings { get; }

    /// <summary>The full intrinsic mapping for this view, in fitted embedding order. Default for version 1.</summary>
    public ImmutableArray<BoundClothMaterialEmbedding> MaterialEmbeddings { get; }
}

/// <summary>The checked cloth rig and posed sampling data for one fitted part in a set or outfit.</summary>
public sealed class BoundClothSurface
{
    readonly CheckedClothSurface _checked;
    readonly SkinnedVertex[] _source;
    readonly ResolvedClothPoint[] _pins;
    readonly ResolvedClothPoint[] _fallback;
    readonly Matrix4x4[] _inverseBind;
    readonly Matrix4x4[] _restPalette;
    readonly int[] _hostBones;
    readonly int _hostBoneCount;
    readonly Vector3[] _fittedNodes;

    internal BoundClothSurface(CheckedClothSurface asset, CoverageSourceTopology source, SkinnedGltfMesh[] materials,
        ResolvedClothPoint[] pins, ResolvedClothPoint[] fallback, ResolvedClothVertex[] render, ReadOnlySpan<int> hostBones,
        int hostBoneCount, ReadOnlySpan<Matrix4x4> restPalette, IReadOnlyList<ReadOnlyMemory<Vector3>>? lifted, uint[]?[]? visible)
    {
        _checked = asset;
        _source = source.SkinVertices.ToArray();
        _pins = pins;
        _fallback = fallback;
        _inverseBind = (Matrix4x4[])materials[0].InverseBind.Clone();
        _restPalette = restPalette.ToArray();
        _hostBones = hostBones.ToArray();
        _hostBoneCount = hostBoneCount;

        for (int i = 0; lifted is not null && i < _source.Length; i++)
        {
            CoverageSourceVertex mapping = source.Vertices[i];
            _source[i].Position = lifted[mapping.MaterialSlice].Span[mapping.SliceVertex];
        }

        var composed = new Matrix4x4[_inverseBind.Length];
        Compose(restPalette, _inverseBind, composed);
        var posed = new Vector3[_source.Length];
        for (int i = 0; i < posed.Length; i++) posed[i] = Skin(_source[i], composed);
        _fittedNodes = new Vector3[_fallback.Length];
        for (int i = 0; i < _fittedNodes.Length; i++)
            if (!_fallback[i].TrySample(posed, out _fittedNodes[i]))
                throw asset.Refuse(ClothSurfaceRefusal.FittedTriangle, "$.fallback", "The fitted fallback sample is degenerate.", i);
        for (int i = 0; i < _pins.Length; i++)
            if (!_pins[i].TrySample(posed, out _fittedNodes[asset.File.Pins[i].Node]))
                throw asset.Refuse(ClothSurfaceRefusal.FittedTriangle, "$.pins", "The fitted pin sample is degenerate or nonfinite.", i);

        var frames = new ClothFittedFrame[Rig.Triangles.Length];
        for (int triangle = 0; triangle < frames.Length; triangle++)
            if (!ClothFittedFrame.TryCreate(Rig.Triangles[triangle], _fittedNodes, out frames[triangle]))
                throw asset.Refuse(ClothSurfaceRefusal.FittedTriangle, "$.triangles", "The fitted triangle has no finite frame.", triangle);

        var embeddings = new List<BoundClothEmbedding>[materials.Length];
        var intrinsic = HasMaterialMapping ? new List<BoundClothMaterialEmbedding>[materials.Length] : null;
        for (int i = 0; i < materials.Length; i++)
        {
            embeddings[i] = [];
            if (intrinsic is not null) intrinsic[i] = [];
        }
        foreach (ResolvedClothVertex vertex in render)
        {
            CoverageSourceVertex mapping = source.Vertices[vertex.Source];
            ClothRenderEmbedding entry = vertex.Embedding;
            ClothTriangle triangle = Rig.Triangles[entry.Triangle];
            Vector3 barycentric = CheckedClothSurface.Blend(triangle, _fittedNodes, entry.Barycentric);
            Vector3 fittedOffset = frames[entry.Triangle].Coordinates(posed[vertex.Source] - barycentric);
            embeddings[mapping.MaterialSlice].Add(new(mapping.SliceVertex, entry.Triangle, entry.Barycentric,
                entry.SignedOffset, fittedOffset));
            ClothRenderEmbedding material = vertex.Material;
            intrinsic?[mapping.MaterialSlice].Add(new(mapping.SliceVertex, material.Triangle, material.Barycentric,
                material.SignedOffset));
        }

        var fitted = ImmutableArray.CreateBuilder<BoundClothMaterial>(materials.Length);
        for (int material = 0; material < materials.Length; material++)
        {
            SkinnedVertex[] vertices = (SkinnedVertex[])materials[material].Vertices.Clone();
            if (lifted is not null)
                for (int i = 0; i < vertices.Length; i++) vertices[i].Position = lifted[material].Span[i];
            uint[] visibleIndices = visible?[material] ?? materials[material].Indices32;
            var view = new BoundClothMaterial(ImmutableArray.Create(vertices), ImmutableArray.Create(visibleIndices),
                ImmutableArray.CreateRange(embeddings[material]),
                intrinsic is null ? default : ImmutableArray.CreateRange(intrinsic[material]));
            HasVisibleMotion |= view.HasMovingVertices(visibleIndices);
            fitted.Add(view);
        }
        Materials = fitted.MoveToImmutable();
    }

    public uint PartId => _checked.PartId;
    internal bool CompatibleMaterial(BoundClothSurface other) =>
        ReferenceEquals(Rig, other.Rig) || (_checked.SidecarDigest is { } digest &&
            digest == other._checked.SidecarDigest && Description.MeshSha256 == other.Description.MeshSha256);

    internal int JointCount => _inverseBind.Length;
    public ClothRig Rig => _checked.Rig;
    public ClothSurfaceFile Description => _checked.File;
    public bool HasMaterialMapping => !Description.MaterialRender.IsDefault;
    public ClothRoundTrip RoundTrip => _checked.RoundTrip;
    public ReadOnlySpan<Vector3> FittedNodes => _fittedNodes;
    public ImmutableArray<BoundClothMaterial> Materials { get; }
    internal bool HasVisibleMotion { get; }

    internal BoundClothSurface? FitOutfit(IReadOnlyList<ReadOnlyMemory<Vector3>>? lifted, uint[]?[]? visible)
    {
        if (!HasVisibleMotion) return null;
        if (visible is not null)
        {
            bool moving = false;
            for (int i = 0; !moving && i < Materials.Length; i++)
                moving = Materials[i].HasMovingVertices(visible[i] is { } indices ? indices : Materials[i].VisibleIndices.AsSpan());
            if (!moving) return null;
        }
        return lifted is null && visible is null ? this : _checked.Fit(_hostBones, _hostBoneCount, _restPalette, lifted, visible);
    }

    /// <summary>Sample the pinned nodes in rig pin order using the current host joint palette and model transform.</summary>
    public void CopyPins(ReadOnlySpan<Matrix4x4> hostPalette, in Matrix4x4 model, Span<Vector3> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(hostPalette.Length, _hostBoneCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(output.Length, _pins.Length);
        CheckModel(model);
        Span<Matrix4x4> composed = stackalloc Matrix4x4[_inverseBind.Length];
        for (int bone = 0; bone < composed.Length; bone++)
        {
            int host = _hostBones[bone];
            if (host >= 0 && !AttachmentSetBinding.ValidMatrix(hostPalette[host]))
                throw _checked.Refuse(ClothSurfaceRefusal.HostPose, "$.hostPalette", "The pinned garment needs a valid host joint pose.", host);
            composed[bone] = host >= 0 ? SkinningMath.Compose(hostPalette[host], _inverseBind[bone]) : Matrix4x4.Identity;
        }
        CopySamples(_pins, composed, model, "$.pins", output);
    }

    /// <summary>Sample one point per node from a fallback garment palette in garment bone order.</summary>
    public void CopyFallbackNodes(ReadOnlySpan<Matrix4x4> garmentPalette, in Matrix4x4 model, Span<Vector3> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(garmentPalette.Length, _inverseBind.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(output.Length, _fallback.Length);
        CheckModel(model);
        for (int bone = 0; bone < garmentPalette.Length; bone++)
            if (!AttachmentSetBinding.ValidMatrix(garmentPalette[bone]))
                throw _checked.Refuse(ClothSurfaceRefusal.GarmentPose, "$.garmentPalette", "The fallback garment needs a valid joint pose.", bone);
        Span<Matrix4x4> composed = stackalloc Matrix4x4[_inverseBind.Length];
        Compose(garmentPalette, _inverseBind, composed);
        CopySamples(_fallback, composed, model, "$.fallback", output);
    }

    void CheckModel(in Matrix4x4 model)
    {
        if (!AttachmentSetBinding.ValidMatrix(model))
            throw _checked.Refuse(ClothSurfaceRefusal.ModelPose, "$.model", "The model transform must be finite, affine and invertible.");
    }

    void CopySamples(ReadOnlySpan<ResolvedClothPoint> samples, ReadOnlySpan<Matrix4x4> composed, in Matrix4x4 model,
        string path, Span<Vector3> output)
    {
        Span<Vector3> posed = stackalloc Vector3[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            ResolvedClothPoint sample = samples[i];
            if (!sample.TrySample(Skin(_source[sample.A], composed), Skin(_source[sample.B], composed),
                    Skin(_source[sample.C], composed), out Vector3 point))
                throw _checked.Refuse(ClothSurfaceRefusal.FittedTriangle, path, "The posed cloth sample is degenerate.", i);
            posed[i] = Vector3.Transform(point, model);
            if (!ChainMath.IsFinite(posed[i]))
                throw _checked.Refuse(ClothSurfaceRefusal.ModelPose, path, "The transformed cloth sample is nonfinite.", i);
        }
        posed.CopyTo(output);
    }

    internal static Vector3 Skin(in SkinnedVertex vertex, ReadOnlySpan<Matrix4x4> composed) =>
        Vector3.Transform(vertex.Position, SkinningMath.BlendSkinMatrix(composed, vertex.BoneIndices, vertex.BoneWeights));

    internal static void Compose(ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<Matrix4x4> inverseBind,
        Span<Matrix4x4> composed)
    {
        for (int bone = 0; bone < composed.Length; bone++)
            composed[bone] = SkinningMath.Compose(palette[bone], inverseBind[bone]);
    }
}
