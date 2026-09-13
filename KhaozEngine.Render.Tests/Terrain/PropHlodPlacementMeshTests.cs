using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public sealed class PropHlodPlacementMeshTests
    {
        static readonly Vector4 White = Vector4.One;

        static GltfMesh Mesh(Vector3[] positions, params ushort[] indices)
        {
            var vertices = new ModelVertex[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                vertices[i] = new ModelVertex(positions[i], Vector3.UnitY, White);
            return new GltfMesh(vertices, indices);
        }

        static Dictionary<string, GltfMesh> Kit(params (string Id, GltfMesh Mesh)[] entries)
        {
            var meshes = new Dictionary<string, GltfMesh>();
            foreach ((string id, GltfMesh mesh) in entries) meshes[id] = mesh;
            return meshes;
        }

        [Fact]
        public void BuildPlacementMesh_UsesGlobalWeldAveragesAndOnlySelectedTriangles()
        {
            GltfMesh target = Mesh(
                new[]
                {
                    new Vector3(0.25f, 0f, 0.25f),
                    new Vector3(1.25f, 0f, 0.25f),
                    new Vector3(0.25f, 0f, 1.25f),
                },
                0, 1, 2);
            GltfMesh neighbour = Mesh(
                new[]
                {
                    new Vector3(0.75f, 0f, 0.75f),
                    new Vector3(1.75f, 0f, 0.75f),
                    new Vector3(0.75f, 0f, 1.75f),
                },
                0, 1, 2);
            var placements = new[]
            {
                new PropPlacement("target", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("neighbour", 0f, 0f, 0f, 1f, 0f, 0),
            };
            Dictionary<string, GltfMesh> kit = Kit(("target", target), ("neighbour", neighbour));

            GltfMesh selected = PropHlod.BuildPlacementMesh(placements, 0, kit, 1f);
            GltfMesh global = PropHlod.BuildMergedMesh(placements, kit, 1f);
            GltfMesh separate = PropHlod.BuildMergedMesh(new[] { placements[0] }, kit, 1f);

            Assert.Equal(2, global.TriangleCount);
            Assert.Equal(1, selected.TriangleCount);
            Assert.Equal(new uint[] { 0, 1, 2 }, selected.Indices32);
            Assert.Equal(new Vector3(0.5f, 0f, 0.5f), selected.Vertices[0].Position);
            Assert.Equal(new Vector3(1.5f, 0f, 0.5f), selected.Vertices[1].Position);
            Assert.Equal(new Vector3(0.5f, 0f, 1.5f), selected.Vertices[2].Position);
            Assert.Equal(global.Vertices[0].Position, selected.Vertices[0].Position);
            Assert.Equal(global.Vertices[1].Position, selected.Vertices[1].Position);
            Assert.Equal(global.Vertices[2].Position, selected.Vertices[2].Position);
            Assert.NotEqual(separate.Vertices[0].Position, selected.Vertices[0].Position);
        }

        [Fact]
        public void BuildPlacementMesh_DropsSelectedTrianglesDegeneratedByGlobalWeld()
        {
            GltfMesh target = Mesh(
                new[]
                {
                    new Vector3(0.1f, 0f, 0.1f),
                    new Vector3(0.2f, 0f, 0.2f),
                    new Vector3(1.1f, 0f, 0.1f),
                    new Vector3(2.1f, 0f, 0.1f),
                    new Vector3(3.1f, 0f, 0.1f),
                    new Vector3(2.1f, 0f, 1.1f),
                },
                0, 1, 2,
                3, 4, 5);
            var placements = new[]
            {
                new PropPlacement("target", 0f, 0f, 0f, 1f, 0f, 0),
            };

            GltfMesh selected = PropHlod.BuildPlacementMesh(
                placements, 0, Kit(("target", target)), weldCellSize: 1f);

            Assert.Equal(1, selected.TriangleCount);
            Assert.Equal(3, selected.Vertices.Length);
            Assert.Equal(new uint[] { 0, 1, 2 }, selected.Indices32);
            Assert.Equal(new Vector3(2.1f, 0f, 0.1f), selected.Vertices[0].Position);
            Assert.Equal(new Vector3(3.1f, 0f, 0.1f), selected.Vertices[1].Position);
            Assert.Equal(new Vector3(2.1f, 0f, 1.1f), selected.Vertices[2].Position);
        }

        [Fact]
        public void BuildPlacementMesh_NonPositiveCellReturnsSelectedFullDetailMerge()
        {
            GltfMesh other = Mesh(
                new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 0f, 1f),
                },
                0, 1, 2);
            GltfMesh target = Mesh(
                new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(2f, 0f, 0f),
                    new Vector3(0f, 0f, 2f),
                },
                0, 1, 2);
            var placements = new[]
            {
                new PropPlacement("other", -10f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("target", 10f, 5f, 20f, 1f, 0f, 0),
            };

            GltfMesh selected = PropHlod.BuildPlacementMesh(
                placements, 1, Kit(("other", other), ("target", target)), weldCellSize: 0f);

            Assert.Equal(3, selected.Vertices.Length);
            Assert.Equal(1, selected.TriangleCount);
            Assert.Equal(new uint[] { 0, 1, 2 }, selected.Indices32);
            Assert.Equal(new Vector3(10f, 5f, 20f), selected.Vertices[0].Position);
            Assert.Equal(new Vector3(12f, 5f, 20f), selected.Vertices[1].Position);
            Assert.Equal(new Vector3(10f, 5f, 22f), selected.Vertices[2].Position);
        }

        [Fact]
        public void BuildPlacementMesh_UnusableSelectedSourceReturnsEmptyMesh()
        {
            GltfMesh known = Mesh(
                new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 0f, 1f),
                },
                0, 1, 2);
            var placements = new[]
            {
                new PropPlacement("known", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("missing", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("empty", 0f, 0f, 0f, 1f, 0f, 0),
            };
            Dictionary<string, GltfMesh> kit = Kit(
                ("known", known),
                ("empty", new GltfMesh(Array.Empty<ModelVertex>(), new uint[] { 0, 1, 2 })));

            GltfMesh missing = PropHlod.BuildPlacementMesh(placements, 1, kit, 1f);
            GltfMesh empty = PropHlod.BuildPlacementMesh(placements, 2, kit, 1f);

            Assert.Empty(missing.Vertices);
            Assert.Empty(missing.Indices32);
            Assert.Empty(empty.Vertices);
            Assert.Empty(empty.Indices32);
        }

        [Fact]
        public void BuildPlacementMesh_ValidatesCollectionsAndPlacementIndex()
        {
            var placements = new[]
            {
                new PropPlacement("target", 0f, 0f, 0f, 1f, 0f, 0),
            };
            var kit = new Dictionary<string, GltfMesh>();

            ArgumentNullException placementsError = Assert.Throws<ArgumentNullException>(
                () => PropHlod.BuildPlacementMesh(null!, 0, kit, 1f));
            ArgumentNullException meshesError = Assert.Throws<ArgumentNullException>(
                () => PropHlod.BuildPlacementMesh(placements, 0, null!, 1f));
            ArgumentOutOfRangeException negativeError = Assert.Throws<ArgumentOutOfRangeException>(
                () => PropHlod.BuildPlacementMesh(placements, -1, kit, 1f));
            ArgumentOutOfRangeException pastEndError = Assert.Throws<ArgumentOutOfRangeException>(
                () => PropHlod.BuildPlacementMesh(placements, placements.Length, kit, 1f));

            Assert.Equal("placements", placementsError.ParamName);
            Assert.Equal("sourceMeshes", meshesError.ParamName);
            Assert.Equal("placementIndex", negativeError.ParamName);
            Assert.Equal("placementIndex", pastEndError.ParamName);
        }
    }
}
