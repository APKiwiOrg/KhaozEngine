using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    OutfitLift? ResolveOutfitLift(OutfitCoverage coverage, BoundAttachmentSet[] sets,
        Dictionary<MeshIdentity, OutfitMeshGeometry> geometry)
    {
        List<LiftingPartMesh>? meshes = null;
        for (int wornIndex = 0; wornIndex < sets.Length; wornIndex++)
        {
            foreach (AttachmentPartDefinition part in sets[wornIndex].Definition.Parts)
            {
                if (part is not SkinnedAttachmentPartDefinition { Lift: not null }) continue;
                var identity = new MeshIdentity(part.Mesh, Skinned: true);
                LiftBasis basis = FindLiftBasis(part.Mesh)
                    ?? throw new InvalidOperationException("A loaded lifting part must have a verified lift basis.");
                (meshes ??= []).Add(new LiftingPartMesh(wornIndex, part.Id, geometry[identity].LiftMaterials(basis)));
            }
        }
        return meshes is null ? null : OutfitLift.Resolve(coverage, meshes);
    }

    ViewKey DisplacedViewKey(BoundAttachmentSet set, int wornIndex, uint partId, SkinnedMeshHandle source,
        ReadOnlyMemory<uint> indices, ReadOnlyMemory<Vector3> positions)
    {
        if (Scene is not IAttachmentDisplacedViewScene)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.OutfitLiftUnsupported,
                $"{set.SourceName}: part {partId} lifts and needs a scene that creates displaced skinned views.")
            { SourceName = set.SourceName, WornIndex = wornIndex, PartId = partId };
        return new ViewKey(source, indices, positions);
    }

    // Pick the decode shape before reading any tagged part. A mesh worn both ways still gets one decode.
    static HashSet<AttachmentMeshRef>? LiftingMeshReferences(AttachmentSetDefinition[] definitions)
    {
        HashSet<AttachmentMeshRef>? liftingMeshes = null;
        foreach (AttachmentSetDefinition definition in definitions)
            foreach (AttachmentPartDefinition part in definition.Parts)
                if (part is SkinnedAttachmentPartDefinition { Lift: not null })
                    (liftingMeshes ??= []).Add(part.Mesh);
        return liftingMeshes;
    }

    // Borrow the renderer's upload, or decode only the needed data from a custom lease's verified bytes.
    OutfitMeshGeometry ReadOutfitGeometry(AttachmentMeshRef reference, bool needsVertices,
        Dictionary<MeshIdentity, OutfitMeshGeometry> read)
    {
        var identity = new MeshIdentity(reference, Skinned: true);
        if (read.TryGetValue(identity, out OutfitMeshGeometry? known)) return known;
        CachedMesh mesh = _meshes[identity];
        OutfitMeshGeometry geometry;
        if (mesh.Lease is SkinnedAttachmentUpload upload)
            geometry = new OutfitMeshGeometry(upload);
        else if (needsVertices)
            geometry = new OutfitMeshGeometry(GltfLoader.LoadSkinnedPartGeometry(mesh.Bytes, reference.Key));
        else
            geometry = new OutfitMeshGeometry(GltfLoader.LoadSkinnedPartIndices(mesh.Bytes, reference.Key));
        read.Add(identity, geometry);
        return geometry;
    }

    sealed class OutfitMeshGeometry
    {
        readonly SkinnedAttachmentUpload? _upload;
        readonly SkinnedGltfMesh[]? _parts;
        LiftMaterialMesh[]? _liftMaterials;

        internal OutfitMeshGeometry(SkinnedAttachmentUpload upload)
        {
            _upload = upload;
            Indices = upload.SourceIndices();
        }

        internal OutfitMeshGeometry(SkinnedGltfMesh[] parts)
        {
            _parts = parts;
            Indices = parts.Select(part => (ReadOnlyMemory<uint>)part.Indices32).ToArray();
        }

        internal OutfitMeshGeometry(ReadOnlyMemory<uint>[] indices) => Indices = indices;

        internal ReadOnlyMemory<uint>[] Indices { get; }

        internal LiftMaterialMesh[] LiftMaterials(LiftBasis basis) => _liftMaterials ??= BuildLiftMaterials(basis);

        LiftMaterialMesh[] BuildLiftMaterials(LiftBasis basis)
        {
            var materials = new LiftMaterialMesh[Indices.Length];
            var directions = new Vector3[materials.Length][];
            var bearings = new float[materials.Length][];
            for (int material = 0; material < materials.Length; material++)
            {
                ReadOnlySpan<SkinnedVertex> vertices = _upload is { } upload
                    ? upload.SourceVertices(material)
                    : _parts![material].Vertices;
                var positions = new Vector3[vertices.Length];
                for (int vertex = 0; vertex < positions.Length; vertex++) positions[vertex] = vertices[vertex].Position;
                directions[material] = new Vector3[positions.Length];
                bearings[material] = new float[positions.Length];
                materials[material] = new LiftMaterialMesh(positions, Indices[material], directions[material], bearings[material]);
            }
            foreach (LiftBasisVertex vertex in basis.Vertices)
            {
                directions[vertex.MaterialPart][vertex.IndexInPart] = vertex.Direction;
                bearings[vertex.MaterialPart][vertex.IndexInPart] = vertex.Bearing;
            }
            return materials;
        }
    }
}
