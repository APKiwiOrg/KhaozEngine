using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Maximum authored seam and render embedding errors, measured on the imported garment's rest skin.</summary>
public readonly record struct ClothRoundTrip(float PinErrorMetres, float RenderErrorMetres);

/// <summary>Checks an offline cloth sidecar with the same import mapping, skinning and error limit used at load.</summary>
public static class ClothSurfaceBinding
{
    public static ClothRoundTrip Check(ReadOnlyMemory<byte> glb, ClothSurfaceFile file, ChainMotionDefinition fallback,
        string sourceName, uint partId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fallback);
        ClothRig rig = file.CreateRig(sourceName, partId);
        string digest = Convert.ToHexStringLower(SHA256.HashData(glb.Span));
        return Read(glb, digest, file, fallback, rig, sourceName, partId).RoundTrip;
    }

    internal static CheckedClothSurface Read(ReadOnlyMemory<byte> glb, string digest, ClothSurfaceFile file,
        ChainMotionDefinition fallback, ClothRig rig, string sourceName, uint partId)
    {
        if (digest != file.MeshSha256)
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.MeshDigest, sourceName, "The cloth sidecar tags different GLB bytes.")
            { PartId = partId, JsonPath = "$.meshSha256" };
        try
        {
            AttachmentGlb.ValidateSkinnedEmbedded(glb, sourceName);
            var (materials, source) = GltfLoader.LoadSkinnedSource(glb, sourceName, digest);
            return new CheckedClothSurface(file, rig, fallback, sourceName, partId, source, materials);
        }
        catch (AttachmentSkinnedGeometryDataException error)
        {
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.SourceInfluence, sourceName, "The cloth requires an admitted skinned GLB.", error)
            { PartId = partId, ElementIndex = error.VertexIndex, JsonPath = error.JsonPath };
        }
        catch (Exception error) when (error is MalformedDataException and not ClothSurfaceDataException
            or GltfImportStateException or MeshIndexStateException)
        {
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.SourceGeometry, sourceName, "The cloth GLB source cannot be decoded.", error)
            { PartId = partId };
        }
    }
}

internal sealed class CheckedClothSurface
{
    readonly CoverageSourceTopology _source;
    readonly SkinnedGltfMesh[] _materials;
    readonly ResolvedClothPoint[] _pins;
    readonly ResolvedClothPoint[] _fallback;
    readonly ResolvedClothVertex[] _render;

    internal CheckedClothSurface(ClothSurfaceFile file, ClothRig rig, ChainMotionDefinition fallback,
        string sourceName, uint partId, CoverageSourceTopology source, SkinnedGltfMesh[] materials)
    {
        File = file;
        Rig = rig;
        SourceName = sourceName;
        PartId = partId;
        _source = source;
        _materials = materials;
        if (source.Vertices.Count != file.SourceVertexCount)
            throw Refuse(ClothSurfaceRefusal.Count, "$.sourceVertices", "The source vertex count differs from the GLB.");
        if (source.Triangles.Count != file.SourceTriangleCount)
            throw Refuse(ClothSurfaceRefusal.Count, "$.sourceTriangles", "The source triangle count differs from the GLB.");

        var sourceVertices = new Dictionary<ClothSourceVertex, int>(source.Vertices.Count);
        for (int index = 0; index < source.Vertices.Count; index++)
        {
            CoverageSourceVertex vertex = source.Vertices[index];
            sourceVertices.Add(new(vertex.Mesh, vertex.Primitive, vertex.Vertex), index);
        }
        var triangles = source.Triangles.ToDictionary(triangle => (triangle.Mesh, triangle.Primitive, triangle.Triangle));
        var moving = FallbackInfluences(fallback, materials[0].Skeleton!);
        var embedded = new bool[moving.Length];
        _render = new ResolvedClothVertex[file.Render.Length];
        for (int i = 0; i < _render.Length; i++)
        {
            ClothRenderEmbedding entry = file.Render[i];
            if (!sourceVertices.TryGetValue(entry.Vertex, out int vertex))
                throw Refuse(ClothSurfaceRefusal.RenderEmbedding, "$.render", "The embedding names a missing source vertex.", i);
            _render[i] = new(vertex, entry);
            embedded[vertex] = true;
        }

        for (int vertex = 0; vertex < moving.Length; vertex++)
        {
            if (moving[vertex] && !embedded[vertex])
                throw Refuse(ClothSurfaceRefusal.UnembeddedFallbackVertex, "$.sourceVertices",
                    "A vertex influenced by a fallback chain needs a cloth embedding.", vertex);
            moving[vertex] |= embedded[vertex];
        }

        var fixedVertices = new bool[source.Vertices.Count];
        foreach (CoverageSourceTriangle triangle in source.Triangles)
            if (!moving[triangle.A] && !moving[triangle.B] && !moving[triangle.C])
                fixedVertices[triangle.A] = fixedVertices[triangle.B] = fixedVertices[triangle.C] = true;
        _pins = new ResolvedClothPoint[file.Pins.Length];
        for (int i = 0; i < _pins.Length; i++)
        {
            ResolvedClothPoint point = Resolve(file.Pins[i].Point, "$.pins", i);
            if (moving[point.A] || moving[point.B] || moving[point.C] || !fixedVertices[point.A])
                throw Refuse(ClothSurfaceRefusal.PinMovesWithCloth, "$.pins", "A pin needs a fixed host-skinned source.", i);
            _pins[i] = point;
        }

        _fallback = new ResolvedClothPoint[file.Fallback.Length];
        for (int i = 0; i < _fallback.Length; i++) _fallback[i] = Resolve(file.Fallback[i], "$.fallback", i);

        RoundTrip = CheckRoundTrip();

        ResolvedClothPoint Resolve(ClothSourcePoint point, string path, int index)
        {
            if (point.Barycentric is Vector3 weights &&
                triangles.TryGetValue((point.Mesh, point.Primitive, point.Element), out var triangle))
                return new(triangle.A, triangle.B, triangle.C, weights, point.SignedOffset, IsTriangle: true);
            if (point.Barycentric is null &&
                sourceVertices.TryGetValue(new(point.Mesh, point.Primitive, point.Element), out int vertex))
                return new(vertex, vertex, vertex, Vector3.UnitX, 0, IsTriangle: false);
            throw Refuse(ClothSurfaceRefusal.SourcePoint, path, "The sample names a missing GLB vertex or triangle.", index);
        }
    }

    internal ClothSurfaceFile File { get; }
    internal ClothRig Rig { get; }
    internal string SourceName { get; }
    internal uint PartId { get; }
    internal ClothRoundTrip RoundTrip { get; }

    bool[] FallbackInfluences(ChainMotionDefinition fallback, Skeleton skeleton)
    {
        var skin = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int bone = 0; bone < skeleton.BoneCount; bone++)
            skin.Add(skeleton.NodeNames[skeleton.JointToNode[bone]], bone);
        var fallbackJoints = fallback.Chains.SelectMany(chain => chain.Joints).ToHashSet(StringComparer.Ordinal);
        var bones = new bool[skeleton.BoneCount];
        for (int chainIndex = 0; chainIndex < fallback.Chains.Count; chainIndex++)
        {
            ChainDefinition chain = fallback.Chains[chainIndex];
            int previous = -1;
            foreach (string name in chain.Joints)
            {
                if (!skin.TryGetValue(name, out int bone))
                    throw Refuse(ClothSurfaceRefusal.FallbackHierarchy, "$.fallback.chains", "A fallback joint must belong to the garment skin.", chainIndex);
                int node = skeleton.JointToNode[bone];
                int parent = skeleton.ParentIndices[node];
                if (previous >= 0 ? parent != previous : parent < 0 || string.IsNullOrEmpty(skeleton.NodeNames[parent]))
                    throw Refuse(ClothSurfaceRefusal.FallbackHierarchy, "$.fallback.chains", "A fallback requires an ordered parent line below a named anchor.", chainIndex);
                if (previous < 0 && fallbackJoints.Contains(skeleton.NodeNames[parent]))
                    throw Refuse(ClothSurfaceRefusal.FallbackHierarchy, "$.fallback.chains", "A fallback anchor must be outside every fallback chain.", chainIndex);
                bones[bone] = true;
                previous = node;
            }
        }
        var flags = new bool[_source.Influences.Length];
        for (int i = 0; i < flags.Length; i++)
        {
            CoverageSkinInfluence influence = _source.Influences[i];
            Vector4 j = influence.Joints, w = influence.Weights;
            flags[i] = (w.X > 0 && bones[(int)j.X]) || (w.Y > 0 && bones[(int)j.Y])
                || (w.Z > 0 && bones[(int)j.Z]) || (w.W > 0 && bones[(int)j.W]);
        }
        return flags;
    }

    ClothRoundTrip CheckRoundTrip()
    {
        var composed = new Matrix4x4[_materials[0].InverseBind.Length];
        BoundClothSurface.Compose(_materials[0].RestPose, _materials[0].InverseBind, composed);
        var posed = new Vector3[_source.Vertices.Count];
        for (int vertex = 0; vertex < posed.Length; vertex++)
            posed[vertex] = BoundClothSurface.Skin(_source.SkinVertices[vertex], composed);
        var nodes = new Vector3[_fallback.Length];
        float pinError = 0, renderError = 0;
        for (int i = 0; i < nodes.Length; i++)
            if (!_fallback[i].TrySample(posed, out nodes[i]))
                throw Refuse(ClothSurfaceRefusal.FittedTriangle, "$.fallback", "The fitted source triangle has no finite normal.", i);
        for (int i = 0; i < _pins.Length; i++)
        {
            if (!_pins[i].TrySample(posed, out Vector3 pin))
                throw Refuse(ClothSurfaceRefusal.FittedTriangle, "$.pins", "The fitted pin sample is degenerate or nonfinite.", i);
            int node = File.Pins[i].Node;
            pinError = MathF.Max(pinError, CheckError(pin, nodes[node], "$.pins", i));
            nodes[node] = pin;
        }
        var frames = new ClothFittedFrame[Rig.Triangles.Length];
        for (int triangle = 0; triangle < frames.Length; triangle++)
            if (!ClothFittedFrame.TryCreate(Rig.Triangles[triangle], nodes, out frames[triangle]))
                throw Refuse(ClothSurfaceRefusal.FittedTriangle, "$.triangles", "The fitted triangle has no finite frame.", triangle);
        for (int i = 0; i < _render.Length; i++)
        {
            ResolvedClothVertex vertex = _render[i];
            ClothRenderEmbedding e = vertex.Embedding;
            ClothTriangle triangle = Rig.Triangles[e.Triangle];
            Vector3 reconstructed = Blend(triangle, nodes, e.Barycentric) + frames[e.Triangle].Normal * e.SignedOffset;
            renderError = MathF.Max(renderError, CheckError(reconstructed, posed[vertex.Source], "$.render", i));
        }
        return new ClothRoundTrip(pinError, renderError);
    }

    float CheckError(Vector3 a, Vector3 b, string path, int index)
    {
        float error = Vector3.Distance(a, b);
        if (!float.IsFinite(error) || error > ClothSurfaceFile.RoundTripTolerance)
            throw Refuse(ClothSurfaceRefusal.RoundTrip, path, "The fitted round-trip error exceeds the cloth limit.", index, error);
        return error;
    }

    internal static Vector3 Blend(ClothTriangle t, ReadOnlySpan<Vector3> nodes, Vector3 w) =>
        nodes[t.A] * w.X + nodes[t.B] * w.Y + nodes[t.C] * w.Z;

    internal ClothSurfaceDataException Refuse(ClothSurfaceRefusal reason, string path, string message, int? index = null,
        float? error = null) => new(reason, SourceName, message)
        { PartId = PartId, ElementIndex = index, ErrorMetres = error, JsonPath = index is int i ? $"{path}[{i}]" : path };

    internal BoundClothSurface Fit(ReadOnlySpan<int> hostBones, int hostBoneCount, ReadOnlySpan<Matrix4x4> garmentRestPose,
        IReadOnlyList<ReadOnlyMemory<Vector3>>? lifted = null, uint[]?[]? visible = null) =>
        new(this, _source, _materials, _pins, _fallback, _render, hostBones, hostBoneCount, garmentRestPose, lifted, visible);
}

internal readonly record struct ResolvedClothVertex(int Source, ClothRenderEmbedding Embedding);

internal readonly record struct ResolvedClothPoint(int A, int B, int C, Vector3 Weights, float Offset, bool IsTriangle)
{
    internal bool TrySample(ReadOnlySpan<Vector3> source, out Vector3 point) => TrySample(source[A], source[B], source[C], out point);

    internal bool TrySample(Vector3 a, Vector3 b, Vector3 c, out Vector3 point)
    {
        point = IsTriangle ? a * Weights.X + b * Weights.Y + c * Weights.Z : a;
        if (!IsTriangle) return ChainMath.IsFinite(point);
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float length = normal.Length();
        if (!(length > 0) || !float.IsFinite(length)) return false;
        if (Offset != 0) point += normal / length * Offset;
        return ChainMath.IsFinite(point);
    }
}

internal readonly record struct ClothFittedFrame(Vector3 U, Vector3 V, Vector3 Normal)
{
    internal static bool TryCreate(ClothTriangle triangle, ReadOnlySpan<Vector3> nodes, out ClothFittedFrame frame)
    {
        Vector3 first = nodes[triangle.B] - nodes[triangle.A];
        Vector3 second = nodes[triangle.C] - nodes[triangle.A];
        Vector3 normal = Vector3.Cross(first, second);
        float length = first.Length(), area = normal.Length();
        if (!(length > 0 && area > 0 && float.IsFinite(length) && float.IsFinite(area)))
        {
            frame = default;
            return false;
        }
        Vector3 u = first / length, n = normal / area;
        frame = new(u, Vector3.Cross(n, u), n);
        return true;
    }

    internal Vector3 Coordinates(Vector3 direction) => new(Vector3.Dot(direction, U), Vector3.Dot(direction, V), Vector3.Dot(direction, Normal));
}
