using System;
using System.Collections.Generic;
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

    sealed class SwingingVertexCollector
    {
        readonly bool[] swingingJoints;
        readonly List<bool> flags = [];

        internal SwingingVertexCollector(JsonElement root, JsonElement skin, IReadOnlyCollection<string> chainJoints)
        {
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

        internal void Add(ReadOnlySpan<float> joints, ReadOnlySpan<float> weights)
        {
            for (int lane = 0; lane < weights.Length; lane++)
                if (weights[lane] > 0 && swingingJoints[(int)joints[lane]])
                {
                    flags.Add(true);
                    return;
                }
            flags.Add(false);
        }

        internal bool[] ToArray() => flags.ToArray();
    }
}
