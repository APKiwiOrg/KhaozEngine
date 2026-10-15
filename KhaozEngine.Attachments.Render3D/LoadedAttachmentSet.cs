using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        for (int i = 0; i < _resources.Count; i++)
            if (_resources[i].IsSkinned)
                throw new InvalidDataException($"Attachment part {_definition.Parts[i].Id}: skinned parts require a skin binding, not a map-only binding.");
        return new BoundAttachmentSet(_owner, _definition, _resources.Select(part => part.RigidHandles.ToArray()).ToArray(), map);
    }
}
