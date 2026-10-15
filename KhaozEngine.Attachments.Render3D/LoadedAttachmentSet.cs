using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A cache-owned definition and its uploaded material groups.</summary>
public sealed class LoadedAttachmentSet
{
    readonly AttachmentSetCache _owner;
    readonly AttachmentSetDefinition _definition;
    readonly string _definitionSha256;
    readonly IReadOnlyList<AttachmentPartResources> _resources;

    internal LoadedAttachmentSet(AttachmentSetCache owner, AttachmentSetDefinition definition,
        string definitionSha256, AttachmentPartResources[] resources)
    {
        _owner = owner;
        _definition = definition;
        _definitionSha256 = definitionSha256;
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

    /// <summary>Bind garment metadata to the explicit host skin and snapshot every joint index once.</summary>
    public BoundAttachmentSet Bind(ContractJointMap map, SkinnedGltfMesh hostMesh)
    {
        _owner.ThrowIfDisposed();
        System.ArgumentNullException.ThrowIfNull(hostMesh);
        return new BoundAttachmentSet(_owner, _definition, _resources,
            new AttachmentSetBinding(_definition, map, hostMesh, _resources));
    }
}
