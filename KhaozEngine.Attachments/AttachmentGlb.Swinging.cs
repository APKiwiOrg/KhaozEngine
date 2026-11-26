using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    /// <summary>
    /// Validates a skinned attachment GLB and returns one flag per vertex in flattened mesh, primitive and vertex
    /// order. A flag is true when a positive weight refers to one of <paramref name="chainJoints"/>. Names match
    /// ordinally against the skin's joints.
    /// </summary>
    public static bool[] SwingingVertices(ReadOnlyMemory<byte> glb, string sourceName, IReadOnlyCollection<string> chainJoints)
    {
        ArgumentNullException.ThrowIfNull(chainJoints);
        bool[] flags = [];
        Validate(glb, sourceName, container => flags = ReadSkinned(container, chainJoints));
        return flags;
    }

    /// <summary>Return the union of cloth and fallback movement in flattened GLB primitive order.</summary>
    public static bool[] SwingingVertices(ReadOnlyMemory<byte> glb, string sourceName, ClothMotionDefinition motion, ClothSurfaceFile surface)
    {
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(surface);
        surface.CreateRig(sourceName);
        if (Convert.ToHexStringLower(SHA256.HashData(glb.Span)) != surface.MeshSha256)
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.MeshDigest, sourceName, "The cloth sidecar tags different GLB bytes.");
        string[] joints = PartMotionFallback.Chains(motion)!.Chains.SelectMany(chain => chain.Joints).ToArray();
        bool[] flags = [];
        Validate(glb, sourceName, container => flags = ReadSkinned(container, joints, surface, sourceName));
        return flags;
    }

    sealed class SwingingVertexCollector
    {
        readonly bool[] swingingJoints;
        readonly List<bool> flags = [];
        readonly ClothSurfaceFile? cloth;
        int mesh, primitive, vertex, nextEmbedding;

        internal SwingingVertexCollector(JsonElement root, JsonElement skin, IReadOnlyCollection<string> chainJoints, ClothSurfaceFile? cloth)
        {
            this.cloth = cloth;
            var names = new HashSet<string>(chainJoints, StringComparer.Ordinal);
            JsonElement nodes = root.GetProperty("nodes");
            JsonElement joints = skin.GetProperty("joints");
            swingingJoints = new bool[joints.GetArrayLength()];
            int skinIndex = 0;
            foreach (JsonElement joint in joints.EnumerateArray())
            {
                string name = nodes[joint.GetInt32()].GetProperty("name").GetString()!;
                swingingJoints[skinIndex++] = names.Contains(name);
            }
        }

        internal void StartPrimitive(int meshIndex, int primitiveIndex) =>
            (mesh, primitive, vertex) = (meshIndex, primitiveIndex, 0);

        internal void Add(ReadOnlySpan<float> joints, ReadOnlySpan<float> weights)
        {
            bool moving = cloth is not null && nextEmbedding < cloth.Render.Length
                && cloth.Render[nextEmbedding].Vertex == new ClothSourceVertex(mesh, primitive, vertex);
            if (moving) nextEmbedding++;
            vertex++;
            for (int lane = 0; !moving && lane < weights.Length; lane++)
                moving = weights[lane] > 0 && swingingJoints[(int)joints[lane]];
            flags.Add(moving);
        }

        internal bool[] ToArray(string? sourceName)
        {
            if (cloth is not null)
            {
                if (flags.Count != cloth.SourceVertexCount)
                    throw new ClothSurfaceDataException(ClothSurfaceRefusal.Count, sourceName!, "The cloth source vertex count differs from the GLB.");
                if (nextEmbedding != cloth.Render.Length)
                    throw new ClothSurfaceDataException(ClothSurfaceRefusal.RenderEmbedding, sourceName!, "A cloth embedding names a missing GLB source vertex.")
                    { ElementIndex = nextEmbedding };
            }
            return flags.ToArray();
        }
    }
}
