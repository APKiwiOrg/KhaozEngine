using System;
using System.Collections.Generic;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    readonly Dictionary<AttachmentMeshRef, LoadedBodyColliders> _bodyColliders = [];

    /// <summary>Load verified collider bytes for the supplied body rig.</summary>
    /// <remarks>Repeated requests for the same key and SHA-256 share the cache-owned result. The caller supplies
    /// the body's identity from its GLB bytes. A file for another rig refuses with
    /// <see cref="AttachmentBindingRefusal.BodyCollidersMismatch"/>.</remarks>
    public LoadedBodyColliders GetBodyColliders(string key, string expectedSha256, string bodyRigIdentity)
    {
        ThrowIfDisposed();
        ValidateRequest(key, expectedSha256);
        ArgumentNullException.ThrowIfNull(bodyRigIdentity);
        var reference = new AttachmentMeshRef(key, expectedSha256);
        if (!_bodyColliders.TryGetValue(reference, out LoadedBodyColliders? loaded))
        {
            var set = BodyCollidersJson.Parse(ReadVerified(reference, key), key);
            loaded = new LoadedBodyColliders(this, key, set);
            _bodyColliders.Add(reference, loaded);
        }
        loaded.ForRig(bodyRigIdentity);
        return loaded;
    }
}
