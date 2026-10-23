using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    public static partial class GltfLoader
    {
        // Keep legacy import acceptance and exception behavior. Explicit DQ failures consistently identify
        // the asset, import sample and profile even when SharpGLTF refuses malformed data before decoding.
        static T ValidateSkinningImport<T>(string identity, SkinningProfile profile, Func<T> load)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (profile.Method == SkinningMethod.LinearBlend) return load();
            try { return load(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or SharpGLTF.Validation.ModelException)
            {
                throw new InvalidOperationException(
                    $"Skinning import failed for asset '{identity}', clip '<none>', sample -1, " +
                    $"profile {profile.Method} version {profile.AlgorithmVersion}: {error.Message}", error);
            }
        }

        static void ValidateImportInfluences(MeshPrimitive primitive, int vertexCount, int boneCount, string identity)
        {
            var sets = new SortedSet<string>(StringComparer.Ordinal) { "0" };
            foreach (string semantic in primitive.VertexAccessors.Keys)
            {
                if (!semantic.StartsWith("JOINTS_", StringComparison.Ordinal) && !semantic.StartsWith("WEIGHTS_", StringComparison.Ordinal)) continue;
                string set = semantic[(semantic.IndexOf('_') + 1)..];
                if (set.Length == 0) throw new InvalidOperationException($"Invalid influence semantic '{semantic}'.");
                foreach (char digit in set)
                    if (digit < '0' || digit > '9') throw new InvalidOperationException($"Invalid influence semantic '{semantic}'.");
                sets.Add(set);
            }

            var influences = new List<(string Set, IList<Vector4> Joints, IList<Vector4> Weights)>(sets.Count);
            foreach (string set in sets)
            {
                var joints = primitive.GetVertexAccessor($"JOINTS_{set}")?.AsVector4Array();
                var weights = primitive.GetVertexAccessor($"WEIGHTS_{set}")?.AsVector4Array();
                if (joints == null || weights == null || joints.Count != vertexCount || weights.Count != vertexCount)
                    throw new InvalidOperationException($"JOINTS_{set}/WEIGHTS_{set} must have one complete pair per vertex.");
                influences.Add((set, joints, weights));
            }

            var context = new SkinningValidationContext(identity, null, -1);
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int positives = 0;
                string? laterPositiveSet = null;
                foreach (var (set, joints, weights) in influences)
                {
                    int count;
                    try { count = SkinningValidation.ValidateRawInfluences(joints[vertex], weights[vertex], boneCount, context, vertex); }
                    catch (ArgumentException error)
                    {
                        throw new InvalidOperationException($"JOINTS_{set}/WEIGHTS_{set}: {error.Message}", error);
                    }
                    positives += count;
                    if (set != "0" && count > 0) laterPositiveSet ??= set;
                }
                if (positives > 4)
                    throw new InvalidOperationException($"vertex {vertex} has {positives} positive source influences, over the four-slot limit before normalization or reduction.");
                if (laterPositiveSet != null)
                    throw new InvalidOperationException($"vertex {vertex} has a positive WEIGHTS_{laterPositiveSet} contribution. This importer supports only JOINTS_0/WEIGHTS_0.");
            }
        }
    }
}
