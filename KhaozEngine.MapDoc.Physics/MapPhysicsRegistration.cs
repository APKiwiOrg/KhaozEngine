using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Movement;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>What one installed static belongs to: a placement id or a terrain chunk id, and its kind.</summary>
public sealed record MapStaticOwner(string OwnerId, MapStaticKind Kind);

/// <summary>A built world's statics installed into a caller-owned <see cref="IPhysicsWorld"/>, with each handle mapped
/// back to its owner and each terrain triangle to its canonical face. Disposing removes every static and leaves the
/// physics world itself to its owner.</summary>
public sealed class MapPhysicsRegistration : IDisposable
{
    readonly List<StaticHandle> _handles;
    readonly Dictionary<StaticHandle, (MapStaticOwner Owner, IReadOnlyList<MapFaceKey> Faces)> _owners;
    bool _disposed;

    /// <summary>The registered world.</summary>
    public MapBuiltWorld World { get; }

    /// <summary>The caller-owned physics world the statics are installed in.</summary>
    public IPhysicsWorld Physics { get; }

    /// <summary>One handle per static, in <see cref="MapBuiltWorld.Statics"/> order. Empty once disposed.</summary>
    public IReadOnlyList<StaticHandle> Handles => _handles;

    MapPhysicsRegistration(MapBuiltWorld world, IPhysicsWorld physics, List<StaticHandle> handles,
        Dictionary<StaticHandle, (MapStaticOwner Owner, IReadOnlyList<MapFaceKey> Faces)> owners)
    {
        World = world;
        Physics = physics;
        _handles = handles;
        _owners = owners;
    }

    /// <summary>Installs every static of <paramref name="world"/> into <paramref name="physics"/> in static order, at
    /// <c>Position - physics.Origin</c>. Throws <see cref="MapDocumentException"/> naming "whole-metre origin" when an
    /// origin component is not a whole metre, since terrain vertices are exact only against whole-metre anchors. Never
    /// call this inside a held query read lease: the backend refuses mutation then. Any exception removes every static
    /// already added, in reverse order, and propagates.</summary>
    public static MapPhysicsRegistration Register(MapBuiltWorld world, IPhysicsWorld physics)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(physics);
        Vector3 origin = physics.Origin;
        if (!float.IsInteger(origin.X) || !float.IsInteger(origin.Y) || !float.IsInteger(origin.Z))
            throw new MapDocumentException(FormattableString.Invariant(
                $"Registration needs a whole-metre origin, and the physics world's origin is ({origin.X}, {origin.Y}, {origin.Z})."));

        var handles = new List<StaticHandle>(world.Statics.Count);
        var owners = new Dictionary<StaticHandle, (MapStaticOwner Owner, IReadOnlyList<MapFaceKey> Faces)>(world.Statics.Count);
        try
        {
            foreach (MapStaticDescriptor descriptor in world.Statics)
            {
                StaticHandle handle = physics.AddStatic(descriptor.Shape,
                    new Pose(descriptor.Position - origin, descriptor.Orientation));
                handles.Add(handle);
                owners.Add(handle, (new MapStaticOwner(descriptor.OwnerId, descriptor.Kind), descriptor.TriangleOwners));
            }
        }
        catch (Exception fault)
        {
            List<Exception>? rollback = RemoveAll(physics, handles);
            if (rollback is null) throw;
            rollback.Insert(0, fault);
            throw new AggregateException("Registration failed and its rollback left statics behind.", rollback);
        }
        return new MapPhysicsRegistration(world, physics, handles, owners);
    }

    /// <summary>The owner of <paramref name="handle"/>, false for a handle this registration did not add.</summary>
    public bool TryOwner(StaticHandle handle, [MaybeNullWhen(false)] out MapStaticOwner owner)
    {
        if (_owners.TryGetValue(handle, out var entry))
        {
            owner = entry.Owner;
            return true;
        }
        owner = null;
        return false;
    }

    /// <summary>The canonical face of triangle <paramref name="faceId"/> of a terrain chunk's mesh, which is the
    /// <see cref="CapsuleIncidentFace.FaceId"/> of an incident face the feature query reports for that chunk. A
    /// feature id is not a triangle index, since edges and vertices number after faces. False for a placement, a handle
    /// this registration did not add or a face id outside the mesh.</summary>
    public bool TryFaceOwner(StaticHandle handle, int faceId, out MapFaceKey face)
    {
        if (_owners.TryGetValue(handle, out var entry) && (uint)faceId < (uint)entry.Faces.Count)
        {
            face = entry.Faces[faceId];
            return true;
        }
        face = default;
        return false;
    }

    /// <summary>A ground move context over a resolver-1 world: its analytic support height, this registration's physics
    /// world and a clamp to the document's playable bounds, with no ground normal, no medium and no query selection.
    /// Throws <see cref="MapDocumentException"/> for a native world, which moves on the contact controller.</summary>
    public GroundMoveContext CreateLegacyMoveContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (World.IsNative || World.LegacySupportHeight is not { } height)
            throw new MapDocumentException("Native worlds move on the contact controller (#438 phase 5), not a legacy move context.");
        MapResolvedBounds playable = World.Document.PlayableBounds;
        return new GroundMoveContext(height, null, Physics,
            (x, z) => new Vector2(Math.Clamp(x, playable.MinX, playable.MaxX), Math.Clamp(z, playable.MinZ, playable.MaxZ)),
            null, null);
    }

    /// <summary>Removes every static this registration added, in reverse order. Never disposes <see cref="Physics"/>.
    /// A second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = RemoveAll(Physics, _handles);
        _handles.Clear();
        _owners.Clear();
        if (failures is not null) throw new AggregateException("Registration left statics behind.", failures);
    }

    // Removes every handle in reverse order, attempting each even after a failure. Returns the failures, or null.
    static List<Exception>? RemoveAll(IPhysicsWorld physics, List<StaticHandle> handles)
    {
        List<Exception>? failures = null;
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            try
            {
                physics.RemoveStatic(handles[i]);
            }
            catch (Exception failure)
            {
                (failures ??= new()).Add(failure);
            }
        }
        return failures;
    }
}
