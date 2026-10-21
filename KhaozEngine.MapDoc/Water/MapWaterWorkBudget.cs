using System;
using System.Numerics;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Counts the pair checks that domain work spends against a fixed limit, so simplicity, overlap and
/// triangulation stay bounded and an oversized domain refuses deterministically. One budget serves one validation
/// call, one compile or one overlap test.</summary>
internal sealed class MapWaterWorkBudget
{
    readonly bool _compile;
    readonly MapWaterWorkBudget? _parent;

    public MapWaterWorkBudget(long limit = MapWaterDomain.MaxPairChecks) : this(limit, compile: false) { }

    /// <summary>A compile budget throws <see cref="MapWaterCapacityException"/> instead, which prepared containment
    /// propagates, so the compile refuses with its limit rather than reporting the overrun as unverified.</summary>
    internal MapWaterWorkBudget(long limit, bool compile, MapWaterWorkBudget? parent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        Limit = limit;
        _compile = compile;
        _parent = parent;
    }

    public long Limit { get; }
    public long Charged { get; private set; }
    public bool Exhausted => Charged > Limit;

    /// <summary>Charges <paramref name="checks"/> pair checks and throws <see cref="MapDocumentException"/>, or
    /// <see cref="MapWaterCapacityException"/> for a compile budget, once the total passes the limit.</summary>
    public void Charge(long checks)
    {
        Charged += checks;
        _parent?.Charge(checks);
        if (Charged <= Limit) return;
        if (_compile) throw new MapWaterCapacityException(MapWaterWorkMeter.PairChecks);
        throw new MapDocumentException($"domain work exceeds {Limit} pair checks");
    }
}

/// <summary>Thrown when compile work passes one of its <see cref="MapWaterCompileLimits"/>. <see cref="Exception.Message"/>
/// is the limit's detail, such as <c>water pair checks</c>.</summary>
internal sealed class MapWaterCapacityException(string limit) : Exception(limit);

/// <summary>The work one body's compile spends, each count checked against its limit before the work it guards. Pair
/// checks run through one compile <see cref="MapWaterWorkBudget"/> shared by the domain kernel, containment and region
/// clipping.</summary>
internal sealed class MapWaterWorkMeter
{
    internal const string Pieces = "water pieces";
    internal const string PieceVertices = "water piece vertices";
    internal const string FootprintCells = "water footprint cells";
    internal const string BoundFaces = "water bound faces";
    internal const string BoundCells = "water bound cells";
    internal const string PairChecks = "water pair checks";
    internal const string Triangles = "water triangles";

    // A piece retains 128 B of wide bounds plus its record, name, ring-array and convex-cache headers/references.
    const long DomainPieceBytes = 512;
    // Per vertex: 64 B wide + 32 B narrow + up to 192 B triangulated corners + 128 B bounded rational limbs.
    // The remaining 96 B covers triangle-array headers, index lists and spare capacity.
    const long DomainVertexBytes = 512;
    // Each cell can retain 136 B of rectangle fields, a tree node, a 24 B snapshot entry and 40 B merge rows/list growth.
    const long FootprintCellBytes = 512;
    // One read retains its result, three list/wrapper sets and patch metadata, including face-free reads.
    const long BoundReadBytes = 512;
    // Four 200 B bound-face copies plus list spare capacity and face/cell indexes. Source compilation is separate.
    const long BoundFaceBytes = 1280;
    // An Int64 row preflight count plus an Int64 traversal/index slot per candidate, including absent cells.
    const long BoundCandidateBytes = 16;

    readonly MapWaterCompileLimits _limits;
    long _boundFaces;
    BigInteger _boundSlots, _boundCells;

    internal MapWaterWorkMeter(MapWaterCompileLimits limits)
    {
        _limits = limits;
        Pairs = new MapWaterWorkBudget(limits.MaxPairChecks, compile: true);
    }

    internal MapWaterWorkBudget Pairs { get; }
    internal int PieceCount { get; private set; }
    internal int VertexCount { get; private set; }
    internal int FootprintRects { get; private set; }
    internal int CellCount { get; private set; }
    internal int BoundReads { get; private set; }
    internal int UpperFaces { get; private set; }
    internal int LowerFaces { get; private set; }
    internal int TriangleCount { get; private set; }

    internal MapWaterWork Work => new(PieceCount, VertexCount, FootprintRects, CellCount, BoundReads, UpperFaces, LowerFaces,
        (int)Math.Min(Pairs.Charged, int.MaxValue), TriangleCount)
    {
        BoundSlots = (int)BigInteger.Min(_boundSlots, int.MaxValue),
        BoundCells = (int)BigInteger.Min(_boundCells, int.MaxValue),
    };

    /// <summary>Counts the domain's pieces and ring vertices from the document, before any geometry.</summary>
    internal void ChargeDomain(MapWaterDomainDoc domain)
    {
        long pieces = (long)(domain.Rects?.Count ?? 0) + (domain.Rings?.Count ?? 0);
        int nextPieces = (int)Math.Min(pieces, int.MaxValue);
        if (pieces > _limits.MaxPieces)
        {
            PieceCount = nextPieces;
            throw new MapWaterCapacityException(Pieces);
        }
        long vertices = 4L * (domain.Rects?.Count ?? 0);
        foreach (MapWaterRing ring in domain.Rings ?? Array.Empty<MapWaterRing>()) vertices += ring?.Vertices?.Count ?? 0;
        int nextVertices = (int)Math.Min(vertices, int.MaxValue);
        if (vertices > _limits.MaxPieceVertices)
        {
            PieceCount = nextPieces; VertexCount = nextVertices;
            throw new MapWaterCapacityException(PieceVertices);
        }
        MapSceneAdmission.Current?.ReserveBytes(checked(pieces * DomainPieceBytes + vertices * DomainVertexBytes));
        PieceCount = nextPieces; VertexCount = nextVertices;
    }

    internal void ChargeFootprintCells(int cells)
    {
        int next = Charge(CellCount, cells, _limits.MaxFootprintCells, FootprintCells);
        MapSceneAdmission.Current?.ReserveBytes(checked(cells * FootprintCellBytes));
        CellCount = next;
    }

    internal void CountFootprintRects(int rects) => FootprintRects = checked(FootprintRects + rects);

    /// <summary>Charges an entire bound-lattice candidate range arithmetically before its slot or cell loop starts.
    /// Wide counts make even ranges whose area exceeds Int64 refuse as capacity, without traversing them.</summary>
    internal void ChargeBoundTraversal(BigInteger slots, BigInteger cells)
    {
        var nextSlots = _boundSlots + slots;
        var nextCells = _boundCells + cells;
        if (nextSlots + nextCells > _limits.MaxBoundCells)
        {
            // Released water-limit diagnostics retain attempted demand. Scene refusals publish no new counts.
            _boundSlots = nextSlots; _boundCells = nextCells;
            throw new MapWaterCapacityException(BoundCells);
        }
        MapSceneAdmission.Current?.ReserveBytes(checked((long)(slots + cells) * BoundCandidateBytes));
        _boundSlots = nextSlots; _boundCells = nextCells;
    }

    /// <summary>Charges one bound read and the faces its preflight counted, before the read runs.</summary>
    internal void ChargeRead(long faces)
    {
        long nextFaces = checked(_boundFaces + faces);
        if (nextFaces > _limits.MaxBoundFaces)
        {
            _boundFaces = nextFaces;
            throw new MapWaterCapacityException(BoundFaces);
        }
        int nextReads = checked(BoundReads + 1);
        // The surface compiler charges PreparedFaces. This reserves only the additional bound-read copies.
        MapSceneAdmission.Current?.ReserveBytes(checked(BoundReadBytes + faces * BoundFaceBytes));
        _boundFaces = nextFaces; BoundReads = nextReads;
    }

    internal void CountFaces(bool upper, int faces)
    {
        if (upper) UpperFaces = checked(UpperFaces + faces);
        else LowerFaces = checked(LowerFaces + faces);
    }

    internal void ChargeTriangles(int triangles)
    {
        int next = Charge(TriangleCount, triangles, _limits.MaxTriangles, Triangles);
        MapSceneAdmission.Current?.ReserveCompilation(triangles);
        TriangleCount = next;
    }

    static int Charge(int current, int amount, int limit, string detail)
    {
        long total = (long)current + amount;
        if (total > limit) throw new MapWaterCapacityException(detail);
        return (int)total;
    }
}
