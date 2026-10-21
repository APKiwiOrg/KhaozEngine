using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Triangle-bearing distinct terrain, six-component buildings and retained seeded prop placements.</summary>
internal class RendererPerfVillageScene : RendererPerfRecipe
{
    readonly GltfMesh[] _terrain;
    readonly GltfMesh[] _props = [MeshPrimitives.Box(.8f), MeshPrimitives.Cone(.5f, 2.2f, 6),
        MeshPrimitives.Sphere(1.6f, 10, 12), MeshPrimitives.RoundedBox(1.4f, .3f, 4)];
    readonly GltfMesh _box = MeshPrimitives.Box(1f);
    readonly Matrix4x4[] _terrainWorlds, _propWorlds, _buildingWorlds;
    readonly SplatLayerImage[] _layers;
    MeshHandle[] _terrainHandles = [], _propHandles = [];
    MeshHandle _buildingHandle;
    readonly int _side;
    internal RendererPerfVillageScene() : this("village", 8, 1024, 24) { }
    protected RendererPerfVillageScene(string id, int side, int props, int buildings) : base(id)
    {
        _side = side;
        _terrain = Enumerable.Range(0, side * side).Select(_ => BuildTerrain()).ToArray();
        _terrainWorlds = Enumerable.Range(0, side * side).Select(i => Matrix4x4.CreateTranslation(
            (i % side - (side - 1) * .5f) * 8f, 0f, (i / side - (side - 1) * .5f) * 8f)).ToArray();
        _propWorlds = StreamedWorldSceneContent.PropWorlds(props, side * 8f, Seed);
        _buildingWorlds = new Matrix4x4[buildings * 6];
        (Vector3 Size, Vector3 Offset)[] components =
        [
            (new(8f, .3f, 6f), new(0f, .15f, 0f)), (new(8f, .3f, 6f), new(0f, 4.15f, 0f)),
            (new(.25f, 4f, 6f), new(-4f, 2f, 0f)), (new(.25f, 4f, 6f), new(4f, 2f, 0f)),
            (new(8f, 4f, .25f), new(0f, 2f, -3f)), (new(8f, 4f, .25f), new(0f, 2f, 3f)),
        ];
        for (int i = 0; i < buildings; i++)
        {
            Vector2 centre = InteriorCutCostScene.BuildingCentre(i, 6, 4, 10f);
            for (int part = 0; part < 6; part++)
                _buildingWorlds[i * 6 + part] = Matrix4x4.CreateScale(components[part].Size)
                    * Matrix4x4.CreateTranslation(components[part].Offset + new Vector3(centre.X, 0f, centre.Y));
        }
        _layers = Enumerable.Range(0, 5).Select(layer =>
        {
            var albedo = new byte[4 * 4 * 4];
            var normal = new byte[albedo.Length];
            for (int pixel = 0; pixel < 16; pixel++)
            {
                albedo[pixel * 4] = (byte)(64 + layer * 24 + (pixel % 2) * 8);
                albedo[pixel * 4 + 1] = (byte)(96 + layer * 16);
                albedo[pixel * 4 + 2] = (byte)(48 + layer * 20);
                albedo[pixel * 4 + 3] = 255;
                normal[pixel * 4] = 128; normal[pixel * 4 + 1] = 128;
                normal[pixel * 4 + 2] = 255; normal[pixel * 4 + 3] = 255;
            }
            return new SplatLayerImage
            {
                AlbedoRgba = albedo,
                NormalRgba = normal,
                Tint = Color.White,
                TilesPerMetre = .25f,
                Roughness = .85f
            };
        }).ToArray();
    }

    internal override IReadOnlyList<GltfMesh> Terrain => _terrain;
    protected override int Buildings => _buildingWorlds.Length;
    protected override int Props => _propWorlds.Length;
    protected override float WorldExtent => _side * 8f;
    protected override object Assets => new
    {
        TextureWidth = 4,
        TextureHeight = 4,
        Mips = 3,
        Layers = _layers,
        TriplanarSharpness = 8f,
        Projection = SplatProjection.Triplanar,
        BaseSpecular = .15f,
        Sampler = new TerrainSamplerConfig(GpuSamplerFilter.Anisotropic, 16, 0),
        PropTint = PropTint,
        BuildingTint = BuildingTint
    };
    static readonly Color PropTint = new(.35f, .55f, .3f, 1f), BuildingTint = new(.6f, .5f, .4f, 1f);
    protected override object Geometry => new
    {
        Terrain = _terrain.Select(MeshFacts).ToArray(),
        TerrainWorlds = _terrainWorlds,
        TerrainGrid = new
        {
            QuadsX = 32,
            QuadsZ = 32,
            WidthMetres = 8,
            HeightMetres = 8,
            Weights = "color.rgba-plus-remainder",
            Material = "splat-five-layer"
        },
        Props = _props.Select(MeshFacts).ToArray(),
        PropWorlds = _propWorlds,
        PropMeshIndices = Enumerable.Range(0, Props).Select(i => i & 3).ToArray(),
        Building = MeshFacts(_box),
        BuildingWorlds = _buildingWorlds,
    };

    protected override void Load(Scene3D scene)
    {
        var material = scene.LoadSplatMaterial(4, 4, _layers, 8f, SplatProjection.Triplanar, .15f,
            new TerrainSamplerConfig(GpuSamplerFilter.Anisotropic, 16, 0));
        _terrainHandles = _terrain.Select(mesh => scene.LoadMesh(mesh, material)).ToArray();
        _propHandles = _props.Select(scene.LoadMesh).ToArray();
        if (Buildings > 0) _buildingHandle = scene.LoadMesh(_box);
    }
    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        for (int i = 0; i < _terrain.Length; i++) scene.Draw(_terrainHandles[i], _terrainWorlds[i], Color.White);
        for (int i = 0; i < _propWorlds.Length; i++) scene.Draw(_propHandles[i & 3], _propWorlds[i], PropTint);
        foreach (Matrix4x4 world in _buildingWorlds) scene.Draw(_buildingHandle, world, BuildingTint);
    }

    static GltfMesh BuildTerrain()
    {
        var vertices = new ModelVertex[33 * 33];
        var indices = new ushort[32 * 32 * 6];
        for (int z = 0; z <= 32; z++)
            for (int x = 0; x <= 32; x++)
                vertices[z * 33 + x] = new ModelVertex(new Vector3(x * .25f - 4f, 0f, z * .25f - 4f),
                    Vector3.UnitY, new Vector4(.4f, .2f, .15f, .1f), new Vector2(x / 32f, z / 32f),
                    new Vector4(1f, 0f, 0f, 1f));
        int next = 0;
        for (int z = 0; z < 32; z++)
            for (int x = 0; x < 32; x++)
            {
                int a = z * 33 + x, b = a + 1, c = a + 33, d = c + 1;
                indices[next++] = (ushort)a; indices[next++] = (ushort)c; indices[next++] = (ushort)b;
                indices[next++] = (ushort)b; indices[next++] = (ushort)c; indices[next++] = (ushort)d;
            }
        return new GltfMesh(vertices, indices);
    }
}
