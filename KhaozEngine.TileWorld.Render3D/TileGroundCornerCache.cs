using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The lattice answers one <see cref="TileGroundMesher"/> build reads at a corner, computed once per corner rather
/// than once per tile touching it. A corner is shared by four tiles, and each of them used to rebuild its height,
/// its central-difference normal (four more height reads), its jitter and its material slot (four visible-underlay
/// walks apiece), so a 64 by 64 region paid about 16k corner evaluations for its 4225 corners.
/// <para>Scoped to one build of one region-plane, which is what makes it safe: nothing writes the document during a
/// build (the view thread meshes the live document between edits, a worker meshes a detached snapshot), and every
/// answer here is a pure function of the document, the plane and the options. A cached answer is the value the
/// direct computation returns, stored, so the memoised mesh is bit for bit the one the direct path builds. The
/// backing arrays are allocated on first use, so a region-plane with no drawable tile pays nothing for them, and
/// they are kept one per answer rather than one array of records so each stays under the large object heap
/// threshold and a rebuild's memo is an ordinary short-lived allocation.</para>
/// </summary>
internal sealed class TileGroundCornerCache
{
    const int Side = TileRegion.Size + 1;

    readonly TileWorldDocument _doc;
    readonly TileGroundMesherOptions _options;
    readonly int _originX;
    readonly int _originZ;
    readonly int _plane;
    readonly float _tileSize;
    readonly bool _memoise;
    // One entry per region-local lattice corner, lz * Side + lx, all allocated together on first use.
    byte[]? _known;
    short[]? _height;
    Vector3[]? _normal;
    float[]? _jitter;
    int[]? _slot;

    /// <summary>A cache for one region-plane of one build.</summary>
    /// <param name="doc">The world being meshed.</param>
    /// <param name="options">The mesher options in force: flat or smooth normals, the jitter amplitude, the slot map.</param>
    /// <param name="originX">World tile x of the region's lowest corner.</param>
    /// <param name="originZ">World tile z of the region's lowest corner.</param>
    /// <param name="plane">The plane being meshed.</param>
    /// <param name="memoise">False computes every answer afresh, which is the direct path the memo is proved
    /// against.</param>
    internal TileGroundCornerCache(TileWorldDocument doc, TileGroundMesherOptions options, int originX, int originZ,
                                   int plane, bool memoise)
    {
        _doc = doc;
        _options = options;
        _originX = originX;
        _originZ = originZ;
        _plane = plane;
        _tileSize = doc.TileSize;
        _memoise = memoise;
    }

    /// <summary>The corner's height in centimetres, <see cref="TileWorldDocument.CornerHeightCm"/>.</summary>
    internal short HeightCm(int cornerX, int cornerZ)
    {
        if (!TryEntry(cornerX, cornerZ, out int at)) return _doc.CornerHeightCm(cornerX, cornerZ, _plane);
        if ((_known![at] & KnownHeight) == 0)
        {
            _height![at] = _doc.CornerHeightCm(cornerX, cornerZ, _plane);
            _known[at] |= KnownHeight;
        }
        return _height![at];
    }

    /// <summary>What a vertex at this corner carries apart from its slots and weights: its region-local position,
    /// its normal (the lattice normal, or up when the options ask for flat shading) and its brightness jitter.</summary>
    internal void Point(int cornerX, int cornerZ, out Vector3 position, out Vector3 normal, out float jitter)
    {
        position = TileWorldSpace.ToWorld(cornerX - _originX, HeightCm(cornerX, cornerZ) * 0.01f,
                                          cornerZ - _originZ, _tileSize);
        if (!TryEntry(cornerX, cornerZ, out int at))
        {
            normal = ComputeNormal(cornerX, cornerZ);
            jitter = ComputeJitter(cornerX, cornerZ);
            return;
        }
        if ((_known![at] & KnownPoint) == 0)
        {
            _normal![at] = ComputeNormal(cornerX, cornerZ);
            _jitter![at] = ComputeJitter(cornerX, cornerZ);
            _known[at] |= KnownPoint;
        }
        normal = _normal![at];
        jitter = _jitter![at];
    }

    /// <summary>The slot the options' slot map holds this corner's <see cref="TileGroundMesher.CornerMaterial"/> in.</summary>
    internal int Slot(int cornerX, int cornerZ)
    {
        if (!TryEntry(cornerX, cornerZ, out int at)) return ComputeSlot(cornerX, cornerZ);
        if ((_known![at] & KnownSlot) == 0)
        {
            _slot![at] = ComputeSlot(cornerX, cornerZ);
            _known[at] |= KnownSlot;
        }
        return _slot![at];
    }

    Vector3 ComputeNormal(int cornerX, int cornerZ) =>
        _options.SmoothNormals ? TileGroundMesher.CornerNormal(_doc, cornerX, cornerZ, _plane) : Vector3.UnitY;

    float ComputeJitter(int cornerX, int cornerZ) =>
        TileGroundMesher.CornerJitter(_doc, cornerX, cornerZ, _plane, _options.JitterAmplitude);

    int ComputeSlot(int cornerX, int cornerZ) =>
        _options.Slots.SlotOf(TileGroundMesher.CornerMaterial(_doc, cornerX, cornerZ, _plane));

    // The entry for a corner of this region-plane's own lattice. A corner outside it, which no current caller
    // asks for, and every corner when memoising is off, answer false and are computed directly.
    bool TryEntry(int cornerX, int cornerZ, out int at)
    {
        int lx = cornerX - _originX;
        int lz = cornerZ - _originZ;
        if (!_memoise || (uint)lx >= Side || (uint)lz >= Side)
        {
            at = -1;
            return false;
        }
        if (_known is null)
        {
            _known = new byte[Side * Side];
            _height = new short[Side * Side];
            _normal = new Vector3[Side * Side];
            _jitter = new float[Side * Side];
            _slot = new int[Side * Side];
        }
        at = lz * Side + lx;
        return true;
    }

    const byte KnownHeight = 1;
    const byte KnownPoint = 2;
    const byte KnownSlot = 4;
}
