using System;
using System.Numerics;

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
    /// <see cref="MapWaterCapacityException"/> for a compile budget, once the total passes the limit.
    /// Document refusals carry WorkCapacityExceeded with Budget, ActualCount and Limit.</summary>
    public void Charge(long checks)
    {
        Charged += checks;
        _parent?.Charge(checks);
        if (Charged <= Limit) return;
        if (_compile) throw new MapWaterCapacityException(MapWaterWorkMeter.PairChecks, "MaxPairChecks", Charged, Limit);
        throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded, $"domain work exceeds {Limit} pair checks")
        { Budget = "WaterDomainPairChecks", ActualCount = Charged, Limit = Limit };
    }
}

/// <summary>Thrown when compile work passes one of its <see cref="MapWaterCompileLimits"/>. <see cref="Exception.Message"/>
/// is the limit's detail, such as <c>water pair checks</c>.</summary>
internal sealed class MapWaterCapacityException : Exception
{
    internal MapWaterCapacityException(string detail, string? budget = null, long? actual = null, long? limit = null) : base(detail) =>
        Finding = new(MapDocumentRefusal.WorkCapacityExceeded, detail) { Budget = budget, ActualCount = actual, Limit = limit };

    internal MapDocumentFinding Finding { get; }
}

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
        PieceCount = (int)Math.Min(pieces, int.MaxValue);
        if (pieces > _limits.MaxPieces) throw new MapWaterCapacityException(Pieces, "MaxPieces", pieces, _limits.MaxPieces);
        long vertices = 4L * (domain.Rects?.Count ?? 0);
        foreach (MapWaterRing ring in domain.Rings ?? Array.Empty<MapWaterRing>()) vertices += ring?.Vertices?.Count ?? 0;
        VertexCount = (int)Math.Min(vertices, int.MaxValue);
        if (vertices > _limits.MaxPieceVertices) throw new MapWaterCapacityException(PieceVertices, "MaxPieceVertices", vertices, _limits.MaxPieceVertices);
    }

    internal void ChargeFootprintCells(int cells) => CellCount = Charge(CellCount, cells, _limits.MaxFootprintCells, FootprintCells, "MaxFootprintCells");

    internal void CountFootprintRects(int rects) => FootprintRects = checked(FootprintRects + rects);

    /// <summary>Charges an entire bound-lattice candidate range arithmetically before its slot or cell loop starts.
    /// Wide counts make even ranges whose area exceeds Int64 refuse as capacity, without traversing them.</summary>
    internal void ChargeBoundTraversal(BigInteger slots, BigInteger cells)
    {
        _boundSlots += slots;
        _boundCells += cells;
        if (_boundSlots + _boundCells > _limits.MaxBoundCells) throw new MapWaterCapacityException(BoundCells, "MaxBoundCells",
            _boundSlots + _boundCells <= long.MaxValue ? (long)(_boundSlots + _boundCells) : null, _limits.MaxBoundCells);
    }

    /// <summary>Charges one bound read and the faces its preflight counted, before the read runs.</summary>
    internal void ChargeRead(long faces)
    {
        _boundFaces = checked(_boundFaces + faces);
        if (_boundFaces > _limits.MaxBoundFaces) throw new MapWaterCapacityException(BoundFaces, "MaxBoundFaces", _boundFaces, _limits.MaxBoundFaces);
        BoundReads = checked(BoundReads + 1);
    }

    internal void CountFaces(bool upper, int faces)
    {
        if (upper) UpperFaces = checked(UpperFaces + faces);
        else LowerFaces = checked(LowerFaces + faces);
    }

    internal void ChargeTriangles(int triangles) => TriangleCount = Charge(TriangleCount, triangles, _limits.MaxTriangles, Triangles, "MaxTriangles");

    static int Charge(int current, int amount, int limit, string detail, string budget)
    {
        long total = (long)current + amount;
        if (total > limit) throw new MapWaterCapacityException(detail, budget, total, limit);
        return (int)total;
    }
}
