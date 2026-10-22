using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A cache-owned definition and its uploaded material groups.</summary>
public sealed class LoadedAttachmentSet
{
    readonly AttachmentSetCache _owner;
    readonly AttachmentSetDefinition _definition;
    readonly string _definitionSha256;
    readonly SkinningProfile _profile;
    readonly IReadOnlyList<AttachmentPartResources> _resources;

    internal LoadedAttachmentSet(AttachmentSetCache owner, AttachmentSetDefinition definition,
        string definitionSha256, SkinningProfile profile, AttachmentPartResources[] resources)
    {
        _owner = owner;
        _definition = definition;
        _definitionSha256 = definitionSha256;
        _profile = profile;
        _resources = System.Array.AsReadOnly(resources);
    }

    public AttachmentSetDefinition Definition
    {
        get { _owner.ThrowIfDisposed(); return _definition; }
    }

    /// <summary>SHA-256 of the exact verified definition bytes, including whitespace.</summary>
    public string DefinitionSha256
    {
        get { _owner.ThrowIfDisposed(); return _definitionSha256; }
    }

    /// <summary>The pre-upload profile selected for all garment parts in this cached view.</summary>
    public SkinningProfile Profile
    {
        get { _owner.ThrowIfDisposed(); return _profile; }
    }

    internal IReadOnlyList<AttachmentPartResources> Resources
    {
        get { _owner.ThrowIfDisposed(); return _resources; }
    }

    /// <summary>Resolve every joint and allocate submission scratch once for the scene's render thread.</summary>
    public BoundAttachmentSet Bind(ContractJointMap map)
    {
        _owner.ThrowIfDisposed();
        return new BoundAttachmentSet(_owner, _definition, _resources, new AttachmentSetBinding(_definition, map));
    }

    /// <summary>Require matching garment and host profiles, then snapshot every joint index once.</summary>
    public BoundAttachmentSet Bind(ContractJointMap map, SkinnedGltfMesh hostMesh)
    {
        _owner.ThrowIfDisposed();
        System.ArgumentNullException.ThrowIfNull(hostMesh);
        return new BoundAttachmentSet(_owner, _definition, _resources,
            new AttachmentSetBinding(_definition, map, hostMesh, _resources));
    }
}
