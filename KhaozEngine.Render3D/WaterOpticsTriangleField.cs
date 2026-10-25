using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Local X, signed rest depth, local Z vertices of one authoritative bed triangle, in metres.</summary>
public readonly record struct WaterOpticsTriangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>A closed local XZ rectangle whose entire area and boundary have certified triangle support.</summary>
public readonly record struct WaterOpticsRectangle(Vector2 Minimum, Vector2 Maximum);

/// <summary>An immutable, bounded scalar atlas of a certified single-valued piecewise planar optical bed.</summary>
public sealed class WaterOpticsTriangleField
{
    public const int MaxDimension = 1024;
    public const int MaxTrianglesPerBin = 32;

    private readonly float[] _atlas;
    private readonly WaterOpticsRectangle[] _certificates;

    /// <summary>Copies the source, proves complete coverage, then packs owned storage. Positive-area overlaps of
    /// distinct planes refuse as ambiguous. Shared segments and points whose depths differ are certified instead:
    /// their exact discontinuity is charged to <see cref="ReconstructionErrorBoundMetres"/>, and a total above
    /// 0.0001 metres refuses as ambiguous.</summary>
    public WaterOpticsTriangleField(Vector2 size, int binColumns, int binRows,
        ReadOnlySpan<WaterOpticsTriangle> triangles, ReadOnlySpan<WaterOpticsRectangle> certifiedRectangles)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0)
            throw new ArgumentException("Field size must be finite and positive.", nameof(size));
        if (binColumns < 1 || binColumns > MaxDimension || binRows < 1 || binRows > MaxDimension)
            throw new ArgumentException("Bin axes must be between 1 and 1024.", nameof(binColumns));
        if (triangles.IsEmpty) throw new ArgumentException("At least one source triangle is required.", nameof(triangles));
        if (certifiedRectangles.IsEmpty)
            throw new ArgumentException("At least one certified rectangle is required.", nameof(certifiedRectangles));
        long minimumScalars = checked(16L + 2L * binColumns * binRows + 12L * triangles.Length);
        if (minimumScalars > (long)MaxDimension * MaxDimension)
            throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.CapacityExceeded,
                "Even the fixed atlas records exceed the scalar capacity.");
        WaterOpticsTriangle[] sources = triangles.ToArray();
        _certificates = certifiedRectangles.ToArray();
        foreach (WaterOpticsTriangle triangle in sources)
        {
            ValidateVertex(triangle.A, size);
            ValidateVertex(triangle.B, size);
            ValidateVertex(triangle.C, size);
        }
        foreach (WaterOpticsRectangle rectangle in _certificates)
        {
            Vector2 lo = rectangle.Minimum, hi = rectangle.Maximum;
            if (!float.IsFinite(lo.X) || !float.IsFinite(lo.Y) || !float.IsFinite(hi.X) || !float.IsFinite(hi.Y)
                || lo.X < 0 || lo.Y < 0 || hi.X > size.X || hi.Y > size.Y || lo.X >= hi.X || lo.Y >= hi.Y)
                throw new ArgumentException("Certificates must be finite, nonempty rectangles within the field.", nameof(certifiedRectangles));
        }
        WaterOpticsTriangleIndex.Packed packed = WaterOpticsTriangleIndex.Build(size, binColumns, binRows, sources, _certificates);
        Size = size;
        BinColumns = binColumns;
        BinRows = binRows;
        TriangleCount = sources.Length;
        EdgeLineCount = packed.Lines;
        CandidateReferenceCount = packed.References;
        MaximumBinOccupancy = packed.MaximumOccupancy;
        ReconstructionErrorBoundMetres = packed.ReconstructionErrorBound;
        TextureWidth = packed.Width;
        TextureHeight = packed.Height;
        _atlas = packed.Scalars;
        TextureBytes = checked(4L * TextureWidth * TextureHeight);
        RetainedBytes = checked(TextureBytes + 16L * _certificates.Length);
    }

    public Vector2 Size { get; }
    public int BinColumns { get; }
    public int BinRows { get; }
    public int TriangleCount { get; }
    public int EdgeLineCount { get; }
    public int CandidateReferenceCount { get; }
    public int MaximumBinOccupancy { get; }
    public int TextureWidth { get; }
    public int TextureHeight { get; }
    public long TextureBytes { get; }

    /// <summary>Upward-rounded bound consumed by local mapping, interpolation and shared-boundary depth
    /// discontinuity, in metres. Every sample whose exact local query point lies in a certified rectangle is within
    /// it of each drawn triangle containing the query, whichever owns a boundary. Producers must include this value
    /// with source conversion and render-origin costs in their total error budget.</summary>
    public float ReconstructionErrorBoundMetres { get; }

    /// <summary>Owned padded float payload plus rectangle payload bytes, excluding object headers.</summary>
    public long RetainedBytes { get; }
    public ReadOnlySpan<WaterOpticsRectangle> CertifiedRectangles => _certificates;

    /// <summary>Immutable physical atlas upload view, including zero padding. No array escapes ownership.</summary>
    internal ReadOnlySpan<float> AtlasScalars => _atlas;

    /// <summary>Samples signed depth, throwing when the normalized query is outside all certificates.</summary>
    public float SampleDepth(float u, float v)
    {
        if (TrySampleDepth(u, v, out float depth)) return depth;
        throw new ArgumentException("The query is outside certified optical coverage.");
    }

    /// <summary>Reports absence independently of signed depth. Invalid normalized coordinates always throw.</summary>
    public bool TrySampleDepth(float u, float v, out float restDepth)
    {
        if (!float.IsFinite(u) || !float.IsFinite(v) || u < 0 || v < 0 || u > 1 || v > 1)
            throw new ArgumentException("Normalized queries must be finite and within [0, 1].");
        float x = u * Size.X, z = v * Size.Y;
        bool certified = false;
        foreach (WaterOpticsRectangle rectangle in _certificates)
        {
            if (x >= rectangle.Minimum.X && x <= rectangle.Maximum.X && z >= rectangle.Minimum.Y && z <= rectangle.Maximum.Y)
            {
                certified = true;
                break;
            }
        }
        restDepth = default;
        if (!certified) return false;
        int column = Math.Min((int)(u * BinColumns), BinColumns - 1);
        int row = Math.Min((int)(v * BinRows), BinRows - 1);
        int binAddress = 16 + 2 * (row * BinColumns + column);
        int first = (int)_atlas[binAddress], count = (int)_atlas[binAddress + 1];
        int triangleBase = (int)_atlas[4], edgeBase = (int)_atlas[5];
        for (int i = 0; i < count; i++)
        {
            int triangleAddress = triangleBase + 12 * (int)_atlas[first + i];
            if (!WaterOpticsTriangleMath.Contains(_atlas, triangleAddress, edgeBase, x, z)) continue;
            restDepth = WaterOpticsTriangleMath.Interpolate(_atlas.AsSpan(triangleAddress, 12), x, z);
            return true;
        }
        throw new InvalidOperationException("A certified optical query found no packed triangle candidate.");
    }

    private static void ValidateVertex(Vector3 vertex, Vector2 size)
    {
        if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z)
            || vertex.X < 0 || vertex.Z < 0 || vertex.X > size.X || vertex.Z > size.Y)
            throw new ArgumentException("Source vertices must be finite and within the field's local XZ extent.", nameof(vertex));
    }
}
