using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    Dictionary<AttachmentMeshRef, CachedBodyColliders>? _bodyColliders;

    /// <summary>Load verified collider bytes for the supplied body mesh and rig identity.</summary>
    /// <remarks>Repeated requests for the same key, SHA-256 and skeleton instance share the cache-owned result.
    /// The caller supplies the body's identity from its GLB bytes. A file for another rig refuses with
    /// <see cref="AttachmentBindingRefusal.BodyCollidersMismatch"/>.</remarks>
    /// <exception cref="ArgumentException">The body mesh has no skeleton.</exception>
    public LoadedBodyColliders GetBodyColliders(string key, string expectedSha256, string bodyRigIdentity, SkinnedGltfMesh hostMesh)
    {
        ThrowIfDisposed();
        ValidateRequest(key, expectedSha256);
        ArgumentNullException.ThrowIfNull(bodyRigIdentity);
        ArgumentNullException.ThrowIfNull(hostMesh);
        Skeleton skeleton = hostMesh.Skeleton ?? throw new ArgumentException("The body mesh must have a skeleton.", nameof(hostMesh));
        var reference = new AttachmentMeshRef(key, expectedSha256);
        if (_bodyColliders is null || !_bodyColliders.TryGetValue(reference, out CachedBodyColliders? cached))
        {
            var set = BodyCollidersJson.Parse(ReadVerified(reference, key), key);
            cached = new CachedBodyColliders(set);
            (_bodyColliders ??= []).Add(reference, cached);
        }
        LoadedBodyColliders.RequireRig(key, cached.Set, bodyRigIdentity);
        return cached.ForSkeleton(this, key, skeleton);
    }

    sealed class CachedBodyColliders(BodyColliderSet set)
    {
        // Collider data stays cached even after the host skeleton and its view are released.
        readonly ConditionalWeakTable<Skeleton, LoadedBodyColliders> _views = new();

        internal BodyColliderSet Set { get; } = set;

        internal LoadedBodyColliders ForSkeleton(AttachmentSetCache owner, string sourceName, Skeleton skeleton)
        {
            if (!_views.TryGetValue(skeleton, out LoadedBodyColliders? loaded))
            {
                loaded = new LoadedBodyColliders(owner, sourceName, Set, skeleton);
                _views.Add(skeleton, loaded);
            }
            return loaded;
        }
    }
}
