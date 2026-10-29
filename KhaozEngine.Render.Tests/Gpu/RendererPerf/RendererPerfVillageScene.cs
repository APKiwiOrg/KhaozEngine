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
    readonly RendererPerfPropPlacement[] _propPlacements;
    readonly SplatConstruction _splat;
    readonly RigidInstanceDraw[] _draws;
    MeshHandle[] _handles = [];
    readonly int _side;
    internal RendererPerfVillageScene() : this("village", 8, 1024, 24) { }
    protected RendererPerfVillageScene(string id, int side, int props, int buildings) : base(id)
    {
        _side = side;
        _terrain = Enumerable.Range(0, side * side).Select(_ => BuildTerrain()).ToArray();
        _terrainWorlds = Enumerable.Range(0, side * side).Select(i => Matrix4x4.CreateTranslation(
            (i % side - (side - 1) * .5f) * 8f, 0f, (i / side - (side - 1) * .5f) * 8f)).ToArray();
        _propPlacements = RendererPerfPropPlacement.Create(props, side * 8f, Seed);
        _propWorlds = _propPlacements.Select(placement => placement.World).ToArray();
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
        SplatLayerImage[] layers = Enumerable.Range(0, 5).Select(layer =>
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
        _splat = new(4, 4, layers, 8f, SplatProjection.Triplanar, .15f,
            new TerrainSamplerConfig(GpuSamplerFilter.Anisotropic, 16, 0));
        // Mesh indices identify recipe-local geometry slots, independent of Scene3D allocation history.
        _draws = _terrainWorlds.Select((world, i) => RigidDraw(i, world, Color.White))
            .Concat(_propWorlds.Select((world, i) => RigidDraw(_terrain.Length + (i & 3), world, PropTint)))
            .Concat(_buildingWorlds.Select(world => RigidDraw(_terrain.Length + _props.Length, world, BuildingTint)))
            .ToArray();
    }

    internal override IReadOnlyList<GltfMesh> Terrain => _terrain;
    protected override int Buildings => _buildingWorlds.Length;
    protected override int Props => _propWorlds.Length;
    protected override float WorldExtent => _side * 8f;
    protected override object Assets => new
    {
        TextureWidth = _splat.Width,
        TextureHeight = _splat.Height,
        Mips = 3,
        _splat.Layers,
        _splat.TriplanarSharpness,
        _splat.Projection,
        _splat.BaseSpecular,
        _splat.Sampler,
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
        PropPlacements = _propPlacements,
        PropMeshIndices = Enumerable.Range(0, Props).Select(i => i & 3).ToArray(),
        Building = MeshFacts(_box),
        BuildingWorlds = _buildingWorlds,
        Draws = _draws,
    };

    /// <summary>Every rigid draw with its recipe-local mesh bounds. Terrain comes first.</summary>
    internal IEnumerable<RendererPerfRigidBounds> RigidBounds()
    {
        MeshBounds[] bounds = [.. _terrain.Concat(_props).Append(_box).Select(mesh => MeshBounds.FromVertices(mesh.Vertices))];
        return _draws.Select(draw => new RendererPerfRigidBounds(bounds[draw.Mesh.Index], draw.World, draw.Mesh.Index < _terrain.Length));
    }

    protected override void Load(Scene3D scene)
    {
        var material = scene.LoadSplatMaterial(_splat.Width, _splat.Height, _splat.Layers, _splat.TriplanarSharpness,
            _splat.Projection, _splat.BaseSpecular, _splat.Sampler);
        var handles = new List<MeshHandle>();
        handles.AddRange(_terrain.Select(mesh => scene.LoadMesh(mesh, material)));
        handles.AddRange(_props.Select(scene.LoadMesh));
        if (Buildings > 0) handles.Add(scene.LoadMesh(_box));
        _handles = handles.ToArray();
    }
    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        foreach (RigidInstanceDraw draw in _draws)
            scene.Draw(draw with { Mesh = _handles[draw.Mesh.Index] });
    }

    static RigidInstanceDraw RigidDraw(int meshIndex, Matrix4x4 world, Color tint) => new(new MeshHandle(meshIndex), world)
    {
        Tint = tint,
        Material = new Material(Color.Transparent, 0f, 32f),
        CastsShadows = true,
        ShadowOnly = false,
        Motion = MotionKey.From(0),
        Dissolve = 0f,
        DissolveEdgeWidth = 0f,
        DissolveEdgeColor = Color.Transparent,
        InvertShadowDissolve = false,
        DissolveComplement = 0f,
        CutInsideInteriors = false,
    };

    sealed record SplatConstruction(int Width, int Height, SplatLayerImage[] Layers, float TriplanarSharpness,
        SplatProjection Projection, float BaseSpecular, TerrainSamplerConfig Sampler);

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
