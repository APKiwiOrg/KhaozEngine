using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    // Model reading: from a path or from a binary glTF in memory, with the index preflight that names a bad index
    // after SharpGLTF's strict validation refuses an asset.
    public static partial class GltfLoader
    {
        static ModelRoot LoadModel(string path, string? entryId = null) =>
            LoadModel(path, settings => ModelRoot.Load(path, settings), entryId);

        // A GLB in memory, read in place when it is array-backed and copied once otherwise. Each read opens a fresh
        // read-only stream, so the validation fallback can re-read it.
        static ModelRoot LoadModel(ReadOnlyMemory<byte> glb, string identity)
        {
            ArraySegment<byte> bytes = MemoryMarshal.TryGetArray(glb, out ArraySegment<byte> segment)
                ? segment : new ArraySegment<byte>(glb.ToArray());
            return LoadModel(identity, settings =>
            {
                using var stream = new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
                return ModelRoot.ReadGLB(stream, settings);
            });
        }

        // The byte overloads' exception contract: SharpGLTF's own exception types stay inside the loader, so a
        // malformed GLB carries GltfImportRefusal.MalformedModel and raw Identity, with the original inner.
        static T TranslateModelErrors<T>(string identity, Func<T> load)
        {
            try { return load(); }
            catch (SharpGLTF.Validation.ModelException ex)
            {
                throw new GltfImportDataException(GltfImportRefusal.MalformedModel,
                    $"glTF asset '{identity}' is malformed: {ex.Message}", ex) { Identity = identity };
            }
        }

        // identity names the asset in errors: a file path, or the caller's identity for bytes. read takes null for
        // SharpGLTF's default strict settings, or the fallback's validation-skipping settings.
        static ModelRoot LoadModel(string identity, Func<ReadSettings?, ModelRoot> read, string? entryId = null)
        {
            try { return read(null); }
            catch (SharpGLTF.Validation.DataException ex)
            {
                // SharpGLTF's strict diagnostic identifies the accessor SLOT but omits the bad index VALUE. Parse
                // only after that failure so our index preflight can name the value and vertex count. Valid assets
                // retain SharpGLTF's full validation and are never parsed twice.
                ModelRoot uncheckedRoot;
                try
                {
                    var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip };
                    uncheckedRoot = read(settings);
                }
                catch (Exception uncheckedEx)
                {
                    throw new GltfImportStateException(GltfImportRefusal.ValidationFailed,
                        $"glTF asset '{identity}' failed validation: {ex.Message}",
                        new AggregateException(ex, uncheckedEx)) { Identity = identity };
                }
                ValidatePrimitiveIndices(uncheckedRoot, identity, entryId);
                throw new GltfImportStateException(GltfImportRefusal.ValidationFailed,
                    $"glTF asset '{identity}' failed validation: {ex.Message}", ex) { Identity = identity };
            }
        }

        static void ValidatePrimitiveIndices(ModelRoot root, string identity, string? entryId)
        {
            string assetIdentity = $"glTF asset '{identity}'";
            foreach (var mesh in root.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    if (pos == null) continue;
                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
                        MeshIndexValidation.Source(a, pos.Count, identity, entryId, diagnosticIdentity: assetIdentity);
                        MeshIndexValidation.Source(b, pos.Count, identity, entryId, diagnosticIdentity: assetIdentity);
                        MeshIndexValidation.Source(c, pos.Count, identity, entryId, diagnosticIdentity: assetIdentity);
                    }
                }
        }
    }
}
