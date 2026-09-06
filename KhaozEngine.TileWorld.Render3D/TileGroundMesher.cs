using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Knobs for <see cref="TileGroundMesher"/>.</summary>
public sealed class TileGroundMesherOptions
{
    ITileGroundSlotMap _slots = IdentitySlotMap.Instance;
    float _jitterAmplitude = TileColors.DefaultJitterAmplitude;

    /// <summary>Per-tile brightness jitter, plus or minus this fraction of the material colour, from 0 (which
    /// disables it) up to but not including 1. The ceiling is load-bearing: the vertex carries the jitter as a
    /// MULTIPLIER, so an amplitude of 1 or more could hand it the 0 that renders a vertex black.</summary>
    public float JitterAmplitude
    {
        get => _jitterAmplitude;
        set => _jitterAmplitude = TileGroundMesher.CheckedAmplitude(value, nameof(value));
    }

    /// <summary>True to take every corner normal from the global height lattice, false for one flat normal per
    /// triangle.</summary>
    public bool SmoothNormals { get; set; } = true;

    /// <summary>Where the material ids in the vertices come from: the slot each one occupies in the material set
    /// the mesh will be drawn with. Defaults to <see cref="IdentitySlotMap.Instance"/>, which is the stand-in for
    /// a caller that has not built a set yet.</summary>
    public ITileGroundSlotMap Slots
    {
        get => _slots;
        set => _slots = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>Builds the ground mesh of one region-plane for the tile-ground pipeline: each drawable tile cut into
/// triangles by the shared <see cref="TileTriangulation"/> (two for a plain tile or a diagonal half, four for a
/// corner cut), each tile carrying the four material slots of its corners with per-vertex weights over them, a
/// per-vertex brightness jitter, and normals read from the global lattice so neighbouring regions agree exactly
/// at their shared border. The material ids and the jitter are read from the GLOBAL tile grid, so a corner is
/// the same material at the same weight from every tile and every region that touches it.</summary>
public static partial class TileGroundMesher
{
    /// <summary>What a material id missing from the catalogs reads as, so a dangling id is visible rather than
    /// invisible: the colour the headless readers paint it, and the colour a material set fills its reserved
    /// <see cref="ITileGroundSlotMap.MissingSlot"/> layer with.</summary>
    public static readonly Vector4 MissingMaterialColor = new(1f, 0f, 1f, 1f);

    /// <summary>The ground mesh of one region-plane in region-local coordinates (draw it with
    /// <see cref="WorldMatrix"/>), or null when the region-plane has no drawable tile.</summary>
    public static GltfMesh? Build(
        TileWorldDocument doc,
        TileWorldCatalogs catalogs,
        RegionCoord region,
        int plane,
        TileGroundMesherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);

        var context = new TileMeshContext(doc, catalogs, options ?? new TileGroundMesherOptions(), region, plane);
        var mesh = new MeshAccumulator();
        for (int lz = 0; lz < TileRegion.Size; lz++)
            for (int lx = 0; lx < TileRegion.Size; lx++)
            {
                if (!IsDrawable(doc, context.OriginX + lx, context.OriginZ + lz, plane)) continue;
                AddTile(mesh, context, lx, lz);
            }
        return mesh.ToMesh();
    }

    /// <summary>Where a region's mesh sits in the world: the corner of its lowest tile coordinates, with Y left
    /// at 0 because the mesh already carries absolute corner heights. The mesh itself is region-LOCAL and runs
    /// from 0 to +64 tiles on x but from 0 to MINUS 64 tiles on z, because world z is minus tile z
    /// (<see cref="TileWorldSpace"/>).</summary>
    public static Matrix4x4 WorldMatrix(TileWorldDocument doc, RegionCoord region)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return Matrix4x4.CreateTranslation(
            TileWorldSpace.WorldX(region.OriginX, doc.TileSize),
            0f,
            TileWorldSpace.WorldZ(region.OriginZ, doc.TileSize));
    }

    /// <summary>The smooth normal at a lattice corner, from central differences over the global height lattice.
    /// Reads across region borders, so two regions meeting at this corner compute the identical normal.</summary>
    public static Vector3 CornerNormal(TileWorldDocument doc, int worldX, int worldZ, int plane)
    {
        ArgumentNullException.ThrowIfNull(doc);
        float hx = doc.CornerHeight(worldX + 1, worldZ, plane) - doc.CornerHeight(worldX - 1, worldZ, plane);
        float hz = doc.CornerHeight(worldX, worldZ + 1, plane) - doc.CornerHeight(worldX, worldZ - 1, plane);
        float span = 2f * doc.TileSize;
        // The normal is WORLD space, and hz is a difference in TILE z, which runs opposite to world z. So the
        // world-z gradient of the surface is -hz/span, and negating that for the normal leaves +hz/span: ground
        // rising toward north (-world z) tilts its normal toward +world z, which is south.
        return Vector3.Normalize(new Vector3(-hx / span, 1f, hz / span));
    }

    /// <summary>The blended underlay colour at a lattice corner: the average of the jittered material colours of
    /// the up-to-four visible underlays that share it. Void tiles and underlays hidden by full overlays are
    /// excluded. All void blends to <see cref="TileColors.Void"/>.
    /// A <see cref="TileSettings.NoDraw"/> tile draws no ground of its own but DOES contribute its underlay here,
    /// so the ground colour stays continuous across a hole punched for an object floor.</summary>
    public static Vector4 CornerColor(
        TileWorldDocument doc,
        TileWorldCatalogs catalogs,
        int worldX,
        int worldZ,
        int plane,
        TileGroundMesherOptions options)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(options);

        // A NoDraw tile is deliberately still counted, because its neighbours' ground would otherwise step to a
        // hard edge at the hole rather than blending across it. A drawn full overlay hides its underlay entirely,
        // so that hidden material must not tint a neighbouring exposed surface.
        Span<Vector4> sharing = stackalloc Vector4[4];
        int count = 0;
        for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int tx = worldX + dx;
                int tz = worldZ + dz;
                ushort underlay = VisibleUnderlay(doc, tx, tz, plane);
                if (underlay == 0) continue;
                sharing[count++] = MaterialColor(catalogs, underlay) * TileColors.Jitter(tx, tz, plane, options.JitterAmplitude);
            }
        return TileColors.Blend(sharing[..count]);
    }

    /// <summary>True when the tile draws ground: it has an underlay and is not marked
    /// <see cref="TileSettings.NoDraw"/>.</summary>
    internal static bool IsDrawable(TileWorldDocument doc, int worldX, int worldZ, int plane) =>
        doc.GetUnderlay(worldX, worldZ, plane) != 0
        && (doc.GetSettings(worldX, worldZ, plane) & TileSettings.NoDraw) == 0;

    /// <summary>The material's colour, or <see cref="MissingMaterialColor"/> when the catalogs do not define it.</summary>
    internal static Vector4 MaterialColor(TileWorldCatalogs catalogs, ushort id)
    {
        GroundMaterial? material = catalogs.Material(id);
        return material is null ? MissingMaterialColor : TileColors.Parse(material);
    }

    static void AddTile(MeshAccumulator mesh, in TileMeshContext c, int lx, int lz)
    {
        int x = c.OriginX + lx;
        int z = c.OriginZ + lz;
        short h00 = c.Doc.CornerHeightCm(x, z, c.Plane);
        short h10 = c.Doc.CornerHeightCm(x + 1, z, c.Plane);
        short h01 = c.Doc.CornerHeightCm(x, z + 1, c.Plane);
        short h11 = c.Doc.CornerHeightCm(x + 1, z + 1, c.Plane);
        TileOverlayShape shape = c.Doc.GetOverlayShape(x, z, c.Plane);
        int rotation = c.Doc.GetOverlayRotation(x, z, c.Plane);
        bool swne = TileTriangulation.SplitSwNe(h00, h10, h01, h11, shape, rotation);

        // The overlay is a slot rather than a colour, so an id the catalogs no longer define lands on the slot
        // map's reserved slot and paints from there, the same way a dangling underlay does.
        ushort overlay = c.Doc.GetOverlay(x, z, c.Plane);
        int? overlaySlot = overlay == 0 ? null : c.Options.Slots.SlotOf(overlay);

        // A shape only cuts the tile when there is an overlay material to paint into the cut, so a shape with no
        // overlay meshes as the plain pair. The raycast reads the same two facts the same way, which is what keeps
        // a click on the triangle that was drawn. The split still comes from the authored shape above, because a
        // diagonal half forces the diagonal whether or not its material survived.
        TileOverlayShape cut = overlaySlot is null ? TileOverlayShape.Full : shape;
        AddCutTile(mesh, c, lx, lz, cut, rotation, overlaySlot, swne);
    }

    /// <summary>Adds a triangle, replacing the three corner normals with the triangle's own when the options ask
    /// for flat shading.</summary>
    static void AddTriangle(MeshAccumulator mesh, in TileMeshContext c, ModelVertex a, ModelVertex b, ModelVertex d)
    {
        if (!c.Options.SmoothNormals)
        {
            Vector3 face = FaceNormal(a.Position, b.Position, d.Position);
            a.Normal = face;
            b.Normal = face;
            d.Normal = face;
        }
        mesh.AddTriangle(a, b, d);
    }

    // The renderer culls nothing, so a face normal is flipped up rather than the winding being reversed: only the
    // explicit normal decides how the triangle lights.
    static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 cross = Vector3.Cross(b - a, c - a);
        if (cross.LengthSquared() <= 0f) return Vector3.UnitY;
        Vector3 normal = Vector3.Normalize(cross);
        // Unreachable for well-formed ground since world z became minus tile z: TileTriangulation hands back a
        // uniform tile-space winding, negating z flips it, and the sign of Y here depends only on that winding
        // projected onto xz, never on the corner heights. Kept as the guard it is, because it is the one thing
        // standing between a future lattice or triangulation change and a region of ground lit from underneath.
        return normal.Y < 0f ? -normal : normal;
    }

    /// <summary>The document, catalogs, options and region-plane one Build call meshes against, so the per-tile
    /// helpers do not carry six parameters each.</summary>
    internal readonly struct TileMeshContext
    {
        internal TileMeshContext(
            TileWorldDocument doc,
            TileWorldCatalogs catalogs,
            TileGroundMesherOptions options,
            RegionCoord region,
            int plane)
        {
            Doc = doc;
            Catalogs = catalogs;
            Options = options;
            OriginX = region.OriginX;
            OriginZ = region.OriginZ;
            Plane = plane;
            TileSize = doc.TileSize;
        }

        /// <summary>The world being meshed.</summary>
        public TileWorldDocument Doc { get; }
        /// <summary>The catalogs its material ids resolve through.</summary>
        public TileWorldCatalogs Catalogs { get; }
        /// <summary>The mesher options in force.</summary>
        public TileGroundMesherOptions Options { get; }
        /// <summary>World tile x of the region's SW corner.</summary>
        public int OriginX { get; }
        /// <summary>World tile z of the region's SW corner.</summary>
        public int OriginZ { get; }
        /// <summary>The plane being meshed.</summary>
        public int Plane { get; }
        /// <summary>Metres per tile.</summary>
        public float TileSize { get; }
    }

    /// <summary>Collects per-triangle vertices and indices. Vertices are never shared, because two triangles of
    /// one tile can carry different slots and weights (an overlay paints some of them and not others).</summary>
    internal sealed class MeshAccumulator
    {
        readonly List<ModelVertex> _vertices = new();
        readonly List<uint> _indices = new();

        /// <summary>Appends one triangle as three fresh vertices.</summary>
        public void AddTriangle(ModelVertex a, ModelVertex b, ModelVertex c)
        {
            uint first = (uint)_vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _indices.Add(first);
            _indices.Add(first + 1);
            _indices.Add(first + 2);
        }

        /// <summary>The collected mesh, or null when nothing was collected.</summary>
        public GltfMesh? ToMesh() =>
            _indices.Count == 0 ? null : new GltfMesh(_vertices.ToArray(), _indices.ToArray());
    }
}
