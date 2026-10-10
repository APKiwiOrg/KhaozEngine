using System;
using System.IO;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    // Model reading: from a path or from a binary glTF in memory, with the index preflight that names a bad index
    // after SharpGLTF's strict validation refuses an asset.
    public static partial class GltfLoader
    {
        static ModelRoot LoadModel(string path) => LoadModel(path, settings => ModelRoot.Load(path, settings));

        // A GLB in memory. Each read opens a fresh seekable stream, so the validation fallback can re-read it.
        static ModelRoot LoadModel(byte[] glb, string identity) => LoadModel(identity, settings =>
        {
            using var stream = new MemoryStream(glb, writable: false);
            return ModelRoot.ReadGLB(stream, settings);
        });

        // read takes null for SharpGLTF's default strict settings, or the fallback's validation-skipping settings.
        static ModelRoot LoadModel(string path, Func<ReadSettings?, ModelRoot> read)
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
                    throw new InvalidOperationException(
                        $"glTF asset '{path}' failed validation: {ex.Message}",
                        new AggregateException(ex, uncheckedEx));
                }
                ValidatePrimitiveIndices(uncheckedRoot, path);
                throw new InvalidOperationException($"glTF asset '{path}' failed validation: {ex.Message}", ex);
            }
        }

        static void ValidatePrimitiveIndices(ModelRoot root, string path)
        {
            string assetIdentity = $"glTF asset '{path}'";
            foreach (var mesh in root.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    if (pos == null) continue;
                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
                        MeshIndexValidation.Source(a, pos.Count, assetIdentity);
                        MeshIndexValidation.Source(b, pos.Count, assetIdentity);
                        MeshIndexValidation.Source(c, pos.Count, assetIdentity);
                    }
                }
        }
    }
}
