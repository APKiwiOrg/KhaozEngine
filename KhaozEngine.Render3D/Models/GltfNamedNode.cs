using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    /// <summary>One named glTF node read by <see cref="GltfLoader.LoadNamedNodes"/>: its glTF
    /// <see cref="Name"/> and its <see cref="WorldTransform"/>, the node's local matrix composed with every
    /// ancestor's in row-vector order (<c>local * parent * ... * root</c>). This is the same matrix
    /// <see cref="GltfLoader.Load"/> bakes into a mesh node's vertices, so transforming a point by it lands in the
    /// loaded mesh's space. Empties (nodes with no mesh) are included, which makes an authored empty a socket for
    /// attaching another mesh, a muzzle or an exhaust point.</summary>
    /// <param name="Name">The node's glTF name, never null or empty.</param>
    /// <param name="WorldTransform">The node's world matrix in the loaded mesh's space.</param>
    public readonly record struct GltfNamedNode(string Name, Matrix4x4 WorldTransform);

    // The named-node read behind GltfLoader.LoadNamedNodes. Kept out of GltfLoader so the loader's geometry paths
    // stay one cohesive type. Walks LogicalNodes, the same list and order the rigid geometry paths walk.
    static class GltfNamedNodeReader
    {
        internal static IReadOnlyList<GltfNamedNode> Read(ModelRoot root)
        {
            var nodes = new List<GltfNamedNode>();
            foreach (Node node in root.LogicalNodes)
                if (!string.IsNullOrEmpty(node.Name))
                    nodes.Add(new GltfNamedNode(node.Name, node.WorldMatrix));
            return nodes;
        }
    }
}
