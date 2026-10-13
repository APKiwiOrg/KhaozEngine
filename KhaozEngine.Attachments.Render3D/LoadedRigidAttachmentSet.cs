using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A cache-owned definition and its uploaded material groups.</summary>
public sealed class LoadedRigidAttachmentSet
{
    readonly RigidAttachmentSetCache _owner;
    readonly RigidAttachmentSetDefinition _definition;
    readonly string _definitionSha256;
    readonly MeshHandle[][] _partHandles;

    internal LoadedRigidAttachmentSet(RigidAttachmentSetCache owner, RigidAttachmentSetDefinition definition,
        string definitionSha256, MeshHandle[][] partHandles)
    {
        _owner = owner;
        _definition = definition;
        _definitionSha256 = definitionSha256;
        _partHandles = partHandles;
    }

    public RigidAttachmentSetDefinition Definition
    {
        get { _owner.ThrowIfDisposed(); return _definition; }
    }

    /// <summary>SHA-256 of the exact verified definition bytes, including whitespace.</summary>
    public string DefinitionSha256
    {
        get { _owner.ThrowIfDisposed(); return _definitionSha256; }
    }

    /// <summary>Resolve every joint and allocate submission scratch once for the scene's render thread.</summary>
    public BoundRigidAttachmentSet Bind(ContractJointMap map)
    {
        _owner.ThrowIfDisposed();
        return new BoundRigidAttachmentSet(_owner, _definition, _partHandles, map);
    }
}
