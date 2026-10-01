using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// A tile world described as static colliders: data only, registered with no physics world. Build it once per
/// loaded world and catalog set, and build it again after an edit.
/// </summary>
public sealed class TileWorldColliders
{
    readonly byte[] _hash;

    TileWorldColliders(TileCollider[] colliders, byte[] hash, TileGroundSampler ground, TileMediumSampler medium)
    {
        Colliders = Array.AsReadOnly(colliders);
        _hash = hash;
        Ground = ground;
        Medium = medium;
    }

    /// <summary>
    /// Every collider in canonical order: by <see cref="TileColliderKind"/>, then region (z, then x), then anchor tile
    /// (z, then x), then object id. A wall corner lists its two edges in the order the collision baker applies them,
    /// and an object lists its walk surfaces in catalog order.
    /// </summary>
    public IReadOnlyList<TileCollider> Colliders { get; }

    /// <summary>
    /// SHA-256 over every collider in order: its kind byte, its shape as <see cref="PropCollisionFormat.Write"/>
    /// writes it, and its pose as seven little-endian floats (position x, y, z, then orientation x, y, z, w). The same
    /// world and catalogs hash equal on every rebuild and reload, whatever order the regions were created or loaded
    /// in. Across machines the hash is exact for every box on a quarter turn. A walk surface on a yaw that is not a
    /// quarter turn takes its centre and orientation from <see cref="MathF"/> trigonometry, which .NET does not
    /// promise is bit-identical across x64 and ARM64, so such a world can hash differently on another architecture.
    /// Each read returns a fresh copy.
    /// </summary>
    public byte[] Hash => (byte[])_hash.Clone();

    /// <summary>The floor on plane 0, read from the same drawn ground triangles as the ground colliders.</summary>
    public TileGroundSampler Ground { get; }

    /// <summary>The water medium on plane 0, from the water bodies of the loaded regions.</summary>
    public TileMediumSampler Medium { get; }

    /// <summary>
    /// Describes the loaded regions of a document as colliders. Plane 0 and walk surfaces only: objects and walk
    /// surfaces on other planes are skipped, and so are roofs and objects whose archetype the catalogs do not define.
    /// <list type="bullet">
    /// <item><b>Ground:</b> one triangle mesh per region with any drawable tile, the
    /// <see cref="TileGroundTriangles.Build"/> triangles with the second and third index of each swapped so the
    /// one-sided front of a backend mesh faces up, posed at the region's world origin.</item>
    /// <item><b>Wall:</b> one box per edge a placed <c>Wall</c> or <c>WallCorner</c> blocks, as the collision baker
    /// reads its rotation, <see cref="TileColliderOptions.WallThickness"/> thick and centred on the edge.</item>
    /// <item><b>Blocked:</b> one box per tile with no underlay or marked <see cref="TileSettings.Blocked"/>, from the
    /// tile's lowest corner up <see cref="TileColliderOptions.BlockedHeight"/>.</item>
    /// <item><b>Object:</b> a box over a <c>Solid</c> object's rotated footprint, or a <c>Diagonal</c> object's
    /// anchor tile.</item>
    /// <item><b>WalkSurface:</b> a box <see cref="TileColliderOptions.WalkSurfaceThickness"/> thick under each walk
    /// surface's top, over its rectangle as <see cref="TileWalkSurfaces"/> resolves it. A rectangle that resolves
    /// empty gets none.</item>
    /// </list>
    /// A wall or object box's top is the height the model stands at, <see cref="TileObjectPlacement.AnchorPosition"/>,
    /// plus the archetype's <see cref="TileObjectArchetype.CollisionHeight"/>. Its bottom is the lower of the lowest
    /// ground corner under it and that standing height, so it never inverts on a steep slope. Every box is
    /// axis-aligned except a walk surface yawed off a quarter turn.
    /// <para>A <see cref="TileSettings.NoDraw"/> tile gets no ground triangle and no blocked box, so it is a hole in the
    /// physics floor. NoDraw marks a plane whose floor is an object, the upper-floor case this round leaves
    /// out.</para>
    /// <para>The floor and water samplers, <see cref="Ground"/> and <see cref="Medium"/>, are built here from the same
    /// document and catalogs.</para>
    /// </summary>
    /// <param name="document">The world. Only its loaded regions are read.</param>
    /// <param name="catalogs">The archetypes its objects are placed from and the materials that mark water.</param>
    /// <param name="options">Thicknesses and the blocked height, defaults when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="catalogs"/> is
    /// null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="TileColliderOptions.WallThickness"/>,
    /// <see cref="TileColliderOptions.WalkSurfaceThickness"/> or a set <see cref="TileColliderOptions.BlockedHeight"/>
    /// is not a finite number above 0. The message names the option.</exception>
    /// <exception cref="TileWorldException">A placed <c>Solid</c>, <c>Diagonal</c>, <c>Wall</c> or <c>WallCorner</c>
    /// object's archetype has no <see cref="TileObjectArchetype.CollisionHeight"/>. The message names the
    /// archetype.</exception>
    public static TileWorldColliders Build(TileWorldDocument document, TileWorldCatalogs catalogs,
                                           TileColliderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new TileColliderOptions();
        RequirePositive(options.WallThickness, nameof(TileColliderOptions.WallThickness));
        RequirePositive(options.WalkSurfaceThickness, nameof(TileColliderOptions.WalkSurfaceThickness));
        if (options.BlockedHeight is { } blockedHeight)
            RequirePositive(blockedHeight, nameof(TileColliderOptions.BlockedHeight));

        RegionCoord[] regions = document.Regions.Keys.OrderBy(c => c.Rz).ThenBy(c => c.Rx).ToArray();
        TileObject[] placed = TileColliderBuilder.PlacedObjects(document, regions);
        var colliders = new List<TileCollider>();
        TileColliderBuilder.AddGround(document, regions, colliders);
        TileColliderBuilder.AddWalls(document, catalogs, placed, options.WallThickness, colliders);
        TileColliderBuilder.AddBlocked(document, regions, options.BlockedHeight ?? document.PlaneHeight, colliders);
        TileColliderBuilder.AddObjects(document, catalogs, placed, colliders);
        TileColliderBuilder.AddWalkSurfaces(document, catalogs, placed, options.WalkSurfaceThickness, colliders);

        TileCollider[] ordered = colliders.ToArray();
        return new TileWorldColliders(ordered, HashOf(ordered), new TileGroundSampler(document, regions),
                                      new TileMediumSampler(document, catalogs, regions));
    }

    static void RequirePositive(float value, string option)
    {
        if (!(float.IsFinite(value) && value > 0f))
            throw new ArgumentOutOfRangeException("options", value,
                $"{nameof(TileColliderOptions)}.{option} must be a finite number above 0.");
    }

    static byte[] HashOf(TileCollider[] colliders)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var shape = new MemoryStream();
        Span<byte> kind = stackalloc byte[1];
        Span<byte> pose = stackalloc byte[7 * sizeof(float)];
        foreach (TileCollider collider in colliders)
        {
            kind[0] = (byte)collider.Kind;
            sha.AppendData(kind);

            shape.SetLength(0);
            PropCollisionFormat.Write(collider.Shape, shape);
            sha.AppendData(shape.GetBuffer(), 0, (int)shape.Length);

            Pose p = collider.Pose;
            BinaryPrimitives.WriteSingleLittleEndian(pose[0..], p.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(pose[4..], p.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(pose[8..], p.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(pose[12..], p.Orientation.X);
            BinaryPrimitives.WriteSingleLittleEndian(pose[16..], p.Orientation.Y);
            BinaryPrimitives.WriteSingleLittleEndian(pose[20..], p.Orientation.Z);
            BinaryPrimitives.WriteSingleLittleEndian(pose[24..], p.Orientation.W);
            sha.AppendData(pose);
        }
        return sha.GetHashAndReset();
    }
}
