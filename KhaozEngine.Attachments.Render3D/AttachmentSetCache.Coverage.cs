using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    readonly Dictionary<SkinnedHostCoverage, int> _coverageLifetimes = [];
    internal void RegisterCoverageLifetime(SkinnedHostCoverage host) =>
        _coverageLifetimes[host] = _coverageLifetimes.GetValueOrDefault(host) + 1;
    internal void UnregisterCoverageLifetime(SkinnedHostCoverage host)
    {
        if (!_coverageLifetimes.TryGetValue(host, out int count)) return;
        if (count == 1) _coverageLifetimes.Remove(host);
        else _coverageLifetimes[host] = count - 1;
    }

    readonly Dictionary<(LoadedAttachmentSet, AttachmentMeshRef), LoadedEquipmentCoverage> _coverage = [];
    readonly Dictionary<MeshIdentity, CoverageSourceTopology> _coverageSources = [];

    /// <summary>Compile a verified companion against this cache's exact owned set and displayed mesh bytes.</summary>
    /// <remarks>Does not read meshes again or upload geometry. The returned immutable view borrows this cache.</remarks>
    public LoadedEquipmentCoverage GetCoverage(LoadedAttachmentSet set, string key, string expectedSha256)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(set);
        ValidateRequest(key, expectedSha256);
        if (!_definitions.Values.Any(entry => ReferenceEquals(entry.View, set)))
            throw new ArgumentException("Coverage requires a set owned by this cache.", nameof(set));
        var reference = new AttachmentMeshRef(key, expectedSha256);
        if (_coverage.TryGetValue((set, reference), out var existing)) return existing;
        byte[] bytes = ReadVerified(reference, key);
        EquipmentCoverageDefinition definition = EquipmentCoverageJson.Read(bytes, key);
        if (definition.AttachmentSha256 != set.DefinitionSha256)
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.AttachmentIdentityMismatch, "Attachment bytes differ from the companion.") { Identity = key, Path = "$.attachmentSha256" };
        var supports = new CoverageSupportSurface[definition.Commands.Count];
        var parts = new int[supports.Length];
        var pending = new Dictionary<MeshIdentity, CoverageSourceTopology>();
        for (int i = 0; i < supports.Length; i++)
        {
            EquipmentCoverageCommand command = definition.Commands[i];
            int part = -1;
            for (int p = 0; p < set.Definition.Parts.Count; p++)
                if (set.Definition.Parts[p].Id == command.PartId) part = p;
            if (part < 0 || set.Definition.Parts[part] is not RigidAttachmentPartDefinition rigid)
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.RigidPartRequired, "Coverage requires a rigid attachment part.") { Identity = key, Path = "$.commands", CommandId = command.Id, PartId = command.PartId };
            var identity = new MeshIdentity(rigid.Mesh, false);
            if (!_coverageSources.TryGetValue(identity, out var source) && !pending.TryGetValue(identity, out source))
            {
                source = GltfLoader.LoadCoverageSource(_meshes[identity].Bytes, rigid.Mesh.Key);
                if (source.SourceSha256 != rigid.Mesh.Sha256)
                    throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.DisplayedSourceMismatch, "Displayed mesh bytes differ.") { Identity = key, Path = "$.commands", CommandId = command.Id, PartId = command.PartId };
                pending.Add(identity, source);
            }
            CoverageTriangleRange[] ranges = command.InwardSupport.Select(r =>
                new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray();
            supports[i] = CoverageSupportSurface.Compile(source, ranges, command.CoverageLocal);
            if (!supports[i].InwardOfClosedSolid || source.Materials.Count != set.Resources[part].RigidHandles.Count)
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.IncompleteDisplayedSolid, "Coverage requires its complete displayed solid.") { Identity = key, Path = "$.commands", CommandId = command.Id, PartId = command.PartId };
            parts[i] = part;
        }
        var loaded = new LoadedEquipmentCoverage(this, set, definition, supports, parts, (Scene as IAttachmentCoverageScene)?.CoverageResourceOwner, expectedSha256);
        foreach (var source in pending) _coverageSources.Add(source.Key, source.Value);
        _coverage.Add((set, reference), loaded);
        return loaded;
    }
}
